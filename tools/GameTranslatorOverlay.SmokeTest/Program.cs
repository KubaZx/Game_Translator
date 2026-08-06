using System.Drawing;
using System.IO;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;

// Smoke test pionowego przepływu bez GUI: syntetyczny tooltip RPG →
// systemowe OCR Windows → grupowanie bloków → pipeline (słownik → SQLite cache → Mock).
// Kod wyjścia 0 = wszystko działa; wymaga Windows z pakietem językowym en.

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("== GameTranslatorOverlay — smoke test OCR + pipeline ==");

using var bitmap = new Bitmap(620, 210);
using (var graphics = Graphics.FromImage(bitmap))
{
    graphics.Clear(Color.FromArgb(18, 18, 24));
    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    using var titleFont = new Font("Segoe UI", 16, FontStyle.Bold);
    using var bodyFont = new Font("Segoe UI", 14);
    graphics.DrawString("Storm Crown", titleFont, Brushes.Gold, 20, 15);
    graphics.DrawString("Energy Shield: 120", bodyFont, Brushes.White, 20, 58);
    graphics.DrawString("24% increased Lightning Damage", bodyFont, Brushes.White, 20, 88);
    graphics.DrawString("+30 to maximum Mana", bodyFont, Brushes.White, 20, 118);
    graphics.DrawString("Level 20", bodyFont, Brushes.White, 20, 148);
}

var ocrProvider = new WindowsOcrProvider();
Console.WriteLine($"OCR: {ocrProvider.Name}; języki: {string.Join(", ", ocrProvider.AvailableLanguages)}; " +
                  $"maks. wymiar obrazu: {ocrProvider.MaxImageDimension} px");

if (!ocrProvider.IsLanguageAvailable("en"))
{
    Console.WriteLine("BRAK pakietu językowego „en” — smoke test przerwany.");
    return 2;
}

using var upscaled = ScreenCapture.Rescale(bitmap, 2.0);
var ocrResult = await ocrProvider.RecognizeAsync(ScreenCapture.ToOcrBitmap(upscaled), "en");

Console.WriteLine($"\nRozpoznane linie ({ocrResult.Lines.Count}):");
foreach (var line in ocrResult.Lines)
{
    Console.WriteLine($"  [{line.Box.X,4},{line.Box.Y,4} {line.Box.Width}×{line.Box.Height}] {line.Text}");
}

var blocks = TextBlockGrouper.Group(ocrResult.Lines)
    .Where(block => JunkFilter.IsMeaningful(block.Text))
    .ToList();
Console.WriteLine($"\nBloki po grupowaniu i filtrze śmieci: {blocks.Count}");

var glossary = new GlossaryService();
glossary.AddTerm(new GlossaryTerm("Level 20", "Poziom 20"));

var databasePath = Path.Combine(Path.GetTempPath(), $"gto-smoke-{Guid.NewGuid():N}.db");
var cache = new SqliteTranslationCache(databasePath);
cache.Initialize();
var usage = new UsageTracker();
var pipeline = new TranslationPipeline(glossary, cache, new MockTranslationProvider(), usage, new TranslationPipelineOptions());

var texts = blocks.Select(block => block.Text).ToList();
var outcomes = await pipeline.TranslateAsync(texts, "en", "pl");

Console.WriteLine("\nWyniki pipeline (1. przebieg):");
foreach (var outcome in outcomes)
{
    Console.WriteLine($"  [{outcome.Origin}] {outcome.SourceText.Replace("\n", " | ")}  →  {outcome.TranslatedText}");
}

var secondPass = await pipeline.TranslateAsync(texts, "en", "pl");
Console.WriteLine($"\n2. przebieg: trafienia w cache = {usage.CacheHits}, zapytania do API = {usage.ApiRequests}");

var ok = ocrResult.Lines.Count >= 4
    && ocrResult.Lines.Any(line => line.Text.Contains("Lightning", StringComparison.OrdinalIgnoreCase))
    && outcomes.Count > 0
    && outcomes.All(outcome => outcome.IsTranslated)
    && secondPass.All(outcome => outcome.Origin is TranslationOrigin.Cache or TranslationOrigin.Glossary);

Console.WriteLine(ok ? "\nSMOKE TEST: OK ✔" : "\nSMOKE TEST: PROBLEM ✘");

Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
File.Delete(databasePath);
return ok ? 0 : 1;
