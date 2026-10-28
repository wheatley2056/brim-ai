using System.Text.Json;

namespace Brim.Ai;

/// <summary>Einstellungen aus <c>plugin.json</c> mit den Werten des Nutzers. Schalter dürfen auch als Text kommen.</summary>
public sealed record PluginSettings
{
    public const string Auto = "auto";
    public const string DefaultOllama = "http://127.0.0.1:11434";
    public const string DefaultLmStudio = "http://127.0.0.1:1234";

    /// <summary><c>auto</c>, eine bestimmte KI oder <c>null</c> („aus“: Fragen gehen nur an die App).</summary>
    public string? Engine { get; init; } = Auto;

    /// <summary>Modell; leer = Standard der KI. Nur Buchstaben, Ziffern und <c>._:-/</c>.</summary>
    public string Model { get; init; } = "";

    /// <summary><c>auto</c> oder Kennung bzw. Name einer App aus <see cref="KnownApps"/>.</summary>
    public string App { get; init; } = Auto;

    public bool HandoffSend { get; init; }

    public string Hotkey { get; init; } = "Ctrl+Alt+K";

    public string Instructions { get; init; } = "";

    public string OllamaUrl { get; init; } = DefaultOllama;

    public string LmStudioUrl { get; init; } = DefaultLmStudio;

    /// <summary>Gewünschte KI, falls nicht <c>auto</c> oder aus.</summary>
    public EngineKind? PreferredEngine => Engine?.ToLowerInvariant() switch
    {
        "claude" or "claude code" or "claudecode" => EngineKind.ClaudeCode,
        "ollama" => EngineKind.Ollama,
        "lmstudio" or "lm studio" or "openai" => EngineKind.LmStudio,
        "gemini" or "gemini cli" => EngineKind.Gemini,
        _ => null,
    };

    public static PluginSettings From(IReadOnlyDictionary<string, JsonElement> values)
    {
        var defaults = new PluginSettings();
        var engine = values.ContainsKey("engine") ? Text(values, "engine").ToLowerInvariant() : Auto;
        var model = Text(values, "model");
        return new PluginSettings
        {
            Engine = engine is "aus" or "off" or "keine" or "nein" ? null : engine.Length == 0 ? Auto : engine,
            Model = model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-' or '/') ? model : "",
            App = Text(values, "app") is { Length: > 0 } app ? app : Auto,
            HandoffSend = Flag(values, "handoffSend", defaults.HandoffSend),
            Hotkey = values.ContainsKey("hotkey") ? Text(values, "hotkey") : defaults.Hotkey,
            Instructions = Text(values, "instructions"),
            OllamaUrl = Url(values, "ollamaUrl", DefaultOllama),
            LmStudioUrl = Url(values, "lmstudioUrl", DefaultLmStudio),
        };
    }

    private static string Url(IReadOnlyDictionary<string, JsonElement> values, string key, string fallback)
    {
        var text = Text(values, key).TrimEnd('/');
        if (text.Length == 0)
        {
            return fallback;
        }
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? text : fallback;
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";

    private static bool Flag(IReadOnlyDictionary<string, JsonElement> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number => value.GetDouble() != 0,
            _ => fallback,
        };
    }
}
