using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Brim.Core;
using static Brim.Ai.Windows.Native;

namespace Brim.Ai.Windows;

/// <summary>
/// Der Windows-Teil: ein eigener Thread mit Nachrichtenschleife für Tastenkürzel, Eingabefeld und Zwischenablage,
/// dazu Apps starten und Text in ihr Fenster einfügen. Alles Win32, damit das Plugin ohne Windows Desktop Runtime läuft.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopShell : IDesktop, IDisposable
{
    private const int HotkeyId = 1;
    private const string ClassName = "BrimAiPrompt";

    /// <summary>Größe des Eingabefelds in DIP und sein Abstand vom oberen Bildschirmrand (unter der Notch).</summary>
    private const int PromptWidth = 640, PromptHeight = 56, PromptTop = 64;

    /// <summary>So lange wartet die Weitergabe darauf, dass das Fenster der App vorn ist.</summary>
    private static readonly TimeSpan HandOffWait = TimeSpan.FromSeconds(10);

    private static DesktopShell? s_shell;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private nint _window;
    private nint _prompt;
    private nint _edit;
    private nint _font;
    private nint _brush;
    private Action<string>? _submit;

    public DesktopShell()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Brim KI Fenster" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Das Tastenkürzel wurde gedrückt.</summary>
    public event Action? HotkeyPressed;

    public Task StartAsync()
    {
        _thread.Start();
        return _ready.Task;
    }

    // ───────── Thread und Fensterprozedur ─────────

    private unsafe void Loop()
    {
        s_shell = this;
        var instance = GetModuleHandleW(null);
        _brush = CreateSolidBrush(ColorRef(BrimColors.Graphite));
        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hbrBackground = _brush,
                lpszClassName = name,
            };
            RegisterClassExW(&wc);
            _window = CreateWindowExW(0, name, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, instance, 0);
        }
        _ready.TrySetResult();

        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_KEYDOWN && msg.hwnd == _edit && _edit != 0 && HandleKey((byte)msg.wParam))
            {
                continue;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    [UnmanagedCallersOnly]
    private unsafe static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var shell = s_shell;
        if (shell is not null)
        {
            switch (msg)
            {
                case WM_APP:
                    while (shell._queue.TryDequeue(out var action))
                    {
                        Run(action);
                    }
                    return 0;
                case WM_HOTKEY when wParam == HotkeyId:
                    Run(() => shell.HotkeyPressed?.Invoke());
                    return 0;
                case WM_CTLCOLOREDIT when hwnd == shell._prompt:
                    SetTextColor(wParam, ColorRef(BrimColors.Fog));
                    SetBkColor(wParam, ColorRef(BrimColors.Graphite));
                    return shell._brush;
                case WM_ACTIVATE when hwnd == shell._prompt && (wParam & 0xFFFF) == 0:
                    // Woanders hingeklickt: Eingabe verwerfen wie mit Esc.
                    PostMessageW(hwnd, WM_CLOSE, 0, 0);
                    return 0;
                case WM_CLOSE when hwnd == shell._prompt:
                    shell.ClosePrompt();
                    return 0;
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fehler im Fenster-Thread: {ex}");
        }
    }

    private void Post(Action action)
    {
        _queue.Enqueue(action);
        PostMessageW(_window, WM_APP, 0, 0);
    }

    private Task<T> Invoke<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                done.TrySetResult(func());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    // ───────── Tastenkürzel ─────────

    public Task<bool> SetHotkeyAsync(Hotkey? hotkey) => Invoke(() =>
    {
        UnregisterHotKey(_window, HotkeyId);
        return hotkey is null || RegisterHotKey(_window, HotkeyId, hotkey.Modifiers | MOD_NOREPEAT, hotkey.Key);
    });

    // ───────── Eingabefeld ─────────

    public unsafe void ShowPrompt(string placeholder, Action<string> submit)
    {
        Post(() =>
        {
            _submit = submit;
            if (_prompt != 0)
            {
                SetCue(placeholder);
                Activate(_prompt, _edit);
                return;
            }
            // Auf dem Hauptbildschirm, wo die Notch sitzt, mittig unter ihr.
            var monitor = MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            GetMonitorInfoW(monitor, &info);
            var area = info.rcMonitor;

            fixed (char* name = ClassName)
            {
                _prompt = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, name, null, WS_POPUP,
                    area.Left, area.Top, 100, 40, 0, 0, GetModuleHandleW(null), 0);
            }
            var scale = GetDpiForWindow(_prompt) / 96.0;
            int S(int dip) => (int)Math.Round(dip * scale);
            var width = S(PromptWidth);
            var height = S(PromptHeight);
            SetWindowPos(_prompt, 0, area.Left + (area.Right - area.Left - width) / 2, area.Top + S(PromptTop), width, height, 0x0010 /* SWP_NOACTIVATE */);

            var corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(_prompt, DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(uint));
            var border = ColorRef(BrimColors.Track);
            DwmSetWindowAttribute(_prompt, DWMWA_BORDER_COLOR, &border, sizeof(uint));

            fixed (char* edit = "EDIT")
            {
                _edit = CreateWindowExW(0, edit, null, WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_AUTOVSCROLL,
                    S(20), S(18), width - S(40), height - S(30), _prompt, 0, GetModuleHandleW(null), 0);
            }
            _font = CreateFontW(-S(15), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, "Segoe UI Variable Text");
            SendMessageW(_edit, WM_SETFONT, _font, 1);
            SetCue(placeholder);

            ShowWindow(_prompt, SW_SHOW);
            Activate(_prompt, _edit);
        });
    }

    private unsafe void SetCue(string placeholder)
    {
        fixed (char* cue = placeholder)
        {
            SendMessageW(_edit, EM_SETCUEBANNER, 1, (nint)cue);
        }
    }

    /// <summary>
    /// Holt das Fenster nach vorn. Windows erlaubt das nur dem Vordergrund-Programm; nach einem Klick in die Notch ist
    /// das Brim, also hängt sich der Thread kurz an dessen Eingabe.
    /// </summary>
    private unsafe static void Activate(nint window, nint focus)
    {
        if (!SetForegroundWindow(window) || GetForegroundWindow() != window)
        {
            var foreground = GetForegroundWindow();
            var other = GetWindowThreadProcessId(foreground, null);
            var me = GetCurrentThreadId();
            var attached = other != 0 && other != me && AttachThreadInput(me, other, true);
            BringWindowToTop(window);
            SetForegroundWindow(window);
            if (attached)
            {
                AttachThreadInput(me, other, false);
            }
        }
        SetFocus(focus);
    }

    /// <summary>Tasten im Eingabefeld: Enter schickt (Umschalt+Enter macht eine neue Zeile), Esc schließt, Strg+A markiert alles.</summary>
    private bool HandleKey(byte key)
    {
        var control = GetKeyState(VK_CONTROL) < 0;
        var shift = GetKeyState(VK_SHIFT) < 0;
        switch (key)
        {
            case VK_RETURN when !shift:
                var text = ReadText(_edit);
                var submit = _submit;
                ClosePrompt();
                if (text.Trim().Length > 0 && submit is not null)
                {
                    Task.Run(() => submit(text));
                }
                return true;
            case VK_ESCAPE:
                ClosePrompt();
                return true;
            case VK_A when control:
                SendMessageW(_edit, EM_SETSEL, 0, -1);
                return true;
        }
        return false;
    }

    private unsafe static string ReadText(nint window)
    {
        var length = GetWindowTextLengthW(window);
        if (length <= 0)
        {
            return "";
        }
        var buffer = new char[length + 1];
        fixed (char* b = buffer)
        {
            var read = GetWindowTextW(window, b, buffer.Length);
            return new string(b, 0, read).Replace("\r\n", "\n");
        }
    }

    private void ClosePrompt()
    {
        if (_prompt == 0)
        {
            return;
        }
        var prompt = _prompt;
        _prompt = 0;
        _edit = 0;
        _submit = null;
        DestroyWindow(prompt);
        if (_font != 0)
        {
            DeleteObject(_font);
            _font = 0;
        }
    }

    // ───────── Zwischenablage ─────────

    public unsafe Task<string?> ReadClipboardAsync() => Invoke(() =>
    {
        if (!OpenClipboardWithRetry())
        {
            return null;
        }
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == 0)
            {
                return null;
            }
            var text = (char*)GlobalLock(handle);
            try
            {
                return text is null ? null : new string(text);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    });

    public unsafe Task<bool> WriteClipboardAsync(string text) => Invoke(() =>
    {
        if (!OpenClipboardWithRetry())
        {
            return false;
        }
        try
        {
            EmptyClipboard();
            var normalized = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var bytes = (nuint)((normalized.Length + 1) * sizeof(char));
            var memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (memory == 0)
            {
                return false;
            }
            var target = (char*)GlobalLock(memory);
            fixed (char* source = normalized)
            {
                Buffer.MemoryCopy(source, target, (long)bytes, normalized.Length * sizeof(char));
            }
            target[normalized.Length] = '\0';
            GlobalUnlock(memory);
            if (SetClipboardData(CF_UNICODETEXT, memory) == 0)
            {
                GlobalFree(memory);
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    });

    /// <summary>Andere Programme halten die Zwischenablage manchmal kurz offen.</summary>
    private bool OpenClipboardWithRetry()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(_window))
            {
                return true;
            }
            Thread.Sleep(25);
        }
        return false;
    }

    // ───────── Apps ─────────

    public void Launch(AiApp app)
    {
        try
        {
            // Über den Ordner „Apps“ startet Windows Store-Apps wie Desktop-Programme; läuft die App schon, kommt sie nach vorn.
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + app.AppId) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"{app.Name} ließ sich nicht starten: {ex.Message}");
        }
    }

    public async Task<bool> HandOffAsync(AiApp app, string text, bool send, CancellationToken cancellationToken)
    {
        if (!await WriteClipboardAsync(text))
        {
            return false;
        }
        Launch(app);
        var deadline = DateTime.UtcNow + HandOffWait;
        while (!IsAppInFront(app))
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }
            await Task.Delay(150, cancellationToken);
        }
        // Dem Fenster einen Moment geben, bis sein Eingabefeld den Fokus hat.
        await Task.Delay(700, cancellationToken);
        if (!IsAppInFront(app))
        {
            return false;
        }
        Press(VK_CONTROL, VK_V);
        if (send)
        {
            await Task.Delay(200, cancellationToken);
            Press(null, VK_RETURN);
        }
        return true;
    }

    private unsafe static bool IsAppInFront(AiApp app)
    {
        var window = GetForegroundWindow();
        if (window == 0)
        {
            return false;
        }
        uint pid;
        GetWindowThreadProcessId(window, &pid);
        if (pid == 0 || pid == Environment.ProcessId)
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (app.ProcessNames.Any(n => string.Equals(n, process.ProcessName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }
        return string.Equals(ReadText(window).Trim(), app.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static void Press(byte? modifier, byte key)
    {
        if (modifier is { } m)
        {
            keybd_event(m, 0, 0, 0);
        }
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KEYEVENTF_KEYUP, 0);
        if (modifier is { } up)
        {
            keybd_event(up, 0, KEYEVENTF_KEYUP, 0);
        }
    }

    public void Dispose()
    {
        if (_window != 0)
        {
            Post(() =>
            {
                ClosePrompt();
                UnregisterHotKey(_window, HotkeyId);
                PostQuitMessage(0);
            });
        }
    }
}
