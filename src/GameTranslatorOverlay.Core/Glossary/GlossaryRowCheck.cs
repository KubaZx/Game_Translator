namespace GameTranslatorOverlay.Core.Glossary;

public sealed record GlossaryRowProblem(int Index, string Message);

public static class GlossaryRowCheck
{
    public static GlossaryRowProblem? FirstProblem(IEnumerable<(string? Source, string? Target)> rows)
    {
        var index = 0;
        foreach (var (source, target) in rows)
        {
            var hasSource = !string.IsNullOrWhiteSpace(source);
            var hasTarget = !string.IsNullOrWhiteSpace(target);
            if (hasSource && !hasTarget)
            {
                return new GlossaryRowProblem(index, $"Termin „{source!.Trim()}” nie ma tłumaczenia — uzupełnij je albo usuń wiersz.");
            }
            if (!hasSource && hasTarget)
            {
                return new GlossaryRowProblem(index, $"Tłumaczenie „{target!.Trim()}” nie ma terminu — uzupełnij go albo usuń wiersz.");
            }
            index++;
        }
        return null;
    }
}
