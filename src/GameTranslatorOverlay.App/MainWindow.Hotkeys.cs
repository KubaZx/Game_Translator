using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.Core.Windows;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.App;

public partial class MainWindow
{
    private MenuItem? _trayTranslateItem;
    private MenuItem? _trayOverlayItem;
    private int _translateHotkeyId;
    private int _overlayHotkeyId;
    private int _liveHotkeyId;

    private enum HotkeyAction { Translate, ToggleOverlay, LiveToggle }

    private static readonly HotkeyAction[] HotkeyActions = [HotkeyAction.Translate, HotkeyAction.ToggleOverlay, HotkeyAction.LiveToggle];

    private HotkeyAction? _hotkeyCapture;
    private string _hotkeyCaptureDisplay = HotkeyCapture.Prompt;
    private string? _hotkeyCaptureError;
    private readonly Dictionary<HotkeyAction, string> _hotkeyChangeErrors = [];

    private void UpdateHotkeyLabels()
    {
        var translate = RegisteredHotkey(HotkeyAction.Translate);
        var overlay = RegisteredHotkey(HotkeyAction.ToggleOverlay);

        foreach (var action in HotkeyActions)
        {
            var capturing = _hotkeyCapture == action;
            HotkeyFieldText(action).Text = capturing ? _hotkeyCaptureDisplay : HotkeyGestureText(action);
            var (note, brushKey) = HotkeyNote(action, capturing);
            var noteBlock = HotkeyNoteBlock(action);
            noteBlock.Text = note;
            noteBlock.Foreground = (System.Windows.Media.Brush)FindResource(brushKey);
        }

        TxtHotkeyResult.Text = translate ?? string.Empty;
        KbdHotkeyResult.Visibility = translate is null ? Visibility.Collapsed : Visibility.Visible;
        TxtTimingHint.Text = translate is null
            ? "Brak — przetłumacz region, żeby zobaczyć rozkład czasu."
            : $"Brak — przetłumacz region ({translate}), żeby zobaczyć rozkład czasu.";
        Visuals.SetCaption(BtnTranslateRegion, translate);
        Visuals.SetCaption(BtnToggleOverlay, overlay);
        if (_trayTranslateItem is not null) _trayTranslateItem.Header = TrayHeader("Przetłumacz region", translate);
        if (_trayOverlayItem is not null) _trayOverlayItem.Header = TrayHeader("Ukryj / pokaż nakładkę", overlay);
    }

    private static string TrayHeader(string label, string? hotkey) => hotkey is null ? label : $"{label}  ({hotkey})";

    private (string Text, string BrushKey) HotkeyNote(HotkeyAction action, bool capturing)
    {
        if (capturing)
        {
            return _hotkeyCaptureError is { } error
                ? (error, "Brush.Danger")
                : ("Litera albo cyfra z Ctrl, Alt lub Win albo F1–F24; Esc anuluje. Gdy nic się nie zmienia, skrót zajmuje inny program.", "Brush.TextMuted");
        }
        if (_hotkeyChangeErrors.TryGetValue(action, out var changeError)) return (changeError, "Brush.Danger");
        if (IsHotkeyRegistered(action)) return (string.Empty, "Brush.TextMuted");
        return HotkeyGesture.TryParse(HotkeyGestureText(action), out _)
            ? ("Nie działa — zajęty przez inny program. Kliknij i wciśnij inny skrót.", "Brush.Warning")
            : ("Nie działa — nie rozumiem zapisu w ustawieniach. Kliknij i wciśnij nowy skrót.", "Brush.Warning");
    }

    private string? RegisteredHotkey(HotkeyAction action) => IsHotkeyRegistered(action) ? HotkeyGestureText(action) : null;

    private string HotkeyGestureText(HotkeyAction action) => action switch
    {
        HotkeyAction.Translate => _settings.TranslateHotkey,
        HotkeyAction.ToggleOverlay => _settings.ToggleOverlayHotkey,
        _ => _settings.LiveToggleHotkey,
    };

    private void SetHotkeyGestureText(HotkeyAction action, string gesture)
    {
        switch (action)
        {
            case HotkeyAction.Translate: _settings.TranslateHotkey = gesture; break;
            case HotkeyAction.ToggleOverlay: _settings.ToggleOverlayHotkey = gesture; break;
            default: _settings.LiveToggleHotkey = gesture; break;
        }
    }

    private bool IsHotkeyRegistered(HotkeyAction action) => action switch
    {
        HotkeyAction.Translate => _translateHotkeyRegistered,
        HotkeyAction.ToggleOverlay => _overlayHotkeyRegistered,
        _ => _liveHotkeyRegistered,
    };

    private int HotkeyId(HotkeyAction action) => action switch
    {
        HotkeyAction.Translate => _translateHotkeyId,
        HotkeyAction.ToggleOverlay => _overlayHotkeyId,
        _ => _liveHotkeyId,
    };

