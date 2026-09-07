using System.Runtime.InteropServices;

namespace GamenTrail.Platform.Windows.Interop;

internal sealed partial class WindowCornerPreferenceScope : IDisposable
{
    private const uint DwmWindowAttributeWindowCornerPreference = 33;
    private const uint CornerPreferenceSize = sizeof(int);
    private readonly nint _window;
    private readonly DwmWindowCornerPreference _originalPreference;
    private bool _disposed;

    private WindowCornerPreferenceScope(nint window, DwmWindowCornerPreference originalPreference)
    {
        _window = window;
        _originalPreference = originalPreference;
    }

    public static WindowCornerPreferenceScope? TryDisable(nint window)
    {
        if (window == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return null;
        }

        if (DwmGetWindowAttribute(
                window,
                DwmWindowAttributeWindowCornerPreference,
                out var originalPreference,
                CornerPreferenceSize) < 0)
        {
            return null;
        }

        var requestedPreference = DwmWindowCornerPreference.DoNotRound;
        if (DwmSetWindowAttribute(
                window,
                DwmWindowAttributeWindowCornerPreference,
                ref requestedPreference,
                CornerPreferenceSize) < 0)
        {
            return null;
        }

        return new WindowCornerPreferenceScope(window, originalPreference);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var preference = _originalPreference;
        _ = DwmSetWindowAttribute(
            _window,
            DwmWindowAttributeWindowCornerPreference,
            ref preference,
            CornerPreferenceSize);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        out DwmWindowCornerPreference value,
        uint valueSize);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        nint window,
        uint attribute,
        ref DwmWindowCornerPreference value,
        uint valueSize);

    private enum DwmWindowCornerPreference
    {
        Default,
        DoNotRound,
        Round,
        RoundSmall,
    }
}
