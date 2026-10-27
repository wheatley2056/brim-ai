using System.Text.Json;

namespace Brim.Ai;

/// <summary>
/// Sucht die KI auf dem Rechner: Claude Code und Gemini CLI im <c>PATH</c> (und wo ihre Installer sie hinlegen),
/// Ollama und LM Studio über ihre lokalen Server, KI-Apps im Startmenü.
/// </summary>
public sealed class ToolScanner(CommandLocator locator, HttpClient http, Func<CancellationToken, Task<IReadOnlyList<StartApp>>> startApps) : IToolScanner
{
    /// <summary>So lange darf ein lokaler Server für die Liste seiner Modelle brauchen.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public async Task<AiTools> ScanAsync(PluginSettings settings, CancellationToken cancellationToken)
    {
        var ollama = ProbeAsync(settings.OllamaUrl + "/api/tags", ParseOllamaModels, cancellationToken);
        var lmStudio = ProbeAsync(settings.LmStudioUrl + "/v1/models", ParseOpenAiModels, cancellationToken);
        var apps = AppsAsync(cancellationToken);

        var engines = new List<EngineInfo>();
        if (locator.Find("claude") is { } claude)
        {
            engines.Add(new EngineInfo(EngineKind.ClaudeCode, claude, []));
        }
        if (await ollama is { Count: > 0 } ollamaModels)
        {
            engines.Add(new EngineInfo(EngineKind.Ollama, settings.OllamaUrl, ollamaModels));
        }
        if (await lmStudio is { Count: > 0 } lmModels)
        {
            engines.Add(new EngineInfo(EngineKind.LmStudio, settings.LmStudioUrl, lmModels));
        }
        if (locator.Find("gemini") is { } gemini)
        {
            engines.Add(new EngineInfo(EngineKind.Gemini, gemini, []));
        }
        return new AiTools(engines, await apps);
    }

    private async Task<IReadOnlyList<AiApp>> AppsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return KnownApps.Match(await startApps(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Modelle eines lokalen Servers; <c>null</c>, wenn er nicht läuft.</summary>
    private async Task<IReadOnlyList<string>?> ProbeAsync(string url, Func<string, IReadOnlyList<string>> parse, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await http.GetAsync(url, timeout.Token);
            return response.IsSuccessStatusCode ? parse(await response.Content.ReadAsStringAsync(timeout.Token)) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Ollama <c>/api/tags</c>: <c>{"models":[{"name":"llama3.2:latest"}]}</c>, ohne Modelle für Embeddings.</summary>
    public static IReadOnlyList<string> ParseOllamaModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return models.EnumerateArray()
            .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
            .Where(ChatModel)
            .ToList();
    }

    /// <summary>OpenAI <c>/v1/models</c>: <c>{"data":[{"id":"qwen2.5-7b-instruct"}]}</c>, ohne Modelle für Embeddings.</summary>
    public static IReadOnlyList<string> ParseOpenAiModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return data.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "")
            .Where(ChatModel)
            .ToList();
    }

    private static bool ChatModel(string name) => name.Length > 0 && !name.Contains("embed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ausgabe von <c>Get-StartApps | ConvertTo-Json</c>: ein Objekt oder eine Liste mit <c>Name</c> und <c>AppID</c>.</summary>
    public static IReadOnlyList<StartApp> ParseStartApps(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.EnumerateArray().ToList(),
            JsonValueKind.Object => [doc.RootElement],
            _ => [],
        };
        return items
            .Where(i => i.ValueKind == JsonValueKind.Object)
            .Select(i => new StartApp(Str(i, "Name"), Str(i, "AppID")))
            .Where(a => a.Name.Length > 0 && a.AppId.Length > 0)
            .ToList();

        static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}

/// <summary>Findet Kommandozeilen-Programme wie Windows: im <c>PATH</c> mit <c>.exe</c>, <c>.cmd</c> oder <c>.bat</c>.</summary>
public sealed class CommandLocator(Func<string, string?> environment, Func<string, bool> fileExists)
{
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

    public static CommandLocator ForThisPc() => new(Environment.GetEnvironmentVariable, File.Exists);

    public string? Find(string name)
    {
        foreach (var dir in Directories())
        {
            // App-Aliase aus dem Store: Claude Desktop legt dort ein „claude.exe“ ab, das nicht Claude Code ist.
            if (dir.TrimEnd('\\', '/').EndsWith(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var extension in Extensions)
            {
                var path = dir.TrimEnd('\\', '/') + @"\" + name + extension;
                if (fileExists(path))
                {
                    return path;
                }
            }
        }
        return null;
    }

    private IEnumerable<string> Directories()
    {
        foreach (var dir in (environment("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return dir.Trim('"');
        }
        // Brim erbt den PATH von der Anmeldung; frisch installierte Programme stehen dort erst nach einer neuen.
        if (environment("USERPROFILE") is { Length: > 0 } profile)
        {
            yield return profile + @"\.local\bin";
        }
        if (environment("APPDATA") is { Length: > 0 } roaming)
        {
            yield return roaming + @"\npm";
        }
    }
}
