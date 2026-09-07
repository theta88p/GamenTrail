using GamenTrail.Core.Audio;
using GamenTrail.Core.Video;

namespace GamenTrail.Core.Container;

public sealed record MuxerConfiguration(
    EncodedVideoFormat Video,
    AudioFormat? Audio,
    TimeSpan TimecodeScale,
    byte[]? AudioEncodingFormat = null,
    ExternalAudioEncoderSettings? ExternalEncoder = null);

public interface IMediaMuxer : IAsyncDisposable
{
    ValueTask OpenAsync(
        string outputPath,
        MuxerConfiguration configuration,
        CancellationToken cancellationToken);

    ValueTask WriteVideoFrameAsync(
        EncodedVideoFrame frame,
        CancellationToken cancellationToken);

    ValueTask WriteAudioPacketAsync(
        AudioPacket packet,
        CancellationToken cancellationToken);

    ValueTask FinalizeAsync(CancellationToken cancellationToken);
}
