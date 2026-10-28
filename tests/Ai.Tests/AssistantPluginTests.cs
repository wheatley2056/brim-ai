using Brim.Core;
using Brim.Core.Widgets;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Ai.Tests;

public sealed class AssistantPluginTests
{
    private readonly RecordingSink _sink = new();
    private readonly FakeScanner _scanner = new();
    private readonly FakeDesktop _desktop = new();
    private readonly FakeTimeProvider _time = new(Test.Now);
    private readonly Dictionary<EngineKind, FakeEngine> _engines = [];

    private FakeEngine Engine(EngineKind kind = EngineKind.ClaudeCode) => _engines[kind];

    private async Task<AssistantPlugin> StartAsync(PluginSettings? settings = null, params EngineInfo[] engines)
    {
        engines = engines.Length > 0 ? engines : [Test.Claude];
        foreach (var info in engines)
        {
            _engines[info.Kind] = new FakeEngine(info);
        }
        _scanner.Tools = new AiTools(engines, [Test.ClaudeApp, Test.ChatGpt]);
        var plugin = new AssistantPlugin(_sink, _scanner, info => _engines[info.Kind], _desktop, _time, settings ?? new PluginSettings());
        await plugin.StartAsync();
        return plugin;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TileSize>> Placed(string id, params TileSize[] sizes) =>
        new Dictionary<string, IReadOnlyList<TileSize>> { [id] = sizes };

    [Fact]
    public async Task Starts_in_the_background_with_the_hotkey_set()
    {
        await StartAsync();

        Assert.True(_sink.Last!.Background);
        Assert.Equal("Ctrl+Alt+K", Assert.Single(_desktop.Hotkeys)!.Text);
        Assert.Contains("Frag Claude Code", Test.Json(_sink.Last.Expanded!));
    }

    [Fact]
    public async Task A_taken_hotkey_is_reported()
    {
        _desktop.HotkeyFree = false;

        await StartAsync();

        Assert.Contains("schon von einem anderen Programm belegt", Test.Json(Assert.Single(_sink.Huds)));
    }

    [Fact]
    public async Task Question_from_the_prompt_streams_into_the_notch()
    {
        var plugin = await StartAsync();
        await plugin.HandleActionAsync(Views.AskAction);
        Assert.Equal("Frag Claude Code …   Enter schickt, Esc schließt", Assert.Single(_desktop.Placeholders));

        _desktop.Submit!("  Was ist DHCP?  ");
        await Engine().Started.Task;
        var thinking = _sink.Last!;
        Assert.False(thinking.Background);
        Assert.Contains("denkt nach", Test.Json(thinking.Trailing!));

        Engine().Stream("**Dynamic** Host");
        await Test.Until(() => Test.Json(_sink.Last!.Expanded!).Contains("Dynamic Host"));

        Engine().Finish("**Dynamic Host Configuration Protocol**: verteilt IP-Adressen.");
        await Test.Until(() => plugin.Current?.Done == true);
        var done = _sink.Last!;
        Assert.Equal("Antwort", ((TextWidget)done.Trailing!).Text);
        var expanded = Test.Json(done.Expanded!);
        Assert.Contains("Was ist DHCP?", expanded);
        Assert.Contains("Dynamic Host Configuration Protocol: verteilt IP-Adressen.", expanded);
        Assert.Contains("\"copy\"", expanded);
        Assert.Contains("In Claude", expanded);
        Assert.Equal("Was ist DHCP?", Engine().Requests.Single().Prompt);
        Assert.Contains("Notch von Brim", Engine().Requests.Single().System);
    }

    [Fact]
    public async Task Follow_up_questions_carry_the_conversation()
    {
        var plugin = await StartAsync();
        var first = plugin.AskAsync("Was ist DNS?", "Was ist DNS?");
        await Engine().Started.Task;
        Engine().Finish("Namensauflösung.");
        await first;

        plugin.OpenPrompt();
        Assert.StartsWith("Weiterfragen bei Claude Code", _desktop.Placeholders[^1]);
        var second = plugin.AskAsync("Und DHCP?", "Und DHCP?");
        await Engine().Started.Task;
        Engine().Finish("IP-Adressen.");
        await second;

        Assert.Equal([new ChatTurn("Was ist DNS?", "Namensauflösung.")], Engine().Requests[1].History);
        Assert.Equal(2, plugin.History.Count);

        await plugin.HandleActionAsync(Views.NewAction);
        Assert.Empty(plugin.History);
        Assert.Null(plugin.Current);
        Assert.True(_sink.Last!.Background);
    }

    [Fact]
    public async Task Clipboard_text_is_summarized()
    {
        var plugin = await StartAsync();
        _desktop.Clipboard = "Sehr geehrte Damen und Herren,\nder Server wird am Samstag gewartet.";

        var asking = plugin.HandleActionAsync(Views.ClipboardPrefix + "summarize");
        await Engine().Started.Task;
        Engine().Finish("Wartung am Samstag.");
        await asking;

        var request = Engine().Requests.Single();
        Assert.StartsWith("Fasse das Wichtigste", request.Prompt);
        Assert.EndsWith("der Server wird am Samstag gewartet.", request.Prompt);
        Assert.Equal("Zusammenfassen: Sehr geehrte Damen und Herren,", plugin.Current!.Title);
    }

    [Fact]
    public async Task Empty_clipboard_says_so()
    {
        var plugin = await StartAsync();

        await plugin.HandleActionAsync(Views.ClipboardPrefix + "translate");

        Assert.Contains("kein Text", Test.Json(Assert.Single(_sink.Huds)));
        Assert.Empty(Engine().Requests);
    }

    [Fact]
    public async Task Copy_puts_the_plain_answer_on_the_clipboard()
    {
        var plugin = await StartAsync();
        var asking = plugin.AskAsync("Gruß", "Schreib einen Gruß");
        await Engine().Started.Task;
        Engine().Finish("**Hallo** Welt");
        await asking;

        await plugin.HandleActionAsync(Views.CopyAction);

        Assert.Equal("Hallo Welt", _desktop.Clipboard);
        Assert.Contains("Antwort kopiert", Test.Json(_sink.Huds[^1]));
    }

    [Fact]
    public async Task Stop_cancels_and_keeps_what_came_so_far()
    {
        var plugin = await StartAsync();
        var asking = plugin.AskAsync("Lang", "Erzähl lang");
        await Engine().Started.Task;
        Engine().Stream("Es war einmal");
        await Test.Until(() => plugin.Current!.Answer.Length > 0);

        await plugin.HandleActionAsync(Views.StopAction);
        await asking;

        Assert.Equal("Abgebrochen", plugin.Current!.Error);
        Assert.Equal("Es war einmal", plugin.Current.Answer);
        Assert.Empty(plugin.History);
    }

    [Fact]
    public async Task Errors_of_the_ai_turn_the_notch_orange()
    {
        var plugin = await StartAsync();
        var asking = plugin.AskAsync("Hallo", "Hallo");
        await Engine().Started.Task;
        Engine().Fail("Claude Code ist nicht angemeldet: im Terminal „claude“ starten und anmelden");
        await asking;

        var last = _sink.Last!;
        Assert.Equal(BrimColors.Warning, ((TextWidget)last.Trailing!).Color);
        Assert.Contains("nicht angemeldet", Test.Json(last.Expanded!));
    }

    [Fact]
    public async Task Without_an_ai_questions_go_to_claude_desktop()
    {
        var plugin = await StartAsync(new PluginSettings { Engine = null, HandoffSend = true });

        await plugin.AskAsync("Plan für morgen", "Plan für morgen");
        await Test.Until(() => _desktop.HandOffs.Count == 1);

        Assert.Equal((Test.ClaudeApp, "Plan für morgen", true), _desktop.HandOffs[0]);
        Assert.Equal("In Claude weitergegeben", plugin.Current!.Note);
        Assert.Empty(Engine().Requests);
    }

    [Fact]
    public async Task A_failed_hand_off_leaves_the_question_on_the_clipboard()
    {
        _desktop.HandOffWorks = false;
        var plugin = await StartAsync(new PluginSettings { Engine = null, App = "chatgpt" });

        await plugin.AskAsync("Hallo", "Hallo");
        await Test.Until(() => _sink.Huds.Count == 1);

        Assert.Equal(Test.ChatGpt, _desktop.HandOffs[0].App);
        Assert.Contains("in ChatGPT mit Strg+V einfügen", Test.Json(_sink.Huds[0]));
    }

    [Fact]
    public async Task Nothing_installed_explains_what_to_install()
    {
        var plugin = new AssistantPlugin(_sink, _scanner, _ => throw new InvalidOperationException(), _desktop, _time, new PluginSettings());
        await plugin.StartAsync();

        await plugin.AskAsync("Hallo", "Hallo");

        Assert.Contains("Keine KI gefunden", plugin.Current!.Error);
    }

    [Fact]
    public async Task Preferred_engine_is_used_when_found()
    {
        var plugin = await StartAsync(new PluginSettings { Engine = "ollama" }, Test.Claude, Test.Ollama);

        Assert.Equal(EngineKind.Ollama, plugin.Engine!.Kind);
        var asking = plugin.AskAsync("Hallo", "Hallo");
        await Engine(EngineKind.Ollama).Started.Task;
        Engine(EngineKind.Ollama).Finish("Hi");
        await asking;
        Assert.Empty(Engine(EngineKind.ClaudeCode).Requests);
    }

    [Fact]
    public async Task Fresh_answers_leave_the_compact_notch_after_five_minutes()
    {
        var plugin = await StartAsync();
        var asking = plugin.AskAsync("Hallo", "Hallo");
        await Engine().Started.Task;
        Engine().Finish("Hi");
        await asking;
        Assert.False(_sink.Last!.Background);

        _time.Advance(AssistantPlugin.AnswerStaysFor);
        await plugin.TickAsync();

        Assert.True(_sink.Last!.Background);
        Assert.Contains("Hi", Test.Json(_sink.Last.Expanded!));
    }

    [Fact]
    public async Task Dismiss_moves_the_answer_out_of_the_way()
    {
        var plugin = await StartAsync();
        var asking = plugin.AskAsync("Hallo", "Hallo");
        await Engine().Started.Task;
        Engine().Finish("Hi");
        await asking;

        await plugin.HandleActionAsync(Views.DismissAction);

        Assert.True(_sink.Last!.Background);
    }

    [Fact]
    public async Task Widgets_show_buttons_and_then_the_answer()
    {
        var plugin = await StartAsync();
        await plugin.SetPlacedWidgetsAsync(new Dictionary<string, IReadOnlyList<TileSize>>
        {
            ["assistant"] = [TileSize.Large, TileSize.Wide],
            ["apps"] = [TileSize.Medium],
        });

        var idle = Test.Json(_sink.LastWidget("assistant", TileSize.Wide)!);
        Assert.Contains("clip:summarize", idle);
        Assert.Contains("clip:improve", idle);
        var apps = Test.Json(_sink.LastWidget("apps", TileSize.Medium)!);
        Assert.Contains("app:claude", apps);
        Assert.Contains("app:chatgpt", apps);

        var asking = plugin.AskAsync("Hallo", "Hallo");
        await Engine().Started.Task;
        Engine().Finish("Hallo zurück!\n\nZweiter Absatz.");
        await asking;

        Assert.Contains("Hallo zurück!", Test.Json(_sink.LastWidget("assistant", TileSize.Wide)!));
        Assert.Contains("Zweiter Absatz.", Test.Json(_sink.LastWidget("assistant", TileSize.Large)!));
    }

    [Fact]
    public async Task App_buttons_launch_the_app()
    {
        var plugin = await StartAsync();

        await plugin.HandleActionAsync(Views.AppPrefix + "chatgpt");

        Assert.Equal(Test.ChatGpt, Assert.Single(_desktop.Launched));
    }

    [Fact]
    public async Task Hand_off_sends_the_last_question_to_the_app()
    {
        var plugin = await StartAsync(new PluginSettings { App = "ChatGPT" });
        var asking = plugin.AskAsync("Rezept", "Rezept für Käsespätzle");
        await Engine().Started.Task;
        Engine().Finish("Spätzle, Käse, Zwiebeln.");
        await asking;

        await plugin.HandleActionAsync(Views.HandOffAction);

        Assert.Equal((Test.ChatGpt, "Rezept für Käsespätzle", false), Assert.Single(_desktop.HandOffs));
    }

    [Fact]
    public async Task Rescans_now_and_then_but_not_while_answering()
    {
        var plugin = await StartAsync();
        Assert.Equal(1, _scanner.Scans);

        _time.Advance(AssistantPlugin.RescanEvery);
        await plugin.TickAsync();
        Assert.Equal(2, _scanner.Scans);

        var asking = plugin.AskAsync("Hallo", "Hallo");
        await Engine().Started.Task;
        _time.Advance(AssistantPlugin.RescanEvery);
        await plugin.TickAsync();
        Assert.Equal(2, _scanner.Scans);
        Engine().Finish("Hi");
        await asking;
    }

    [Fact]
    public async Task Changing_the_hotkey_registers_it_again()
    {
        var plugin = await StartAsync();

        await plugin.ApplySettingsAsync(new PluginSettings { Hotkey = "" });

        Assert.Equal([Hotkey.Parse("Ctrl+Alt+K"), null], _desktop.Hotkeys);
    }
}
