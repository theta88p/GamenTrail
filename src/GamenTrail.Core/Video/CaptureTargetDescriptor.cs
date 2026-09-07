namespace GamenTrail.Core.Video;

public sealed record CaptureTargetDescriptor(
    string Id,
    string DisplayName,
    CaptureTarget Target,
    int Width,
    int Height,
    int X = 0,
    int Y = 0,
    int? ProcessId = null);

public interface ICaptureTargetProvider
{
    IReadOnlyList<CaptureTargetDescriptor> GetMonitors();

    IReadOnlyList<CaptureTargetDescriptor> GetWindows();
}
