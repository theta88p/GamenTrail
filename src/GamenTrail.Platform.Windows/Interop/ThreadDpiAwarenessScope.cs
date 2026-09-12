using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GamenTrail.Platform.Windows.Interop;

internal readonly partial struct ThreadDpiAwarenessScope : IDisposable
{
    private static readonly nint PerMonitorAwareV2 = new(-4);
    private readonly nint _previousContext;

    public ThreadDpiAwarenessScope()
    {
        _previousContext = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        if (_previousContext == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose() => _ = SetThreadDpiAwarenessContext(_previousContext);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetThreadDpiAwarenessContext(nint context);
}
