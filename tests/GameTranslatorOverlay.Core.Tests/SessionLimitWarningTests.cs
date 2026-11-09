using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class SessionLimitWarningTests
{
    private static void Spend(UsageTracker usage, int characters)
    {
        using var reservation = usage.TryReserveApiCharacters(characters);
        Assert.NotNull(reservation);
        reservation.Complete();
    }

    [Fact]
    public void Prog_80_procent_jest_zgloszony_dokladnie_raz()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 1000 };

        Spend(usage, 700);
        Assert.False(usage.TryTakeLimitWarning());
        Assert.Equal(SessionLimitLevel.None, usage.LimitStatus.Level);

        Spend(usage, 100);
        Assert.Equal(SessionLimitLevel.Near, usage.LimitStatus.Level);
        Assert.True(usage.TryTakeLimitWarning());
        Assert.False(usage.TryTakeLimitWarning());

        Spend(usage, 50);
        Assert.False(usage.TryTakeLimitWarning());
    }

    [Fact]
    public void Pusty_limit_nigdy_nie_ostrzega()
    {
        var usage = new UsageTracker { SessionCharacterLimit = null };

        Spend(usage, 5_000_000);

        Assert.False(usage.TryTakeLimitWarning());
        Assert.Equal(SessionLimitLevel.None, usage.LimitStatus.Level);
        Assert.Null(ProviderStateText.DescribeLimit(usage.LimitStatus));
    }

    [Fact]
    public void Limit_zdjety_dla_dostawcy_lokalnego_nie_ogranicza_i_nie_ostrzega()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        Spend(usage, 90);
        Assert.Equal(SessionLimitLevel.Near, usage.LimitStatus.Level);

        usage.SessionCharacterLimit = null;

        Assert.False(usage.TryTakeLimitWarning());
        Assert.False(usage.WouldExceedSessionLimit(1_000_000));
        Assert.Equal(SessionLimitLevel.None, usage.LimitStatus.Level);
    }

    [Fact]
    public void Zmiana_limitu_pozwala_ostrzec_ponownie_a_to_samo_ustawienie_nie()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        Spend(usage, 85);
        Assert.True(usage.TryTakeLimitWarning());

        usage.SessionCharacterLimit = 100;
        Assert.False(usage.TryTakeLimitWarning());

        usage.SessionCharacterLimit = 100;
        usage.SessionCharacterLimit = 90;
        Assert.True(usage.TryTakeLimitWarning());
    }

    [Fact]
    public void Reset_odnawia_ostrzezenie()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        Spend(usage, 85);
        Assert.True(usage.TryTakeLimitWarning());

        usage.Reset();
        Spend(usage, 85);

        Assert.True(usage.TryTakeLimitWarning());
    }

    [Fact]
    public void Wyczerpany_limit_nie_jest_ostrzezeniem_tylko_stanem()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        Spend(usage, 100);

        Assert.Equal(SessionLimitLevel.Exhausted, usage.LimitStatus.Level);
        Assert.False(usage.TryTakeLimitWarning());
        Assert.Contains("Limit znaków na sesję", ProviderStateText.DescribeLimit(usage.LimitStatus));
    }

    [Fact]
    public void Tekst_okna_przy_80_procentach_podaje_zuzycie_i_pole()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 100_000 };
        Spend(usage, 85_000);

        var text = ProviderStateText.DescribeLimit(usage.LimitStatus);

        Assert.NotNull(text);
        Assert.Contains("85%", text);
        Assert.Contains("Limit znaków na sesję", text);
        Assert.Contains("Tłumaczenie", text);
    }
}

