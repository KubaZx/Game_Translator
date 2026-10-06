using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveReadingStabilizerTests
{
    [Theory]
    [InlineData("The door is locked", "The door is open")]
    [InlineData("Turn left", "Turn right")]
    [InlineData("Go north", "Go south")]
    public void Similar_clean_changes_replace_after_two_consecutive_readings(string displayed, string candidate)
    {
        var stabilizer = new LiveReadingStabilizer();
        Assert.True(TextSimilarity.Ratio(displayed, candidate) >= 0.5);
        Assert.Equal(1.0, ReadingQuality.Score(displayed));
        Assert.Equal(1.0, ReadingQuality.Score(candidate));

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("label", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("label", displayed, candidate));
    }

    [Fact]
    public void Alternating_candidates_do_not_accumulate_nonconsecutive_confirmations()
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "The door is locked";
        const string first = "The door is open";
        const string second = "The door is closed";

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("door", displayed, first));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("door", displayed, second));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("door", displayed, first));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("door", displayed, first));
    }

    [Theory]
    [InlineData("The door is locked")]
    [InlineData("THE DOOR IS LOCKED")]
    public void Seeing_the_displayed_text_again_breaks_the_candidate_streak(string repeatedDisplayed)
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "The door is locked";
        const string candidate = "The door is open";

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("door", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Keep, stabilizer.Observe("door", displayed, repeatedDisplayed));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("door", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("door", displayed, candidate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_reading_breaks_the_candidate_streak(string empty)
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "The door is locked";
        const string candidate = "The door is open";

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("door", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Keep, stabilizer.Observe("door", displayed, empty));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("door", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("door", displayed, candidate));
    }

    [Fact]
    public void Worse_OCR_is_rejected_even_when_repeated_and_breaks_a_previous_streak()
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "The door is locked";
        const string candidate = "The door is open";
        const string damaged = "The d00r i5 l0cked";
        Assert.True(ReadingQuality.Score(damaged) < ReadingQuality.Score(displayed) - 0.1);

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("door", displayed, candidate));
        for (var reading = 0; reading < 3; reading++)
            Assert.Equal(LiveReadingDecision.Keep, stabilizer.Observe("door", displayed, damaged));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("door", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("door", displayed, candidate));
    }

    [Fact]
    public void A_very_different_clean_message_replaces_immediately()
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "The door is locked";
        const string candidate = "Inventory full";
        Assert.True(TextSimilarity.Ratio(displayed, candidate) < 0.5);
        Assert.True(ReadingQuality.Score(candidate) >= 0.9);

        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("label", displayed, candidate));
    }

    [Theory]
    [InlineData("The door is")]
    [InlineData("door is locked")]
    [InlineData("locked")]
    public void Repeated_whole_word_prefix_or_suffix_fragments_do_not_replace_full_text(string fragment)
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "The door is locked";
        Assert.Equal(1.0, ReadingQuality.Score(fragment));
        // Even a short suffix with similarity below 0.5 is a crop, not a new sentence.

        for (var reading = 0; reading < 4; reading++)
            Assert.Equal(LiveReadingDecision.Keep, stabilizer.Observe("door", displayed, fragment));
    }

    [Fact]
    public void A_real_numeric_change_still_requires_two_consecutive_readings()
    {
        var stabilizer = new LiveReadingStabilizer();

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("counter", "Ammo: 20", "Ammo: 21"));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("counter", "Ammo: 20", "Ammo: 21"));
    }

    [Fact]
    public void A_clearly_better_reading_replaces_after_confirmation()
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "Pr016gue";
        const string candidate = "Prologue";
        Assert.True(ReadingQuality.Score(candidate) > ReadingQuality.Score(displayed) + 0.1);
        Assert.True(TextSimilarity.Ratio(displayed, candidate) >= 0.5);

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("chapter", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("chapter", displayed, candidate));
    }

    [Fact]
    public void A_fuller_reading_keeps_the_existing_length_rule_even_below_clean_quality()
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "Pr016gue";
        const string candidate = "Pr016gue II";
        Assert.True(ReadingQuality.Score(candidate) < 0.9);
        Assert.True(ReadingQuality.Score(candidate) <= ReadingQuality.Score(displayed) + 0.1);
        Assert.True(TextSimilarity.Ratio(displayed, candidate) >= 0.5);
        Assert.True(candidate.Length >= displayed.Length + 2);

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("chapter", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("chapter", displayed, candidate));
    }

    [Fact]
    public void Reset_only_breaks_the_requested_keys_streak()
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "The door is locked";
        const string candidate = "The door is open";
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("first", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("second", displayed, candidate));

        stabilizer.Reset("first");

        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("second", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("first", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("first", displayed, candidate));
    }

    [Fact]
    public void Prune_forgets_removed_keys_and_preserves_the_remaining_streak()
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "The door is locked";
        const string candidate = "The door is open";
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("removed", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("retained", displayed, candidate));

        stabilizer.Prune(new HashSet<string>(StringComparer.Ordinal) { "retained" });

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("removed", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("retained", displayed, candidate));
    }

    [Fact]
    public void A_confirmation_does_not_transfer_to_a_different_displayed_text_at_the_same_key()
    {
        var stabilizer = new LiveReadingStabilizer();
        const string candidate = "The door is open";

        Assert.Equal(LiveReadingDecision.Confirm,
            stabilizer.Observe("door", "The door is locked", candidate));
        Assert.Equal(LiveReadingDecision.Confirm,
            stabilizer.Observe("door", "The door is closed", candidate));
        Assert.Equal(LiveReadingDecision.Replace,
            stabilizer.Observe("door", "The door is closed", candidate));
    }
    [Fact]
    public void Clear_forgets_all_pending_confirmations()
    {
        var stabilizer = new LiveReadingStabilizer();
        const string displayed = "The door is locked";
        const string candidate = "The door is open";
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("first", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("second", displayed, candidate));

        stabilizer.Clear();

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("first", displayed, candidate));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("second", displayed, candidate));
    }

    [Theory]
    [InlineData("Loading…")]
    [InlineData("Lv5 Key")]
    [InlineData("Price: €5")]
    public void Wiarygodny_nowy_napis_gorszej_jakosci_zastepuje_stary_po_dwoch_odczytach(string candidate)
    {
        var stabilizer = new LiveReadingStabilizer();

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("label", "Inspect", candidate));
        Assert.Equal(LiveReadingDecision.Replace, stabilizer.Observe("label", "Inspect", candidate));
    }

    [Fact]
    public void Rozne_przeklamania_nad_napisem_nie_sumuja_sie_w_podmiane()
    {
        var stabilizer = new LiveReadingStabilizer();

        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("label", "Inspect", "Loading…"));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("label", "Inspect", "Lv5 Key"));
        Assert.Equal(LiveReadingDecision.Keep, stabilizer.Observe("label", "Inspect", "Inspect"));
        Assert.Equal(LiveReadingDecision.Confirm, stabilizer.Observe("label", "Inspect", "Loading…"));
    }

    [Fact]
    public void Smieciowy_odczyt_nadal_nie_zastepuje_napisu_nawet_powtorzony()
    {
        var stabilizer = new LiveReadingStabilizer();

        for (var reading = 0; reading < 3; reading++)
            Assert.Equal(LiveReadingDecision.Keep, stabilizer.Observe("label", "Inspect", "lRrgIé@ue"));
    }
}
