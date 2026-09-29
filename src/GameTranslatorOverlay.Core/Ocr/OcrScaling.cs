namespace GameTranslatorOverlay.Core.Ocr;

/// <summary>Preferencja powiększania przed OCR: współczynnik i zgoda na automatyczne 2× dla małych regionów.</summary>
public readonly record struct UpscalePreference(double Preferred, bool AllowAuto);

public static class OcrScaling
{
    public const int SmallRegionHeight = 120;
    public const int SmallRegionWidth = 420;

    /// <summary>
    /// Dobiera współczynnik powiększenia obrazu przed OCR. Systemowe OCR Windows radzi sobie
    /// wyraźnie lepiej z małym tekstem po dwukrotnym powiększeniu, ale obraz nie może
    /// przekroczyć maksymalnego wymiaru silnika. <paramref name="allowAutoUpscale"/> = false
    /// wyłącza automatyczne 2× (profil gry jawnie ustawił „upscale”: 1.0).
    /// </summary>
    public static double ComputeUpscale(
        int width, int height, int maxEngineDimension, double preferredUpscale = 0, bool allowAutoUpscale = true)
    {
        if (width <= 0 || height <= 0) return 1.0;

        var factor = preferredUpscale > 1.0
            ? preferredUpscale
            : allowAutoUpscale && (height < SmallRegionHeight || width < SmallRegionWidth) ? 2.0 : 1.0;

        var longest = Math.Max(width, height);
        if (longest * factor > maxEngineDimension)
        {
            factor = Math.Max(1.0, (double)maxEngineDimension / longest);
        }

        return factor;
    }

    /// <summary>
    /// Łączy ustawienie profilu z ustawieniem aplikacji. Profil bez „upscale” korzysta
    /// z ustawień (0 = automatyka); jawne 1.0 w profilu wyłącza powiększanie, a wartość
    /// powyżej 1.0 ustala stały współczynnik.
    /// </summary>
    public static UpscalePreference ResolvePreference(double? profileUpscale, double settingsUpscale) =>
        profileUpscale is { } fromProfile
            ? new UpscalePreference(fromProfile, AllowAuto: fromProfile > 1.0)
            : new UpscalePreference(settingsUpscale, AllowAuto: true);

    /// <summary>Zmniejsza obraz, który sam z siebie przekracza limit silnika OCR.</summary>
    public static double ComputeDownscale(int width, int height, int maxEngineDimension)
    {
        var longest = Math.Max(width, height);
        return longest <= maxEngineDimension ? 1.0 : (double)maxEngineDimension / longest;
    }
}
