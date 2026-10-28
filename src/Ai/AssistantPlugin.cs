using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;

namespace Brim.Ai;

/// <summary>
/// Der Assistent: nimmt Fragen aus dem Eingabefeld (Tastenkürzel oder Knopf) oder Text aus der Zwischenablage, lässt
/// die gefundene KI antworten und zeigt die Antwort in der Notch und als Widget, während sie entsteht. Ohne KI zum
/// Antworten gehen Fragen an die KI-App (Claude Desktop, ChatGPT …). Schickt nur, was sich geändert hat.
/// </summary>
public sealed class AssistantPlugin
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>So oft wird nach neu installierter oder gestarteter KI gesucht.</summary>
    public static readonly TimeSpan RescanEvery = TimeSpan.FromMinutes(10);

    /// <summary>So lange bleibt eine neue Antwort kompakt in der Notch stehen.</summary>
    public static readonly TimeSpan AnswerStaysFor = TimeSpan.FromMinutes(5);

    /// <summary>Länger wartet der Assistent nicht auf eine Antwort.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Während die Antwort entsteht, höchstens so oft neu zeichnen.</summary>
    public static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>So viele Fragen und Antworten gehen als Gespräch mit.</summary>
    public const int MaxHistory = 6;

    /// <summary>Mehr Text aus der Zwischenablage geht nicht an die KI.</summary>
    public const int MaxClipboard = 20_000;

    private readonly INotchSink _sink;
    private readonly IToolScanner _scanner;
    private readonly Func<EngineInfo, IAiEngine> _createEngine;
    private readonly IDesktop _desktop;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _sent = [];
    private readonly List<ChatTurn> _history = [];
    private IReadOnlyDictionary<string, IReadOnlyList<TileSize>> _placed = new Dictionary<string, IReadOnlyList<TileSize>>();
    private DateTimeOffset? _scannedAt;
    private DateTimeOffset? _freshUntil;
    private DateTimeOffset _progressAt;
    private CancellationTokenSource? _run;
    private int _runId;

    /// <summary>Die Frage, die der Nutzer (oder eine neue Frage) abgebrochen hat.</summary>
    private int _stoppedRun;

    public AssistantPlugin(INotchSink sink, IToolScanner scanner, Func<EngineInfo, IAiEngine> createEngine, IDesktop desktop, TimeProvider time, PluginSettings settings)
    {
        _sink = sink;
        _scanner = scanner;
        _createEngine = createEngine;
        _desktop = desktop;
        _time = time;
        Settings = settings;
    }

    public PluginSettings Settings { get; private set; }

    public AiTools Tools { get; private set; } = AiTools.None;

    public Exchange? Current { get; private set; }

    public IReadOnlyList<ChatTurn> History => _history;

    /// <summary>Die KI, die gerade antworten würde; <c>null</c> = keine (Fragen gehen an die App).</summary>
    public EngineInfo? Engine =>
        Settings.Engine is null ? null
        : Settings.PreferredEngine is { } kind ? Tools.Engines.FirstOrDefault(e => e.Kind == kind)
        : Tools.Engines.FirstOrDefault();

    /// <summary>Die App zum Weiterfragen.</summary>
    public AiApp? App =>
        string.Equals(Settings.App, PluginSettings.Auto, StringComparison.OrdinalIgnoreCase)
            ? Tools.Apps.FirstOrDefault()
            : Tools.Apps.FirstOrDefault(a => string.Equals(a.Id, Settings.App, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name, Settings.App, StringComparison.OrdinalIgnoreCase)) ?? Tools.Apps.FirstOrDefault();

    private Views.State State => new(Current, Fresh, Engine, App, Tools, _scannedAt is not null, _history.Count > 0);

    private bool Fresh => _freshUntil is { } until && _time.GetUtcNow() < until;

    /// <summary>Die Anweisung an die KI vor jeder Frage.</summary>
    public static string SystemPrompt(PluginSettings settings)
    {
        var text = "Du bist ein Assistent in der Notch von Brim unter Windows 11. Der Platz ist knapp: Antworte kurz und klar, "
            + "meist in wenigen Sätzen, ohne Tabellen und ohne Rückfragen, wenn es nicht nötig ist. Antworte in der Sprache der Frage.";
        return settings.Instructions.Length > 0 ? text + " " + settings.Instructions : text;
    }

    // ───────── Ablauf ─────────

    public async Task StartAsync()
    {
        await ApplyHotkeyAsync();
        await _gate.WaitAsync();
        try
        {
            await ScanAsync(CancellationToken.None);
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, _time, cancellationToken);
                await TickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Sucht ab und zu neu nach KI und nimmt eine alte Antwort aus der kompakten Notch.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_run is null && (_scannedAt is not { } scanned || _time.GetUtcNow() - scanned >= RescanEvery))
            {
                await ScanAsync(cancellationToken);
            }
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var before = Tools;
        Tools = await _scanner.ScanAsync(Settings, cancellationToken);
        _scannedAt = _time.GetUtcNow();
        if (Describe(before) != Describe(Tools))
        {
            await _sink.LogAsync("Gefunden: " + Describe(Tools), "info");
        }
    }

    private static string Describe(AiTools tools)
    {
        var engines = tools.Engines.Select(e => e.Models.Count > 0 ? $"{e.Name} ({string.Join(", ", e.Models.Take(5))})" : $"{e.Name} ({e.Location})");
        var text = string.Join("; ", engines.Concat(tools.Apps.Select(a => $"App {a.Name}")));
        return text.Length > 0 ? text : "keine KI";
    }

    private async Task ApplyHotkeyAsync()
    {
        var hotkey = Hotkey.Parse(Settings.Hotkey);
        if (Settings.Hotkey.Length > 0 && hotkey is null)
        {
            await _sink.LogAsync($"Tastenkürzel „{Settings.Hotkey}“ verstehe ich nicht; Beispiel: Ctrl+Alt+K", "warn");
        }
        if (!await _desktop.SetHotkeyAsync(hotkey) && hotkey is not null)
        {
            await _sink.LogAsync($"Tastenkürzel {hotkey.Text} ist schon belegt", "warn");
            await _sink.ShowHudAsync(Views.Hud($"Tastenkürzel {hotkey.Text} ist schon von einem anderen Programm belegt"));
        }
    }

    // ───────── Fragen ─────────

    /// <summary>Öffnet das Eingabefeld; die Frage geht nach Enter an die KI.</summary>
    public void OpenPrompt()
    {
        var target = Engine?.Name ?? App?.Name ?? "die KI";
        var placeholder = _history.Count > 0 ? $"Weiterfragen bei {target} …" : $"Frag {target} …";
        _desktop.ShowPrompt(placeholder + "   Enter schickt, Esc schließt", text => _ = AskAsync(text.Trim(), text.Trim()));
    }

    /// <summary>Text aus der Zwischenablage mit einer der <see cref="Views.ClipboardTasks"/>.</summary>
    public async Task AskAboutClipboardAsync(string taskId)
    {
        var task = Views.ClipboardTasks.FirstOrDefault(t => t.Id == taskId);
        if (task.Id is null)
        {
            return;
        }
        var text = (await _desktop.ReadClipboardAsync())?.Trim() ?? "";
        if (text.Length == 0)
        {
            await _sink.ShowHudAsync(Views.Hud("In der Zwischenablage ist kein Text", Views.PasteGlyph));
            return;
        }
        if (text.Length > MaxClipboard)
        {
            text = text[..MaxClipboard];
        }
        await AskAsync($"{task.Label}: {Answer.FirstLine(text, 70)}", task.Prompt + "\n\n" + text);
    }

    /// <summary>Stellt die Frage. Eine laufende Frage wird abgebrochen.</summary>
    public async Task AskAsync(string title, string prompt)
    {
        if (prompt.Length == 0)
        {
            return;
        }
        IAiEngine engine;
        ChatRequest request;
        CancellationTokenSource run;
        int id;
        await _gate.WaitAsync();
        try
        {
            CancelRun();
            if (Engine is not { } info)
            {
                await HandOffInsteadAsync(title, prompt);
                return;
            }
            engine = _createEngine(info);
            request = new ChatRequest(_history.ToList(), prompt, SystemPrompt(Settings), Settings.Model);
            run = new CancellationTokenSource(AnswerTimeout, _time);
            _run = run;
            id = ++_runId;
            Current = new Exchange { Title = title, Prompt = prompt, Engine = info.Name };
            _freshUntil = DateTimeOffset.MaxValue;
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }

        string? answer = null;
        string? error = null;
        try
        {
            answer = Answer.WithoutThinking(await engine.AskAsync(request, text => _ = ProgressAsync(id, text), run.Token));
            if (answer.Length == 0)
            {
                error = $"{engine.Info.Name} hat nichts geantwortet";
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            error = id == _stoppedRun ? "Abgebrochen" : $"Keine Antwort nach {AnswerTimeout.TotalMinutes:0} Minuten";
        }
        catch (Exception ex) when (ex is AiException or HttpRequestException or JsonException or IOException)
        {
            error = ex is AiException ? ex.Message : $"{engine.Info.Name}: {ex.Message}";
            await _sink.LogAsync(error, "warn");
        }

        await _gate.WaitAsync();
        try
        {
            if (id != _runId || Current is null)
            {
                return;
            }
            _run = null;
            run.Dispose();
            if (error is null && answer is not null)
            {
                Current = Current with { Answer = answer, Done = true };
                _history.Add(new ChatTurn(prompt, answer));
                if (_history.Count > MaxHistory)
                {
                    _history.RemoveAt(0);
                }
            }
            else
            {
                Current = Current with { Error = error, Done = true };
            }
            _freshUntil = _time.GetUtcNow() + AnswerStaysFor;
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Neuer Text der entstehenden Antwort; zeichnet höchstens alle <see cref="ProgressEvery"/>.</summary>
    private async Task ProgressAsync(int id, string text)
    {
        var now = _time.GetUtcNow();
        if (now - _progressAt < ProgressEvery)
        {
            return;
        }
        _progressAt = now;
        await _gate.WaitAsync();
        try
        {
            if (id == _runId && Current is { Done: false } current && text.Length > current.Answer.Length)
            {
                Current = current with { Answer = text };
                await RenderAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Keine KI zum Antworten: Frage an die App geben (unter dem Gate).</summary>
    private async Task HandOffInsteadAsync(string title, string prompt)
    {
        if (App is not { } app)
        {
            Current = new Exchange { Title = title, Prompt = prompt, Done = true, Error = Views.NoAiText(State) };
            _freshUntil = _time.GetUtcNow() + AnswerStaysFor;
            await RenderAsync();
            return;
        }
        Current = new Exchange { Title = title, Prompt = prompt, Done = true, Engine = app.Name, Note = $"In {app.Name} weitergegeben" };
        _freshUntil = _time.GetUtcNow() + AnswerStaysFor;
        await RenderAsync();
        _ = HandOffAsync(app, prompt);
    }

    private async Task HandOffAsync(AiApp app, string text)
    {
        if (!await _desktop.HandOffAsync(app, text, Settings.HandoffSend, CancellationToken.None))
        {
            await _sink.ShowHudAsync(Views.Hud($"Frage liegt in der Zwischenablage: in {app.Name} mit Strg+V einfügen", Views.PasteGlyph));
        }
    }

    private void CancelRun()
    {
        if (_run is { } run)
        {
            _stoppedRun = _runId;
            run.Cancel();
        }
    }

    // ───────── Anzeigen ─────────

    private async Task RenderAsync()
    {
        var state = State;
        var activity = Views.Activity(state);
        if (Changed("activity", activity))
        {
            await _sink.SetActivityAsync(activity);
        }
        foreach (var size in _placed.GetValueOrDefault("assistant") ?? [])
        {
            var widget = Views.AssistantWidget(state, size);
            if (Changed("assistant:" + size, widget))
            {
                await _sink.SetWidgetAsync("assistant", size, widget);
            }
        }
        foreach (var size in _placed.GetValueOrDefault("apps") ?? [])
        {
            var widget = Views.AppsWidget(state, size);
            if (Changed("apps:" + size, widget))
            {
                await _sink.SetWidgetAsync("apps", size, widget);
            }
        }
    }

    /// <summary>Merkt sich, was zuletzt geschickt wurde; <c>true</c>, wenn <paramref name="value"/> neu ist.</summary>
    private bool Changed(string key, object value)
    {
        var json = JsonSerializer.Serialize(value, value.GetType(), PluginJson.Options);
        if (_sent.TryGetValue(key, out var sent) && sent == json)
        {
            return false;
        }
        _sent[key] = json;
        return true;
    }

    // ───────── Ereignisse von Brim ─────────

    public async Task SetPlacedWidgetsAsync(IReadOnlyDictionary<string, IReadOnlyList<TileSize>> placed)
    {
        await _gate.WaitAsync();
        try
        {
            _placed = placed;
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplySettingsAsync(PluginSettings settings)
    {
        var hotkeyChanged = settings.Hotkey != Settings.Hotkey;
        await _gate.WaitAsync();
        try
        {
            var rescan = settings.OllamaUrl != Settings.OllamaUrl || settings.LmStudioUrl != Settings.LmStudioUrl;
            Settings = settings;
            if (rescan)
            {
                await ScanAsync(CancellationToken.None);
            }
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
        if (hotkeyChanged)
        {
            await ApplyHotkeyAsync();
        }
    }

    public async Task HandleActionAsync(string actionId)
    {
        switch (actionId)
        {
            case Views.AskAction:
                OpenPrompt();
                return;
            case Views.StopAction:
                await WithGateAsync(() =>
                {
                    CancelRun();
                    return Task.CompletedTask;
                });
                return;
            case Views.NewAction:
                await WithGateAsync(async () =>
                {
                    CancelRun();
                    _runId++;
                    _run = null;
                    _history.Clear();
                    Current = null;
                    _freshUntil = null;
                    await RenderAsync();
                });
                return;
            case Views.DismissAction:
                await WithGateAsync(async () =>
                {
                    _freshUntil = null;
                    await RenderAsync();
                });
                return;
            case Views.CopyAction:
                if (Current is { Answer.Length: > 0 } current)
                {
                    var copied = await _desktop.WriteClipboardAsync(Answer.Plain(current.Answer));
                    await _sink.ShowHudAsync(Views.Hud(copied ? "Antwort kopiert" : "Zwischenablage ist gerade belegt, bitte noch mal"));
                }
                return;
            case Views.HandOffAction:
                if (App is not { } app)
                {
                    await _sink.ShowHudAsync(Views.Hud("Keine KI-App gefunden"));
                }
                else if (Current?.Prompt is { Length: > 0 } prompt)
                {
                    await HandOffAsync(app, prompt);
                }
                else
                {
                    _desktop.Launch(app);
                }
                return;
            case Views.RescanAction:
                await WithGateAsync(async () =>
                {
                    await ScanAsync(CancellationToken.None);
                    await RenderAsync();
                });
                return;
        }
        if (actionId.StartsWith(Views.ClipboardPrefix, StringComparison.Ordinal))
        {
            await AskAboutClipboardAsync(actionId[Views.ClipboardPrefix.Length..]);
        }
        else if (actionId.StartsWith(Views.AppPrefix, StringComparison.Ordinal)
            && Tools.Apps.FirstOrDefault(a => a.Id == actionId[Views.AppPrefix.Length..]) is { } target)
        {
            _desktop.Launch(target);
        }
    }

    private async Task WithGateAsync(Func<Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
