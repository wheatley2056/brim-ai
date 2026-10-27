namespace Brim.Ai;

/// <summary>Tastenkürzel wie „Ctrl+Alt+K“ oder „Strg+Umschalt+Leertaste“, als Werte für <c>RegisterHotKey</c>.</summary>
/// <param name="Modifiers">MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8.</param>
/// <param name="Key">Virtueller Tastencode.</param>
public sealed record Hotkey(uint Modifiers, uint Key, string Text)
{
    public const uint Alt = 1, Control = 2, Shift = 4, Win = 8;

    /// <summary>Liest das Kürzel; <c>null</c> bei leerem Text oder wenn es nicht taugt (ohne Strg, Alt oder Win).</summary>
    public static Hotkey? Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" or "strg" or "control" => Control,
                "alt" => Alt,
                "shift" or "umschalt" or "hoch" => Shift,
                "win" or "windows" => Win,
                _ => 0x100,
            };
        }
        if (modifiers >= 0x100 || (modifiers & (Control | Alt | Win)) == 0 || KeyCode(parts[^1]) is not { } key)
        {
            return null;
        }
        return new Hotkey(modifiers, key, string.Join("+", parts));
    }

    private static uint? KeyCode(string key)
    {
        var k = key.ToUpperInvariant();
        if (k.Length == 1 && (k[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            return k[0];
        }
        if (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }
        return k switch
        {
            "SPACE" or "LEERTASTE" or "LEER" => 0x20,
            "ENTER" or "EINGABE" => 0x0D,
            "." => 0xBE,
            "," => 0xBC,
            "-" => 0xBD,
            "#" => 0xBF,
            _ => null,
        };
    }
}
