using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.Core.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+T", "Ctrl+Shift+T")]
    [InlineData("ctrl+shift+t", "Ctrl+Shift+T")]
    [InlineData(" Control + Shift + t ", "Ctrl+Shift+T")]
    [InlineData("Shift+Win+Alt+Ctrl+K", "Ctrl+Alt+Shift+Win+K")]
    [InlineData("windows+1", "Win+1")]
    [InlineData("Ctrl+Alt+F9", "Ctrl+Alt+F9")]
    [InlineData("Ctrl+Alt+Shift+F21", "Ctrl+Alt+Shift+F21")]
    [InlineData("f9", "F9")]
    [InlineData("F24", "F24")]
    [InlineData("Alt+0", "Alt+0")]
    public void Zapis_z_ustawien_ma_jeden_zapis_kanoniczny(string text, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));

        Assert.Equal(expected, gesture.ToString());
        Assert.Equal(expected, HotkeyGesture.Normalize(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("T")]
    [InlineData("7")]
    [InlineData("Esc")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+Space")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F+1")]
    [InlineData("Hyper+T")]
    [InlineData("Ctrl+TT")]
    [InlineData("Ctrl+Ą")]
    [InlineData("Ctrl+F٣")]
    public void Niepoprawny_zapis_nie_jest_skrotem(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
        Assert.Null(HotkeyGesture.Normalize(text));
    }

    [Fact]
    public void Ten_sam_skrot_w_innym_zapisie_jest_rowny()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+T", out var canonical));
        Assert.True(HotkeyGesture.TryParse("shift+control+T", out var other));

        Assert.Equal(canonical, other);
    }

    [Theory]
    [InlineData("F1", 0x70u)]
    [InlineData("F12", 0x7Bu)]
    [InlineData("F24", 0x87u)]
    [InlineData("Ctrl+A", 0x41u)]
    [InlineData("Ctrl+Z", 0x5Au)]
    [InlineData("Ctrl+0", 0x30u)]
    [InlineData("Ctrl+9", 0x39u)]
    public void Klawisz_ma_kod_wirtualny_jak_w_RegisterHotKey(string text, uint expectedVirtualKey)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));

        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
    }

    [Fact]
    public void Modyfikatory_maja_wartosci_flag_MOD_z_RegisterHotKey()
    {
        Assert.Equal(0x1, (int)HotkeyModifiers.Alt);
        Assert.Equal(0x2, (int)HotkeyModifiers.Control);
        Assert.Equal(0x4, (int)HotkeyModifiers.Shift);
        Assert.Equal(0x8, (int)HotkeyModifiers.Win);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0x4Bu, true)]
    [InlineData(HotkeyModifiers.None, 0x4Bu, false)]
    [InlineData(HotkeyModifiers.None, 0x31u, false)]
    [InlineData(HotkeyModifiers.None, 0x78u, true)]
    [InlineData(HotkeyModifiers.Control, 0x20u, false)]
    [InlineData(HotkeyModifiers.Control, 0x09u, false)]
    [InlineData(HotkeyModifiers.Control, 0x60u, false)]
    [InlineData(HotkeyModifiers.Alt, 0x1Bu, false)]
    public void Skrot_z_klawiszy_wymaga_modyfikatora_albo_klawisza_funkcyjnego(HotkeyModifiers modifiers, uint virtualKey, bool expected)
    {
        Assert.Equal(expected, HotkeyGesture.TryCreate(modifiers, virtualKey, out _));
    }

    [Fact]
    public void Domyslne_skroty_sa_poprawne_i_rozne()
    {
        string[] defaults = [HotkeyDefaults.Translate, HotkeyDefaults.ToggleOverlay, HotkeyDefaults.LiveToggle];

        var parsed = defaults.Select(static d => HotkeyGesture.TryParse(d, out var g) ? g : (HotkeyGesture?)null).ToList();

        Assert.All(parsed, static g => Assert.NotNull(g));
        Assert.Equal(3, parsed.Distinct().Count());
        Assert.Equal(defaults, defaults.Select(HotkeyGesture.Normalize));
    }
}

