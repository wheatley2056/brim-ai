using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Ai;

/// <summary>Alle Inhalte des Plugins als Widget-Bäume. Reine Funktionen, damit Tests sie prüfen können.</summary>
public static class Views
{
    /// <summary>Segoe Fluent Icons „Robot“.</summary>
    public static readonly string AssistantGlyph = "";

    /// <summary>„AllApps“.</summary>
    public static readonly string AppsGlyph = "";

    private static readonly string CopyGlyph = "";
    private static readonly string StopGlyph = "";
    private static readonly string AskGlyph = "";
    private static readonly string NewGlyph = "";
    private static readonly string CloseGlyph = "";
    public static readonly string PasteGlyph = "";

    public const string Accent = "#C08BFF";
    public const string ActivityId = "assistant";

    public const string AskAction = "ask";
    public const string NewAction = "new";
    public const string StopAction = "stop";
    public const string CopyAction = "copy";
    public const string HandOffAction = "handoff";
    public const string DismissAction = "dismiss";
    public const string RescanAction = "rescan";

    /// <summary>„clip:summarize“ nimmt den Text aus der Zwischenablage.</summary>
    public const string ClipboardPrefix = "clip:";

    /// <summary>„app:claude“ öffnet die App.</summary>
    public const string AppPrefix = "app:";

    /// <summary>Aufgaben für Text aus der Zwischenablage: Kennung, Knopf, Auftrag an die KI.</summary>
    public static IReadOnlyList<(string Id, string Label, string Prompt)> ClipboardTasks { get; } =
    [
        ("explain", "Erklären", "Erkläre kurz und verständlich, worum es in diesem Text geht:"),
        ("summarize", "Zusammenfassen", "Fasse das Wichtigste in wenigen Sätzen zusammen:"),
        ("translate", "Übersetzen", "Übersetze ins Deutsche; ist der Text schon Deutsch, ins Englische. Gib nur die Übersetzung aus:"),
        ("improve", "Verbessern", "Verbessere Rechtschreibung, Grammatik und Stil, ohne den Inhalt zu ändern. Gib nur den verbesserten Text aus:"),
    ];

    /// <summary>Was die Ansichten brauchen.</summary>
    /// <param name="Fresh">Frage läuft oder Antwort ist neu: kompakt in der Notch zeigen.</param>
    public sealed record State(Exchange? Current, bool Fresh, EngineInfo? Engine, AiApp? App, AiTools Tools, bool Scanned, bool InConversation);

    // ───────── Texte ─────────

    public static string NoAiText(State s) =>
        !s.Scanned ? "Sucht KI auf dem Rechner …"
        : "Keine KI gefunden. Claude Code, Ollama, LM Studio oder Gemini CLI installieren, oder Claude Desktop zum Weiterfragen";

    /// <summary>Kurz neben dem Symbol in der Notch.</summary>
    public static string Compact(Exchange e) =>
        e.Error is not null ? "Fehler"
        : e.Note is not null ? "Übergeben"
        : !e.Done ? "denkt nach …"
        : "Antwort";

    // ───────── Notch ─────────

    /// <summary>
    /// Die Aktivität „assistant“: läuft eine Frage oder ist die Antwort neu, kompakt in der Notch; sonst im Hintergrund,
    /// damit der Assistent aufgeklappt immer erreichbar ist.
    /// </summary>
    public static ActivityParams Activity(State s)
    {
        var e = s.Current;
        var color = e?.Error is not null ? BrimColors.Warning : Accent;
        return new ActivityParams
        {
            Id = ActivityId,
            Priority = ActivityPriority.Ongoing,
            Leading = new IconWidget(AssistantGlyph) { Size = 14, Color = color },
            Trailing = e is null
                ? new TextWidget("KI") { Style = TextStyle.Caption, FontSize = 12 }
                : !e.Done
                    ? new StackWidget(
                        new LevelWidget { Color = Accent, Height = 12 },
                        new TextWidget(Compact(e)) { Style = TextStyle.Caption, FontSize = 12 })
                    { Spacing = 6, AccessibleName = $"{e.Engine ?? "Die KI"} denkt nach" }
                    : new TextWidget(Compact(e))
                    {
                        Style = TextStyle.Caption,
                        FontSize = 12,
                        Color = e.Error is not null ? BrimColors.Warning : BrimColors.Fog,
                    },
            Bead = new IconWidget(AssistantGlyph) { Size = 8, Color = color },
            Expanded = Panel(s, TileSize.Full, notch: true),
            Accent = Accent,
            Rank = 4,
            Background = !s.Fresh,
            Announcement = e is { Done: true, Error: null, Note: null } && s.Fresh ? "Antwort der KI ist da" : null,
        };
    }

