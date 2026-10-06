using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslatorOverlay.CorpusTool.Parsing;

public sealed class NamePattern
{
    private readonly Regex _regex;

    public NamePattern(string glob)
    {
        Glob = glob;
        var builder = new StringBuilder("^");
        foreach (var ch in glob)
        {
            builder.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }
        builder.Append('$');
        _regex = new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    public string Glob { get; }

    public bool IsMatch(string name) => _regex.IsMatch(name);

    public static bool Matches(string name, IReadOnlyList<NamePattern> include, IReadOnlyList<NamePattern> exclude) =>
        include.Any(p => p.IsMatch(name)) && !exclude.Any(p => p.IsMatch(name));
}
