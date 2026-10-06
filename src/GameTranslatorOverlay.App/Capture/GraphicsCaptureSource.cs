using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using GameTranslatorOverlay.Core.Ocr;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GameTranslatorOverlay.App.Capture;

public sealed class GraphicsCaptureSource : IDisposable
{
    private readonly Lock _gate = new();
    private readonly IntPtr _device;
    private readonly IntPtr _context;
    private readonly IDirect3DDevice _device3D;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private IntPtr _latest;
    private uint _latestWidth;
    private uint _latestHeight;
    private int _contentWidth;
    private int _contentHeight;
    private IntPtr _fullStaging;
    private uint _fullStagingWidth;
    private uint _fullStagingHeight;
    private IntPtr _regionStaging;
    private uint _regionStagingWidth;
    private uint _regionStagingHeight;
    private long _frames;
    private long _lastFrameTimestamp;
    private volatile bool _closed;
    private TaskCompletionSource _nextFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    private GraphicsCaptureSource(IntPtr device, IntPtr context, IDirect3DDevice device3D, GraphicsCaptureItem item)
    {
        _device = device;
        _context = context;
        _device3D = device3D;
        _item = item;
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device3D, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _session = _pool.CreateCaptureSession(item);
        BorderDisabled = Direct3D.DisableBorder(_session);
        try { _session.IsCursorCaptureEnabled = false; } catch (Exception) { }
        _item.Closed += OnClosed;
        _pool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    public bool BorderDisabled { get; }

    public bool IsClosed => _closed;

    public long FrameCount => Interlocked.Read(ref _frames);

    public Task NextFrame => Volatile.Read(ref _nextFrame).Task;

    public TimeSpan SinceLastFrame
    {
        get
        {
            var last = Interlocked.Read(ref _lastFrameTimestamp);
            return last == 0 ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(last);
        }
    }

    public (int Width, int Height) ContentSize
    {
        get
        {
            lock (_gate) return (_contentWidth, _contentHeight);
        }
    }

    public static GraphicsCaptureSource? TryStart(IntPtr window, out string? failure)
    {
        failure = null;
        if (window == IntPtr.Zero)
        {
            failure = "brak okna";
            return null;
        }
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
            {
                failure = "system nie wspiera Windows Graphics Capture";
                return null;
            }
            var created = Direct3D.CreateDevice();
            device = created.Device;
            context = created.Context;
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var itemId = Direct3D.CaptureItemId;
            var pointer = interop.CreateForWindow(window, ref itemId);
            GraphicsCaptureItem item;
            try
            {
                item = GraphicsCaptureItem.FromAbi(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }
            return new GraphicsCaptureSource(device, context, created.Device3D, item);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            Direct3D.Release(context);
            Direct3D.Release(device);
            return null;
        }
    }

    public Bitmap? CaptureFull(int expectedWidth, int expectedHeight)
    {
        lock (_gate)
        {
            if (!CanRead(expectedWidth, expectedHeight)) return null;
            var width = (uint)_contentWidth;
            var height = (uint)_contentHeight;
            EnsureStaging(ref _fullStaging, ref _fullStagingWidth, ref _fullStagingHeight, width, height);
            Direct3D.CopyRegion(_context, _fullStaging, _latest, new TextureBox { Right = width, Bottom = height, Back = 1 });
            var mapped = Direct3D.Map(_context, _fullStaging);
            Bitmap? bitmap = null;
            try
            {
                bitmap = new Bitmap((int)width, (int)height, PixelFormat.Format32bppArgb);
                var data = bitmap.LockBits(new Rectangle(0, 0, (int)width, (int)height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    CopyRows(mapped, data.Scan0, data.Stride, (int)width, (int)height);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                return bitmap;
            }
            catch
            {
                bitmap?.Dispose();
                throw;
            }
            finally
            {
                Direct3D.Unmap(_context, _fullStaging);
            }
        }
    }

    public OcrBitmap? CopyRegion(RectPx rect, int expectedWidth, int expectedHeight)
    {
        lock (_gate)
        {
            if (!CanRead(expectedWidth, expectedHeight)) return null;
            rect = rect.Intersect(new RectPx(0, 0, _contentWidth, _contentHeight));
            if (rect.IsEmpty) return null;
            EnsureStaging(ref _regionStaging, ref _regionStagingWidth, ref _regionStagingHeight, (uint)rect.Width, (uint)rect.Height);
            Direct3D.CopyRegion(_context, _regionStaging, _latest, new TextureBox
            {
                Left = (uint)rect.X,
                Top = (uint)rect.Y,
                Right = (uint)rect.Right,
                Bottom = (uint)rect.Bottom,
                Back = 1,
            });
            var mapped = Direct3D.Map(_context, _regionStaging);
            try
            {
                var stride = rect.Width * 4;
                var pixels = new byte[stride * rect.Height];
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    CopyRows(mapped, handle.AddrOfPinnedObject(), stride, rect.Width, rect.Height);
                }
                finally
                {
                    handle.Free();
                }
                return new OcrBitmap(pixels, rect.Width, rect.Height, stride);
            }
            finally
            {
                Direct3D.Unmap(_context, _regionStaging);
            }
        }
    }

    private bool CanRead(int expectedWidth, int expectedHeight) =>
        !_disposed && !_closed && _latest != IntPtr.Zero && _contentWidth == expectedWidth && _contentHeight == expectedHeight
        && expectedWidth > 0 && expectedHeight > 0;

    private void EnsureStaging(ref IntPtr staging, ref uint stagingWidth, ref uint stagingHeight, uint width, uint height)
    {
        if (staging != IntPtr.Zero && stagingWidth >= width && stagingHeight >= height) return;
        Direct3D.Release(staging);
        stagingWidth = Math.Max(width, stagingWidth);
        stagingHeight = Math.Max(height, stagingHeight);
        staging = Direct3D.CreateTexture(_device, stagingWidth, stagingHeight, staging: true);
    }

    private static unsafe void CopyRows(MappedSubresource mapped, IntPtr target, int targetStride, int width, int height)
    {
        var opaque = new Vector<uint>(0xFF000000u);
        for (var y = 0; y < height; y++)
        {
            var source = new ReadOnlySpan<uint>((byte*)mapped.Data + (long)y * mapped.RowPitch, width);
            var row = new Span<uint>((byte*)target + (long)y * targetStride, width);
            var x = 0;
            if (Vector.IsHardwareAccelerated)
            {
                for (; x <= width - Vector<uint>.Count; x += Vector<uint>.Count)
                    (new Vector<uint>(source[x..]) | opaque).CopyTo(row[x..]);
            }
            for (; x < width; x++) row[x] = source[x] | 0xFF000000u;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        var content = frame.ContentSize;
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var textureId = Direct3D.Texture2DId;
        var texture = access.GetInterface(ref textureId);
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                var desc = Direct3D.Describe(texture);
                if (_latest == IntPtr.Zero || _latestWidth != desc.Width || _latestHeight != desc.Height)
                {
                    Direct3D.Release(_latest);
                    _latest = Direct3D.CreateTexture(_device, desc.Width, desc.Height, staging: false);
                    _latestWidth = desc.Width;
                    _latestHeight = desc.Height;
                }
                var width = (uint)Math.Clamp(content.Width, 0, (int)desc.Width);
                var height = (uint)Math.Clamp(content.Height, 0, (int)desc.Height);
                if (width == 0 || height == 0) return;
                Direct3D.CopyRegion(_context, _latest, texture, new TextureBox { Right = width, Bottom = height, Back = 1 });
                _contentWidth = (int)width;
                _contentHeight = (int)height;
                Interlocked.Increment(ref _frames);
                Interlocked.Exchange(ref _lastFrameTimestamp, Stopwatch.GetTimestamp());
            }
            Interlocked.Exchange(ref _nextFrame, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
        finally
        {
            Direct3D.Release(texture);
        }
        if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _poolSize = content;
                _pool.Recreate(_device3D, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
            }
        }
    }

    private void OnClosed(GraphicsCaptureItem sender, object args)
    {
        _closed = true;
        Volatile.Read(ref _nextFrame).TrySetResult();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _pool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnClosed;
        try { _session.Dispose(); } catch (Exception) { }
        try { _pool.Dispose(); } catch (Exception) { }
        lock (_gate)
        {
            Direct3D.Release(_latest);
            Direct3D.Release(_fullStaging);
            Direct3D.Release(_regionStaging);
            _latest = _fullStaging = _regionStaging = IntPtr.Zero;
            Direct3D.Release(_context);
            Direct3D.Release(_device);
        }
        (_device3D as IDisposable)?.Dispose();
    }
}
