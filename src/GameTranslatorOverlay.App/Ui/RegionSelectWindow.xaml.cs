using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.App.Ui;

/// <summary>
/// Pełnoekranowe okno zaznaczania regionu na monitorze, na którym stoi kursor.
/// Zwraca prostokąt w fizycznych pikselach ekranu albo null przy anulowaniu.
/// </summary>
public partial class RegionSelectWindow : Window
{
    private readonly MonitorArea _monitor;
    private readonly TaskCompletionSource<RectPx?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativeMethods.POINT _dragStart;
    private bool _dragging;

    public static Task<RectPx?> SelectAsync()
    {
        var window = new RegionSelectWindow();
        window.Show();
        window.Activate();
        return window._completion.Task;
    }

    private RegionSelectWindow()
    {
        InitializeComponent();
        _monitor = Displays.FromCursor();
        Loaded += OnLoadedHandler;
        Closed += (_, _) => _completion.TrySetResult(null);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            _monitor.Bounds.X, _monitor.Bounds.Y, _monitor.Bounds.Width, _monitor.Bounds.Height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    private void OnLoadedHandler(object sender, RoutedEventArgs e)
    {
        Keyboard.Focus(this);
        HintBorder.UpdateLayout();
        Canvas_SetCenteredHint();
    }

    private void Canvas_SetCenteredHint()
    {
        System.Windows.Controls.Canvas.SetLeft(HintBorder, Math.Max(0, (ActualWidth - HintBorder.ActualWidth) / 2));
        System.Windows.Controls.Canvas.SetTop(HintBorder, 28);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        NativeMethods.GetCursorPos(out _dragStart);
        _dragging = true;
        HintBorder.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Visible;
        CaptureMouse();
        UpdateSelectionVisual(_dragStart);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        NativeMethods.GetCursorPos(out var current);
        UpdateSelectionVisual(current);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        NativeMethods.GetCursorPos(out var current);
        var region = NormalizedRect(_dragStart, current);

        if (region.Width >= 5 && region.Height >= 5)
        {
            _completion.TrySetResult(region);
        }
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void UpdateSelectionVisual(NativeMethods.POINT current)
    {
        var rect = NormalizedRect(_dragStart, current);
        var scale = _monitor.Scale;

        System.Windows.Controls.Canvas.SetLeft(SelectionBorder, (rect.X - _monitor.Bounds.X) / scale);
        System.Windows.Controls.Canvas.SetTop(SelectionBorder, (rect.Y - _monitor.Bounds.Y) / scale);
        SelectionBorder.Width = rect.Width / scale;
        SelectionBorder.Height = rect.Height / scale;
    }

    private static RectPx NormalizedRect(NativeMethods.POINT a, NativeMethods.POINT b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new RectPx(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
