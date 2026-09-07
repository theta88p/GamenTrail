using System.Threading.Channels;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Container;
using GamenTrail.Core.Media;
using GamenTrail.Core.Video;

namespace GamenTrail.Core.Recording;

public sealed class Recorder : IAsyncDisposable
{
    private readonly IVideoCapture _videoCapture;
    private readonly IVideoEncoder _videoEncoder;
    private readonly IAudioCapture _audioCapture;
    private readonly IMediaMuxer _muxer;
    private readonly RecordingClock _clock;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private Channel<VideoFrame>? _videoQueue;
    private Channel<AudioPacket>? _audioQueue;
    private Channel<IMuxItem>? _muxQueue;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _videoWorker;
    private Task? _audioWorker;
    private Task? _muxWorker;
    private bool _audioEnabled;
    private Task? _faultCleanup;
    private RecorderState _state;
    private int _faultSignaled;
    private long _capturedVideoFrames;
    private long _encodedVideoFrames;
    private long _droppedVideoFrames;
    private long _capturedAudioPackets;
    private long _captureEndTimestampTicks;
    private long _muxedBytes;

    public Recorder(
        IVideoCapture videoCapture,
        IVideoEncoder videoEncoder,
        IAudioCapture audioCapture,
        IMediaMuxer muxer,
        RecordingClock clock)
    {
        _videoCapture = videoCapture;
        _videoEncoder = videoEncoder;
        _audioCapture = audioCapture;
        _muxer = muxer;
        _clock = clock;
    }

    public event EventHandler<RecorderStateChangedEventArgs>? StateChanged;

    public RecorderState State => _state;

    public Exception? LastError { get; private set; }

    public RecordingStatistics GetStatistics() => new(
        _clock.GetElapsedTime(),
        Interlocked.Read(ref _capturedVideoFrames),
        Interlocked.Read(ref _encodedVideoFrames),
        Interlocked.Read(ref _droppedVideoFrames),
        Interlocked.Read(ref _capturedAudioPackets),
        Interlocked.Read(ref _muxedBytes));

