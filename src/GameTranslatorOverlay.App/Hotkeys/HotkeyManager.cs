using System.Windows;
using System.Windows.Interop;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Core.Windows;

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

    public bool TryRegister(string gesture, Action callback, out string error) =>
        TryRegister(gesture, callback, out _, out error);

    public bool TryRegister(string gesture, Action callback, out int id, out string error)
    {
        id = 0;
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

        var candidate = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, candidate, modifiers | NativeMethods.MOD_NOREPEAT, key))
        {
            error = $"Skrót {gesture} jest już zajęty przez inny program. Wybierz inny w sekcji O programie → Skróty klawiszowe.";
            return false;
        }

        _handlers[candidate] = callback;
        id = candidate;
        return true;
    }

    public void Unregister(int id)
    {
        if (!_handlers.Remove(id) || _source is null) return;
        NativeMethods.UnregisterHotKey(_source.Handle, id);
    }

    public static bool TryParseGesture(string gesture, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (!HotkeyGesture.TryParse(gesture, out var parsed)) return false;

        modifiers = (uint)parsed.Modifiers;
        key = parsed.VirtualKey;
        return true;
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
