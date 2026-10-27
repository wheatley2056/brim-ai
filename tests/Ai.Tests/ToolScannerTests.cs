using System.Net;

namespace Brim.Ai.Tests;

public sealed class ToolScannerTests
{
    private readonly Dictionary<string, string> _env = new()
    {
        ["PATH"] = @"C:\Windows\system32;C:\Users\tobias\AppData\Local\Microsoft\WindowsApps;""C:\Program Files\nodejs""",
        ["USERPROFILE"] = @"C:\Users\tobias",
        ["APPDATA"] = @"C:\Users\tobias\AppData\Roaming",
    };
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    private CommandLocator Locator => new(k => _env.GetValueOrDefault(k), _files.Contains);

    [Fact]
    public void Finds_npm_shims_and_the_native_installer()
    {
        _files.Add(@"C:\Users\tobias\AppData\Roaming\npm\gemini.cmd");
        _files.Add(@"C:\Users\tobias\.local\bin\claude.exe");

        Assert.Equal(@"C:\Users\tobias\.local\bin\claude.exe", Locator.Find("claude"));
        Assert.Equal(@"C:\Users\tobias\AppData\Roaming\npm\gemini.cmd", Locator.Find("gemini"));
        Assert.Null(Locator.Find("codex"));
    }

    [Fact]
    public void Ignores_the_store_alias_of_claude_desktop()
    {
        _files.Add(@"C:\Users\tobias\AppData\Local\Microsoft\WindowsApps\claude.exe");

        Assert.Null(Locator.Find("claude"));
    }

    [Fact]
    public void Path_wins_over_the_usual_places()
    {
        _files.Add(@"C:\Program Files\nodejs\claude.cmd");
        _files.Add(@"C:\Users\tobias\.local\bin\claude.exe");

        Assert.Equal(@"C:\Program Files\nodejs\claude.cmd", Locator.Find("claude"));
    }

    [Fact]
    public void Server_model_lists_skip_embedding_models()
    {
        Assert.Equal(["llama3.2:latest"], ToolScanner.ParseOllamaModels("""{"models":[{"name":"llama3.2:latest"},{"name":"nomic-embed-text:latest"}]}"""));
        Assert.Equal(["qwen2.5-7b-instruct"], ToolScanner.ParseOpenAiModels("""{"data":[{"id":"text-embedding-nomic"},{"id":"qwen2.5-7b-instruct"}],"object":"list"}"""));
        Assert.Empty(ToolScanner.ParseOllamaModels("""{"models":[]}"""));
    }

    [Fact]
    public void Start_apps_come_as_one_object_or_a_list()
    {
        Assert.Equal([new StartApp("Claude", "Claude_pzs8sxrjxfjjc!Claude")], ToolScanner.ParseStartApps("""{"Name":"Claude","AppID":"Claude_pzs8sxrjxfjjc!Claude"}"""));
        Assert.Equal(2, ToolScanner.ParseStartApps("""[{"Name":"Rechner","AppID":"calc"},{"Name":"ChatGPT","AppID":"chat"}]""").Count);
        Assert.Empty(ToolScanner.ParseStartApps(""));
    }

    [Fact]
    public void Known_ai_apps_are_picked_from_the_start_menu_in_a_fixed_order()
    {
        var apps = KnownApps.Match(
        [
            new StartApp("Microsoft 365 Copilot", "office"),
            new StartApp("ChatGPT", "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT"),
            new StartApp("Rechner", "calc"),
            new StartApp("Claude", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\AnthropicClaude\claude.exe"),
        ]);

        Assert.Equal(["claude", "chatgpt"], apps.Select(a => a.Id));
        Assert.Equal(["claude"], apps[0].ProcessNames);
    }

    [Fact]
    public async Task Scan_lists_engines_in_order_and_skips_servers_that_are_down()
    {
        _files.Add(@"C:\Users\tobias\AppData\Roaming\npm\gemini.cmd");
        _files.Add(@"C:\Users\tobias\.local\bin\claude.exe");
        var http = new FakeHttp();
        http.Routes["/api/tags"] = (HttpStatusCode.OK, """{"models":[{"name":"llama3.2:latest"}]}""");
        var scanner = new ToolScanner(Locator, new HttpClient(http), _ => Task.FromResult<IReadOnlyList<StartApp>>([new StartApp("Claude", "claude-id")]));

        var tools = await scanner.ScanAsync(new PluginSettings(), CancellationToken.None);

        Assert.Equal([EngineKind.ClaudeCode, EngineKind.Ollama, EngineKind.Gemini], tools.Engines.Select(e => e.Kind));
        Assert.Equal(["llama3.2:latest"], tools.Engines[1].Models);
        Assert.Equal("claude-id", Assert.Single(tools.Apps).AppId);
    }

    [Fact]
    public async Task A_broken_start_menu_still_finds_the_engines()
    {
        _files.Add(@"C:\Users\tobias\.local\bin\claude.exe");
        var scanner = new ToolScanner(Locator, new HttpClient(new FakeHttp()), _ => throw new InvalidOperationException("PowerShell fehlt"));

        var tools = await scanner.ScanAsync(new PluginSettings(), CancellationToken.None);

        Assert.Single(tools.Engines);
        Assert.Empty(tools.Apps);
    }
}
