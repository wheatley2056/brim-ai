namespace Brim.Ai.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+K", Hotkey.Control | Hotkey.Alt, 'K')]
    [InlineData("strg + umschalt + leertaste", Hotkey.Control | Hotkey.Shift, 0x20)]
    [InlineData("Win+Shift+F9", Hotkey.Win | Hotkey.Shift, 0x78)]
    [InlineData("Alt+1", Hotkey.Alt, '1')]
    public void Reads_common_spellings(string text, uint modifiers, uint key)
    {
        var hotkey = Hotkey.Parse(text);

        Assert.NotNull(hotkey);
        Assert.Equal(modifiers, hotkey.Modifiers);
        Assert.Equal(key, hotkey.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("K")]
    [InlineData("Shift+K")]
    [InlineData("Ctrl+Hyper+K")]
    [InlineData("Ctrl+Alt+Pause")]
    public void Rejects_unusable_shortcuts(string text) => Assert.Null(Hotkey.Parse(text));
}

public sealed class PluginSettingsTests
{
    [Fact]
    public void Defaults_use_any_ai_and_ctrl_alt_k()
    {
        var settings = PluginSettings.From(Test.Values(new { }));

        Assert.Equal(PluginSettings.Auto, settings.Engine);
        Assert.Null(settings.PreferredEngine);
        Assert.Equal("Ctrl+Alt+K", settings.Hotkey);
        Assert.Equal(PluginSettings.DefaultOllama, settings.OllamaUrl);
    }

    [Theory]
    [InlineData("claude", EngineKind.ClaudeCode)]
    [InlineData("Ollama", EngineKind.Ollama)]
    [InlineData("LM Studio", EngineKind.LmStudio)]
    [InlineData("gemini", EngineKind.Gemini)]
    public void Picks_a_preferred_engine(string text, EngineKind kind) =>
        Assert.Equal(kind, PluginSettings.From(Test.Values(new { engine = text })).PreferredEngine);

    [Fact]
    public void Off_means_hand_questions_to_the_app()
    {
        var settings = PluginSettings.From(Test.Values(new { engine = "aus" }));

        Assert.Null(settings.Engine);
    }

    [Fact]
    public void Model_names_with_odd_characters_are_dropped()
    {
        Assert.Equal("llama3.2:latest", PluginSettings.From(Test.Values(new { model = "llama3.2:latest" })).Model);
        Assert.Equal("", PluginSettings.From(Test.Values(new { model = "sonnet & calc" })).Model);
    }

    [Fact]
    public void Server_addresses_get_a_scheme_and_lose_the_slash()
    {
        var settings = PluginSettings.From(Test.Values(new { ollamaUrl = "gpu-box:11434/", lmstudioUrl = "ftp://nope" }));

        Assert.Equal("http://gpu-box:11434", settings.OllamaUrl);
        Assert.Equal(PluginSettings.DefaultLmStudio, settings.LmStudioUrl);
    }

    [Fact]
    public void Empty_hotkey_switches_it_off()
    {
        Assert.Equal("", PluginSettings.From(Test.Values(new { hotkey = "" })).Hotkey);
    }
}

public sealed class AnswerTests
{
    [Fact]
    public void Markdown_becomes_plain_paragraphs()
    {
        var blocks = Answer.Blocks("## Kurz gesagt\n\nDas ist **wichtig** und `code`.\n\n- erstens\n- [zweitens](https://x.de)\n\n---\nEnde");

        Assert.Equal(["Kurz gesagt", "Das ist wichtig und code.", "• erstens\n• zweitens", "Ende"], blocks.Select(b => b.Text));
        Assert.All(blocks, b => Assert.False(b.Code));
    }

    [Fact]
    public void Code_blocks_stay_together_even_while_still_open()
    {
        var done = Answer.Blocks("So:\n```powershell\nGet-Process\n  | Sort CPU\n```\nFertig");
        var open = Answer.Blocks("So:\n```\nGet-Process");

        Assert.Equal(new AnswerBlock("Get-Process\n  | Sort CPU", true), done[1]);
        Assert.Equal("Fertig", done[2].Text);
        Assert.Equal(new AnswerBlock("Get-Process", true), open[1]);
    }

    [Fact]
    public void Thinking_of_reasoning_models_is_hidden()
    {
        Assert.Equal("Antwort", Answer.WithoutThinking("<think>hmm, also …</think>\nAntwort"));
        Assert.Equal("", Answer.WithoutThinking("<think>noch am Denken"));
    }

    [Fact]
    public void Tables_become_lines()
    {
        var blocks = Answer.Blocks("| Name | Wert |\n|---|---|\n| A | 1 |");

        Assert.Equal("Name · Wert\nA · 1", blocks.Single().Text);
    }

    [Fact]
    public void Long_paragraphs_are_split_for_brim()
    {
        var blocks = Answer.Blocks(new string('a', 4000));

        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.True(b.Text.Length <= Answer.MaxBlockLength));
    }

    [Fact]
    public void Italic_markers_go_but_lone_stars_stay()
    {
        Assert.Equal("ganz wichtig, 2 * 3", Answer.Inline("*ganz* wichtig, 2 * 3"));
    }
}
