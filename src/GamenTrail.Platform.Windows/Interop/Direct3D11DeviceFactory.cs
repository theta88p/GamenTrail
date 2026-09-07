using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GamenTrail.Platform.Windows.Interop;

internal static partial class Direct3D11DeviceFactory
{
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private static readonly Guid IdxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static IDirect3DDevice Create()
    {
        var result = CreateNativeDevice(D3DDriverType.Hardware, out var device, out var context);
        if (result < 0)
        {
            ReleaseIfPresent(device);
            ReleaseIfPresent(context);
            result = CreateNativeDevice(D3DDriverType.Warp, out device, out context);
        }

        Marshal.ThrowExceptionForHR(result);

        nint dxgiDevice = 0;
        nint inspectableDevice = 0;
        try
        {
            var interfaceId = IdxgiDeviceId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in interfaceId, out dxgiDevice));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDxgiDevice(dxgiDevice, out inspectableDevice));
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectableDevice);
        }
        finally
        {
            ReleaseIfPresent(inspectableDevice);
            ReleaseIfPresent(dxgiDevice);
            ReleaseIfPresent(context);
            ReleaseIfPresent(device);
        }
    }

    private static int CreateNativeDevice(
        D3DDriverType driverType,
        out nint device,
        out nint context) =>
        D3D11CreateDevice(
            0,
            driverType,
            0,
            D3D11CreateDeviceBgraSupport,
            0,
            0,
            D3D11SdkVersion,
            out device,
            out _,
            out context);

    private static void ReleaseIfPresent(nint instance)
    {
        if (instance != 0)
        {
            Marshal.Release(instance);
        }
    }

    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static partial int D3D11CreateDevice(
        nint adapter,
        D3DDriverType driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out uint selectedFeatureLevel,
        out nint immediateContext);

    [LibraryImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static partial int CreateDirect3D11DeviceFromDxgiDevice(
        nint dxgiDevice,
        out nint graphicsDevice);

    private enum D3DDriverType : uint
    {
        Hardware = 1,
        Warp = 5,
    }
}
