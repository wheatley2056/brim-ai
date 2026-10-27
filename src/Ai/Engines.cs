using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Brim.Ai;

public static partial class Engines
{
    /// <summary>Die passende Anbindung für eine gefundene KI.</summary>
    public static IAiEngine Create(EngineInfo info, HttpClient http, string workingDirectory) => info.Kind switch
    {
        EngineKind.Ollama => new OllamaEngine(info, http),
        EngineKind.LmStudio => new OpenAiEngine(info, http),
        _ => new CliEngine(info, workingDirectory),
    };

    /// <summary>Nachrichten im Format von Ollama und OpenAI: Anweisung, bisheriges Gespräch, neue Frage.</summary>
    public static JsonArray Messages(ChatRequest request)
    {
        var messages = new JsonArray { Message("system", request.System) };
        foreach (var turn in request.History)
        {
            messages.Add(Message("user", turn.Question));
            messages.Add(Message("assistant", turn.Answer));
        }
        messages.Add(Message("user", request.Prompt));
        return messages;

        static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };
    }

    /// <summary>Für Kommandozeilen: alles in einem Text über die Standardeingabe, damit nichts in die Befehlszeile muss.</summary>
    public static string CliPrompt(ChatRequest request)
    {
        var text = new StringBuilder(request.System).Append("\n\n");
        if (request.History.Count > 0)
        {
            text.Append("Bisheriges Gespräch:\n\n");
            foreach (var turn in request.History)
            {
                text.Append("Frage: ").Append(turn.Question).Append("\n\nAntwort: ").Append(turn.Answer).Append("\n\n");
            }
            text.Append("Neue Frage:\n\n");
        }
        return text.Append(request.Prompt).ToString();
    }

    /// <summary>Farb- und Steuerzeichen aus Terminal-Ausgaben entfernen.</summary>
    public static string StripAnsi(string text) => AnsiPattern().Replace(text, "");

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07")]
    private static partial Regex AnsiPattern();

    internal static string Shorten(string text, int max = 240)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}

/// <summary>
/// Claude Code (<c>claude -p</c>) oder Gemini CLI (<c>gemini</c>): Frage über die Standardeingabe, Antwort von der
/// Standardausgabe. Beide nutzen die Anmeldung, die der Nutzer im Terminal einmal gemacht hat.
/// </summary>
public sealed class CliEngine(EngineInfo info, string workingDirectory) : IAiEngine
{
    public EngineInfo Info => info;

    /// <summary>Wie das Programm gestartet wird. Eine <c>.cmd</c> von npm braucht <c>cmd.exe</c>.</summary>
    public static ProcessStartInfo StartInfo(EngineInfo info, string model, string workingDirectory)
    {
        var args = new List<string>();
        if (info.Kind == EngineKind.ClaudeCode)
        {
            args.AddRange(["-p", "--output-format", "text"]);
            if (model.Length > 0)
            {
                args.AddRange(["--model", model]);
            }
        }
        else if (model.Length > 0)
        {
            args.AddRange(["-m", model]);
        }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        if (Path.GetExtension(info.Location).ToLowerInvariant() is ".cmd" or ".bat")
        {
            // Die Argumente sind fest oder ein geprüfter Modellname, also ohne Zeichen, die cmd.exe deuten würde.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = $"/d /s /c \"\"{info.Location}\" {string.Join(' ', args)}\"";
        }
        else
        {
            psi.FileName = info.Location;
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        psi.Environment["NO_COLOR"] = "1";
        return psi;
    }

    public async Task<string> AskAsync(ChatRequest request, Action<string> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        Process process;
        try
        {
            process = Process.Start(StartInfo(info, request.Model, workingDirectory)) ?? throw new AiException($"{info.Name} ließ sich nicht starten");
        }
        catch (Win32Exception ex)
        {
            throw new AiException($"{info.Name} ließ sich nicht starten: {ex.Message}");
        }
        using (process)
        await using (cancellationToken.Register(() => Kill(process)))
        {
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.StandardInput.WriteAsync(Engines.CliPrompt(request));
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Das Programm hat sich schon beendet; der Fehler steht gleich in stderr.
            }

            var output = new StringBuilder();
            var buffer = new char[1024];
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                output.Append(buffer, 0, read);
                progress(Engines.StripAnsi(output.ToString()));
            }
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();

            var answer = Engines.StripAnsi(output.ToString()).Trim();
            if (process.ExitCode != 0 || answer.Length == 0)
            {
                throw new AiException(ErrorText(info, Engines.StripAnsi(await errors), answer, process.ExitCode));
            }
            return answer;
        }
    }

    /// <summary>Lesbarer Grund aus der Fehlerausgabe, z. B. „Claude Code ist nicht angemeldet …“.</summary>
    public static string ErrorText(EngineInfo info, string stderr, string stdout, int exitCode)
    {
        var all = stderr + "\n" + stdout;
        if (all.Contains("/login", StringComparison.OrdinalIgnoreCase)
            || all.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || all.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)
            || all.Contains("auth", StringComparison.OrdinalIgnoreCase) && all.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            var command = info.Kind == EngineKind.ClaudeCode ? "claude" : "gemini";
            return $"{info.Name} ist nicht angemeldet: im Terminal „{command}“ starten und anmelden";
        }
        var line = all.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return line is null
            ? exitCode == 0 ? $"{info.Name} hat nichts geantwortet" : $"{info.Name} ist mit Code {exitCode} abgebrochen"
            : $"{info.Name}: {Engines.Shorten(line)}";
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Schon beendet.
        }
    }
}

