using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Ai.Tests;

internal sealed class RecordingSink : INotchSink
{
    public List<ActivityParams> Activities { get; } = [];
    public List<HudParams> Huds { get; } = [];
    public List<(string Id, TileSize Size, Widget Content)> Widgets { get; } = [];
    public List<string> Logs { get; } = [];

    public ActivityParams? Last => Activities.Count > 0 ? Activities[^1] : null;

    public Widget? LastWidget(string id, TileSize size) => Widgets.LastOrDefault(w => w.Id == id && w.Size == size).Content;

    public Task SetActivityAsync(ActivityParams activity)
    {
        lock (Activities)
        {
            Activities.Add(activity);
        }
        return Task.CompletedTask;
    }

    public Task EndActivityAsync(string id) => Task.CompletedTask;

    public Task ShowHudAsync(HudParams hud)
    {
        Huds.Add(hud);
        return Task.CompletedTask;
    }

    public Task LogAsync(string message, string level)
    {
        Logs.Add(message);
        return Task.CompletedTask;
    }

    public Task SetWidgetAsync(string id, TileSize size, Widget content)
    {
        Widgets.Add((id, size, content));
        return Task.CompletedTask;
    }
}

internal sealed class FakeScanner : IToolScanner
{
    public AiTools Tools { get; set; } = AiTools.None;
    public int Scans { get; private set; }

    public Task<AiTools> ScanAsync(PluginSettings settings, CancellationToken cancellationToken)
    {
        Scans++;
        return Task.FromResult(Tools);
    }
}

/// <summary>KI, deren Antwort der Test in der Hand hat: <see cref="Stream"/> schickt Text, <see cref="Finish"/> beendet.</summary>
internal sealed class FakeEngine(EngineInfo info) : IAiEngine
{
    private TaskCompletionSource<string> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<string>? _progress;

    public EngineInfo Info => info;
    public List<ChatRequest> Requests { get; } = [];
    public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> AskAsync(ChatRequest request, Action<string> progress, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        _progress = progress;
        var answer = _answer;
        Started.TrySetResult();
        using (cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken)))
        {
            return await answer.Task;
        }
    }

    public void Stream(string text) => _progress?.Invoke(text);

    public void Finish(string text)
    {
        var answer = _answer;
        _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        answer.TrySetResult(text);
    }

    public void Fail(string message)
    {
        var answer = _answer;
        _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        answer.TrySetException(new AiException(message));
    }
}

internal sealed class FakeDesktop : IDesktop
{
    public List<string> Placeholders { get; } = [];
    public Action<string>? Submit { get; private set; }
    public string? Clipboard { get; set; }
    public List<AiApp> Launched { get; } = [];
    public List<(AiApp App, string Text, bool Send)> HandOffs { get; } = [];
    public bool HandOffWorks { get; set; } = true;
    public bool HotkeyFree { get; set; } = true;
    public List<Hotkey?> Hotkeys { get; } = [];

    public void ShowPrompt(string placeholder, Action<string> submit)
    {
        Placeholders.Add(placeholder);
        Submit = submit;
    }

    public Task<string?> ReadClipboardAsync() => Task.FromResult(Clipboard);

    public Task<bool> WriteClipboardAsync(string text)
    {
        Clipboard = text;
        return Task.FromResult(true);
    }

    public Task<bool> SetHotkeyAsync(Hotkey? hotkey)
    {
        Hotkeys.Add(hotkey);
        return Task.FromResult(HotkeyFree);
    }

    public void Launch(AiApp app) => Launched.Add(app);

    public Task<bool> HandOffAsync(AiApp app, string text, bool send, CancellationToken cancellationToken)
    {
        HandOffs.Add((app, text, send));
        Clipboard = text;
        return Task.FromResult(HandOffWorks);
    }
}

/// <summary>Antwortet je Pfad mit Status und Inhalt; merkt sich die Anfragen samt Inhalt.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];
    public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var url = request.RequestUri!.ToString();
        Requests.Add((request.Method, url, body));
        foreach (var (path, (status, content)) in Routes)
        {
            if (url.EndsWith(path, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(status) { Content = new StringContent(content) };
            }
        }
        throw new HttpRequestException("Verbindung abgelehnt");
    }
}

internal static class Test
{
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.FromHours(1));

    public static readonly EngineInfo Claude = new(EngineKind.ClaudeCode, @"C:\Users\tobias\.local\bin\claude.exe", []);
    public static readonly EngineInfo Ollama = new(EngineKind.Ollama, "http://127.0.0.1:11434", ["llama3.2:latest"]);
    public static readonly AiApp ClaudeApp = new("claude", "Claude", "Claude_pzs8sxrjxfjjc!Claude");
    public static readonly AiApp ChatGpt = new("chatgpt", "ChatGPT", "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT");

    private static readonly JsonSerializerOptions Readable = new(PluginJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Lesbar statt \u00FC usw., damit die Tests nach Text suchen können.</summary>
    public static string Json(object value) => JsonSerializer.Serialize(value, value.GetType(), Readable);

    public static IReadOnlyDictionary<string, JsonElement> Values(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    /// <summary>Wartet, bis <paramref name="condition"/> stimmt (Antworten laufen im Hintergrund weiter).</summary>
    public static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}