    public async Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.QueueCapacity, 1);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is not RecorderState.Idle)
            {
                throw new InvalidOperationException($"Cannot start while recorder state is {_state}.");
            }

            ResetStatistics();
            LastError = null;
            Interlocked.Exchange(ref _faultSignaled, 0);
            _faultCleanup = null;
            ChangeState(RecorderState.Starting);
            _sessionCancellation = new CancellationTokenSource();
            var sessionToken = _sessionCancellation.Token;

            await _videoCapture.InitializeAsync(options.VideoCapture, cancellationToken).ConfigureAwait(false);
            await _videoEncoder.InitializeAsync(
                _videoCapture.Format,
                options.VideoEncoder,
                cancellationToken).ConfigureAwait(false);
            _audioEnabled = options.AudioCapture is not null;
            AudioFormat? audioFormat = null;
            if (options.AudioCapture is not null)
            {
                await _audioCapture.InitializeAsync(options.AudioCapture, cancellationToken).ConfigureAwait(false);
                audioFormat = _audioCapture.Format;
            }

            await _muxer.OpenAsync(
                options.OutputPath,
                new MuxerConfiguration(
                    _videoEncoder.OutputFormat,
                    audioFormat,
                    options.MatroskaTimecodeScale,
                    options.AudioCapture?.EncodedWaveFormat, options.AudioCapture?.ExternalEncoder),
                cancellationToken).ConfigureAwait(false);

            _videoQueue = CreateQueue<VideoFrame>(options.QueueCapacity);
            _audioQueue = _audioEnabled ? CreateQueue<AudioPacket>(options.QueueCapacity * 4) : null;
            _muxQueue = CreateQueue<IMuxItem>(options.QueueCapacity * 5, singleWriter: false);
            var useConstantFrameRate = string.Equals(
                Path.GetExtension(options.OutputPath),
                ".avi",
                StringComparison.OrdinalIgnoreCase);
            _videoWorker = ProcessVideoAsync(
                _videoQueue.Reader,
                _muxQueue.Writer,
                useConstantFrameRate,
                _videoCapture.Format.FramesPerSecond,
                sessionToken);
            _audioWorker = _audioQueue is not null
                ? ProcessAudioAsync(_audioQueue.Reader, _muxQueue.Writer,
                    useConstantFrameRate || options.AudioCapture?.ExternalEncoder is not null || options.AudioCapture?.EncodedWaveFormat is { Length: > 0 }, sessionToken)
                : null;
            _muxWorker = ProcessMuxAsync(_muxQueue.Reader, sessionToken);
            ObserveWorker(_videoWorker);
            if (_audioWorker is not null)
            {
                ObserveWorker(_audioWorker);
            }
            ObserveWorker(_muxWorker);
            SubscribeCaptureEvents();

            _clock.Start();
            if (_audioEnabled)
            {
                await _audioCapture.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            await _videoCapture.StartAsync(cancellationToken).ConfigureAwait(false);
            ChangeState(RecorderState.Recording);
        }
        catch (Exception error)
        {
            LastError = error;
            ChangeState(RecorderState.Faulted, error);
            await StopSessionCoreAsync(graceful: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var faultCleanup = _faultCleanup;
        if (faultCleanup is not null)
        {
            await faultCleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is RecorderState.Idle)
            {
                return;
            }

            if (_state is RecorderState.Faulted)
            {
                if (_sessionCancellation is not null)
                {
                    await StopSessionCoreAsync(graceful: false, CancellationToken.None).ConfigureAwait(false);
                }

                ChangeState(RecorderState.Idle);
                return;
            }

            ChangeState(RecorderState.Stopping);
            try
            {
                await StopSessionCoreAsync(graceful: true, cancellationToken).ConfigureAwait(false);
                ChangeState(RecorderState.Idle);
            }
            catch (Exception error)
            {
                LastError = error;
                ChangeState(RecorderState.Faulted, error);
                throw;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_state is not RecorderState.Idle)
        {
            await StopAsync().ConfigureAwait(false);
        }

        await _videoCapture.DisposeAsync().ConfigureAwait(false);
        await _videoEncoder.DisposeAsync().ConfigureAwait(false);
        await _audioCapture.DisposeAsync().ConfigureAwait(false);
        await _muxer.DisposeAsync().ConfigureAwait(false);
        _sessionCancellation?.Dispose();
        _stateGate.Dispose();
    }

    private static Channel<T> CreateQueue<T>(int capacity, bool singleWriter = true) => Channel.CreateBounded<T>(
        new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = singleWriter,
        });

    private void SubscribeCaptureEvents()
    {
        _videoCapture.FrameArrived += OnVideoFrameArrived;
        _videoCapture.CaptureFaulted += OnCaptureFaulted;
        if (_audioEnabled)
        {
            _audioCapture.PacketArrived += OnAudioPacketArrived;
            _audioCapture.CaptureFaulted += OnCaptureFaulted;
        }
    }

    private void UnsubscribeCaptureEvents()
    {
        _videoCapture.FrameArrived -= OnVideoFrameArrived;
        _videoCapture.CaptureFaulted -= OnCaptureFaulted;
        if (_audioEnabled)
        {
            _audioCapture.PacketArrived -= OnAudioPacketArrived;
            _audioCapture.CaptureFaulted -= OnCaptureFaulted;
        }
    }

    private void OnVideoFrameArrived(object? sender, VideoFrame frame)
    {
        Interlocked.Increment(ref _capturedVideoFrames);
        if (_videoQueue?.Writer.TryWrite(frame) != true)
        {
            Interlocked.Increment(ref _droppedVideoFrames);
            frame.Dispose();
        }
    }

    private void OnAudioPacketArrived(object? sender, AudioPacket packet)
    {
        Interlocked.Increment(ref _capturedAudioPackets);
        if (_audioQueue?.Writer.TryWrite(packet) != true)
        {
            packet.Dispose();
        }
    }

    private void OnCaptureFaulted(object? sender, CaptureFaultedEventArgs args) => SignalFault(args.Error);

    private async Task ProcessVideoAsync(
        ChannelReader<VideoFrame> reader,
        ChannelWriter<IMuxItem> writer,
        bool useConstantFrameRate,
        double framesPerSecond,
        CancellationToken cancellationToken)
    {
        if (!useConstantFrameRate)
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using (frame)
                {
                    await EncodeAndQueueAsync(frame, frame.Timestamp, writer, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return;
        }

        VideoFrame? pendingFrame = null;
        long nextFrameIndex = 0;
        try
        {
            await foreach (var capturedFrame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                VideoFrame? incomingFrame = capturedFrame;
                try
                {
                    if (pendingFrame is null)
                    {
                        pendingFrame = incomingFrame;
                        incomingFrame = null;
                        await EncodeAndQueueAsync(
                            pendingFrame,
                            GetFrameTimestamp(nextFrameIndex++, framesPerSecond),
                            writer,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    nextFrameIndex = await EncodeConstantFrameRateUntilAsync(
                        pendingFrame,
                        incomingFrame.Timestamp,
                        nextFrameIndex,
                        framesPerSecond,
                        writer,
                        cancellationToken).ConfigureAwait(false);
                    pendingFrame.Dispose();
                    pendingFrame = incomingFrame;
                    incomingFrame = null;
                }
                finally
                {
                    incomingFrame?.Dispose();
                }
            }

            if (pendingFrame is not null)
            {
                var endTicks = Interlocked.Read(ref _captureEndTimestampTicks);
                if (endTicks <= 0)
                {
                    endTicks = pendingFrame.Timestamp.Ticks + checked((long)Math.Round(TimeSpan.TicksPerSecond / framesPerSecond));
                }

                await EncodeConstantFrameRateUntilAsync(
                    pendingFrame,
                    TimeSpan.FromTicks(endTicks),
                    nextFrameIndex,
                    framesPerSecond,
                    writer,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            pendingFrame?.Dispose();
        }
    }

    private async ValueTask<long> EncodeConstantFrameRateUntilAsync(
        VideoFrame frame,
        TimeSpan endTimestamp,
        long nextFrameIndex,
        double framesPerSecond,
        ChannelWriter<IMuxItem> writer,
        CancellationToken cancellationToken)
    {
        while (GetFrameTimestamp(nextFrameIndex, framesPerSecond) < endTimestamp)
        {
            await EncodeAndQueueAsync(
                frame,
                GetFrameTimestamp(nextFrameIndex, framesPerSecond),
                writer,
                cancellationToken).ConfigureAwait(false);
            nextFrameIndex++;
        }

        return nextFrameIndex;
    }

    private async ValueTask EncodeAndQueueAsync(
        VideoFrame frame,
        TimeSpan outputTimestamp,
        ChannelWriter<IMuxItem> writer,
        CancellationToken cancellationToken)
    {
        var encoded = await _videoEncoder.EncodeAsync(frame, outputTimestamp, cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Increment(ref _encodedVideoFrames);
        try
        {
            await writer.WriteAsync(new VideoMuxItem(encoded), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            encoded.Dispose();
            throw;
        }
    }

    private static TimeSpan GetFrameTimestamp(long frameIndex, double framesPerSecond) =>
        TimeSpan.FromTicks(checked((long)Math.Round(frameIndex * (double)TimeSpan.TicksPerSecond / framesPerSecond)));

    private async Task ProcessAudioAsync(
        ChannelReader<AudioPacket> reader,
        ChannelWriter<IMuxItem> writer,
        bool fillGaps,
        CancellationToken cancellationToken)
    {
        long nextSample = 0;
        var format = _audioCapture.Format;
        var blockAlign = checked(format.ChannelCount * (format.BitsPerSample / 8));
        await foreach (var packet in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!fillGaps)
            {
                await QueueAudioAsync(packet, writer, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (packet)
            {
                if (packet.Buffer.Length % blockAlign != 0)
                {
                    throw new InvalidDataException("PCM audio packet must contain complete sample frames.");
                }

                var startSample = GetAudioSampleIndex(packet.Timestamp, format.SampleRate);
                nextSample = await PadAudioUntilAsync(
                    nextSample, startSample, format, writer, cancellationToken).ConfigureAwait(false);
                var sampleCount = packet.Buffer.Length / blockAlign;
                var skippedSamples = (int)Math.Min(sampleCount, Math.Max(0, nextSample - startSample));
                if (skippedSamples == sampleCount)
                {
                    continue;
                }

                var remainingSamples = sampleCount - skippedSamples;
                var output = new AudioPacket(
                    MediaBuffer.CopyFrom(packet.Buffer.Memory.Span[(skippedSamples * blockAlign)..]),
                    GetAudioTimestamp(nextSample, format.SampleRate),
                    GetAudioTimestamp(remainingSamples, format.SampleRate));
                await QueueAudioAsync(output, writer, cancellationToken).ConfigureAwait(false);
                nextSample += remainingSamples;
            }
        }

        if (fillGaps)
        {
            var endTimestamp = TimeSpan.FromTicks(Interlocked.Read(ref _captureEndTimestampTicks));
            await PadAudioUntilAsync(
                nextSample, GetAudioSampleIndex(endTimestamp, format.SampleRate),
                format, writer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long GetAudioSampleIndex(TimeSpan timestamp, int sampleRate) =>
        checked((long)Math.Round(timestamp.Ticks * (decimal)sampleRate / TimeSpan.TicksPerSecond));

    private static TimeSpan GetAudioTimestamp(long sampleIndex, int sampleRate) =>
        TimeSpan.FromTicks(checked((long)(sampleIndex * (decimal)TimeSpan.TicksPerSecond / sampleRate)));

    private static async ValueTask<long> PadAudioUntilAsync(
        long nextSample,
        long endSample,
        AudioFormat format,
        ChannelWriter<IMuxItem> writer,
        CancellationToken cancellationToken)
    {
        var blockAlign = checked(format.ChannelCount * (format.BitsPerSample / 8));
        // Bound each allocation to 100 ms, even after a long period without packets.
        var chunkSamples = Math.Max(1, format.SampleRate / 10);
        while (nextSample < endSample)
        {
            var samples = (int)Math.Min(chunkSamples, endSample - nextSample);
            var silence = new byte[checked(samples * blockAlign)];
            // WAVE PCM uses unsigned samples for 8-bit integer audio.
            if (format.SampleFormat == AudioSampleFormat.SignedInteger && format.BitsPerSample == 8)
            {
                Array.Fill(silence, (byte)128);
            }

            var packet = new AudioPacket(
                MediaBuffer.CopyFrom(silence),
                GetAudioTimestamp(nextSample, format.SampleRate),
                GetAudioTimestamp(samples, format.SampleRate));
            await QueueAudioAsync(packet, writer, cancellationToken).ConfigureAwait(false);
            nextSample += samples;
        }

        return nextSample;
    }

    private static async ValueTask QueueAudioAsync(
        AudioPacket packet,
        ChannelWriter<IMuxItem> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync(new AudioMuxItem(packet), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }

    private async Task ProcessMuxAsync(ChannelReader<IMuxItem> reader, CancellationToken cancellationToken)
    {
        await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            using (item)
            {
                await item.WriteAsync(_muxer, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _muxedBytes, item.Length);
            }
        }
    }

    private void ObserveWorker(Task worker)
    {
        _ = worker.ContinueWith(
            static (completed, state) =>
                ((Recorder)state!).SignalFault(completed.Exception!.GetBaseException()),
            this,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SignalFault(Exception error)
    {
        if (_state is not (RecorderState.Starting or RecorderState.Recording) ||
            Interlocked.Exchange(ref _faultSignaled, 1) != 0)
        {
            return;
        }

        LastError = error;
        _sessionCancellation?.Cancel();
        _faultCleanup = Task.Run(CleanupAfterFaultAsync);
        ChangeState(RecorderState.Faulted, error);
    }

    private async Task CleanupAfterFaultAsync()
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessionCancellation is not null)
            {
                await StopSessionCoreAsync(graceful: false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception cleanupError)
        {
            LastError = LastError is null
                ? cleanupError
                : new AggregateException(LastError, cleanupError);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task StopSessionCoreAsync(bool graceful, CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        Interlocked.Exchange(ref _captureEndTimestampTicks, _clock.GetElapsedTime().Ticks);
        UnsubscribeCaptureEvents();
        if (!graceful)
        {
            _sessionCancellation?.Cancel();
        }

        await TryAsync(() => _videoCapture.StopAsync(cancellationToken), errors).ConfigureAwait(false);
        if (_audioEnabled)
        {
            await TryAsync(() => _audioCapture.StopAsync(cancellationToken), errors).ConfigureAwait(false);
        }
        _videoQueue?.Writer.TryComplete();
        _audioQueue?.Writer.TryComplete();
        await AwaitWorkersAsync(graceful, errors).ConfigureAwait(false);
        DrainQueues();

        if (graceful)
        {
            await TryAsync(() => _videoEncoder.FlushAsync(cancellationToken), errors).ConfigureAwait(false);
        }

        await TryAsync(() => _muxer.FinalizeAsync(cancellationToken), errors).ConfigureAwait(false);
        _clock.Stop();
        ReleaseSession();

        if (graceful && errors.Count > 0)
        {
            throw new AggregateException("Recording could not be stopped cleanly.", errors);
        }
    }

    private async Task AwaitWorkersAsync(bool graceful, List<Exception> errors)
    {
        if (_videoWorker is not null)
        {
            try
            {
                if (_audioWorker is null)
                {
                    await _videoWorker.ConfigureAwait(false);
                }
                else
                {
                    await Task.WhenAll(_videoWorker, _audioWorker).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (!graceful && error is OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                _muxQueue?.Writer.TryComplete();
            }
        }

        if (_muxWorker is not null)
        {
            try
            {
                await _muxWorker.ConfigureAwait(false);
            }
            catch (Exception error) when (!graceful && error is OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
    }

    private void DrainQueues()
    {
        Drain(_videoQueue?.Reader);
        Drain(_audioQueue?.Reader);
        Drain(_muxQueue?.Reader);

        static void Drain<T>(ChannelReader<T>? reader)
            where T : IDisposable
        {
            while (reader?.TryRead(out var item) == true)
            {
                item.Dispose();
            }
        }
    }

    private static async ValueTask TryAsync(
        Func<ValueTask> operation,
        List<Exception> errors)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private void ReleaseSession()
    {
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        _videoQueue = null;
        _audioQueue = null;
        _muxQueue = null;
        _videoWorker = null;
        _audioWorker = null;
        _muxWorker = null;
        _audioEnabled = false;
    }

    private void ResetStatistics()
    {
        Interlocked.Exchange(ref _capturedVideoFrames, 0);
        Interlocked.Exchange(ref _encodedVideoFrames, 0);
        Interlocked.Exchange(ref _droppedVideoFrames, 0);
        Interlocked.Exchange(ref _capturedAudioPackets, 0);
        Interlocked.Exchange(ref _captureEndTimestampTicks, 0);
        Interlocked.Exchange(ref _muxedBytes, 0);
    }

    private void ChangeState(RecorderState next, Exception? error = null)
    {
        var previous = _state;
        _state = next;
        StateChanged?.Invoke(this, new RecorderStateChangedEventArgs(previous, next, error));
    }

    private interface IMuxItem : IDisposable
    {
        int Length { get; }

        ValueTask WriteAsync(IMediaMuxer muxer, CancellationToken cancellationToken);
    }

    private sealed class VideoMuxItem(EncodedVideoFrame frame) : IMuxItem
    {
        public int Length => frame.Buffer.Length;

        public ValueTask WriteAsync(IMediaMuxer muxer, CancellationToken cancellationToken) =>
            muxer.WriteVideoFrameAsync(frame, cancellationToken);

        public void Dispose() => frame.Dispose();
    }

    private sealed class AudioMuxItem(AudioPacket packet) : IMuxItem
    {
        public int Length => packet.Buffer.Length;

        public ValueTask WriteAsync(IMediaMuxer muxer, CancellationToken cancellationToken) =>
            muxer.WriteAudioPacketAsync(packet, cancellationToken);

        public void Dispose() => packet.Dispose();
    }
}
