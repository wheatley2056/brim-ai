namespace Brim.Ai;

// Was das Plugin vom Rechner braucht, als Schnittstellen: Die Tests setzen Fakes ein, Program.cs die echten.

/// <summary>Sucht die installierte KI.</summary>
public interface IToolScanner
{
    Task<AiTools> ScanAsync(PluginSettings settings, CancellationToken cancellationToken);
}

/// <summary>Eine KI, die Fragen beantwortet.</summary>
public interface IAiEngine
{
    EngineInfo Info { get; }

    /// <summary>Fragt und meldet den bisherigen Text der Antwort laufend an <paramref name="progress"/>.</summary>
    /// <exception cref="AiException">Die KI hat mit einem Fehler geantwortet.</exception>
    Task<string> AskAsync(ChatRequest request, Action<string> progress, CancellationToken cancellationToken);
}

public sealed class AiException(string message) : Exception(message);

/// <summary>Eingabefeld, Zwischenablage, Tastenkürzel und Apps von Windows.</summary>
public interface IDesktop
{
    /// <summary>Zeigt das Eingabefeld unter der Notch; <paramref name="submit"/> bekommt den Text nach Enter.</summary>
    void ShowPrompt(string placeholder, Action<string> submit);

    Task<string?> ReadClipboardAsync();

    Task<bool> WriteClipboardAsync(string text);

    /// <summary>Setzt das Tastenkürzel (<c>null</c> = keins); <c>false</c>, wenn es schon belegt ist.</summary>
    Task<bool> SetHotkeyAsync(Hotkey? hotkey);

    /// <summary>Startet die App oder holt sie nach vorn.</summary>
    void Launch(AiApp app);

    /// <summary>
    /// Legt <paramref name="text"/> in die Zwischenablage, öffnet die App und fügt ihn ein, sobald ihr Fenster vorn ist
    /// (mit <paramref name="send"/> auch Enter). <c>false</c>, wenn das Fenster nicht kam: Der Text liegt dann in der Zwischenablage.
    /// </summary>
    Task<bool> HandOffAsync(AiApp app, string text, bool send, CancellationToken cancellationToken);
}
