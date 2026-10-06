using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal sealed class ReplayWindow
{
    private readonly Window _window;
    private readonly Image _image;
    private readonly Thread _thread;
    private readonly Func<double> _clock;
    private readonly Action<int, double> _rendered;
    private int _pendingFrame = -1;

    private ReplayWindow(Window window, Image image, Thread thread, Func<double> clock, Action<int, double> rendered)
    {
        _window = window;
        _image = image;
        _thread = thread;
        _clock = clock;
        _rendered = rendered;
        Handle = new WindowInteropHelper(window).Handle;
        DpiScale = VisualTreeHelper.GetDpi(window).DpiScaleX;
    }

    public IntPtr Handle { get; }

    public double DpiScale { get; }

    public Dispatcher Dispatcher => _window.Dispatcher;

    public static Task<ReplayWindow> CreateAsync(int pixelWidth, int pixelHeight, Func<double> clock, Action<int, double> rendered)
    {
        var ready = new TaskCompletionSource<ReplayWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? thread = null;
        thread = new Thread(() =>
        {
            try
            {
                var image = new Image
                {
                    Stretch = Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    SnapsToDevicePixels = true,
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                var window = new Window
                {
                    Title = "GTO MotionLab — odtwarzanie",
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    Topmost = true,
                    Left = 0,
                    Top = 0,
                    Width = 200,
                    Height = 200,
                    Background = Brushes.Black,
                    UseLayoutRounding = true,
                    SnapsToDevicePixels = true,
                    Content = image,
                };
                window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                window.Show();
                var dpi = VisualTreeHelper.GetDpi(window);
                window.Left = 0;
                window.Top = 0;
                window.Width = pixelWidth / dpi.DpiScaleX;
                window.Height = pixelHeight / dpi.DpiScaleY;
                image.Width = window.Width;
                image.Height = window.Height;
                var replay = new ReplayWindow(window, image, thread!, clock, rendered);
                CompositionTarget.Rendering += replay.OnRendering;
                ready.TrySetResult(replay);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "replay-window",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var pending = _pendingFrame;
        if (pending < 0) return;
        _pendingFrame = -1;
        _rendered(pending, _clock());
    }

    public void SetFrame(BitmapSource frame, int index)
    {
        _image.Source = frame;
        _pendingFrame = index;
    }

    public Task ShowAsync(BitmapSource frame, int index)
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            _image.Source = frame;
            EventHandler? handler = null;
            var ticks = 0;
            handler = (_, _) =>
            {
                if (++ticks < 3) return;
                CompositionTarget.Rendering -= handler;
                shown.TrySetResult();
            };
            CompositionTarget.Rendering += handler;
            _pendingFrame = index;
        });
        return shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async Task CloseAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            CompositionTarget.Rendering -= OnRendering;
            _window.Close();
        }).Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Okno odtwarzania nie zamknęło się.");
    }
}