public class ProviderStateTextTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SessionLimitStatus NoLimit = new(SessionLimitLevel.None, 0, null);

    [Fact]
    public void Dzialajacy_dostawca_bez_limitu_nie_ma_tekstu()
    {
        Assert.Null(ProviderStateText.Describe(ProviderPauseStatus.Running, NoLimit, Now));
    }

    [Fact]
    public void Trwale_wstrzymanie_mowi_co_zrobic()
    {
        var pause = new ProviderPauseStatus(ProviderPauseKind.Permanent, TranslationFailureKind.InvalidApiKey, "x", null);

        var text = ProviderStateText.Describe(pause, NoLimit, Now)!;

        Assert.Contains("wstrzymany", text);
        Assert.Contains("odrzucony", text);
        Assert.Contains("zapisaniu klucza", text);
        Assert.Contains("trybu live", text);
    }

    [Fact]
    public void Przerwa_chwilowa_podaje_liczbe_sekund()
    {
        var pause = new ProviderPauseStatus(
            ProviderPauseKind.Temporary, TranslationFailureKind.NetworkError, "x", Now + TimeSpan.FromSeconds(14.2));

        var text = ProviderStateText.Describe(pause, NoLimit, Now)!;

        Assert.Contains("chwilowo wstrzymany", text);
        Assert.Contains("15 s", text);
    }

    [Fact]
    public void Wstrzymanie_i_limit_sa_w_osobnych_liniach()
    {
        var pause = new ProviderPauseStatus(ProviderPauseKind.Permanent, TranslationFailureKind.QuotaExceeded, "x", null);
        var limit = new SessionLimitStatus(SessionLimitLevel.Near, 80, 100);

        var text = ProviderStateText.Describe(pause, limit, Now)!;

        Assert.Equal(2, text.Split(Environment.NewLine).Length);
    }
}

public class CounterAndLimitNoticeTests
{
    private static readonly TimeSpan Zero = TimeSpan.Zero;

    private static TranslationOutcome WithNotice(OutcomeNotice notice) =>
        new("a", "a", "PL:a", TranslationOrigin.Provider) { Notices = notice };

    private static TranslationOutcome Failed() =>
        new("b", "b", null, TranslationOrigin.Unavailable, "klucz")
        {
            Issue = OutcomeIssue.Provider, FailureKind = TranslationFailureKind.InvalidApiKey,
        };

    [Fact]
    public void Nowe_komunikaty_mieszcza_sie_w_limicie_znakow()
    {
        string[] texts = [NoticeTexts.SessionLimit, NoticeTexts.SessionLimitNear, NoticeTexts.CounterSkipped];

        Assert.All(texts, static text => Assert.InRange(text.Length, 1, NoticeTexts.MaxLength));
        Assert.Contains("Tłumaczeni", NoticeTexts.SessionLimit);
    }

    [Fact]
    public void Ostrzezenie_o_limicie_pokazuje_sie_raz_na_sesje()
    {
        var policy = new OverlayNoticePolicy();

        var first = policy.OfferFrame([WithNotice(OutcomeNotice.SessionLimitNear)], "DeepL", false, Zero);
        var again = policy.OfferFrame([WithNotice(OutcomeNotice.SessionLimitNear)], "DeepL", false, TimeSpan.FromMinutes(5));

        Assert.Equal(NoticeTexts.SessionLimitNear, first!.Text);
        Assert.Null(again);

        policy.Reset();
        Assert.NotNull(policy.OfferFrame([WithNotice(OutcomeNotice.SessionLimitNear)], "DeepL", false, TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public void Komunikat_o_liczniku_pokazuje_sie_raz_na_sesje()
    {
        var policy = new OverlayNoticePolicy();

        var first = policy.OfferFrame([WithNotice(OutcomeNotice.CounterSkipped)], "DeepL", false, Zero);
        var again = policy.OfferFrame([WithNotice(OutcomeNotice.CounterSkipped)], "DeepL", false, TimeSpan.FromMinutes(5));

        Assert.Equal(NoticeTexts.CounterSkipped, first!.Text);
        Assert.Null(again);
    }

    [Fact]
    public void Ostrzezenie_czeka_na_wygasniecie_waznejszego_komunikatu()
    {
        var policy = new OverlayNoticePolicy();

        var failure = policy.OfferFrame([Failed()], "DeepL", false, Zero);
        var blocked = policy.OfferFrame([WithNotice(OutcomeNotice.SessionLimitNear)], "DeepL", false, TimeSpan.FromSeconds(1));
        var later = policy.OfferFrame([new TranslationOutcome("c", "c", "PL:c", TranslationOrigin.Cache)], "DeepL", false, TimeSpan.FromSeconds(10));

        Assert.NotNull(failure);
        Assert.Null(blocked);
        Assert.Equal(NoticeTexts.SessionLimitNear, later!.Text);
    }

    [Fact]
    public void Pominiete_teksty_nie_daja_komunikatu_o_porazce_ani_bledzie()
    {
        TranslationOutcome[] outcomes =
        [
            new("a", "a", null, TranslationOrigin.Unavailable, "bez liter") { Issue = OutcomeIssue.Skipped },
        ];

        Assert.Null(OverlayNotices.FromOutcomes(outcomes, "DeepL"));
        Assert.Null(OverlayNotices.ForManualResult(outcomes, "DeepL"));
    }
}
