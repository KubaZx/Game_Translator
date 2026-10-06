using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Settings;

internal sealed record OverlayLayer(int Id, BitmapSource Bitmap, Box Box);

internal sealed class OverlayHost
{
    private static readonly FieldInfo? LiveElementsField =
        typeof(OverlayWindow).GetField("_liveElements", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly OverlayWindow _overlay;
    private readonly AppSettings _settings;
    private readonly TranslationOrchestrator _orchestrator;
    private readonly OverlayNoticeEcho _echo;
    private readonly Func<RectPx> _windowBounds;
    private readonly Grid _root;
    private readonly Canvas _live;
    private readonly Canvas _notices;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private bool _dirty = true;
    private long _signature;
    private int _nextLayerId;

    public OverlayHost(AppSettings settings, TranslationOrchestrator orchestrator, OverlayNoticeEcho echo,
        Func<RectPx> windowBounds, bool visibleOnScreen, int pixelWidth, int pixelHeight)
    {
        _settings = settings;
        _orchestrator = orchestrator;
        _echo = echo;
        _windowBounds = windowBounds;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _overlay = new OverlayWindow();
        if (!visibleOnScreen) _overlay.Opacity = 0;
        _root = (Grid)_overlay.Content;
        _live = (Canvas)_root.Children[0];
        _notices = (Canvas)_root.Children[1];
    }

    public OverlayWindow Window => _overlay;

    public bool ElementsReflection => LiveElementsField is not null;

    public OverlayLayer? CurrentLayer { get; private set; }

    public int LastSeq { get; set; }

    public bool Stopped { get; private set; }

    public double Scale { get; set; } = 1.0;

    public void Apply(LiveUpdate update)
    {
        if (update.ClearOverlay)
        {
            _overlay.ClearBlocks(preserveUserHidden: true);
        }
        if (update.ClearSubtitle) _overlay.ClearSubtitle();
        if (update.HideOverlay)
        {
            _overlay.Hide();
        }
        if (update.Notice is { } notice)
        {
            ShowNotice(notice, update.WindowBounds.IsEmpty ? _windowBounds() : update.WindowBounds);
        }
        if (update.ClearOverlay || update.ClearSubtitle || update.HideOverlay || update.Notice is not null || update.Blocks is not null)
            _dirty = true;
        if (update.Stopped)
        {
            Stopped = true;
            return;
        }

        if (update.Blocks is { } blocks)
        {
            if (_settings.LiveDisplayMode == "subtitle")
            {
                if (update.SubtitleText is { Length: > 0 } subtitle)
                {
                    _overlay.ShowSubtitle(subtitle, update.WindowBounds, _settings, update.PreserveSubtitleLifetime);
                }
                else
                {
                    _overlay.RepositionSubtitle(update.WindowBounds);
                }
            }
            else
            {
                _overlay.ProfileFontFamily = _orchestrator.ActiveProfile?.Overlay?.FontFamily;
                _overlay.UpdateLiveBlocks(blocks, _settings);
            }
        }
    }

    private void ShowNotice(OverlayNotice notice, RectPx anchor)
    {
        if (!_settings.ShowOverlayNotices) return;
        _echo.Remember(notice.Text);
        _overlay.ShowNotice(notice, anchor);
    }

    public (bool Visible, OverlayLayer? Layer, IReadOnlyList<ElementDto> Elements, bool Rendered) Snapshot(Action<OverlayLayer> saveLayer)
    {
        _root.UpdateLayout();
        var signature = ComputeSignature();
        var rendered = false;
        if (_dirty || signature != _signature)
        {
            CurrentLayer = RenderLayer();
            if (CurrentLayer is not null) saveLayer(CurrentLayer);
            _signature = signature;
            _dirty = false;
            rendered = true;
        }
        var visible = _overlay.IsVisible;
        return (visible, visible ? CurrentLayer : null, ReadElements(), rendered);
    }

    private IEnumerable<FrameworkElement> VisibleChildren()
    {
        if (_live.Visibility == Visibility.Visible)
            foreach (var child in _live.Children.OfType<FrameworkElement>())
                if (child.Visibility == Visibility.Visible) yield return child;
        if (_notices.Visibility == Visibility.Visible)
            foreach (var child in _notices.Children.OfType<FrameworkElement>())
                if (child.Visibility == Visibility.Visible) yield return child;
    }

    private long ComputeSignature()
    {
        var hash = new HashCode();
        hash.Add(_live.Visibility);
        hash.Add(_notices.Visibility);
        foreach (var child in VisibleChildren())
        {
            hash.Add(RuntimeHelpers.GetHashCode(child));
            hash.Add(Math.Round(Canvas.GetLeft(child), 2));
            hash.Add(Math.Round(Canvas.GetTop(child), 2));
            hash.Add(Math.Round(child.Opacity, 3));
            hash.Add(Math.Round(child.ActualWidth, 2));
            hash.Add(Math.Round(child.ActualHeight, 2));
            if (child is Border border)
            {
                hash.Add(OverlayBlockRenderer.GetText(border));
                if (border.Child is GameTextElement native && native.Tag is not null) hash.Add(RuntimeHelpers.GetHashCode(native.Tag));
            }
            if (child is Border { Child: TextBlock notice }) hash.Add(notice.Text);
        }
        return hash.ToHashCode();
    }

    private Rect BoundsOf(FrameworkElement child)
    {
        var local = VisualTreeHelper.GetDescendantBounds(child);
        if (local.IsEmpty) local = new Rect(0, 0, child.ActualWidth, child.ActualHeight);
        return child.TransformToAncestor(_root).TransformBounds(local);
    }

    private OverlayLayer? RenderLayer()
    {
        var union = Rect.Empty;
        foreach (var child in VisibleChildren())
        {
            if (child.Opacity <= 0.001) continue;
            var bounds = BoundsOf(child);
            if (!bounds.IsEmpty) union.Union(bounds);
        }
        if (union.IsEmpty) return null;
        var x0 = Math.Clamp((int)Math.Floor(union.X * Scale) - 2, 0, _pixelWidth);
        var y0 = Math.Clamp((int)Math.Floor(union.Y * Scale) - 2, 0, _pixelHeight);
        var x1 = Math.Clamp((int)Math.Ceiling(union.Right * Scale) + 2, 0, _pixelWidth);
        var y1 = Math.Clamp((int)Math.Ceiling(union.Bottom * Scale) + 2, 0, _pixelHeight);
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;
        var box = new Box(x0, y0, x1 - x0, y1 - y0);
        var view = new Rect(box.X / Scale, box.Y / Scale, box.W / Scale, box.H / Scale);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var brush = new VisualBrush(_root)
            {
                Viewbox = view,
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, view.Width, view.Height));
        }
        var bitmap = new RenderTargetBitmap(box.W, box.H, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return new OverlayLayer(++_nextLayerId, bitmap, box);
    }

    private IReadOnlyList<ElementDto> ReadElements()
    {
        if (LiveElementsField?.GetValue(_overlay) is not Dictionary<string, Border> elements) return [];
        var result = new List<ElementDto>(elements.Count);
        foreach (var (key, element) in elements)
        {
            var bounds = element.IsVisible || _live.IsAncestorOf(element) ? BoundsOf(element) : Rect.Empty;
            var box = bounds.IsEmpty
                ? default
                : new Box((int)Math.Floor(bounds.X * Scale), (int)Math.Floor(bounds.Y * Scale),
                    (int)Math.Ceiling(bounds.Width * Scale), (int)Math.Ceiling(bounds.Height * Scale));
            result.Add(new ElementDto(key, box, Math.Round(element.Opacity, 3), RuntimeHelpers.GetHashCode(element),
                OverlayBlockRenderer.IsNative(element)));
        }
        return result;
    }

    public void Close()
    {
        _overlay.ClearBlocks();
        _overlay.Close();
    }
}
