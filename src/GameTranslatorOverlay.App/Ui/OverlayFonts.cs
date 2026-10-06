using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;
using GameTranslatorOverlay.Infrastructure.Settings;

namespace GameTranslatorOverlay.App.Ui;

public sealed record InkMetrics(double Ascent, double Width, double Left, double Density, double BaselineShift);

public static class OverlayFonts
{
    public const string Auto = "auto";
    public const string DefaultFamily = "Segoe UI";
    private const double ReferenceEm = 64;

    private static readonly string[] BundledFamilies = ["Lexend Deca"];
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IReadOnlyList<Typeface>> Weights = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(Typeface, string), InkMetrics> Metrics = [];

    public static IReadOnlyList<string> Bundled => BundledFamilies;

    public static string? ProfileFont { get; set; }

    public static FontFamily Resolve(AppSettings settings) => Family(ResolveFamilyName(settings, ProfileFont));

    public static bool IsAuto(string? setting) =>
        string.IsNullOrWhiteSpace(setting) || setting.Equals(Auto, StringComparison.OrdinalIgnoreCase);

    public static string ResolveFamilyName(AppSettings settings, string? profileFont)
    {
        if (!IsAuto(settings.OverlayFontFamily)) return settings.OverlayFontFamily;
        return string.IsNullOrWhiteSpace(profileFont) ? DefaultFamily : profileFont.Trim();
    }

