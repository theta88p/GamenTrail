using System.Buffers.Binary;
using System.Text;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Container;
using GamenTrail.Core.Video;

namespace GamenTrail.Platform.Windows.Container;

public sealed class AviWriter : IMediaMuxer
{
    private const string VideoChunkId = "00dc";
    private const string AudioChunkId = "01wb";
    private const uint AviHasIndex = 0x10;
    private const uint AviIsInterleaved = 0x100;
    private const uint AviTrustChunkType = 0x800;
    private const uint AviIndexKeyFrame = 0x10;
    private const uint NonKeyFrameSizeFlag = 0x80000000;
    private const byte AviIndexOfIndexes = 0;
    private const byte AviIndexOfChunks = 1;
    private const int SuperIndexCapacity = 1024;
    private const long MaximumRiffSegmentSize = 1024L * 1024 * 1024;

    private readonly List<StandardIndexEntry> _videoEntries = [];
    private readonly List<StandardIndexEntry> _audioEntries = [];
    private readonly List<LegacyIndexEntry> _legacyEntries = [];
    private readonly List<SuperIndexEntry> _videoSuperEntries = [];
    private readonly List<SuperIndexEntry> _audioSuperEntries = [];
    private FileStream? _stream;
    private MuxerConfiguration? _configuration;
    private long _riffSizePosition;
    private long _riffStartPosition;
    private long _moviSizePosition;
    private long _moviTypePosition;
    private long _mainMaxBytesPerSecondPosition;
    private long _mainTotalFramesPosition;
    private long _mainSuggestedBufferPosition;
    private long _videoLengthPosition;
    private long _videoSuggestedBufferPosition;
    private long _audioLengthPosition;
    private long _audioSuggestedBufferPosition;
    private long _dmlhTotalFramesPosition;
    private long _videoSuperEntryCountPosition;
    private long _videoSuperEntriesPosition;
    private long _audioSuperEntryCountPosition;
    private long _audioSuperEntriesPosition;
    private long _totalVideoFrames;
    private long _totalAudioSamples;
    private uint _largestVideoChunk;
    private uint _largestAudioChunk;
    private bool _currentSegmentIsFirst;
    private bool _segmentHasData;
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
            throw new InvalidOperationException("The AVI writer is already open.");
        }

        _stream = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _configuration = configuration;
        _finalized = false;

        try
        {
            await BeginRiffAsync("AVI ", isFirst: true, cancellationToken).ConfigureAwait(false);
            await WriteHeaderAsync(cancellationToken).ConfigureAwait(false);
            await BeginMoviAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask WriteVideoFrameAsync(
        EncodedVideoFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        await EnsureChunkFitsAsync(frame.Buffer.Length, cancellationToken).ConfigureAwait(false);
        await WriteMediaChunkAsync(
            VideoChunkId,
            frame.Buffer.Memory,
            frame.IsKeyFrame,
            duration: 1,
            _videoEntries,
            cancellationToken).ConfigureAwait(false);
        _totalVideoFrames++;
        _largestVideoChunk = Math.Max(_largestVideoChunk, checked((uint)frame.Buffer.Length));
    }

    public async ValueTask WriteAudioPacketAsync(
        AudioPacket packet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var configuration = GetConfiguration();
        var audio = configuration.Audio ??
            throw new InvalidOperationException("The AVI file was opened without an audio track.");
        var blockAlign = GetAudioBlockAlign(audio);
        if (packet.Buffer.Length % blockAlign != 0)
        {
            throw new InvalidDataException("PCM audio packet size must be aligned to a complete sample frame.");
        }

        var samples = audio.CodecId == "A_FLAC" ? 1u : checked((uint)(packet.Buffer.Length / blockAlign));
        await EnsureChunkFitsAsync(packet.Buffer.Length, cancellationToken).ConfigureAwait(false);
        await WriteMediaChunkAsync(
            AudioChunkId,
            packet.Buffer.Memory,
            isKeyFrame: true,
            samples,
            _audioEntries,
            cancellationToken).ConfigureAwait(false);
        _totalAudioSamples += samples;
        _largestAudioChunk = Math.Max(_largestAudioChunk, checked((uint)packet.Buffer.Length));
    }

    public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_stream is null || _configuration is null || _finalized)
        {
            return;
        }

        await FinishCurrentRiffAsync(cancellationToken).ConfigureAwait(false);
        await PatchHeadersAsync(cancellationToken).ConfigureAwait(false);
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
        var configuration = GetConfiguration();
        var headerListSizePosition = await BeginListAsync("hdrl", cancellationToken).ConfigureAwait(false);

        var mainHeader = new byte[56];
        WriteUInt32(mainHeader, 0, checked((uint)Math.Round(1_000_000d / configuration.Video.FramesPerSecond)));
        var flags = AviHasIndex | AviTrustChunkType;
        if (configuration.Audio is not null)
        {
            flags |= AviIsInterleaved;
        }

        WriteUInt32(mainHeader, 12, flags);
        WriteUInt32(mainHeader, 24, configuration.Audio is null ? 1u : 2u);
        WriteUInt32(mainHeader, 32, checked((uint)configuration.Video.Width));
        WriteUInt32(mainHeader, 36, checked((uint)configuration.Video.Height));
        var mainHeaderPosition = await WriteChunkAsync("avih", mainHeader, cancellationToken).ConfigureAwait(false);
        _mainMaxBytesPerSecondPosition = mainHeaderPosition + 4;
        _mainTotalFramesPosition = mainHeaderPosition + 16;
        _mainSuggestedBufferPosition = mainHeaderPosition + 28;

        await WriteVideoStreamHeaderAsync(configuration, cancellationToken).ConfigureAwait(false);
        if (configuration.Audio is not null)
        {
            await WriteAudioStreamHeaderAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        var odmlListSizePosition = await BeginListAsync("odml", cancellationToken).ConfigureAwait(false);
        var dmlhPosition = await WriteChunkAsync("dmlh", new byte[248], cancellationToken).ConfigureAwait(false);
        _dmlhTotalFramesPosition = dmlhPosition;
        await EndContainerAsync(odmlListSizePosition, cancellationToken).ConfigureAwait(false);
        await EndContainerAsync(headerListSizePosition, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteVideoStreamHeaderAsync(
        MuxerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var listSizePosition = await BeginListAsync("strl", cancellationToken).ConfigureAwait(false);
        var handler = GetVideoHandler(configuration.Video);
        var (rate, scale) = GetRateAndScale(configuration.Video.FramesPerSecond);
        var streamHeader = new byte[56];
        WriteFourCc(streamHeader, 0, "vids");
        WriteFourCc(streamHeader, 4, handler);
        WriteUInt32(streamHeader, 20, scale);
        WriteUInt32(streamHeader, 24, rate);
        WriteUInt32(streamHeader, 40, uint.MaxValue);
        WriteInt16(streamHeader, 52, checked((short)configuration.Video.Width));
        WriteInt16(streamHeader, 54, checked((short)configuration.Video.Height));
        var streamHeaderPosition = await WriteChunkAsync("strh", streamHeader, cancellationToken).ConfigureAwait(false);
        _videoLengthPosition = streamHeaderPosition + 32;
        _videoSuggestedBufferPosition = streamHeaderPosition + 36;
        await WriteChunkAsync("strf", configuration.Video.CodecPrivate, cancellationToken).ConfigureAwait(false);
        (_videoSuperEntryCountPosition, _videoSuperEntriesPosition) =
            await WriteSuperIndexPlaceholderAsync(VideoChunkId, cancellationToken).ConfigureAwait(false);
        await EndContainerAsync(listSizePosition, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteAudioStreamHeaderAsync(
        MuxerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var listSizePosition = await BeginListAsync("strl", cancellationToken).ConfigureAwait(false);
        var audio = configuration.Audio ?? throw new ArgumentException("Audio format is required.", nameof(configuration));
        var blockAlign = GetAudioBlockAlign(audio);
        var averageBytesPerSecond = GetAudioBytesPerSecond(audio);
        var streamHeader = new byte[56];
        WriteFourCc(streamHeader, 0, "auds");
        WriteUInt32(streamHeader, 20, checked((uint)GetAudioTimeScale(audio)));
        WriteUInt32(streamHeader, 24, checked((uint)averageBytesPerSecond));
        WriteUInt32(streamHeader, 40, uint.MaxValue);
        WriteUInt32(streamHeader, 44, audio.CodecId == "A_FLAC" ? 0u : checked((uint)blockAlign));
        var streamHeaderPosition = await WriteChunkAsync("strh", streamHeader, cancellationToken).ConfigureAwait(false);
        _audioLengthPosition = streamHeaderPosition + 32;
        _audioSuggestedBufferPosition = streamHeaderPosition + 36;

        var waveFormat = new byte[18];
        WriteUInt16(waveFormat, 0, audio.SampleFormat switch
        {
            AudioSampleFormat.SignedInteger => 1,
            AudioSampleFormat.IeeeFloat => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        });
        WriteUInt16(waveFormat, 2, checked((ushort)audio.ChannelCount));
        WriteUInt32(waveFormat, 4, checked((uint)audio.SampleRate));
        WriteUInt32(waveFormat, 8, checked((uint)averageBytesPerSecond));
        WriteUInt16(waveFormat, 12, checked((ushort)blockAlign));
        WriteUInt16(waveFormat, 14, checked((ushort)audio.BitsPerSample));
        if (audio.CodecId == "A_FLAC")
        {
            // WAVE_FORMAT_FLAC + the raw 34-byte STREAMINFO (without the native FLAC wrapper).
            waveFormat = new byte[52];
            WriteUInt16(waveFormat, 0, 0xF1AC);
            WriteUInt16(waveFormat, 2, checked((ushort)audio.ChannelCount));
            WriteUInt32(waveFormat, 4, checked((uint)audio.SampleRate));
            WriteUInt32(waveFormat, 8, 0); // Variable bitrate: not a byte-based clock.
            WriteUInt16(waveFormat, 12, 1);
            WriteUInt16(waveFormat, 14, checked((ushort)audio.BitsPerSample));
            WriteUInt16(waveFormat, 16, 34);
            audio.CodecPrivate.Span.Slice(8, 34).CopyTo(waveFormat.AsSpan(18));
        }
        await WriteChunkAsync("strf", audio.WaveFormat.IsEmpty ? waveFormat : audio.WaveFormat, cancellationToken).ConfigureAwait(false);
        (_audioSuperEntryCountPosition, _audioSuperEntriesPosition) =
            await WriteSuperIndexPlaceholderAsync(AudioChunkId, cancellationToken).ConfigureAwait(false);
        await EndContainerAsync(listSizePosition, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(long EntryCountPosition, long EntriesPosition)> WriteSuperIndexPlaceholderAsync(
        string chunkId,
        CancellationToken cancellationToken)
    {
        var data = new byte[24 + (SuperIndexCapacity * 16)];
        WriteUInt16(data, 0, 4);
        data[2] = 0;
        data[3] = AviIndexOfIndexes;
        WriteFourCc(data, 8, chunkId);
        var dataPosition = await WriteChunkAsync("indx", data, cancellationToken).ConfigureAwait(false);
        return (dataPosition + 4, dataPosition + 24);
    }

    private async ValueTask EnsureChunkFitsAsync(int dataLength, CancellationToken cancellationToken)
    {
        var writer = GetStream();
        var paddedChunkLength = 8L + dataLength + (dataLength & 1);
        var projectedIndexLength = EstimateCurrentIndexLength(additionalEntries: 1);
        if (_segmentHasData &&
            writer.Position - _riffStartPosition + paddedChunkLength + projectedIndexLength > MaximumRiffSegmentSize)
        {
            await FinishCurrentRiffAsync(cancellationToken).ConfigureAwait(false);
            await BeginRiffAsync("AVIX", isFirst: false, cancellationToken).ConfigureAwait(false);
            await BeginMoviAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private long EstimateCurrentIndexLength(int additionalEntries)
    {
        var videoCount = _videoEntries.Count + additionalEntries;
        var audioCount = _audioEntries.Count + additionalEntries;
        var standardIndexes = 64L + (8L * (videoCount + audioCount));
        var legacyIndex = _currentSegmentIsFirst
            ? 8L + (16L * (_legacyEntries.Count + additionalEntries))
            : 0;
        return standardIndexes + legacyIndex;
    }

    private async ValueTask WriteMediaChunkAsync(
        string chunkId,
        ReadOnlyMemory<byte> data,
        bool isKeyFrame,
        uint duration,
        List<StandardIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        var writer = GetStream();
        var chunkHeaderPosition = writer.Position;
        await WriteFourCcAsync(chunkId, cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(checked((uint)data.Length), cancellationToken).ConfigureAwait(false);
        var dataPosition = writer.Position;
        await writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        if ((data.Length & 1) != 0)
        {
            await writer.WriteAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        }

        entries.Add(new StandardIndexEntry(dataPosition, checked((uint)data.Length), isKeyFrame, duration));
        if (_currentSegmentIsFirst)
        {
            _legacyEntries.Add(new LegacyIndexEntry(
                chunkId,
                isKeyFrame ? AviIndexKeyFrame : 0,
                checked((uint)(chunkHeaderPosition - _moviTypePosition)),
                checked((uint)data.Length)));
        }

        _segmentHasData = true;
    }

    private async ValueTask BeginRiffAsync(
        string riffType,
        bool isFirst,
        CancellationToken cancellationToken)
    {
        var writer = GetStream();
        _riffStartPosition = writer.Position;
        await WriteFourCcAsync("RIFF", cancellationToken).ConfigureAwait(false);
        _riffSizePosition = writer.Position;
        await WriteUInt32Async(0, cancellationToken).ConfigureAwait(false);
        await WriteFourCcAsync(riffType, cancellationToken).ConfigureAwait(false);
        _currentSegmentIsFirst = isFirst;
        _segmentHasData = false;
        _videoEntries.Clear();
        _audioEntries.Clear();
        _legacyEntries.Clear();
    }

    private async ValueTask BeginMoviAsync(CancellationToken cancellationToken)
    {
        await WriteFourCcAsync("LIST", cancellationToken).ConfigureAwait(false);
        _moviSizePosition = GetStream().Position;
        await WriteUInt32Async(0, cancellationToken).ConfigureAwait(false);
        _moviTypePosition = GetStream().Position;
        await WriteFourCcAsync("movi", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FinishCurrentRiffAsync(CancellationToken cancellationToken)
    {
        var writer = GetStream();
        await PatchUInt32Async(
            _moviSizePosition,
            checked((uint)(writer.Position - (_moviSizePosition + sizeof(uint)))),
            cancellationToken).ConfigureAwait(false);

        if (_currentSegmentIsFirst && _legacyEntries.Count > 0)
        {
            await WriteLegacyIndexAsync(cancellationToken).ConfigureAwait(false);
        }

        var videoIndex = await WriteStandardIndexAsync(
            "ix00",
            VideoChunkId,
            _videoEntries,
            cancellationToken).ConfigureAwait(false);
        if (videoIndex is { } videoEntry)
        {
            _videoSuperEntries.Add(videoEntry);
        }

        var audioIndex = await WriteStandardIndexAsync(
            "ix01",
            AudioChunkId,
            _audioEntries,
            cancellationToken).ConfigureAwait(false);
        if (audioIndex is { } audioEntry)
        {
            _audioSuperEntries.Add(audioEntry);
        }

        if (_videoSuperEntries.Count > SuperIndexCapacity || _audioSuperEntries.Count > SuperIndexCapacity)
        {
            throw new InvalidOperationException("AVI recording exceeded the reserved OpenDML index capacity.");
        }

        await PatchUInt32Async(
            _riffSizePosition,
            checked((uint)(writer.Position - (_riffSizePosition + sizeof(uint)))),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteLegacyIndexAsync(CancellationToken cancellationToken)
    {
        await WriteFourCcAsync("idx1", cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(checked((uint)(_legacyEntries.Count * 16)), cancellationToken).ConfigureAwait(false);
        foreach (var entry in _legacyEntries)
        {
            await WriteFourCcAsync(entry.ChunkId, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(entry.Flags, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(entry.Offset, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(entry.Size, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<SuperIndexEntry?> WriteStandardIndexAsync(
        string indexChunkId,
        string mediaChunkId,
        List<StandardIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var writer = GetStream();
        var indexPosition = writer.Position;
        var dataSize = checked((uint)(24 + (entries.Count * 8)));
        await WriteFourCcAsync(indexChunkId, cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(dataSize, cancellationToken).ConfigureAwait(false);
        await WriteUInt16Async(2, cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(new byte[] { 0, AviIndexOfChunks }, cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(checked((uint)entries.Count), cancellationToken).ConfigureAwait(false);
        await WriteFourCcAsync(mediaChunkId, cancellationToken).ConfigureAwait(false);
        await WriteUInt64Async(checked((ulong)_riffStartPosition), cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(0, cancellationToken).ConfigureAwait(false);

        uint duration = 0;
        foreach (var entry in entries)
        {
            await WriteUInt32Async(
                checked((uint)(entry.DataPosition - _riffStartPosition)),
                cancellationToken).ConfigureAwait(false);
            var size = entry.Size;
            if (!entry.IsKeyFrame)
            {
                size |= NonKeyFrameSizeFlag;
            }

            await WriteUInt32Async(size, cancellationToken).ConfigureAwait(false);
            duration = checked(duration + entry.Duration);
        }

        return new SuperIndexEntry(indexPosition, checked(dataSize + 8), duration);
    }

    private async ValueTask PatchHeadersAsync(CancellationToken cancellationToken)
    {
        var writer = GetStream();
        var configuration = GetConfiguration();
        var totalFrames = checked((uint)_totalVideoFrames);
        var audioSamples = checked((uint)_totalAudioSamples);
        var suggestedBuffer = Math.Max(_largestVideoChunk, _largestAudioChunk);
        var videoDuration = _totalVideoFrames / configuration.Video.FramesPerSecond;
        var audioDuration = configuration.Audio is null
            ? 0
            : _totalAudioSamples * (double)GetAudioTimeScale(configuration.Audio.Value) / GetAudioBytesPerSecond(configuration.Audio.Value);
        var duration = Math.Max(videoDuration, audioDuration);
        var maxBytesPerSecond = duration > 0
            ? checked((uint)Math.Min(uint.MaxValue, Math.Ceiling(writer.Length / duration)))
            : 0;

        await PatchUInt32Async(_mainMaxBytesPerSecondPosition, maxBytesPerSecond, cancellationToken)
            .ConfigureAwait(false);
        await PatchUInt32Async(_mainTotalFramesPosition, totalFrames, cancellationToken).ConfigureAwait(false);
        await PatchUInt32Async(_mainSuggestedBufferPosition, suggestedBuffer, cancellationToken).ConfigureAwait(false);
        await PatchUInt32Async(_videoLengthPosition, totalFrames, cancellationToken).ConfigureAwait(false);
        await PatchUInt32Async(_videoSuggestedBufferPosition, _largestVideoChunk, cancellationToken).ConfigureAwait(false);
        if (configuration.Audio is not null)
        {
            await PatchUInt32Async(_audioLengthPosition, audioSamples, cancellationToken).ConfigureAwait(false);
            await PatchUInt32Async(_audioSuggestedBufferPosition, _largestAudioChunk, cancellationToken)
                .ConfigureAwait(false);
        }
        await PatchUInt32Async(_dmlhTotalFramesPosition, totalFrames, cancellationToken).ConfigureAwait(false);
        await PatchSuperIndexAsync(
            _videoSuperEntryCountPosition,
            _videoSuperEntriesPosition,
            _videoSuperEntries,
            cancellationToken).ConfigureAwait(false);
        if (configuration.Audio is not null)
        {
            await PatchSuperIndexAsync(
                _audioSuperEntryCountPosition,
                _audioSuperEntriesPosition,
                _audioSuperEntries,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask PatchSuperIndexAsync(
        long entryCountPosition,
        long entriesPosition,
        List<SuperIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        await PatchUInt32Async(entryCountPosition, checked((uint)entries.Count), cancellationToken)
            .ConfigureAwait(false);
        for (var index = 0; index < entries.Count; index++)
        {
            var position = entriesPosition + (index * 16L);
            await PatchUInt64Async(position, checked((ulong)entries[index].Offset), cancellationToken)
                .ConfigureAwait(false);
            await PatchUInt32Async(position + 8, entries[index].Size, cancellationToken).ConfigureAwait(false);
            await PatchUInt32Async(position + 12, entries[index].Duration, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<long> BeginListAsync(string listType, CancellationToken cancellationToken)
    {
        await WriteFourCcAsync("LIST", cancellationToken).ConfigureAwait(false);
        var sizePosition = GetStream().Position;
        await WriteUInt32Async(0, cancellationToken).ConfigureAwait(false);
        await WriteFourCcAsync(listType, cancellationToken).ConfigureAwait(false);
        return sizePosition;
    }

    private async ValueTask EndContainerAsync(long sizePosition, CancellationToken cancellationToken)
    {
        await PatchUInt32Async(
            sizePosition,
            checked((uint)(GetStream().Position - (sizePosition + sizeof(uint)))),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> WriteChunkAsync(
        string chunkId,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await WriteFourCcAsync(chunkId, cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(checked((uint)data.Length), cancellationToken).ConfigureAwait(false);
        var dataPosition = GetStream().Position;
        await GetStream().WriteAsync(data, cancellationToken).ConfigureAwait(false);
        if ((data.Length & 1) != 0)
        {
            await GetStream().WriteAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        }

        return dataPosition;
    }

    private async ValueTask WriteFourCcAsync(string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length != 4)
        {
            throw new ArgumentException("A FOURCC must contain exactly four ASCII characters.", nameof(value));
        }

        await GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteUInt16Async(ushort value, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        await GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteUInt32Async(uint value, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        await GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteUInt64Async(ulong value, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        await GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PatchUInt32Async(
        long position,
        uint value,
        CancellationToken cancellationToken)
    {
        var writer = GetStream();
        var returnPosition = writer.Position;
        writer.Position = position;
        await WriteUInt32Async(value, cancellationToken).ConfigureAwait(false);
        writer.Position = returnPosition;
    }

    private async ValueTask PatchUInt64Async(
        long position,
        ulong value,
        CancellationToken cancellationToken)
    {
        var writer = GetStream();
        var returnPosition = writer.Position;
        writer.Position = position;
        await WriteUInt64Async(value, cancellationToken).ConfigureAwait(false);
        writer.Position = returnPosition;
    }

    private static string GetVideoHandler(EncodedVideoFormat format)
    {
        if (!string.Equals(format.CodecId, "V_MS/VFW/FOURCC", StringComparison.Ordinal) ||
            format.CodecPrivate.Length < 40)
        {
            throw new NotSupportedException("AVI output requires a VCM BITMAPINFOHEADER video format.");
        }

        return Encoding.ASCII.GetString(format.CodecPrivate.Span.Slice(16, 4));
    }

    private static (uint Rate, uint Scale) GetRateAndScale(double framesPerSecond)
    {
        if (Math.Abs(framesPerSecond - Math.Round(framesPerSecond)) < 0.000_001)
        {
            return (checked((uint)Math.Round(framesPerSecond)), 1);
        }

        const uint scale = 1000;
        return (checked((uint)Math.Round(framesPerSecond * scale)), scale);
    }

    private static int GetAudioTimeScale(AudioFormat format) => format.CodecId == "A_FLAC"
        ? BinaryPrimitives.ReadUInt16BigEndian(format.CodecPrivate.Span[10..])
        : GetAudioBlockAlign(format);

    private static int GetAudioBlockAlign(AudioFormat format) => format.CodecId == "A_FLAC" ? 1 : format.WaveFormat.IsEmpty
        ? checked(format.ChannelCount * format.BitsPerSample / 8)
        : BinaryPrimitives.ReadUInt16LittleEndian(format.WaveFormat.Span[12..]);

    private static int GetAudioBytesPerSecond(AudioFormat format) => format.CodecId == "A_FLAC" ? format.SampleRate : format.WaveFormat.IsEmpty
        ? checked(format.SampleRate * GetAudioBlockAlign(format))
        : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(format.WaveFormat.Span[8..]));

    private static void ValidateConfiguration(MuxerConfiguration configuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuration.Video.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuration.Video.Height, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuration.Video.FramesPerSecond, 0);
        if (configuration.Audio is { } audio)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(audio.SampleRate, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(audio.ChannelCount, 0);
            if (audio.WaveFormat.IsEmpty) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(audio.BitsPerSample, 0);
            if (audio.WaveFormat.IsEmpty && audio.BitsPerSample % 8 != 0)
            {
                throw new NotSupportedException("AVI PCM audio requires a whole number of bytes per sample.");
            }

            if (audio.CodecId == "A_FLAC")
            {
                var info = audio.CodecPrivate.Span;
                if (info.Length != 42 || !info[..4].SequenceEqual("fLaC"u8) ||
                    BinaryPrimitives.ReadUInt16BigEndian(info[8..]) == 0 ||
                    BinaryPrimitives.ReadUInt16BigEndian(info[8..]) != BinaryPrimitives.ReadUInt16BigEndian(info[10..]))
                    throw new NotSupportedException("AVIのFLACには固定ブロックサイズのSTREAMINFOが必要です。");
            }
            _ = GetAudioBlockAlign(audio);
        }

        _ = GetVideoHandler(configuration.Video);
    }

    private static void WriteFourCc(Span<byte> destination, int offset, string value)
    {
        if (value.Length != 4 || Encoding.ASCII.GetByteCount(value) != 4)
        {
            throw new ArgumentException("A FOURCC must contain exactly four ASCII characters.", nameof(value));
        }

        Encoding.ASCII.GetBytes(value, destination[offset..(offset + 4)]);
    }

    private static void WriteUInt16(Span<byte> destination, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], value);

    private static void WriteInt16(Span<byte> destination, int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(destination[offset..], value);

    private static void WriteUInt32(Span<byte> destination, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], value);

    private FileStream GetStream() =>
        _stream ?? throw new InvalidOperationException("The AVI writer is not open.");

    private MuxerConfiguration GetConfiguration() =>
        _configuration ?? throw new InvalidOperationException("The AVI writer is not open.");

    private void ResetState()
    {
        _stream = null;
        _configuration = null;
        _videoEntries.Clear();
        _audioEntries.Clear();
        _legacyEntries.Clear();
        _videoSuperEntries.Clear();
        _audioSuperEntries.Clear();
        _riffSizePosition = 0;
        _riffStartPosition = 0;
        _moviSizePosition = 0;
        _moviTypePosition = 0;
        _mainMaxBytesPerSecondPosition = 0;
        _mainTotalFramesPosition = 0;
        _mainSuggestedBufferPosition = 0;
        _videoLengthPosition = 0;
        _videoSuggestedBufferPosition = 0;
        _audioLengthPosition = 0;
        _audioSuggestedBufferPosition = 0;
        _dmlhTotalFramesPosition = 0;
        _videoSuperEntryCountPosition = 0;
        _videoSuperEntriesPosition = 0;
        _audioSuperEntryCountPosition = 0;
        _audioSuperEntriesPosition = 0;
        _totalVideoFrames = 0;
        _totalAudioSamples = 0;
        _largestVideoChunk = 0;
        _largestAudioChunk = 0;
        _currentSegmentIsFirst = false;
        _segmentHasData = false;
        _finalized = false;
    }

    private readonly record struct StandardIndexEntry(
        long DataPosition,
        uint Size,
        bool IsKeyFrame,
        uint Duration);

    private readonly record struct LegacyIndexEntry(
        string ChunkId,
        uint Flags,
        uint Offset,
        uint Size);

    private readonly record struct SuperIndexEntry(long Offset, uint Size, uint Duration);
}