/// <summary>Ollama über <c>/api/chat</c>; die Antwort kommt Stück für Stück als JSON-Zeilen.</summary>
public sealed class OllamaEngine(EngineInfo info, HttpClient http) : IAiEngine
{
    public EngineInfo Info => info;

    public async Task<string> AskAsync(ChatRequest request, Action<string> progress, CancellationToken cancellationToken)
    {
        var model = request.Model.Length > 0 ? request.Model
            : info.Models.FirstOrDefault() ?? throw new AiException("In Ollama ist kein Modell: z. B. „ollama pull llama3.2“");
        var body = new JsonObject { ["model"] = model, ["messages"] = Engines.Messages(request), ["stream"] = true };
        using var message = new HttpRequestMessage(HttpMethod.Post, info.Location + "/api/chat") { Content = JsonContent.Create(body) };
        using var response = await Send(http, message, info, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AiException($"Ollama: {await ErrorAsync(response, cancellationToken)}");
        }
        var answer = new StringBuilder();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw new AiException($"Ollama: {Engines.Shorten(error.ToString())}");
            }
            if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } piece)
            {
                answer.Append(piece);
                progress(answer.ToString());
            }
            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            {
                break;
            }
        }
        return answer.ToString().Trim();
    }

    internal static async Task<HttpResponseMessage> Send(HttpClient http, HttpRequestMessage message, EngineInfo info, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new AiException($"{info.Name} läuft nicht mehr unter {info.Location}");
        }
    }

    /// <summary>Fehlertext aus <c>{"error":"…"}</c> oder <c>{"error":{"message":"…"}}</c>, sonst der Status.</summary>
    internal static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() : error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                if (message is { Length: > 0 })
                {
                    return Engines.Shorten(message);
                }
            }
        }
        catch (JsonException)
        {
        }
        return $"Fehler {(int)response.StatusCode}";
    }
}

/// <summary>LM Studio oder ein anderer OpenAI-kompatibler Server über <c>/v1/chat/completions</c> mit Server-Sent Events.</summary>
public sealed class OpenAiEngine(EngineInfo info, HttpClient http) : IAiEngine
{
    public EngineInfo Info => info;

    public async Task<string> AskAsync(ChatRequest request, Action<string> progress, CancellationToken cancellationToken)
    {
        var model = request.Model.Length > 0 ? request.Model
            : info.Models.FirstOrDefault() ?? throw new AiException("In LM Studio ist kein Modell geladen");
        var body = new JsonObject { ["model"] = model, ["messages"] = Engines.Messages(request), ["stream"] = true };
        using var message = new HttpRequestMessage(HttpMethod.Post, info.Location + "/v1/chat/completions") { Content = JsonContent.Create(body) };
        using var response = await OllamaEngine.Send(http, message, info, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AiException($"{info.Name}: {await OllamaEngine.ErrorAsync(response, cancellationToken)}");
        }
        var answer = new StringBuilder();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                break;
            }
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw new AiException($"{info.Name}: {Engines.Shorten(error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : error.ToString())}");
            }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } piece)
            {
                answer.Append(piece);
                progress(answer.ToString());
            }
        }
        return answer.ToString().Trim();
    }
}
