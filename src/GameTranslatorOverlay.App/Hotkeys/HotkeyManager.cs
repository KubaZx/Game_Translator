using System.Windows;
using System.Windows.Interop;
using GameTranslatorOverlay.App.Interop;

namespace GameTranslatorOverlay.App.Hotkeys;

/// <summary>
/// Globalne skróty klawiszowe (RegisterHotKey). Skróty sterują wyłącznie
/// aplikacją tłumacza — nigdy nie wysyłają niczego do gry.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, Action> _handlers = [];
    private HwndSource? _source;
    private int _nextId = 0xB00;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    public bool TryRegister(string gesture, Action callback, out string error)
    {
        error = string.Empty;

        if (_source is null)
        {
            error = "Menedżer skrótów nie jest podpięty do okna.";
            return false;
        }
        if (!TryParseGesture(gesture, out var modifiers, out var key))
        {
            error = $"Nie rozumiem skrótu „{gesture}”. Przykład poprawnego zapisu: Ctrl+Shift+T.";
            return false;
        }

        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, key))
        {
            error = $"Skrót {gesture} jest już zajęty przez inny program. Wybierz inny w ustawieniach.";
            return false;
        }

        _handlers[id] = callback;
        return true;
    }

    public static bool TryParseGesture(string gesture, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        var tokens = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return false;

        foreach (var token in tokens[..^1])
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "win" or "windows": modifiers |= NativeMethods.MOD_WIN; break;
                default: return false;
            }
        }

        var keyToken = tokens[^1].ToUpperInvariant();
        if (keyToken.Length == 1 && (char.IsAsciiLetter(keyToken[0]) || char.IsAsciiDigit(keyToken[0])))
        {
            key = keyToken[0];
            return true;
        }
        if (keyToken.Length is 2 or 3 && keyToken[0] == 'F'
            && int.TryParse(keyToken[1..], out var functionKey) && functionKey is >= 1 and <= 24)
        {
            key = (uint)(0x6F + functionKey);
            return true;
        }

        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            foreach (var id in _handlers.Keys)
            {
                NativeMethods.UnregisterHotKey(_source.Handle, id);
            }
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _handlers.Clear();
    }
}
