namespace Brim.Ai;

/// <summary>Eine KI, die Fragen direkt beantwortet; die Antwort erscheint in der Notch.</summary>
public enum EngineKind
{
    /// <summary>Claude Code (<c>claude -p</c>), angemeldet mit dem Claude-Konto des Nutzers.</summary>
    ClaudeCode,
    /// <summary>Ollama mit einem lokalen Modell (HTTP, Port 11434).</summary>
    Ollama,
    /// <summary>LM Studio oder ein anderer OpenAI-kompatibler Server (Port 1234).</summary>
    LmStudio,
    /// <summary>Gemini CLI (<c>gemini</c>), angemeldet mit dem Google-Konto des Nutzers.</summary>
    Gemini,
}

/// <summary>Gefundene KI zum Antworten.</summary>
/// <param name="Location">Pfad der Exe oder des <c>.cmd</c> bei Kommandozeilen, Adresse bei Servern.</param>
/// <param name="Models">Bei Servern die geladenen Modelle; bei Kommandozeilen leer.</param>
public sealed record EngineInfo(EngineKind Kind, string Location, IReadOnlyList<string> Models)
{
    public string Name => NameOf(Kind);

    public static string NameOf(EngineKind kind) => kind switch
    {
        EngineKind.ClaudeCode => "Claude Code",
        EngineKind.Ollama => "Ollama",
        EngineKind.LmStudio => "LM Studio",
        EngineKind.Gemini => "Gemini CLI",
        _ => kind.ToString(),
    };
}

/// <summary>Installierte KI-App, in der man weiterfragen kann.</summary>
/// <param name="Id">Kennung aus <see cref="KnownApps"/>, z. B. <c>claude</c>.</param>
/// <param name="AppId">Start-Kennung von Windows (AppUserModelID oder Pfad), wie <c>Get-StartApps</c> sie liefert.</param>
public sealed record AiApp(string Id, string Name, string AppId)
{
    public IReadOnlyList<string> ProcessNames => KnownApps.Find(Id)?.ProcessNames ?? [];
}

/// <summary>Ein Eintrag im Startmenü.</summary>
public sealed record StartApp(string Name, string AppId);

/// <summary>Alles, was auf dem Rechner an KI gefunden wurde.</summary>
public sealed record AiTools(IReadOnlyList<EngineInfo> Engines, IReadOnlyList<AiApp> Apps)
{
    public static AiTools None { get; } = new([], []);
}

/// <summary>Frage und Antwort aus dem laufenden Gespräch.</summary>
public sealed record ChatTurn(string Question, string Answer);

/// <summary>Was an eine KI geht: bisheriges Gespräch, neue Frage, Anweisung und Modell.</summary>
public sealed record ChatRequest(IReadOnlyList<ChatTurn> History, string Prompt, string System, string Model);

/// <summary>Bekannte KI-Apps: wie sie im Startmenü heißen und wie ihr Prozess heißt (zum Erkennen des Fensters).</summary>
public static class KnownApps
{
    public sealed record Known(string Id, string Name, IReadOnlyList<string> StartNames, IReadOnlyList<string> ProcessNames);

    public static IReadOnlyList<Known> All { get; } =
    [
        new("claude", "Claude", ["Claude"], ["claude"]),
        new("chatgpt", "ChatGPT", ["ChatGPT"], ["ChatGPT"]),
        new("copilot", "Copilot", ["Copilot", "Microsoft Copilot"], ["Copilot", "Microsoft.Copilot"]),
        new("perplexity", "Perplexity", ["Perplexity"], ["Perplexity"]),
    ];

    public static Known? Find(string id) => All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Die bekannten Apps aus dem Startmenü, in der Reihenfolge von <see cref="All"/>.</summary>
    public static IReadOnlyList<AiApp> Match(IEnumerable<StartApp> startApps)
    {
        var apps = startApps.ToList();
        var found = new List<AiApp>();
        foreach (var known in All)
        {
            var hit = apps.FirstOrDefault(a => known.StartNames.Any(n => string.Equals(a.Name.Trim(), n, StringComparison.OrdinalIgnoreCase)));
            if (hit is not null && hit.AppId.Length > 0)
            {
                found.Add(new AiApp(known.Id, known.Name, hit.AppId));
            }
        }
        return found;
    }
}

/// <summary>Die aktuelle Frage mit ihrer (entstehenden) Antwort.</summary>
public sealed record Exchange
{
    /// <summary>Was oben steht: die Frage oder z. B. „Zusammenfassen: …“ für die Zwischenablage.</summary>
    public required string Title { get; init; }

    /// <summary>Was an die KI ging.</summary>
    public required string Prompt { get; init; }

    public string Answer { get; init; } = "";

    public string? Error { get; init; }

    /// <summary>Wer antwortet, z. B. „Claude Code“.</summary>
    public string? Engine { get; init; }

    public bool Done { get; init; }

    /// <summary>Hinweis statt Antwort, z. B. „In Claude weitergegeben“.</summary>
    public string? Note { get; init; }
}
