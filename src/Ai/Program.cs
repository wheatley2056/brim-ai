// KI-Assistent für Brim: Fragen per Tastenkürzel oder aus der Notch, Antworten in der Notch und als Widget. Nutzt die
// KI, die schon auf dem Rechner ist (Claude Code, Ollama, LM Studio, Gemini CLI), und gibt sonst an Claude Desktop,
// ChatGPT, Copilot oder Perplexity weiter.
using Brim.Ai;
using Brim.Ai.Windows;
using Brim.Sdk;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Der KI-Assistent läuft nur unter Windows.");
    return 1;
}

BrimPlugin brim;
try
{
    brim = await BrimPlugin.ConnectAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Keine Verbindung zu Brim: {ex.Message}");
    return 1;
}

await using (brim)
{
    // Arbeitsordner für Claude Code und Gemini CLI: leer, damit sie dort nichts finden, was sie lesen wollen.
    var workDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "PluginData", "ai");
    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    using var shell = new DesktopShell();
    await shell.StartAsync();

    var settings = PluginSettings.From(brim.Settings);
    var scanner = new ToolScanner(CommandLocator.ForThisPc(), http, StartApps.ReadAsync);
    var plugin = new AssistantPlugin(new BrimSink(brim), scanner, info => Engines.Create(info, http, workDir), shell, TimeProvider.System, settings);

    shell.HotkeyPressed += plugin.OpenPrompt;
    brim.ActionInvoked += (action, value) => _ = plugin.HandleActionAsync(action);
    brim.SettingsChanged += values => _ = plugin.ApplySettingsAsync(PluginSettings.From(values));
    brim.WidgetsPlaced += placed => _ = plugin.SetPlacedWidgetsAsync(placed);
    _ = plugin.SetPlacedWidgetsAsync(brim.PlacedWidgets);

    await plugin.StartAsync();
    Console.WriteLine($"KI zum Antworten: {plugin.Engine?.Name ?? "keine"}, App zum Weiterfragen: {plugin.App?.Name ?? "keine"}, Tastenkürzel: {settings.Hotkey}");

    using var stop = new CancellationTokenSource();
    var loop = plugin.RunAsync(stop.Token);
    await brim.RunAsync();

    // Brim gibt 2 Sekunden zum Beenden.
    stop.Cancel();
    await Task.WhenAny(loop, Task.Delay(500));
}
return 0;
