namespace GameTranslatorOverlay.Core.Ocr;

public static class OcrScaling
{
    public const int SmallRegionHeight = 120;
    public const int SmallRegionWidth = 420;

    /// <summary>
    /// Dobiera współczynnik powiększenia obrazu przed OCR. Systemowe OCR Windows radzi sobie
    /// wyraźnie lepiej z małym tekstem po dwukrotnym powiększeniu, ale obraz nie może
    /// przekroczyć maksymalnego wymiaru silnika.
    /// </summary>
    public static double ComputeUpscale(int width, int height, int maxEngineDimension, double preferredUpscale = 0)
    {
        if (width <= 0 || height <= 0) return 1.0;

        var factor = preferredUpscale > 1.0
            ? preferredUpscale
            : (height < SmallRegionHeight || width < SmallRegionWidth) ? 2.0 : 1.0;

        var longest = Math.Max(width, height);
        if (longest * factor > maxEngineDimension)
        {
            factor = Math.Max(1.0, (double)maxEngineDimension / longest);
        }

        return factor;
    }

    /// <summary>Zmniejsza obraz, który sam z siebie przekracza limit silnika OCR.</summary>
    public static double ComputeDownscale(int width, int height, int maxEngineDimension)
    {
        var longest = Math.Max(width, height);
        return longest <= maxEngineDimension ? 1.0 : (double)maxEngineDimension / longest;
    }
}
