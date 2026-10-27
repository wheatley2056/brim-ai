using System.Text;
using System.Text.RegularExpressions;

namespace Brim.Ai;

/// <summary>Ein Absatz der Antwort; Code in Monospace.</summary>
public sealed record AnswerBlock(string Text, bool Code);

/// <summary>
/// Macht aus der Antwort der KI (meist Markdown) Absätze für die Notch: ohne Sternchen und Rauten, Aufzählungen mit
/// „•“, Codeblöcke für sich. Denkschritte von Modellen wie DeepSeek-R1 (<c>&lt;think&gt;</c>) fallen weg.
/// </summary>
public static partial class Answer
{
    /// <summary>Brim zeigt höchstens 2000 Zeichen je Text.</summary>
    public const int MaxBlockLength = 1800;

    public static string WithoutThinking(string text)
    {
        text = ThinkPattern().Replace(text, "");
        // Noch nicht fertig gedacht: alles ab dem offenen <think> ist Denken.
        var open = text.IndexOf("<think>", StringComparison.Ordinal);
        return (open >= 0 ? text[..open] : text).Trim();
    }

    public static IReadOnlyList<AnswerBlock> Blocks(string markdown)
    {
        var blocks = new List<AnswerBlock>();
        var paragraph = new List<string>();
        var code = (List<string>?)null;

        foreach (var raw in WithoutThinking(markdown).Replace("\r", "").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (code is null)
                {
                    Flush();
                    code = [];
                }
                else
                {
                    Add(string.Join('\n', code), true);
                    code = null;
                }
                continue;
            }
            if (code is not null)
            {
                code.Add(raw.TrimEnd());
                continue;
            }
            var line = raw.Trim();
            if (line.Length == 0 || HorizontalRule().IsMatch(line))
            {
                Flush();
                continue;
            }
            if (Heading().Match(line) is { Success: true } heading)
            {
                Flush();
                paragraph.Add(Inline(heading.Groups[1].Value));
                Flush();
                continue;
            }
            if (Bullet().Match(raw) is { Success: true } bullet)
            {
                var indent = bullet.Groups[1].Value.Length >= 2 ? "   " : "";
                paragraph.Add(indent + "• " + Inline(bullet.Groups[2].Value));
                continue;
            }
            if (TableRow().IsMatch(line))
            {
                if (!TableRule().IsMatch(line))
                {
                    paragraph.Add(string.Join(" · ", line.Trim('|').Split('|').Select(c => Inline(c.Trim()))));
                }
                continue;
            }
            paragraph.Add(Inline(line.StartsWith('>') ? line.TrimStart('>', ' ') : line));
        }
        if (code is not null)
        {
            // Noch im Entstehen: offenen Codeblock trotzdem zeigen.
            Add(string.Join('\n', code), true);
        }
        Flush();
        return blocks;

        void Flush()
        {
            if (paragraph.Count > 0)
            {
                Add(string.Join('\n', paragraph), false);
                paragraph.Clear();
            }
        }

        void Add(string text, bool isCode)
        {
            text = isCode ? text.Trim('\n') : text.Trim();
            for (var start = 0; start < text.Length; start += MaxBlockLength)
            {
                blocks.Add(new AnswerBlock(text.Substring(start, Math.Min(MaxBlockLength, text.Length - start)), isCode));
            }
        }
    }

    /// <summary>Die Antwort als schlichter Text, z. B. für die Sprachausgabe oder eine Zeile im Widget.</summary>
    public static string Plain(string markdown) => string.Join("\n\n", Blocks(markdown).Select(b => b.Text));

    /// <summary>Erste Zeile, gekürzt.</summary>
    public static string FirstLine(string text, int max = 90)
    {
        var line = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Fett, kursiv, Code und Links ohne Markdown-Zeichen.</summary>
    public static string Inline(string text)
    {
        text = Link().Replace(text, "$1");
        text = Bold().Replace(text, "$2");
        text = InlineCode().Replace(text, "$1");
        text = Italic().Replace(text, "$1");
        return text;
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkPattern();

    [GeneratedRegex(@"^#{1,6}\s+(.*?)\s*#*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(?:-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^\|.*\|$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"^\|[\s:|-]+\|$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1")]
    private static partial Regex Bold();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"(?<![\w*])\*(?!\s)([^*]+?)(?<!\s)\*(?![\w*])")]
    private static partial Regex Italic();
}
