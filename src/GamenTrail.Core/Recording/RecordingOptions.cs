using GamenTrail.Core.Audio;
using GamenTrail.Core.Video;

namespace GamenTrail.Core.Recording;

public sealed record RecordingOptions(
    string OutputPath,
    VideoCaptureOptions VideoCapture,
    VideoEncoderSettings VideoEncoder,
    AudioCaptureOptions? AudioCapture,
    int QueueCapacity = 8)
{
    public TimeSpan MatroskaTimecodeScale { get; init; } = TimeSpan.FromMilliseconds(1);
}

public enum RecorderState
{
    Idle,
    Starting,
    Recording,
    Stopping,
    Faulted,
}

public sealed class RecorderStateChangedEventArgs(
    RecorderState previous,
    RecorderState current,
    Exception? error = null) : EventArgs
{
    public RecorderState Previous { get; } = previous;

    public RecorderState Current { get; } = current;

    public Exception? Error { get; } = error;
}

public sealed record RecordingStatistics(
    TimeSpan Elapsed,
    long CapturedVideoFrames,
    long EncodedVideoFrames,
    long DroppedVideoFrames,
    long CapturedAudioPackets,
    long MuxedBytes);
