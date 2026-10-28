using System.Net;
using System.Text.Json;

namespace Brim.Ai.Tests;

public sealed class EnginesTests
{
    private static readonly ChatRequest Request = new([new ChatTurn("Was ist DNS?", "Das Telefonbuch des Internets.")], "Und DHCP?", "Sei knapp.", "");

    [Fact]
    public void Chat_messages_carry_system_history_and_question()
    {
        var messages = Engines.Messages(Request);

        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => (string)m!["role"]!));
        Assert.Equal("Und DHCP?", (string)messages[^1]!["content"]!);
    }

    [Fact]
    public void Command_line_prompt_holds_everything()
    {
        var text = Engines.CliPrompt(Request);

        Assert.StartsWith("Sei knapp.", text);
        Assert.Contains("Frage: Was ist DNS?\n\nAntwort: Das Telefonbuch des Internets.", text);
        Assert.EndsWith("Neue Frage:\n\nUnd DHCP?", text);
    }

    [Fact]
    public void Claude_code_runs_in_print_mode_with_the_model()
    {
        var psi = CliEngine.StartInfo(Test.Claude, "sonnet", @"C:\Brim\ai");

        Assert.Equal(Test.Claude.Location, psi.FileName);
        Assert.Equal(["-p", "--output-format", "text", "--model", "sonnet"], psi.ArgumentList);
        Assert.True(psi.RedirectStandardInput);
        Assert.Equal(@"C:\Brim\ai", psi.WorkingDirectory);
    }

    [Fact]
    public void Npm_shims_go_through_cmd()
    {
        var gemini = new EngineInfo(EngineKind.Gemini, @"C:\Users\tobias\AppData\Roaming\npm\gemini.cmd", []);

        var psi = CliEngine.StartInfo(gemini, "gemini-2.5-flash", @"C:\Brim\ai");

        Assert.EndsWith("cmd.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"/d /s /c """"C:\Users\tobias\AppData\Roaming\npm\gemini.cmd"" -m gemini-2.5-flash""", psi.Arguments);
    }

    [Fact]
    public void Missing_login_is_explained()
    {
        var text = CliEngine.ErrorText(Test.Claude, "Invalid API key · Please run /login", "", 1);

        Assert.Equal("Claude Code ist nicht angemeldet: im Terminal „claude“ starten und anmelden", text);
    }

    [Fact]
    public void Other_errors_show_the_last_line()
    {
        Assert.Equal("Claude Code: Error: rate limit", CliEngine.ErrorText(Test.Claude, "starting\nError: rate limit\n", "", 1));
        Assert.Equal("Claude Code ist mit Code 3 abgebrochen", CliEngine.ErrorText(Test.Claude, "", "", 3));
    }

    [Fact]
    public void Terminal_colors_are_removed() => Assert.Equal("rot", Engines.StripAnsi("\u001b[31mrot\u001b[0m"));

    [Fact]
    public async Task Ollama_streams_the_answer()
    {
        var http = new FakeHttp();
        http.Routes["/api/chat"] = (HttpStatusCode.OK,
            """
            {"message":{"role":"assistant","content":"Dynamic "},"done":false}
            {"message":{"role":"assistant","content":"Host Configuration"},"done":false}
            {"message":{"role":"assistant","content":""},"done":true}
            """);
        var seen = new List<string>();

        var answer = await new OllamaEngine(Test.Ollama, new HttpClient(http)).AskAsync(Request, seen.Add, CancellationToken.None);

        Assert.Equal("Dynamic Host Configuration", answer);
        Assert.Equal(["Dynamic ", "Dynamic Host Configuration"], seen);
        using var body = JsonDocument.Parse(http.Requests.Single().Body!);
        Assert.Equal("llama3.2:latest", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(4, body.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Ollama_errors_are_shown()
    {
        var http = new FakeHttp();
        http.Routes["/api/chat"] = (HttpStatusCode.NotFound, """{"error":"model \"mistral\" not found, try pulling it first"}""");

        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new OllamaEngine(Test.Ollama, new HttpClient(http)).AskAsync(Request with { Model = "mistral" }, _ => { }, CancellationToken.None));

        Assert.Equal("Ollama: model \"mistral\" not found, try pulling it first", ex.Message);
    }

    [Fact]
    public async Task A_stopped_server_is_named()
    {
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new OllamaEngine(Test.Ollama, new HttpClient(new FakeHttp())).AskAsync(Request, _ => { }, CancellationToken.None));

        Assert.Equal("Ollama läuft nicht mehr unter http://127.0.0.1:11434", ex.Message);
    }

    [Fact]
    public async Task Lm_studio_streams_server_sent_events()
    {
        var info = new EngineInfo(EngineKind.LmStudio, "http://127.0.0.1:1234", ["qwen2.5-7b-instruct"]);
        var http = new FakeHttp();
        http.Routes["/v1/chat/completions"] = (HttpStatusCode.OK,
            """
            data: {"choices":[{"delta":{"role":"assistant"}}]}

            data: {"choices":[{"delta":{"content":"Verteilt "}}]}

            data: {"choices":[{"delta":{"content":"IP-Adressen."}}]}

            data: [DONE]

            """);

        var answer = await new OpenAiEngine(info, new HttpClient(http)).AskAsync(Request, _ => { }, CancellationToken.None);

        Assert.Equal("Verteilt IP-Adressen.", answer);
    }
}
