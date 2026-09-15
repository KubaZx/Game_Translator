using GameTranslatorOverlay.LiveDiag;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveDiagOptionsTests
{
    [Fact]
    public void Defaults_do_not_enable_text_screenshots_or_a_game_profile()
    {
        var options = LiveDiagOptions.Parse([]);
        Assert.Equal(36, options.Seconds);
        Assert.Null(options.AttachTitle);
        Assert.Null(options.ProfileId);
        Assert.Null(options.OutputPath);
        Assert.Equal(0, options.Upscale);
        Assert.False(options.DumpFrames);
        Assert.False(options.IncludeText);
    }

    [Theory]
    [InlineData("45", null, 45)]
    [InlineData("Escape Academy", "Escape Academy", 25)]
    public void Preserves_single_positional_argument(string value, string? title, int seconds)
    {
        var options = LiveDiagOptions.Parse([value]);
        Assert.Equal(title, options.AttachTitle);
        Assert.Equal(seconds, options.Seconds);
    }

    [Fact]
    public void Parses_attached_window_with_explicit_private_diagnostic_options()
    {
        var options = LiveDiagOptions.Parse(["Escape Academy", "--profile", "none", "120",
            "--upscale", "1.5", "--output", "metrics.jsonl", "--dump-frames", "--include-text"]);
        Assert.Equal("Escape Academy", options.AttachTitle);
        Assert.Equal(120, options.Seconds);
        Assert.Equal(1.5, options.Upscale);
        Assert.Equal("metrics.jsonl", options.OutputPath);
        Assert.Null(options.ProfileId);
        Assert.True(options.DumpFrames);
        Assert.True(options.IncludeText);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--list-windows")]
    public void Read_only_commands_are_parsed_without_desktop_access(string option)
    {
        var options = LiveDiagOptions.Parse([option]);
        Assert.Equal(option == "--help", options.Help);
        Assert.Equal(option == "--list-windows", options.ListWindows);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("--unknown")]
    [InlineData("")]
    public void Rejects_invalid_single_arguments(string value) =>
        Assert.Throws<ArgumentException>(() => LiveDiagOptions.Parse([value]));

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.1")]
    [InlineData("4.1")]
    [InlineData("1,5")]
    public void Rejects_unsafe_or_culture_dependent_upscale(string value) =>
        Assert.Throws<ArgumentException>(() => LiveDiagOptions.Parse(["--upscale", value]));

    [Fact]
    public void Rejects_incomplete_ambiguous_or_silently_ignored_options()
    {
        string[][] invalid =
        [
            ["--output"], ["--profile", "--help"], ["20", "30"],
            ["Escape Academy", "not-seconds"], ["Escape Academy", "20", "extra"],
            ["--list-windows", "Escape Academy"], ["--dump-frames", "--dump-frames"],
            ["--profile", "generic", "--upscale", "2"],
        ];
        foreach (var args in invalid)
            Assert.Throws<ArgumentException>(() => LiveDiagOptions.Parse(args));
    }

    [Fact]
    public void Keeps_explicit_profile_id_for_catalog_validation() =>
        Assert.Equal("generic", LiveDiagOptions.Parse(["--profile", "generic"]).ProfileId);
}
