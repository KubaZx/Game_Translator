using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Corpus;

public static class CorpusTranslationKey
{
    public static string DisplayText(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : CorpusText.StripRichText(CorpusText.UnescapeLineBreaks(text));

    public static string Normalize(string text) => TextNormalizer.Normalize(DisplayText(text));

    public static string For(CorpusEntry entry) => Normalize(entry.En);
}
