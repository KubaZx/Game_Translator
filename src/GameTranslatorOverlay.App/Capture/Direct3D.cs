using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GameTranslatorOverlay.App.Capture;

[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public int Format;
    public uint SampleCount;
    public uint SampleQuality;
    public int Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MappedSubresource
{
    public IntPtr Data;
    public uint RowPitch;
    public uint DepthPitch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TextureBox
{
    public uint Left;
    public uint Top;
    public uint Front;
    public uint Right;
    public uint Bottom;
    public uint Back;
}

[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

internal static unsafe class Direct3D
{
    public const int FormatBgra = 87;
    private const int UsageDefault = 0;
    private const int UsageStaging = 3;
    private const uint CpuRead = 0x20000;

    public static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid MultithreadId = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    private static readonly Guid CaptureSession3Id = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint count,
        uint sdkVersion, out IntPtr device, out int level, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static IntPtr* Table(IntPtr instance) => *(IntPtr**)instance;

    public static (IntPtr Device, IntPtr Context, IDirect3DDevice Device3D) CreateDevice()
    {
        Check(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out var device, out _, out var context));
        try
        {
            if (Query(device, MultithreadId) is var multithread && multithread != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Table(multithread)[5])(multithread, 1);
                Release(multithread);
            }
            var dxgi = Query(device, DxgiDeviceId);
            if (dxgi == IntPtr.Zero) throw new InvalidOperationException("Brak IDXGIDevice.");
            try
            {
                Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try
                {
                    return (device, context, MarshalInterface<IDirect3DDevice>.FromAbi(inspectable));
                }
                finally
                {
                    Release(inspectable);
                }
            }
            finally
            {
                Release(dxgi);
            }
        }
        catch
        {
            Release(context);
            Release(device);
            throw;
        }
    }

    public static bool DisableBorder(object session)
    {
        var native = MarshalInspectable<object>.FromManaged(session);
        try
        {
            var session3 = Query(native, CaptureSession3Id);
            if (session3 == IntPtr.Zero) return false;
            try
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Table(session3)[7])(session3, 0) >= 0;
            }
            finally
            {
                Release(session3);
            }
        }
        finally
        {
            Release(native);
        }
    }

    public static IntPtr Query(IntPtr instance, Guid id)
    {
        IntPtr result;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Table(instance)[0])(instance, &id, &result) < 0 ? IntPtr.Zero : result;
    }

    public static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Table(instance)[2])(instance);
    }

    public static Texture2DDesc Describe(IntPtr texture)
    {
        Texture2DDesc desc;
        ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Table(texture)[10])(texture, &desc);
        return desc;
    }

    public static IntPtr CreateTexture(IntPtr device, uint width, uint height, bool staging)
    {
        var desc = new Texture2DDesc
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = FormatBgra,
            SampleCount = 1,
            Usage = staging ? UsageStaging : UsageDefault,
            CpuAccessFlags = staging ? CpuRead : 0,
        };
        IntPtr texture;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Table(device)[5])(device, &desc, IntPtr.Zero, &texture));
        return texture;
    }

    public static void CopyRegion(IntPtr context, IntPtr target, IntPtr source, TextureBox box) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, TextureBox*, void>)Table(context)[46])(
            context, target, 0, 0, 0, 0, source, 0, &box);

    public static MappedSubresource Map(IntPtr context, IntPtr resource)
    {
        MappedSubresource mapped;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSubresource*, int>)Table(context)[14])(
            context, resource, 0, 1, 0, &mapped));
        return mapped;
    }

    public static void Unmap(IntPtr context, IntPtr resource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Table(context)[15])(context, resource, 0);

    private static void Check(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }
}
