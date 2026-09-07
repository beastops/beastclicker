using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace BeastClicker;

/// <summary>
/// Registers system-wide hotkeys so start/stop works while a game holds focus.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    [Flags]
    private enum Mod : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var act))
        {
            act();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _nextId = 1;
    }

    /// <summary>Returns true if Windows accepted the binding.</summary>
    public bool Register(string spec, Action action)
    {
        if (!TryParse(spec, out var mods, out var vk)) return false;
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, (uint)(mods | Mod.NoRepeat), vk)) return false;
        _actions[id] = action;
        return true;
    }

    private static bool TryParse(string spec, out Mod mods, out uint vk)
    {
        mods = Mod.None;
        vk = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Mod.Control; break;
                case "alt": mods |= Mod.Alt; break;
                case "shift": mods |= Mod.Shift; break;
                case "win" or "super" or "meta": mods |= Mod.Win; break;
                default: return false;
            }
        }

        if (!TryParseKey(parts[^1], out var key)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    private static bool TryParseKey(string name, out Key key)
    {
        if (Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None) return true;

        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (char.IsDigit(c)) { key = Key.D0 + (c - '0'); return true; }
            if (c is >= 'A' and <= 'Z') { key = Key.A + (c - 'A'); return true; }
        }
        if (name.Equals("Space", StringComparison.OrdinalIgnoreCase)) { key = Key.Space; return true; }

        key = Key.None;
        return false;
    }

    /// <summary>Renders a WPF key press into the string form we persist.</summary>
    public static string? Describe(Key key, ModifierKeys modifiers)
    {
        // Alt arrives as Key.System with the real key in SystemKey; callers resolve
        // that before getting here, but guard anyway.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
            return null;

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        string name = key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => $"NumPad{key - Key.NumPad0}",
            Key.Space => "Space",
            _ => key.ToString(),
        };

        parts.Add(name);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(Hook);
    }
}