    private void SetHotkeyRegistration(HotkeyAction action, bool registered, int id)
    {
        switch (action)
        {
            case HotkeyAction.Translate: _translateHotkeyRegistered = registered; _translateHotkeyId = id; break;
            case HotkeyAction.ToggleOverlay: _overlayHotkeyRegistered = registered; _overlayHotkeyId = id; break;
            default: _liveHotkeyRegistered = registered; _liveHotkeyId = id; break;
        }
    }

    private static string HotkeyActionLabel(HotkeyAction action) => action switch
    {
        HotkeyAction.Translate => "Przetłumacz region",
        HotkeyAction.ToggleOverlay => "Ukryj / pokaż nakładkę",
        _ => "Start / stop live",
    };

    private static string DefaultHotkey(HotkeyAction action) => action switch
    {
        HotkeyAction.Translate => HotkeyDefaults.Translate,
        HotkeyAction.ToggleOverlay => HotkeyDefaults.ToggleOverlay,
        _ => HotkeyDefaults.LiveToggle,
    };

    private ToggleButton HotkeyField(HotkeyAction action) => action switch
    {
        HotkeyAction.Translate => BtnHotkeyTranslate,
        HotkeyAction.ToggleOverlay => BtnHotkeyOverlay,
        _ => BtnHotkeyLive,
    };

    private TextBlock HotkeyFieldText(HotkeyAction action) => action switch
    {
        HotkeyAction.Translate => TxtHotkeyTranslate,
        HotkeyAction.ToggleOverlay => TxtHotkeyOverlay,
        _ => TxtHotkeyLive,
    };

    private TextBlock HotkeyNoteBlock(HotkeyAction action) => action switch
    {
        HotkeyAction.Translate => TxtHotkeyTranslateNote,
        HotkeyAction.ToggleOverlay => TxtHotkeyOverlayNote,
        _ => TxtHotkeyLiveNote,
    };

    private HotkeyAction? HotkeyActionOf(object sender) =>
        HotkeyActions.Cast<HotkeyAction?>().FirstOrDefault(a => ReferenceEquals(HotkeyField(a!.Value), sender));

    private bool RegisterHotkey(HotkeyAction action, string gesture, out string error)
    {
        var registered = _hotkeys.TryRegister(gesture, () => OnGlobalHotkey(action), out var id, out error);
        SetHotkeyRegistration(action, registered, registered ? id : 0);
        return registered;
    }

    private void UnregisterHotkey(HotkeyAction action)
    {
        if (IsHotkeyRegistered(action)) _hotkeys.Unregister(HotkeyId(action));
        SetHotkeyRegistration(action, false, 0);
    }

    private void OnGlobalHotkey(HotkeyAction action)
    {
        if (_hotkeyCapture is { } capturing && HotkeyGesture.TryParse(HotkeyGestureText(action), out var pressed))
        {
            CompleteHotkeyCapture(capturing, pressed);
            return;
        }

        switch (action)
        {
            case HotkeyAction.Translate: _ = TranslateRegionInteractiveAsync(); break;
            case HotkeyAction.ToggleOverlay: _overlay.ToggleVisibility(); break;
            default: _ = ToggleLiveFromHotkeyAsync(); break;
        }
    }

    private void OnHotkeyFieldChecked(object sender, RoutedEventArgs e)
    {
        if (HotkeyActionOf(sender) is not { } action) return;
        if (_hotkeyCapture is { } previous && previous != action)
        {
            _hotkeyCapture = null;
            HotkeyField(previous).IsChecked = false;
        }

        _hotkeyCapture = action;
        _hotkeyCaptureDisplay = HotkeyCapture.Prompt;
        _hotkeyCaptureError = null;
        _hotkeyChangeErrors.Remove(action);
        HotkeyField(action).Focus();
        UpdateHotkeyLabels();
    }

    private void OnHotkeyFieldUnchecked(object sender, RoutedEventArgs e)
    {
        if (HotkeyActionOf(sender) is { } action && _hotkeyCapture == action) EndHotkeyCapture();
    }

