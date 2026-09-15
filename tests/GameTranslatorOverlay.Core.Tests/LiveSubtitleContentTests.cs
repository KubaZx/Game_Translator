using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveSubtitleContentTests
{
    private static KeyValuePair<string, string> Text(string key, string value) => new(key, value);

    [Fact]
    public void Removing_an_old_prompt_does_not_erase_a_new_independent_dialog()
    {
        var content = new LiveSubtitleContent();
        content.Replace([Text("inspect", "Zbadaj")]);
        Assert.Equal("Drzwi są otwarte", content.Replace([Text("dialog", "Drzwi są otwarte")]));

        Assert.Null(content.Remove(["inspect"]));
        // The newer dialog still owns the subtitle and can be removed later.
        Assert.Equal(string.Empty, content.Remove(["dialog"]));
    }

    [Fact]
    public void Removing_one_member_of_a_combined_subtitle_preserves_the_other_member()
    {
        var content = new LiveSubtitleContent();
        Assert.Equal("Zbadaj\nDrzwi są otwarte",
            content.Replace([Text("inspect", "Zbadaj"), Text("dialog", "Drzwi są otwarte")]));

        Assert.Equal("Drzwi są otwarte", content.Remove(["inspect"]));
        Assert.Null(content.Remove(["inspect"]));
        Assert.Equal(string.Empty, content.Remove(["dialog"]));
        Assert.Null(content.Remove(["dialog"]));
    }

    [Fact]
    public void Removing_the_last_current_source_explicitly_clears_the_subtitle()
    {
        var content = new LiveSubtitleContent();
        content.Replace([Text("dialog", "Witaj w akademii")]);

        Assert.Equal(string.Empty, content.Remove(["unrelated", "dialog", "dialog"]));
        Assert.Null(content.Remove(["dialog"]));
    }

    [Fact]
    public void Partial_removal_preserves_source_order_and_trims_the_result()
    {
        var content = new LiveSubtitleContent();
        Assert.Equal("Pierwszy\nDrugi\nTrzeci",
            content.Replace([Text("a", "  Pierwszy"), Text("b", "Drugi"), Text("c", "Trzeci  ")]));

        Assert.Equal("Pierwszy\nTrzeci", content.Remove(["b"]));
        Assert.Equal("Trzeci", content.Remove(["a"]));
    }

    [Fact]
    public void Clear_forgets_current_sources_and_does_not_retain_the_previous_subtitle()
    {
        var content = new LiveSubtitleContent();
        content.Replace([Text("old", "Stara treść")]);
        content.Clear();

        Assert.Null(content.Remove(["old"]));
        Assert.Equal("Nowa treść", content.Replace([Text("new", "Nowa treść")]));
        Assert.Null(content.Remove(["old"]));
        Assert.Equal(string.Empty, content.Remove(["new"]));
    }

    [Fact]
    public void Empty_replace_discards_the_current_subtitle_and_empty_removal_changes_nothing()
    {
        var content = new LiveSubtitleContent();
        content.Replace([Text("old", "Stara treść")]);

        Assert.Null(content.Remove([]));
        Assert.Equal(string.Empty, content.Replace([]));
        Assert.Null(content.Remove(["old"]));
    }

    [Fact]
    public void Source_keys_use_ordinal_identity_even_when_the_translated_text_is_equal()
    {
        var content = new LiveSubtitleContent();
        content.Replace([Text("Key", "Dalej"), Text("key", "Dalej")]);

        Assert.Null(content.Remove(["KEY"]));
        Assert.Equal("Dalej", content.Remove(["Key"]));
        Assert.Equal(string.Empty, content.Remove(["key"]));
    }

    [Fact]
    public void Text_at_the_limit_stays_complete_and_ellipsis_counts_toward_the_limit()
    {
        var content = new LiveSubtitleContent();
        var exact = new string('a', 400);
        Assert.Equal(exact, content.Replace([Text("a", exact)]));

        var shortened = content.Replace([Text("a", exact + "b")]);
        Assert.Equal(new string('a', 399) + "…", shortened);
        Assert.Equal(400, shortened.Length);
    }

    [Fact]
    public void Removing_a_long_first_source_reveals_the_complete_remaining_source()
    {
        var content = new LiveSubtitleContent();
        Assert.Equal(400, content.Replace([Text("long", new string('a', 450)), Text("remaining", "Krótki dialog")]).Length);

        // Keep the original source content, not merely the truncated display string.
        Assert.Equal("Krótki dialog", content.Remove(["long"]));
    }

    [Fact]
    public void Truncation_does_not_leave_half_of_a_surrogate_pair()
    {
        var content = new LiveSubtitleContent();
        var text = new string('a', 398) + "\U0001F511" + "More text";

        var shortened = content.Replace([Text("key", text)]);

        Assert.Equal(new string('a', 398) + "…", shortened);
        Assert.True(shortened.Length <= 400);
    }
}