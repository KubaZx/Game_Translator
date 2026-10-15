using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed class PreviewReport(string folder)
{
    private readonly StringBuilder _text = new();
    private readonly List<string> _files = [];
    private readonly SortedSet<int> _iconGlyphs = [];

    public string Folder { get; } = folder;
    public int FileCount => _files.Count;
    public int ClippedCount { get; private set; }

    public void Environment(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var families = Fonts.SystemFontFamilies.Select(static f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Line($"System: {System.Environment.OSVersion.VersionString}");
        Line($"DPI okna: {dpi.PixelsPerInchX:0} ({dpi.DpiScaleX:P0}); ekran główny {SystemParameters.PrimaryScreenWidth:0}×{SystemParameters.PrimaryScreenHeight:0} DIP; obszar roboczy {SystemParameters.WorkArea}");
        foreach (var family in new[] { "Segoe UI Variable Text", "Segoe UI Variable Display", "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            Line($"Czcionka „{family}”: {(families.Contains(family) ? "jest" : "brak")}");
        Line($"Okno: {window.Width:0}×{window.Height:0} (min {window.MinWidth:0}×{window.MinHeight:0})");
        Line(string.Empty);
    }

    public void CheckClipping(Window window, string name)
    {
        window.UpdateLayout();
        var findings = ClipChecker.Find(window);
        var clipped = findings.Count(static f => f.Clipped);
        ClippedCount += clipped;
        Line($"[{name}] ucięte: {clipped}, skrócone wielokropkiem: {findings.Count - clipped}");
        foreach (var finding in findings) Line("  " + finding.Text);
        foreach (var block in Renderer.VisualDescendants<System.Windows.Controls.TextBlock>(window))
        {
            if (!block.FontFamily.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase)
                && !block.FontFamily.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var character in block.Text) _iconGlyphs.Add(character);
        }
    }

    public void CheckHitTesting(Window window, string name)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var checkedCount = 0;
        var blocked = new List<string>();
        foreach (var control in Renderer.VisualDescendants<System.Windows.Controls.Control>(root))
        {
            if (control is not (System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.ListBoxItem
                or System.Windows.Controls.ComboBox or System.Windows.Controls.TextBox or System.Windows.Controls.PasswordBox)) continue;
            if (!control.IsVisible || !control.IsEnabled || !control.IsHitTestVisible || control.ActualWidth < 4) continue;
            if (control.TemplatedParent is System.Windows.Controls.ComboBox or System.Windows.Controls.Primitives.ScrollBar) continue;
            var center = control.TransformToAncestor(root).Transform(new Point(control.ActualWidth / 2, control.ActualHeight / 2));
            if (center.X < 0 || center.Y < 0 || center.X > root.ActualWidth || center.Y > root.ActualHeight) continue;
            if (!InsideViewport(control, center, root)) continue;
            checkedCount++;
            var hit = root.InputHitTest(center) as DependencyObject;
            var current = hit;
            while (current is not null && !ReferenceEquals(current, control)) current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            if (current is null)
                blocked.Add($"{control.GetType().Name} {control.Name} → trafienie w {hit?.GetType().Name} {(hit as FrameworkElement)?.Name}");
        }
        Line($"[{name}] klikalność: sprawdzono {checkedCount}, zasłonięte {blocked.Count}");
        foreach (var item in blocked) Line("  " + item);
        BlockedCount += blocked.Count;
    }

    public int BlockedCount { get; private set; }

    private static bool InsideViewport(DependencyObject element, Point center, FrameworkElement root)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not System.Windows.Controls.ScrollContentPresenter presenter) continue;
            var topLeft = presenter.TransformToAncestor(root).Transform(new Point(0, 0));
            if (center.X < topLeft.X || center.Y < topLeft.Y || center.X > topLeft.X + presenter.ActualWidth || center.Y > topLeft.Y + presenter.ActualHeight)
                return false;
        }
        return true;
    }

    public void Save(BitmapSource bitmap, string fileName)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(Folder, fileName))) encoder.Save(stream);
        Saved(fileName);
    }

    public void Saved(string fileName) => _files.Add(fileName);

    public void Line(string text) => _text.AppendLine(text);

    public void Finish()
    {
        Line(string.Empty);
        if (_iconGlyphs.Count > 0)
        {
            var missing = new List<string>();
            if (new Typeface("Segoe MDL2 Assets").TryGetGlyphTypeface(out var mdl2))
                missing.AddRange(_iconGlyphs.Where(c => !mdl2.CharacterToGlyphMap.ContainsKey(c)).Select(static c => $"U+{c:X4}"));
            Line($"Glify ikon użyte w oknie: {_iconGlyphs.Count}; brak w Segoe MDL2 Assets (zapas dla Windows 10): " +
                 (missing.Count == 0 ? "0" : string.Join(", ", missing)));
        }
        Line($"Pliki ({_files.Count}):");
        foreach (var file in _files) Line("  " + file);
        File.WriteAllText(Path.Combine(Folder, "raport.txt"), _text.ToString(), new UTF8Encoding(false));
    }
}
