using System.Diagnostics;
using System.Text;

namespace Brim.Ai.Windows;

/// <summary>Die Apps im Startmenü, wie <c>Get-StartApps</c> sie liefert: Desktop-Programme und Store-Apps mit Start-Kennung.</summary>
internal static class StartApps
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task<IReadOnlyList<StartApp>> ReadAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[]
        {
            "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-StartApps | Select-Object Name, AppID | ConvertTo-Json -Compress",
        })
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell ließ sich nicht starten");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return ToolScanner.ParseStartApps(output);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
