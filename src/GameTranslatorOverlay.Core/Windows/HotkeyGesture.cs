using System.Globalization;

namespace GameTranslatorOverlay.Core.Windows;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private const uint FirstFunctionKey = 0x70;
    private const uint LastFunctionKey = 0x87;
    private const HotkeyModifiers AllModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win;

    public bool IsFunctionKey => IsFunction(VirtualKey);

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return false;

        var modifiers = HotkeyModifiers.None;
        foreach (var token in tokens[..^1])
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Win; break;
                default: return false;
            }
        }

        var keyToken = tokens[^1].ToUpperInvariant();
        uint key;
        if (keyToken.Length == 1 && (char.IsAsciiLetter(keyToken[0]) || char.IsAsciiDigit(keyToken[0])))
        {
            key = keyToken[0];
        }
        else if (keyToken.Length is 2 or 3 && keyToken[0] == 'F'
                 && int.TryParse(keyToken[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey)
                 && functionKey is >= 1 and <= 24)
        {
            key = (uint)(FirstFunctionKey - 1 + functionKey);
        }
        else
        {
            return false;
        }

        return TryCreate(modifiers, key, out gesture);
    }

    public static bool TryCreate(HotkeyModifiers modifiers, uint virtualKey, out HotkeyGesture gesture)
    {
        gesture = default;
        modifiers &= AllModifiers;
        if (IsLetterOrDigit(virtualKey))
        {
            if (modifiers == HotkeyModifiers.None) return false;
        }
        else if (!IsFunction(virtualKey))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return true;
    }

    public static string? Normalize(string? text) => TryParse(text, out var gesture) ? gesture.ToString() : null;

    public static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        return string.Join('+', parts);
    }

    public override string ToString()
    {
        var modifiers = FormatModifiers(Modifiers);
        var key = IsFunction(VirtualKey)
            ? "F" + (VirtualKey - FirstFunctionKey + 1).ToString(CultureInfo.InvariantCulture)
            : ((char)VirtualKey).ToString();
        return modifiers.Length == 0 ? key : modifiers + "+" + key;
    }

    internal static bool IsLetterOrDigit(uint virtualKey) => virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsFunction(uint virtualKey) => virtualKey is >= FirstFunctionKey and <= LastFunctionKey;
}

public static class HotkeyDefaults
{
    public const string Translate = "Ctrl+Shift+T";
    public const string ToggleOverlay = "Ctrl+Shift+H";
    public const string LiveToggle = "Ctrl+Shift+L";
}

public enum HotkeyCaptureState
{
    Waiting,
    Cancelled,
    Rejected,
    Captured,
}

public sealed record HotkeyCaptureResult(HotkeyCaptureState State, string Display, HotkeyGesture? Gesture = null, string? Error = null);

public static class HotkeyCapture
{
    public const string Prompt = "Wciśnij skrót…";

    public const string UnsupportedKeyMessage = "Ten klawisz nie nadaje się na skrót — użyj litery, cyfry albo F1–F24.";

    public const string MissingModifierMessage = "Do litery i cyfry dodaj Ctrl, Alt albo Win (bez Ctrl+Alt — to AltGr) — sam klawisz połykałby pisanie, także w grze.";

    private const uint Escape = 0x1B;

    private static readonly HashSet<uint> ModifierKeys =
    [
        0x10, 0x11, 0x12,
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,
        0x5B, 0x5C,
    ];

    public static HotkeyCaptureResult Interpret(HotkeyModifiers modifiers, uint virtualKey)
    {
        if (virtualKey == Escape) return new HotkeyCaptureResult(HotkeyCaptureState.Cancelled, string.Empty);

        if (ModifierKeys.Contains(virtualKey))
        {
            var held = HotkeyGesture.FormatModifiers(modifiers);
            return new HotkeyCaptureResult(HotkeyCaptureState.Waiting, held.Length == 0 ? Prompt : held + "+…");
        }

        if (HotkeyGesture.TryCreate(modifiers, virtualKey, out var gesture))
        {
            return new HotkeyCaptureResult(HotkeyCaptureState.Captured, gesture.ToString(), gesture);
        }

        var error = HotkeyGesture.IsLetterOrDigit(virtualKey) ? MissingModifierMessage : UnsupportedKeyMessage;
        return new HotkeyCaptureResult(HotkeyCaptureState.Rejected, Prompt, Error: error);
    }
}

public static class HotkeyRules
{
    private static readonly (HotkeyGesture Gesture, string Reason)[] Reserved =
    [
        (new HotkeyGesture(HotkeyModifiers.Alt, 0x73), "Alt+F4 zamyka okna w każdym programie — wybierz inny skrót."),
        (new HotkeyGesture(HotkeyModifiers.Control, 'C'), EditingReason("Ctrl+C")),
        (new HotkeyGesture(HotkeyModifiers.Control, 'V'), EditingReason("Ctrl+V")),
        (new HotkeyGesture(HotkeyModifiers.Control, 'X'), EditingReason("Ctrl+X")),
        (new HotkeyGesture(HotkeyModifiers.Control, 'Z'), EditingReason("Ctrl+Z")),
        (new HotkeyGesture(HotkeyModifiers.Control, 'A'), EditingReason("Ctrl+A")),
    ];

    public static string? Check(HotkeyGesture candidate, IEnumerable<(string Action, string? Gesture)> others)
    {
        ArgumentNullException.ThrowIfNull(others);

        foreach (var (gesture, reason) in Reserved)
        {
            if (gesture == candidate) return reason;
        }

        if (candidate.Modifiers == HotkeyModifiers.Shift && !candidate.IsFunctionKey)
        {
            return "Sam Shift z literą albo cyfrą blokowałby wielkie litery, np. na czacie gry — dodaj Ctrl albo Alt.";
        }

        if (candidate.Modifiers.HasFlag(HotkeyModifiers.Control | HotkeyModifiers.Alt)
            && !candidate.Modifiers.HasFlag(HotkeyModifiers.Win) && !candidate.IsFunctionKey)
        {
            return "Ctrl+Alt to w Windows AltGr — z literą albo cyfrą blokowałby polskie znaki (ą, ę, ł…). Wybierz Ctrl+Shift albo klawisz F.";
        }

        foreach (var (action, text) in others)
        {
            if (HotkeyGesture.TryParse(text, out var other) && other == candidate)
            {
                return $"{candidate} ma już „{action}” — wybierz inny skrót.";
            }
        }

        return null;
    }

    private static string EditingReason(string gesture) =>
        $"{gesture} to skrót edycji w każdym programie — przejęty globalnie przestałby działać. Wybierz inny.";
}
