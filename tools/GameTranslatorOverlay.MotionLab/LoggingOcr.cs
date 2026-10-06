using GameTranslatorOverlay.Core.Ocr;

internal sealed class LoggingOcr(IOcrProvider inner, Func<double> clock, JsonlWriter writer, bool probeAngle) : IOcrProvider
{
    private AngleAwareOcr? _probe;
    private int _calls;
    private int _probeBusy;
    private int _probes;

    public string Name => inner.Name;

    public int MaxImageDimension => inner.MaxImageDimension;

    public IReadOnlyList<string> AvailableLanguages => inner.AvailableLanguages;

    public int Calls => Volatile.Read(ref _calls);

    public int Probes => Volatile.Read(ref _probes);

    public bool IsLanguageAvailable(string languageTag) => inner.IsLanguageAvailable(languageTag);

    public async Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
    {
        var number = Interlocked.Increment(ref _calls);
        var start = clock();
        OcrResult result;
        try
        {
            result = await inner.RecognizeAsync(bitmap, languageTag, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            writer.Write(new OcrDto(number, Json.R(start), Json.R(clock()), bitmap.Width, bitmap.Height, 0, true, null));
            throw;
        }
        var end = clock();
        var lines = result.Lines.Count;
        if (probeAngle && Interlocked.CompareExchange(ref _probeBusy, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                double? angle = null;
                try
                {
                    _probe ??= new AngleAwareOcr(languageTag);
                    angle = (await _probe.RecognizeAsync(bitmap).ConfigureAwait(false)).Angle ?? 0;
                    Interlocked.Increment(ref _probes);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    angle = null;
                }
                finally
                {
                    Volatile.Write(ref _probeBusy, 0);
                }
                writer.Write(new OcrDto(number, Json.R(start), Json.R(end), bitmap.Width, bitmap.Height, lines, false,
                    angle is { } a ? Math.Round(a, 2) : null));
            }, CancellationToken.None);
        }
        else
        {
            writer.Write(new OcrDto(number, Json.R(start), Json.R(end), bitmap.Width, bitmap.Height, lines, false, null));
        }
        return result;
    }
}
