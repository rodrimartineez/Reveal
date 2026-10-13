using System.Text;
using System.Windows.Input;
using Reveal.Platform.Native;

namespace Reveal.Platform.Hotkeys;

/// <summary>A global shortcut such as Ctrl+Shift+T. Parses from / formats to its text form.</summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public bool IsValid => VirtualKey != 0 && Modifiers != 0;

    public bool HasCtrl => (Modifiers & NativeMethods.MOD_CONTROL) != 0;

    public bool HasAlt => (Modifiers & NativeMethods.MOD_ALT) != 0;

    public bool HasShift => (Modifiers & NativeMethods.MOD_SHIFT) != 0;

    public bool HasWin => (Modifiers & NativeMethods.MOD_WIN) != 0;

    public static HotkeyGesture FromKey(Key key, ModifierKeys modifiers)
    {
        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= NativeMethods.MOD_WIN;
        return new HotkeyGesture(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint mods = 0;
        uint vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= NativeMethods.MOD_CONTROL; continue;
                case "alt": mods |= NativeMethods.MOD_ALT; continue;
                case "shift": mods |= NativeMethods.MOD_SHIFT; continue;
                case "win" or "windows": mods |= NativeMethods.MOD_WIN; continue;
            }

            if (vk != 0) return false;
            if (!TryParseKey(raw, out var key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }

        gesture = new HotkeyGesture(mods, vk);
        return gesture.IsValid;
    }

    private static bool TryParseKey(string name, out Key key)
    {
        if (name.Length == 1 && char.IsDigit(name[0]))
        {
            key = Key.D0 + (name[0] - '0');
            return true;
        }

        return Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (HasCtrl) sb.Append("Ctrl+");
        if (HasAlt) sb.Append("Alt+");
        if (HasShift) sb.Append("Shift+");
        if (HasWin) sb.Append("Win+");
        var key = KeyInterop.KeyFromVirtualKey((int)VirtualKey);
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1].ToString();
        sb.Append(name);
        return sb.ToString();
    }
}