public class HotkeyCaptureTests
{
    [Theory]
    [InlineData(HotkeyModifiers.None)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift)]
    public void Esc_anuluje_edycje(HotkeyModifiers modifiers)
    {
        var result = HotkeyCapture.Interpret(modifiers, 0x1B);

        Assert.Equal(HotkeyCaptureState.Cancelled, result.State);
        Assert.Null(result.Gesture);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0xA2u, "Ctrl+…")]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0xA0u, "Ctrl+Shift+…")]
    [InlineData(HotkeyModifiers.Alt, 0x12u, "Alt+…")]
    [InlineData(HotkeyModifiers.Win, 0x5Bu, "Win+…")]
    [InlineData(HotkeyModifiers.None, 0x11u, HotkeyCapture.Prompt)]
    public void Same_modyfikatory_czekaja_na_klawisz(HotkeyModifiers modifiers, uint virtualKey, string display)
    {
        var result = HotkeyCapture.Interpret(modifiers, virtualKey);

        Assert.Equal(HotkeyCaptureState.Waiting, result.State);
        Assert.Equal(display, result.Display);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4Bu, "Ctrl+Alt+K")]
    [InlineData(HotkeyModifiers.None, 0x78u, "F9")]
    [InlineData(HotkeyModifiers.Shift, 0x74u, "Shift+F5")]
    [InlineData(HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x33u, "Shift+Win+3")]
    public void Gotowy_skrot_ma_zapis_kanoniczny(HotkeyModifiers modifiers, uint virtualKey, string expected)
    {
        var result = HotkeyCapture.Interpret(modifiers, virtualKey);

        Assert.Equal(HotkeyCaptureState.Captured, result.State);
        Assert.Equal(expected, result.Gesture?.ToString());
        Assert.Equal(expected, result.Display);
    }

    [Fact]
    public void Gola_litera_jest_odrzucona_z_podpowiedzia_o_modyfikatorze()
    {
        var result = HotkeyCapture.Interpret(HotkeyModifiers.None, 0x54);

        Assert.Equal(HotkeyCaptureState.Rejected, result.State);
        Assert.Equal(HotkeyCapture.MissingModifierMessage, result.Error);
        Assert.Equal(HotkeyCapture.Prompt, result.Display);
    }

    [Theory]
    [InlineData(0x09u)]
    [InlineData(0x0Du)]
    [InlineData(0x20u)]
    [InlineData(0x2Eu)]
    [InlineData(0x60u)]
    [InlineData(0x2Cu)]
    public void Klawisz_spoza_liter_cyfr_i_F_jest_odrzucony(uint virtualKey)
    {
        var result = HotkeyCapture.Interpret(HotkeyModifiers.Control, virtualKey);

        Assert.Equal(HotkeyCaptureState.Rejected, result.State);
        Assert.Equal(HotkeyCapture.UnsupportedKeyMessage, result.Error);
    }
}

public class HotkeyRulesTests
{
    private static HotkeyGesture Parse(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture), text);
        return gesture;
    }

    [Fact]
    public void Skrot_innej_czynnosci_jest_odrzucony_takze_w_innym_zapisie()
    {
        var problem = HotkeyRules.Check(Parse("Ctrl+Shift+L"), [("Przetłumacz region", "Ctrl+Shift+T"), ("Start / stop live", "shift+ctrl+l")]);

        Assert.NotNull(problem);
        Assert.Contains("Start / stop live", problem);
        Assert.Contains("Ctrl+Shift+L", problem);
    }

    [Fact]
    public void Wolny_skrot_przechodzi()
    {
        Assert.Null(HotkeyRules.Check(Parse("Ctrl+Shift+K"), [("Przetłumacz region", "Ctrl+Shift+T"), ("Ukryj / pokaż nakładkę", "Ctrl+Shift+H")]));
    }

    [Fact]
    public void Nieczytelny_zapis_innej_czynnosci_nie_blokuje_skrotu()
    {
        Assert.Null(HotkeyRules.Check(Parse("Ctrl+Shift+K"), [("Przetłumacz region", "zepsuty"), ("Start / stop live", null)]));
    }

    [Theory]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+X")]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+A")]
    [InlineData("Shift+T")]
    [InlineData("Shift+5")]
    [InlineData("Ctrl+Alt+L")]
    [InlineData("Ctrl+Alt+A")]
    [InlineData("Ctrl+Alt+Shift+E")]
    [InlineData("Ctrl+Alt+5")]
    public void Kombinacje_innych_programow_i_wielkie_litery_sa_odrzucone(string text)
    {
        Assert.NotNull(HotkeyRules.Check(Parse(text), []));
    }

    [Theory]
    [InlineData("Ctrl+Shift+C")]
    [InlineData("Ctrl+Alt+F4")]
    [InlineData("Shift+F5")]
    [InlineData("F9")]
    [InlineData("Win+Shift+K")]
    [InlineData("Ctrl+Alt+Shift+F9")]
    [InlineData("Win+Ctrl+Alt+K")]
    public void Podobne_ale_wolne_kombinacje_przechodza(string text)
    {
        Assert.Null(HotkeyRules.Check(Parse(text), []));
    }

    [Fact]
    public void Domyslne_skroty_przechodza_reguly_edytora()
    {
        (string Action, string? Gesture)[] all =
        [
            ("Przetłumacz region", HotkeyDefaults.Translate),
            ("Ukryj / pokaż nakładkę", HotkeyDefaults.ToggleOverlay),
            ("Start / stop live", HotkeyDefaults.LiveToggle),
        ];

        foreach (var (action, gesture) in all)
        {
            var others = all.Where(o => o.Action != action);
            Assert.Null(HotkeyRules.Check(Parse(gesture!), others));
        }
    }
}
