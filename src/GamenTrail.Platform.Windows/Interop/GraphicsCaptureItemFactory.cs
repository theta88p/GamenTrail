using System.Runtime.InteropServices;
using GamenTrail.Core.Video;
using Windows.Graphics.Capture;
using WinRT;

namespace GamenTrail.Platform.Windows.Interop;

internal static unsafe class GraphicsCaptureItemFactory
{
    private const string RuntimeClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private static readonly Guid GraphicsCaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem Create(CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var factory = ActivationFactory.Get(RuntimeClassName, GraphicsCaptureItemInteropId);
        nint result = 0;
        var itemId = GraphicsCaptureItemId;
        var vtable = *(nint**)factory.ThisPtr;

        var methodIndex = target switch
        {
            CaptureTarget.Window => 3,
            CaptureTarget.Monitor => 4,
            CaptureTarget.Region => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        var handle = target switch
        {
            CaptureTarget.Window window => window.Handle,
            CaptureTarget.Monitor monitor => monitor.Handle,
            CaptureTarget.Region region => region.MonitorHandle,
            _ => 0,
        };

        if (handle == 0)
        {
            throw new ArgumentException("The capture target handle must not be zero.", nameof(target));
        }

        var createItem = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[methodIndex];
        var hresult = createItem(factory.ThisPtr, handle, &itemId, &result);
        if (hresult < 0)
        {
            throw new InvalidOperationException(
                "The selected capture target is no longer available. Refresh the target list and select it again.",
                Marshal.GetExceptionForHR(hresult));
        }

        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
        }
        finally
        {
            if (result != 0)
            {
                Marshal.Release(result);
            }
        }
    }
}