    public static FontFamily Family(string name)
    {
        lock (Gate)
        {
            if (Families.TryGetValue(name, out var cached)) return cached;
            FontFamily family;
            var bundled = BundledFamilies.FirstOrDefault(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (bundled is not null)
            {
                _ = System.Windows.Application.Current;
                family = new FontFamily(new Uri("pack://application:,,,/GameTranslatorOverlay;component/Fonts/"), "./#" + bundled);
            }
            else
            {
                family = new FontFamily(name);
            }
            Families[name] = family;
            return family;
        }
    }

    public static void WarmUp(string name)
    {
        foreach (var typeface in AvailableWeights(name)) Measure(typeface, "Ąg Hint");
        var element = new GameTextElement { Text = "Ąg Hint", Typeface = Regular(name), EmSize = 40 };
        element.SetOutline(Colors.Black, 2, new Vector(2, 2));
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }

    public static Typeface Regular(string name) => new(Family(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static IReadOnlyList<Typeface> AvailableWeights(string name)
    {
        lock (Gate)
        {
            if (Weights.TryGetValue(name, out var cached)) return cached;
        }
        var family = Family(name);
        var weights = family.FamilyTypefaces
            .Where(static t => t.Style == FontStyles.Normal && t.Stretch == FontStretches.Normal)
            .Select(static t => t.Weight)
            .Append(FontWeights.Normal)
            .Distinct()
            .OrderBy(static w => w.ToOpenTypeWeight())
            .ToList();
        var result = new List<Typeface>();
        foreach (var weight in weights)
        {
            var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyphs)) continue;
            if (Math.Abs(glyphs.Weight.ToOpenTypeWeight() - weight.ToOpenTypeWeight()) > 50) continue;
            if (glyphs.StyleSimulations != StyleSimulations.None) continue;
            if (glyphs.Weight.ToOpenTypeWeight() is < 300 or > 800) continue;
            if (!glyphs.FamilyNames.Values.Concat(glyphs.Win32FamilyNames.Values).Any(n => n.StartsWith(name, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(typeface);
        }
        if (result.Count == 0)
        {
            if (!name.Equals(DefaultFamily, StringComparison.OrdinalIgnoreCase)) return AvailableWeights(DefaultFamily);
            result.Add(Regular(name));
        }
        lock (Gate)
        {
            Weights[name] = result;
        }
        return result;
    }

    public static InkMetrics Measure(Typeface typeface, string text)
    {
        var key = (typeface, text);
        lock (Gate)
        {
            if (Metrics.TryGetValue(key, out var cached)) return cached;
        }
        var metrics = Rasterize(typeface, text) ?? new InkMetrics(0.7, 0, 0, 0, 0);
        lock (Gate)
        {
            if (Metrics.Count > 4096) Metrics.Clear();
            Metrics[key] = metrics;
        }
        return metrics;
    }

    private static InkMetrics? Rasterize(Typeface typeface, string text)
    {
        const int pad = 16;
        var formatted = GameTextElement.Format(text, typeface, ReferenceEm, Brushes.Black, 1.0);
        var width = (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + formatted.OverhangLeading + formatted.OverhangTrailing) + 2 * pad;
        var height = (int)Math.Ceiling(formatted.Height + formatted.OverhangAfter + ReferenceEm * 0.5) + 2 * pad;
        if (width <= 2 * pad || height <= 2 * pad || (long)width * height > 16_000_000) return null;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawText(formatted, new Point(pad, pad));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var mask = new bool[width * height];
        for (var i = 0; i < mask.Length; i++) mask[i] = pixels[i * 4 + 3] >= 128;
        var profile = InkProfile.Measure(mask, width, new RectPx(0, 0, width, height));
        if (profile is null) return null;
        var trueBaseline = pad + formatted.Baseline;
        return new InkMetrics(
            profile.Ascent / ReferenceEm,
            (profile.Right - profile.Left) / ReferenceEm,
            (profile.Left - pad) / ReferenceEm,
            profile.Density,
            (trueBaseline - profile.Baseline) / ReferenceEm);
    }

    public static Typeface ChooseWeight(string familyName, string referenceText, double density)
    {
        var candidates = AvailableWeights(familyName);
        if (candidates.Count == 1 || density <= 0 || string.IsNullOrWhiteSpace(referenceText)) return PreferMedium(candidates);
        var best = candidates[0];
        var bestError = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var error = Math.Abs(Measure(candidate, referenceText).Density - density);
            if (error < bestError)
            {
                bestError = error;
                best = candidate;
            }
        }
        return best;
    }

    private sealed record WeightVote(string Text, double Ascent, int TextRgb, bool Outlined, int Weight);
    private static readonly Dictionary<string, List<WeightVote>> Votes = new(StringComparer.OrdinalIgnoreCase);

    public static void VoteWeight(string familyName, string referenceText, double density, double ascent, int textRgb, bool outlined)
    {
        if (density <= 0 || ascent <= 0 || string.IsNullOrWhiteSpace(referenceText)) return;
        lock (Gate)
        {
            if (Votes.TryGetValue(familyName, out var existing)
                && existing.Any(v => v.Text == referenceText && v.Outlined == outlined && Math.Abs(v.Ascent - ascent) <= ascent * 0.05 && SameColor(v.TextRgb, textRgb)))
                return;
        }
        var weight = ChooseWeight(familyName, referenceText, density).Weight.ToOpenTypeWeight();
        lock (Gate)
        {
            if (!Votes.TryGetValue(familyName, out var list)) Votes[familyName] = list = [];
            if (list.Count >= 256) list.RemoveRange(0, 128);
            list.Add(new WeightVote(referenceText, ascent, textRgb, outlined, weight));
        }
    }

    public static Typeface ChooseStyleWeight(string familyName, string referenceText, double density, double ascent, int textRgb, bool outlined)
    {
        VoteWeight(familyName, referenceText, density, ascent, textRgb, outlined);
        List<int> peers;
        lock (Gate)
        {
            peers = Votes.TryGetValue(familyName, out var list)
                ? list.Where(v => v.Outlined == outlined && Math.Abs(v.Ascent - ascent) <= Math.Max(2, ascent * 0.2) && SameColor(v.TextRgb, textRgb))
                    .Select(static v => v.Weight)
                    .ToList()
                : [];
        }
        if (peers.Count == 0) return ChooseWeight(familyName, referenceText, density);
        peers.Sort();
        var median = peers[peers.Count / 2];
        return AvailableWeights(familyName).OrderBy(t => Math.Abs(t.Weight.ToOpenTypeWeight() - median)).First();
    }

    public static void ForgetWeightVotes()
    {
        lock (Gate)
        {
            Votes.Clear();
        }
    }

    private static bool SameColor(int a, int b)
    {
        if (a < 0 || b < 0) return a == b;
        return Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF))
            + Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF))
            + Math.Abs((a & 0xFF) - (b & 0xFF)) <= 90;
    }

    private static Typeface PreferMedium(IReadOnlyList<Typeface> candidates) =>
        candidates.OrderBy(static t => Math.Abs(t.Weight.ToOpenTypeWeight() - 450)).First();
}
