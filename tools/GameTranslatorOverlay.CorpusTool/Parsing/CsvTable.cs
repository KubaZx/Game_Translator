using System.Text;

namespace GameTranslatorOverlay.CorpusTool.Parsing;

public sealed class CsvTable
{
    private CsvTable(IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Header = header;
        Rows = rows;
    }

    public IReadOnlyList<string> Header { get; }
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public int ColumnIndex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        for (var i = 0; i < Header.Count; i++)
        {
            if (Header[i].Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    public static string Cell(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : string.Empty;

    public static CsvTable Parse(string text)
    {
        var records = ParseRecords(text);
        if (records.Count == 0) return new CsvTable([], []);
        return new CsvTable(records[0], records.Skip(1).ToList());
    }

    public static List<IReadOnlyList<string>> ParseRecords(string text)
    {
        var records = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        var i = 0;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRecord()
        {
            EndField();
            if (!(fields.Count == 1 && fields[0].Length == 0)) records.Add(fields.ToArray());
            fields.Clear();
        }

        while (i < text.Length)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(ch);
                i++;
                continue;
            }

            switch (ch)
            {
                case '"' when !fieldStarted && field.Length == 0:
                    inQuotes = true;
                    fieldStarted = true;
                    i++;
                    break;
                case ',':
                    EndField();
                    i++;
                    break;
                case '\r':
                    EndRecord();
                    i += i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                    break;
                case '\n':
                    EndRecord();
                    i++;
                    break;
                default:
                    field.Append(ch);
                    fieldStarted = true;
                    i++;
                    break;
            }
        }
        if (field.Length > 0 || fields.Count > 0 || fieldStarted) EndRecord();
        return records;
    }
}