    // ───────── Widget „KI-Assistent“ ─────────

    public static Widget AssistantWidget(State s, TileSize size) => size switch
    {
        TileSize.Large or TileSize.Full => Panel(s, size, notch: false),
        _ => Strip(s, size),
    };

    /// <summary>Groß und in der Notch: Frage, Antwort zum Scrollen, Knöpfe.</summary>
    public static Widget Panel(State s, TileSize size, bool notch)
    {
        var wide = size == TileSize.Full;
        var header = Header(s, notch);
        Widget body;
        if (s.Current is { } e)
        {
            var parts = new List<Widget>
            {
                new TextWidget(e.Title) { Style = TextStyle.Secondary, FontSize = 12, AccessibleName = "Frage: " + e.Title },
                new ListWidget(AnswerLines(e)) { Spacing = 6, Scrollable = true, Grow = 1 },
                Buttons(AnswerButtons(s, e, wide || notch)),
            };
            body = new StackWidget(parts) { Orientation = StackOrientation.Vertical, Spacing = 6, Align = CrossAlign.Stretch, Grow = 1 };
        }
        else
        {
            var hint = s.Engine is { } engine
                ? $"Frag {engine.Name} oder nimm Text aus der Zwischenablage." + (s.Scanned && s.App is { } app ? $" Weiterfragen in {app.Name}." : "")
                : s.App is { } only ? $"Fragen gehen an {only.Name}. Für Antworten direkt hier Claude Code, Ollama oder LM Studio installieren."
                : NoAiText(s);
            body = new StackWidget(
                new TextWidget(hint) { Style = TextStyle.Secondary, FontSize = 12, Wrap = true },
                new SpacerWidget { Grow = 1 },
                Buttons(IdleButtons(s, wide || notch)))
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 6,
                Align = CrossAlign.Stretch,
                Grow = 1,
            };
        }
        return new StackWidget(header, body)
        {
            Orientation = StackOrientation.Vertical,
            Spacing = notch ? 8 : TileCard.HeaderGap,
            Align = CrossAlign.Stretch,
            Grow = 1,
        };
    }

    /// <summary>Mittel und breit: eine Zeile Antwort oder die Knöpfe.</summary>
    private static Widget Strip(State s, TileSize size)
    {
        var header = Header(s, notch: false);
        if (s.Current is { } e)
        {
            var line = e.Error ?? e.Note ?? (e.Answer.Length > 0 ? Answer.FirstLine(Answer.Plain(e.Answer), size == TileSize.Wide ? 120 : 50) : "denkt nach …");
            var row = new List<Widget>
            {
                new TextWidget(line)
                {
                    Style = e.Error is null ? TextStyle.Body : TextStyle.Secondary,
                    FontSize = 12,
                    Color = e.Error is null ? null : BrimColors.Warning,
                    Grow = 1,
                },
            };
            row.Add(!e.Done ? IconButton(StopAction, StopGlyph, "Abbrechen") : IconButton(AskAction, AskGlyph, "Weiterfragen"));
            if (size == TileSize.Wide && e is { Done: true, Error: null, Note: null })
            {
                row.Insert(1, IconButton(CopyAction, CopyGlyph, "Antwort kopieren"));
            }
            return TileCard.Card(header, new StackWidget(row) { Spacing = 4 });
        }
        var buttons = new List<Widget> { TextButton(AskAction, "Fragen", AskGlyph) };
        buttons.AddRange(ClipboardTasks.Take(size == TileSize.Wide ? 4 : 1).Select(t => TextButton(ClipboardPrefix + t.Id, t.Label, null)));
        return TileCard.Card(header, new StackWidget(buttons) { Spacing = 6 });
    }

    private static StackWidget Header(State s, bool notch)
    {
        var trailing = s.Current?.Engine ?? s.Engine?.Name ?? s.App?.Name;
        var children = new List<Widget>
        {
            new IconWidget(AssistantGlyph) { Size = notch ? 14 : 12, Color = Accent },
            new TextWidget("KI-Assistent") { Style = notch ? TextStyle.Body : TextStyle.Caption, Grow = 1 },
        };
        if (trailing is not null)
        {
            children.Add(new TextWidget(trailing) { Style = TextStyle.Caption });
        }
        if (notch && s.Current is { Done: true })
        {
            children.Add(IconButton(DismissAction, CloseGlyph, "Schließen"));
        }
        return new StackWidget(children) { Spacing = 6 };
    }

    /// <summary>Die Antwort in Absätzen; solange nichts da ist „denkt nach …“.</summary>
    public static IReadOnlyList<Widget> AnswerLines(Exchange e)
    {
        var lines = new List<Widget>();
        foreach (var block in Answer.Blocks(e.Answer).Take(150))
        {
            lines.Add(block.Code
                ? new StackWidget(new TextWidget(block.Text) { Style = TextStyle.Mono, FontSize = 12, Wrap = true, Grow = 1 })
                {
                    Background = BrimColors.Graphite,
                    CornerRadius = 6,
                    Padding = (8, 6, 8, 6),
                }
                : new TextWidget(block.Text) { Style = TextStyle.Body, Wrap = true });
        }
        if (e.Note is not null)
        {
            lines.Add(new TextWidget(e.Note) { Style = TextStyle.Secondary, Wrap = true });
        }
        if (e.Error is not null)
        {
            lines.Add(new TextWidget(e.Error) { Style = TextStyle.Body, Color = BrimColors.Warning, Wrap = true });
        }
        if (!e.Done && lines.Count == 0)
        {
            lines.Add(new StackWidget(
                new LevelWidget { Color = Accent, Height = 12 },
                new TextWidget($"{e.Engine ?? "Die KI"} denkt nach …") { Style = TextStyle.Secondary })
            { Spacing = 8 });
        }
        return lines;
    }

    private static List<Widget> AnswerButtons(State s, Exchange e, bool roomy)
    {
        if (!e.Done)
        {
            return [TextButton(StopAction, "Abbrechen", StopGlyph)];
        }
        var buttons = new List<Widget>();
        if (e.Error is null && e.Note is null && e.Answer.Length > 0)
        {
            buttons.Add(TextButton(CopyAction, "Kopieren", CopyGlyph));
        }
        buttons.Add(TextButton(AskAction, s.InConversation ? "Weiterfragen" : "Fragen", AskGlyph));
        if (s.App is { } app && e.Note is null && roomy)
        {
            buttons.Add(TextButton(HandOffAction, "In " + app.Name, null));
        }
        buttons.Add(IconButton(NewAction, NewGlyph, "Neues Gespräch"));
        return buttons;
    }

    private static List<Widget> IdleButtons(State s, bool roomy)
    {
        var buttons = new List<Widget> { TextButton(AskAction, "Fragen", AskGlyph) };
        if (s.Engine is not null)
        {
            buttons.AddRange(ClipboardTasks.Take(roomy ? 4 : 2).Select(t => TextButton(ClipboardPrefix + t.Id, t.Label, null)));
        }
        else if (!s.Scanned || s.App is null)
        {
            buttons.Add(TextButton(RescanAction, "Neu suchen", null));
        }
        return buttons;
    }

    private static StackWidget Buttons(List<Widget> buttons) => new(buttons) { Spacing = 6 };

    private static ButtonWidget TextButton(string action, string label, string? glyph) => new(action)
    {
        Kind = ButtonKind.Secondary,
        Label = label,
        Glyph = glyph,
        GlyphSize = 12,
    };

    private static ButtonWidget IconButton(string action, string glyph, string name) => new(action)
    {
        Glyph = glyph,
        Size = 28,
        GlyphSize = 13,
        AccessibleName = name,
    };

    // ───────── Widget „KI-Apps“ ─────────

    public static Widget AppsWidget(State s, TileSize size)
    {
        if (s.Tools.Apps.Count == 0)
        {
            return TileCard.Empty(AppsGlyph, !s.Scanned ? "Sucht …" : size == TileSize.Small ? "Keine KI-App" : "Keine KI-App gefunden");
        }
        var header = TileCard.Header(AppsGlyph, Accent, "KI-Apps");
        var max = size switch { TileSize.Small => 1, TileSize.Medium => 2, _ => 5 };
        var buttons = s.Tools.Apps.Take(max)
            .Select(a => (Widget)new ButtonWidget(AppPrefix + a.Id) { Kind = ButtonKind.Secondary, Label = a.Name, AccessibleName = a.Name + " öffnen" })
            .ToList();
        if (size == TileSize.Wide && s.Engine is not null)
        {
            buttons.Add(new SpacerWidget { Grow = 1 });
            buttons.Add(TextButton(AskAction, "Fragen", AskGlyph));
        }
        return TileCard.Card(header, new StackWidget(buttons) { Spacing = 6 });
    }

    // ───────── Meldungen ─────────

    public static HudParams Hud(string text, string? glyph = null) => new()
    {
        Id = "ai",
        Content = new StackWidget(
            new IconWidget(glyph ?? AssistantGlyph) { Size = 16, Color = Accent },
            new TextWidget(text) { Style = TextStyle.Body, Grow = 1 })
        {
            Spacing = 12,
            Padding = (18, 0, 18, 0),
        },
        DurationMs = 5000,
        Announcement = text,
    };
}
