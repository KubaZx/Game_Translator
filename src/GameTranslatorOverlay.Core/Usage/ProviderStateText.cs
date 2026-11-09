using System.Globalization;
using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Usage;

public static class ProviderStateText
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    public static string? Describe(ProviderPauseStatus pause, SessionLimitStatus limit, DateTimeOffset now)
    {
        var lines = new List<string>(2);
        if (pause.IsPaused) lines.Add(DescribePause(pause, now));
        if (DescribeLimit(limit) is { } limitLine) lines.Add(limitLine);
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    public static string DescribePause(ProviderPauseStatus pause, DateTimeOffset now)
    {
        var reason = pause.ToException().UserFriendlyMessage;
        if (pause.Kind == ProviderPauseKind.Temporary)
        {
            var seconds = pause.ResumeAt is { } resumeAt ? Math.Max(1, (int)Math.Ceiling((resumeAt - now).TotalSeconds)) : 1;
            return $"Dostawca chwilowo wstrzymany (jeszcze około {seconds} s): {reason}";
        }
        return $"Dostawca wstrzymany, żeby nie wysyłać tekstu z ekranu bez sensu. {reason} " +
               "Tłumaczenie ruszy po zapisaniu klucza, zmianie ustawień, udanym teście połączenia albo ponownym włączeniu trybu live.";
    }

    public static string? DescribeLimit(SessionLimitStatus limit) => limit.Level switch
    {
        SessionLimitLevel.Near =>
            $"Zużyto {limit.Percent}% limitu znaków sesji ({limit.Used.ToString("N0", Polish)} z {limit.Limit!.Value.ToString("N0", Polish)}). " +
            "Zmienisz go w polu „Limit znaków na sesję” (sekcja Tłumaczenie).",
        SessionLimitLevel.Exhausted =>
            "Limit znaków sesji wyczerpany. Zwiększ go w polu „Limit znaków na sesję” (sekcja Tłumaczenie) albo uruchom aplikację ponownie.",
        _ => null,
    };
}