    private void OnHotkeyFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (HotkeyActionOf(sender) is { } action && _hotkeyCapture == action) EndHotkeyCapture();
    }

    private void OnHotkeyFieldPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_hotkeyCapture is not { } action || HotkeyActionOf(sender) != action) return;
        e.Handled = true;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        var result = HotkeyCapture.Interpret(CurrentHotkeyModifiers(), (uint)KeyInterop.VirtualKeyFromKey(key));
        switch (result.State)
        {
            case HotkeyCaptureState.Cancelled:
                EndHotkeyCapture();
                SetStatus($"Skrót „{HotkeyActionLabel(action)}” bez zmian.");
                break;
            case HotkeyCaptureState.Captured when result.Gesture is { } gesture:
                CompleteHotkeyCapture(action, gesture);
                break;
            default:
                _hotkeyCaptureDisplay = result.Display;
                if (result.Error is not null) _hotkeyCaptureError = result.Error;
                UpdateHotkeyLabels();
                break;
        }
    }

    private void OnHotkeyFieldPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_hotkeyCapture is not { } action || HotkeyActionOf(sender) != action) return;
        e.Handled = true;
        var held = HotkeyGesture.FormatModifiers(CurrentHotkeyModifiers());
        _hotkeyCaptureDisplay = held.Length == 0 ? HotkeyCapture.Prompt : held + "+…";
        UpdateHotkeyLabels();
    }

    private static HotkeyModifiers CurrentHotkeyModifiers()
    {
        var keys = Keyboard.Modifiers;
        var modifiers = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (keys.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (keys.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    private void EndHotkeyCapture()
    {
        if (_hotkeyCapture is not { } action) return;
        _hotkeyCapture = null;
        _hotkeyCaptureError = null;
        HotkeyField(action).IsChecked = false;
        UpdateHotkeyLabels();
    }

    private void CompleteHotkeyCapture(HotkeyAction action, HotkeyGesture gesture)
    {
        var label = HotkeyActionLabel(action);
        if (IsHotkeyRegistered(action) && HotkeyGesture.TryParse(HotkeyGestureText(action), out var current) && current == gesture)
        {
            EndHotkeyCapture();
            SetStatus($"Skrót „{label}” bez zmian: {gesture}.");
            return;
        }

        var others = HotkeyActions
            .Where(other => other != action)
            .Select(other => (HotkeyActionLabel(other), (string?)HotkeyGestureText(other)));
        if (HotkeyRules.Check(gesture, others) is { } problem)
        {
            _hotkeyCaptureDisplay = gesture.ToString();
            _hotkeyCaptureError = problem;
            UpdateHotkeyLabels();
            return;
        }

        EndHotkeyCapture();
        var previous = HotkeyGestureText(action);
        var wasRegistered = IsHotkeyRegistered(action);
        UnregisterHotkey(action);
        if (RegisterHotkey(action, gesture.ToString(), out _))
        {
            var saved = SaveHotkeySettings(() => SetHotkeyGestureText(action, gesture.ToString()));
            SetStatus(saved
                ? $"Skrót „{label}”: {gesture} — działa od razu."
                : $"⚠ Skrót „{label}”: {gesture} działa, ale nie udało się go zapisać — po restarcie wróci {previous}.");
        }
        else
        {
            if (wasRegistered) RegisterHotkey(action, previous, out _);
            var message = TakenMessage(gesture.ToString(), IsHotkeyRegistered(action) ? previous : null);
            _hotkeyChangeErrors[action] = message;
            SetStatus(message);
        }
        UpdateHotkeyLabels();
        UpdateLiveControls();
    }

    private static string TakenMessage(string wanted, string? kept) => kept is null
        ? $"{wanted} jest zajęty przez inny program — wybierz inny skrót."
        : $"{wanted} jest zajęty przez inny program — zostaje {kept}.";

    private void OnResetHotkeysClick(object sender, RoutedEventArgs e)
    {
        EndHotkeyCapture();
        var changing = HotkeyActions
            .Where(action => !(IsHotkeyRegistered(action)
                               && HotkeyGesture.Normalize(HotkeyGestureText(action)) == HotkeyGesture.Normalize(DefaultHotkey(action))))
            .ToList();
        if (changing.Count == 0)
        {
            SetStatus("Skróty są już domyślne.");
            return;
        }

        var previous = changing.ToDictionary(action => action, action => (Text: HotkeyGestureText(action), Registered: IsHotkeyRegistered(action)));
        foreach (var action in changing) UnregisterHotkey(action);

        var failed = new List<HotkeyAction>();
        foreach (var action in changing)
        {
            _hotkeyChangeErrors.Remove(action);
            if (!RegisterHotkey(action, DefaultHotkey(action), out _)) failed.Add(action);
        }

        if (failed.Count > 0)
        {
            foreach (var action in changing) UnregisterHotkey(action);
            foreach (var action in changing)
            {
                var (text, registered) = previous[action];
                if (registered) RegisterHotkey(action, text, out _);
            }
            foreach (var action in failed)
            {
                _hotkeyChangeErrors[action] = $"{DefaultHotkey(action)} jest zajęty przez inny program — skróty zostają bez zmian.";
            }
            UpdateHotkeyLabels();
            UpdateLiveControls();
            SetStatus($"Nie przywrócono domyślnych skrótów: {string.Join(", ", failed.Select(DefaultHotkey))} zajmuje inny program — skróty zostają bez zmian.");
            return;
        }

        var saved = SaveHotkeySettings(() =>
        {
            foreach (var action in changing) SetHotkeyGestureText(action, DefaultHotkey(action));
        });
        UpdateHotkeyLabels();
        UpdateLiveControls();

        var summary = $"Przywrócono domyślne skróty: {HotkeyDefaults.Translate}, {HotkeyDefaults.LiveToggle}, {HotkeyDefaults.ToggleOverlay}.";
        SetStatus(saved ? summary : "⚠ " + summary + " Nie udało się ich zapisać — po restarcie wrócą poprzednie.");
    }

    private bool SaveHotkeySettings(Action change)
    {
        var wasApplied = SettingsSnapshot() == _appliedSettingsJson;
        change();
        try
        {
            _settingsStore.Save(_settings);
            if (wasApplied) _appliedSettingsJson = SettingsSnapshot();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Nie udało się zapisać skrótów w ustawieniach");
            return false;
        }
    }
}
