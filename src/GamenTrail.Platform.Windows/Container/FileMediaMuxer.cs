using GamenTrail.Platform.Windows.Audio;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Container;
using GamenTrail.Core.Video;

namespace GamenTrail.Platform.Windows.Container;

public sealed class FileMediaMuxer : IMediaMuxer
{
    private IMediaMuxer? _inner;
    private AcmAudioEncoder? _audioEncoder;
    private ExternalAudioEncoder? _externalEncoder;

    public async ValueTask OpenAsync(
        string outputPath,
        MuxerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (_inner is not null)
        {
            throw new InvalidOperationException("The media muxer is already open.");
        }

        _inner = Path.GetExtension(outputPath).ToLowerInvariant() switch
        {
            ".avi" => new AviWriter(),
            ".mkv" => new MatroskaWriter(),
            _ => throw new NotSupportedException("Output extension must be .avi or .mkv."),
        };

        try
        {
            if (configuration.ExternalEncoder is { } external && configuration.Audio is { } externalAudio)
            {
                _externalEncoder = new ExternalAudioEncoder(externalAudio, external);
                configuration = configuration with { Audio = _externalEncoder.OutputFormat };
            }
            if (_externalEncoder is null && configuration.Audio is { } audio && configuration.AudioEncodingFormat is { Length: > 0 } encoding)
            {
                _audioEncoder = new AcmAudioEncoder(audio, encoding);
                configuration = configuration with { Audio = _audioEncoder.OutputFormat };
            }
            await _inner.OpenAsync(outputPath, configuration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _externalEncoder?.Dispose();
            _externalEncoder = null;
            _audioEncoder?.Dispose();
            _audioEncoder = null;
            await _inner.DisposeAsync().ConfigureAwait(false);
            _inner = null;
            throw;
        }
    }

    public ValueTask WriteVideoFrameAsync(
        EncodedVideoFrame frame,
        CancellationToken cancellationToken) =>
        GetInner().WriteVideoFrameAsync(frame, cancellationToken);

    public async ValueTask WriteAudioPacketAsync(AudioPacket packet, CancellationToken cancellationToken)
    {
        if (_externalEncoder is not null)
        {
            await _externalEncoder.WriteAsync(packet.Buffer.Memory, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_audioEncoder is null)
        {
            await GetInner().WriteAudioPacketAsync(packet, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var encoded = _audioEncoder.ConvertPacket(packet.Buffer.Memory.Span);
        if (encoded is not null)
            await GetInner().WriteAudioPacketAsync(encoded, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_externalEncoder is not null)
        {
            await _externalEncoder.CompleteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var packet in _externalEncoder.ReadPackets())
            {
                using (packet) await GetInner().WriteAudioPacketAsync(packet, cancellationToken).ConfigureAwait(false);
            }
            _externalEncoder.Dispose();
            _externalEncoder = null;
        }
        if (_audioEncoder is not null)
        {
            using var final = _audioEncoder.ConvertPacket([], final: true);
            if (final is not null)
                await GetInner().WriteAudioPacketAsync(final, cancellationToken).ConfigureAwait(false);
            _audioEncoder.Dispose();
            _audioEncoder = null;
        }
        await GetInner().FinalizeAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (_inner is not null)
        {
            _externalEncoder?.Dispose();
            _externalEncoder = null;
            _audioEncoder?.Dispose();
            _audioEncoder = null;
            await _inner.DisposeAsync().ConfigureAwait(false);
            _inner = null;
        }
    }

    private IMediaMuxer GetInner() =>
        _inner ?? throw new InvalidOperationException("The media muxer is not open.");
}
