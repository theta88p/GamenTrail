using GamenTrail.Core.Audio;
using GamenTrail.Core.Recording;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Audio;
using GamenTrail.Platform.Windows.Container;
using GamenTrail.Platform.Windows.Video;

namespace GamenTrail.Platform.Windows;

public sealed class GamenTrailServices
{
    public static bool SupportsProcessLoopback => WasapiLoopbackCapture.IsProcessLoopbackSupported;

    public WindowsCaptureTargetProvider CaptureTargets { get; } = new();

    public IAudioDeviceProvider AudioDevices { get; } = new WindowsAudioDeviceProvider();

    public IProcessProvider Processes { get; } = new WindowsProcessProvider();

    public IVideoCodecProvider VideoCodecs { get; } = new VcmCodecProvider();

    public static Recorder CreateRecorder() => new(
        new WindowsGraphicsCapture(),
        new VcmVideoEncoder(),
        new WasapiLoopbackCapture(),
        new FileMediaMuxer(),
        new RecordingClock());
}
