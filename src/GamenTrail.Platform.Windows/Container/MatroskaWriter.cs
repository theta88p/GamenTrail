using System.Buffers.Binary;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Container;
using GamenTrail.Core.Video;

namespace GamenTrail.Platform.Windows.Container;

public sealed class MatroskaWriter : IMediaMuxer
{
    private const ulong VideoTrackNumber = 1;
    private const ulong AudioTrackNumber = 2;
    private const long MaximumClusterSpan = 30_000;
    private const int PatchedSizeWidth = 8;

    private FileStream? _stream;
    private EbmlWriter? _ebml;
    private MuxerConfiguration? _configuration;
    private readonly List<CueEntry> _cues = [];
    private long _segmentContentPosition;
    private long _durationPosition;
    private double _durationTimestampUnits;
    private long? _clusterSizePosition;
    private long _clusterContentPosition;
    private long _clusterSegmentPosition;
    private long _clusterTimestamp;
    private bool _clusterHasVideoCue;
    private bool _finalized;

    public async ValueTask OpenAsync(
        string outputPath,
        MuxerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateConfiguration(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        if (_stream is not null)
        {
            throw new InvalidOperationException("The Matroska writer is already open.");
        }

        _stream = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _ebml = new EbmlWriter(_stream);
        _configuration = configuration;
        _finalized = false;

        try
        {
            await WriteHeaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask WriteVideoFrameAsync(
        EncodedVideoFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return WriteBlockAsync(
            VideoTrackNumber,
            frame.Buffer.Memory,
            frame.Timestamp,
            frame.Duration,
            frame.IsKeyFrame,
            cancellationToken);
    }

    public ValueTask WriteAudioPacketAsync(
        AudioPacket packet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (GetConfiguration().Audio is null)
        {
            throw new InvalidOperationException("The Matroska file was opened without an audio track.");
        }

        return WriteBlockAsync(
            AudioTrackNumber,
            packet.Buffer.Memory,
            packet.Timestamp,
            packet.Duration,
            isKeyFrame: false,
            cancellationToken);
    }

    public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_stream is null || _ebml is null || _finalized)
        {
            return;
        }

        await CloseClusterAsync(cancellationToken).ConfigureAwait(false);
        await WriteCuesAsync(cancellationToken).ConfigureAwait(false);
        await _ebml.PatchDoubleAsync(
            _durationPosition,
            _durationTimestampUnits,
            cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _finalized = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        ResetState();
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        ResetState();
    }

    private async ValueTask WriteHeaderAsync(CancellationToken cancellationToken)
    {
        var writer = GetWriter();
        var configuration = GetConfiguration();

        await writer.WriteMasterAsync(MatroskaElement.Ebml, async header =>
        {
            await header.WriteUnsignedAsync(MatroskaElement.EbmlVersion, 1, cancellationToken).ConfigureAwait(false);
            await header.WriteUnsignedAsync(MatroskaElement.EbmlReadVersion, 1, cancellationToken).ConfigureAwait(false);
            await header.WriteUnsignedAsync(MatroskaElement.EbmlMaxIdLength, 4, cancellationToken).ConfigureAwait(false);
            await header.WriteUnsignedAsync(MatroskaElement.EbmlMaxSizeLength, 8, cancellationToken).ConfigureAwait(false);
            await header.WriteUtf8Async(MatroskaElement.DocType, "matroska", cancellationToken).ConfigureAwait(false);
            await header.WriteUnsignedAsync(MatroskaElement.DocTypeVersion, 4, cancellationToken).ConfigureAwait(false);
            await header.WriteUnsignedAsync(MatroskaElement.DocTypeReadVersion, 2, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        await writer.WriteIdAsync(MatroskaElement.Segment, cancellationToken).ConfigureAwait(false);
        await writer.WriteUnknownSizeAsync(PatchedSizeWidth, cancellationToken).ConfigureAwait(false);
        _segmentContentPosition = writer.Position;

        await WriteInfoAsync(writer, configuration, cancellationToken).ConfigureAwait(false);

        await writer.WriteMasterAsync(MatroskaElement.Tracks, async tracks =>
        {
            await WriteVideoTrackAsync(tracks, configuration, cancellationToken).ConfigureAwait(false);
            if (configuration.Audio is not null)
            {
                await WriteAudioTrackAsync(tracks, configuration, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteInfoAsync(
        EbmlWriter writer,
        MuxerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await writer.WriteIdAsync(MatroskaElement.Info, cancellationToken).ConfigureAwait(false);
        var sizePosition = await writer.WriteUnknownSizeAsync(PatchedSizeWidth, cancellationToken).ConfigureAwait(false);
        var contentPosition = writer.Position;
        await writer.WriteUnsignedAsync(
            MatroskaElement.TimestampScale,
            GetTimestampScaleNanoseconds(configuration),
            cancellationToken).ConfigureAwait(false);
        await writer.WriteUtf8Async(MatroskaElement.MuxingApp, "GamenTrail", cancellationToken).ConfigureAwait(false);
        await writer.WriteUtf8Async(MatroskaElement.WritingApp, "GamenTrail", cancellationToken).ConfigureAwait(false);
        await writer.WriteIdAsync(MatroskaElement.Duration, cancellationToken).ConfigureAwait(false);
        await writer.WriteSizeAsync(sizeof(double), cancellationToken).ConfigureAwait(false);
        _durationPosition = writer.Position;
        await writer.WriteRawAsync(new byte[sizeof(double)], cancellationToken).ConfigureAwait(false);
        await writer.PatchSizeAsync(
            sizePosition,
            checked((ulong)(writer.Position - contentPosition)),
            PatchedSizeWidth,
            cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask WriteVideoTrackAsync(
        EbmlWriter writer,
        MuxerConfiguration configuration,
        CancellationToken cancellationToken) =>
        writer.WriteMasterAsync(MatroskaElement.TrackEntry, async track =>
        {
            await track.WriteUnsignedAsync(MatroskaElement.TrackNumber, VideoTrackNumber, cancellationToken)
                .ConfigureAwait(false);
            await track.WriteUnsignedAsync(MatroskaElement.TrackUid, VideoTrackNumber, cancellationToken)
                .ConfigureAwait(false);
            await track.WriteUnsignedAsync(MatroskaElement.TrackType, 1, cancellationToken).ConfigureAwait(false);
            await track.WriteUnsignedAsync(MatroskaElement.FlagLacing, 0, cancellationToken).ConfigureAwait(false);
            await track.WriteUtf8Async(MatroskaElement.CodecId, configuration.Video.CodecId, cancellationToken)
                .ConfigureAwait(false);
            await track.WriteBinaryAsync(
                MatroskaElement.CodecPrivate,
                configuration.Video.CodecPrivate,
                cancellationToken).ConfigureAwait(false);

            if (configuration.Video.FramesPerSecond > 0)
            {
                var defaultDuration = checked((ulong)Math.Round(
                    1_000_000_000d / configuration.Video.FramesPerSecond,
                    MidpointRounding.AwayFromZero));
                await track.WriteUnsignedAsync(MatroskaElement.DefaultDuration, defaultDuration, cancellationToken)
                    .ConfigureAwait(false);
            }

            await track.WriteMasterAsync(MatroskaElement.Video, async video =>
            {
                await video.WriteUnsignedAsync(
                    MatroskaElement.PixelWidth,
                    checked((ulong)configuration.Video.Width),
                    cancellationToken).ConfigureAwait(false);
                await video.WriteUnsignedAsync(
                    MatroskaElement.PixelHeight,
                    checked((ulong)configuration.Video.Height),
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static ValueTask WriteAudioTrackAsync(
        EbmlWriter writer,
        MuxerConfiguration configuration,
        CancellationToken cancellationToken) =>
        writer.WriteMasterAsync(MatroskaElement.TrackEntry, async track =>
        {
            var format = configuration.Audio ?? throw new ArgumentException("Audio format is required.");
            await track.WriteUnsignedAsync(MatroskaElement.TrackNumber, AudioTrackNumber, cancellationToken)
                .ConfigureAwait(false);
            await track.WriteUnsignedAsync(MatroskaElement.TrackUid, AudioTrackNumber, cancellationToken)
                .ConfigureAwait(false);
            await track.WriteUnsignedAsync(MatroskaElement.TrackType, 2, cancellationToken).ConfigureAwait(false);
            await track.WriteUnsignedAsync(MatroskaElement.FlagLacing, 0, cancellationToken).ConfigureAwait(false);
            await track.WriteUtf8Async(
                MatroskaElement.CodecId,
                GetAudioCodecId(format),
                cancellationToken).ConfigureAwait(false);
            if (!format.CodecPrivate.IsEmpty)
                await track.WriteBinaryAsync(MatroskaElement.CodecPrivate, format.CodecPrivate, cancellationToken).ConfigureAwait(false);
            else if (format.CodecId is null && !format.WaveFormat.IsEmpty)
                await track.WriteBinaryAsync(MatroskaElement.CodecPrivate, format.WaveFormat, cancellationToken).ConfigureAwait(false);
            await track.WriteMasterAsync(MatroskaElement.Audio, async audio =>
            {
                await audio.WriteDoubleAsync(
                    MatroskaElement.SamplingFrequency,
                    format.SampleRate,
                    cancellationToken).ConfigureAwait(false);
                await audio.WriteUnsignedAsync(
                    MatroskaElement.Channels,
                    checked((ulong)format.ChannelCount),
                    cancellationToken).ConfigureAwait(false);
                if (format.WaveFormat.IsEmpty) await audio.WriteUnsignedAsync(
                    MatroskaElement.BitDepth,
                    checked((ulong)format.BitsPerSample),
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async ValueTask WriteBlockAsync(
        ulong trackNumber,
        ReadOnlyMemory<byte> data,
        TimeSpan timestamp,
        TimeSpan duration,
        bool isKeyFrame,
        CancellationToken cancellationToken)
    {
        var writer = GetWriter();
        var timestampUnits = ToTimestampUnits(timestamp);

        if (_clusterSizePosition is null || !FitsCurrentCluster(timestampUnits))
        {
            await CloseClusterAsync(cancellationToken).ConfigureAwait(false);
            await OpenClusterAsync(Math.Max(0, timestampUnits), cancellationToken).ConfigureAwait(false);
        }

        if (trackNumber == VideoTrackNumber && isKeyFrame && !_clusterHasVideoCue)
        {
            _cues.Add(new CueEntry(timestampUnits, _clusterSegmentPosition));
            _clusterHasVideoCue = true;
        }

        _durationTimestampUnits = Math.Max(
            _durationTimestampUnits,
            timestampUnits + ToDurationUnits(duration));

        var relativeTimestamp = checked((short)(timestampUnits - _clusterTimestamp));
        var blockHeader = new byte[4];
        blockHeader[0] = checked((byte)(0x80 | trackNumber));
        BinaryPrimitives.WriteInt16BigEndian(blockHeader.AsSpan(1, 2), relativeTimestamp);
        blockHeader[3] = isKeyFrame ? (byte)0x80 : (byte)0;

        await writer.WriteIdAsync(MatroskaElement.SimpleBlock, cancellationToken).ConfigureAwait(false);
        await writer.WriteSizeAsync(checked((ulong)(blockHeader.Length + data.Length)), cancellationToken)
            .ConfigureAwait(false);
        await writer.WriteRawAsync(blockHeader, cancellationToken).ConfigureAwait(false);
        await writer.WriteRawAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask OpenClusterAsync(long timestamp, CancellationToken cancellationToken)
    {
        var writer = GetWriter();
        _clusterSegmentPosition = writer.Position - _segmentContentPosition;
        await writer.WriteIdAsync(MatroskaElement.Cluster, cancellationToken).ConfigureAwait(false);
        _clusterSizePosition = await writer.WriteUnknownSizeAsync(PatchedSizeWidth, cancellationToken)
            .ConfigureAwait(false);
        _clusterContentPosition = writer.Position;
        _clusterTimestamp = timestamp;
        _clusterHasVideoCue = false;
        await writer.WriteUnsignedAsync(
            MatroskaElement.Timestamp,
            checked((ulong)timestamp),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteCuesAsync(CancellationToken cancellationToken)
    {
        if (_cues.Count == 0)
        {
            return;
        }

        var writer = GetWriter();
        await writer.WriteMasterAsync(MatroskaElement.Cues, async cues =>
        {
            foreach (var cue in _cues)
            {
                await cues.WriteMasterAsync(MatroskaElement.CuePoint, async cuePoint =>
                {
                    await cuePoint.WriteUnsignedAsync(
                        MatroskaElement.CueTime,
                        checked((ulong)cue.Timestamp),
                        cancellationToken).ConfigureAwait(false);
                    await cuePoint.WriteMasterAsync(MatroskaElement.CueTrackPositions, async trackPosition =>
                    {
                        await trackPosition.WriteUnsignedAsync(
                            MatroskaElement.CueTrack,
                            VideoTrackNumber,
                            cancellationToken).ConfigureAwait(false);
                        await trackPosition.WriteUnsignedAsync(
                            MatroskaElement.CueClusterPosition,
                            checked((ulong)cue.ClusterPosition),
                            cancellationToken).ConfigureAwait(false);
                    }, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CloseClusterAsync(CancellationToken cancellationToken)
    {
        if (_clusterSizePosition is null)
        {
            return;
        }

        var writer = GetWriter();
        var size = checked((ulong)(writer.Position - _clusterContentPosition));
        await writer.PatchSizeAsync(
            _clusterSizePosition.Value,
            size,
            PatchedSizeWidth,
            cancellationToken).ConfigureAwait(false);
        _clusterSizePosition = null;
    }

    private bool FitsCurrentCluster(long timestamp)
    {
        var relative = timestamp - _clusterTimestamp;
        return relative is >= short.MinValue and <= MaximumClusterSpan;
    }

    private long ToTimestampUnits(TimeSpan timestamp)
    {
        var configuration = GetConfiguration();
        return checked((long)Math.Round(
            timestamp.Ticks / (double)configuration.TimecodeScale.Ticks,
            MidpointRounding.AwayFromZero));
    }

    private double ToDurationUnits(TimeSpan duration) =>
        duration.Ticks / (double)GetConfiguration().TimecodeScale.Ticks;

    private static string GetAudioCodecId(AudioFormat format) => format.CodecId ?? (!format.WaveFormat.IsEmpty ? "A_MS/ACM" : format.SampleFormat switch
    {
        AudioSampleFormat.SignedInteger => "A_PCM/INT/LIT",
        AudioSampleFormat.IeeeFloat => "A_PCM/FLOAT/IEEE",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    });

    private static ulong GetTimestampScaleNanoseconds(MuxerConfiguration configuration) =>
        checked((ulong)configuration.TimecodeScale.Ticks * 100);

    private static void ValidateConfiguration(MuxerConfiguration configuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuration.Video.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuration.Video.Height, 0);
        if (configuration.Audio is { } audio)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(audio.SampleRate, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(audio.ChannelCount, 0);
            if (audio.WaveFormat.IsEmpty) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(audio.BitsPerSample, 0);
        }

        if (configuration.TimecodeScale <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "Timecode scale must be positive.");
        }
    }

    private EbmlWriter GetWriter() =>
        _ebml ?? throw new InvalidOperationException("The Matroska writer is not open.");

    private MuxerConfiguration GetConfiguration() =>
        _configuration ?? throw new InvalidOperationException("The Matroska writer is not open.");

    private void ResetState()
    {
        _stream = null;
        _ebml = null;
        _configuration = null;
        _cues.Clear();
        _segmentContentPosition = 0;
        _durationPosition = 0;
        _durationTimestampUnits = 0;
        _clusterSizePosition = null;
        _clusterContentPosition = 0;
        _clusterSegmentPosition = 0;
        _clusterTimestamp = 0;
        _clusterHasVideoCue = false;
    }

    private readonly record struct CueEntry(long Timestamp, long ClusterPosition);
}
