using GamenTrail.Core.Audio;
using GamenTrail.Core.Container;
using GamenTrail.Core.Media;
using GamenTrail.Core.Recording;
using GamenTrail.Core.Video;

internal static class RecorderSmokeTests
{
    public static async Task RunAsync()
    {
        await ValidateGracefulRecordingAsync().ConfigureAwait(false);
        await ValidateVideoOnlyRecordingAsync().ConfigureAwait(false);
        await ValidateAviFrameDuplicationAsync().ConfigureAwait(false);
        await ValidateAviSilenceAsync(new AudioFormat(48_000, 2, 32, AudioSampleFormat.IeeeFloat), 0).ConfigureAwait(false);
        await ValidateAviSilenceAsync(new AudioFormat(44_100, 2, 16, AudioSampleFormat.SignedInteger), 0).ConfigureAwait(false);
        await ValidateAviSilenceAsync(new AudioFormat(8_000, 1, 8, AudioSampleFormat.SignedInteger), 128).ConfigureAwait(false);
        await ValidateAviAudioGapsAsync().ConfigureAwait(false);
        await ValidatePipelineFaultAsync().ConfigureAwait(false);
    }

    private static async Task ValidateVideoOnlyRecordingAsync()
    {
        var videoCapture = new FakeVideoCapture();
        var audioCapture = new FakeAudioCapture();
        var muxer = new FakeMuxer();
        await using var recorder = new Recorder(
            videoCapture,
            new FakeVideoEncoder(failEncoding: false),
            audioCapture,
            muxer,
            new RecordingClock());

        await recorder.StartAsync(CreateOptions(includeAudio: false)).ConfigureAwait(false);
        videoCapture.EmitFrame();
        await muxer.VideoWritten.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await recorder.StopAsync().ConfigureAwait(false);

        Assert(!audioCapture.Initialized, "Video-only recording must not initialize audio capture.");
        Assert(!audioCapture.Started, "Video-only recording must not start audio capture.");
        Assert(muxer.Configuration?.Audio is null, "Video-only recording must open the muxer without audio.");
    }

    private static async Task ValidateAviFrameDuplicationAsync()
    {
        var videoCapture = new FakeVideoCapture(framesPerSecond: 10);
        var videoEncoder = new FakeVideoEncoder(failEncoding: false);
        var audioCapture = new FakeAudioCapture();
        await using var recorder = new Recorder(
            videoCapture,
            videoEncoder,
            audioCapture,
            new FakeMuxer(),
            new RecordingClock());

        await recorder.StartAsync(CreateOptions("unused.avi", framesPerSecond: 10)).ConfigureAwait(false);
        videoCapture.EmitFrame(TimeSpan.Zero);
        audioCapture.EmitPacket();
        videoCapture.EmitFrame(TimeSpan.FromMilliseconds(350));
        await videoEncoder.FourFramesEncoded.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await recorder.StopAsync().ConfigureAwait(false);

        var timestamps = videoEncoder.EncodedTimestamps;
        Assert(timestamps.Count >= 4, "AVI recording must fill missing CFR frame slots.");
        Assert(timestamps[0] == TimeSpan.Zero, "The first AVI frame must start at zero.");
        Assert(timestamps[1] == TimeSpan.FromMilliseconds(100), "Missing AVI frame at 100 ms.");
        Assert(timestamps[2] == TimeSpan.FromMilliseconds(200), "Missing AVI frame at 200 ms.");
        Assert(timestamps[3] == TimeSpan.FromMilliseconds(300), "Missing AVI frame at 300 ms.");
    }

    private static async Task ValidateAviSilenceAsync(AudioFormat format, byte silenceValue)
    {
        var videoCapture = new FakeVideoCapture(framesPerSecond: 10);
        var videoEncoder = new FakeVideoEncoder(failEncoding: false);
        var muxer = new FakeMuxer();
        await using var recorder = new Recorder(
            videoCapture, videoEncoder, new FakeAudioCapture(format), muxer, new RecordingClock());
        await recorder.StartAsync(CreateOptions("unused.avi", framesPerSecond: 10)).ConfigureAwait(false);
        videoCapture.EmitFrame();
        await Task.Delay(150).ConfigureAwait(false);
        await recorder.StopAsync().ConfigureAwait(false);

        var audio = muxer.AudioData.ToArray();
        var blockAlign = format.ChannelCount * format.BitsPerSample / 8;
        Assert(audio.Length >= format.SampleRate / 10 * blockAlign, "A silent recording must contain PCM samples.");
        Assert(audio.All(value => value == silenceValue), "Synthesized PCM must represent silence.");
        Assert(audio.Length % blockAlign == 0, "Silence must contain whole sample frames.");
        var audioDuration = audio.Length / (double)(format.SampleRate * blockAlign);
        var videoDuration = videoEncoder.EncodedTimestamps.Count / 10d;
        Assert(Math.Abs(audioDuration - videoDuration) <= 0.101, "Silent AVI tracks must agree within one frame.");
        Assert(recorder.GetStatistics().CapturedAudioPackets == 0, "Silence must not count as captured audio.");
    }

    private static async Task ValidateAviAudioGapsAsync()
    {
        var videoCapture = new FakeVideoCapture(framesPerSecond: 10);
        var videoEncoder = new FakeVideoEncoder(failEncoding: false);
        var audioCapture = new FakeAudioCapture();
        var muxer = new FakeMuxer();
        await using var recorder = new Recorder(
            videoCapture, videoEncoder, audioCapture, muxer, new RecordingClock());
        await recorder.StartAsync(CreateOptions("unused.avi", framesPerSecond: 10)).ConfigureAwait(false);
        videoCapture.EmitFrame();
        audioCapture.EmitPacket(TimeSpan.FromMilliseconds(100), sampleCount: 480, value: 17);
        audioCapture.EmitPacket(TimeSpan.FromMilliseconds(300), sampleCount: 480, value: 34);
        await Task.Delay(450).ConfigureAwait(false);
        await recorder.StopAsync().ConfigureAwait(false);

        var audio = muxer.AudioData.ToArray();
        const int bytesPerMillisecond = 48 * 8;
        Assert(audio.Length >= 400 * bytesPerMillisecond, "Trailing silence must extend to recording stop.");
        Assert(audio.Take(100 * bytesPerMillisecond).All(value => value == 0), "Leading silence is missing.");
        Assert(audio.Skip(100 * bytesPerMillisecond).Take(10 * bytesPerMillisecond).All(value => value == 17),
            "The first audio packet must retain its samples and position.");
        Assert(audio.Skip(110 * bytesPerMillisecond).Take(190 * bytesPerMillisecond).All(value => value == 0),
            "Silence between audio packets is missing.");
        Assert(audio.Skip(300 * bytesPerMillisecond).Take(10 * bytesPerMillisecond).All(value => value == 34),
            "The second audio packet must retain its samples and position.");
        Assert(audio.Skip(310 * bytesPerMillisecond).All(value => value == 0), "Trailing silence is missing.");
        Assert(Math.Abs(audio.Length / (48_000d * 8) - videoEncoder.EncodedTimestamps.Count / 10d) <= 0.101,
            "AVI with intermittent audio must agree within one video frame.");

        long samples = 0;
        foreach (var packet in muxer.AudioPackets)
        {
            Assert(Math.Abs(packet.Timestamp.TotalSeconds - samples / 48_000d) < 0.000001,
                "Output audio timestamps must form a continuous timeline.");
            samples += packet.Length / 8;
        }
    }

    private static async Task ValidateGracefulRecordingAsync()
    {
        var videoCapture = new FakeVideoCapture();
        var audioCapture = new FakeAudioCapture();
        var muxer = new FakeMuxer();
        await using var recorder = new Recorder(
            videoCapture,
            new FakeVideoEncoder(failEncoding: false),
            audioCapture,
            muxer,
            new RecordingClock());

        await recorder.StartAsync(CreateOptions()).ConfigureAwait(false);
        videoCapture.EmitFrame();
        audioCapture.EmitPacket();
        await muxer.BothTracksWritten.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await recorder.StopAsync().ConfigureAwait(false);

        Assert(recorder.State is RecorderState.Idle, "Recorder must return to Idle after stopping.");
        Assert(muxer.Finalized, "Recorder must finalize the muxer.");
        var statistics = recorder.GetStatistics();
        Assert(statistics.CapturedVideoFrames == 1, "Recorder must count captured frames.");
        Assert(statistics.EncodedVideoFrames == 1, "Recorder must count encoded frames.");
        Assert(statistics.CapturedAudioPackets == 1, "Recorder must count audio packets.");
        Assert(statistics.MuxedBytes == 12, "Recorder must count muxed payload bytes.");
    }

    private static async Task ValidatePipelineFaultAsync()
    {
        var videoCapture = new FakeVideoCapture();
        var faultObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var recorder = new Recorder(
            videoCapture,
            new FakeVideoEncoder(failEncoding: true),
            new FakeAudioCapture(),
            new FakeMuxer(),
            new RecordingClock());
        recorder.StateChanged += (_, args) =>
        {
            if (args.Current is RecorderState.Faulted)
            {
                faultObserved.TrySetResult();
            }
        };

        await recorder.StartAsync(CreateOptions()).ConfigureAwait(false);
        videoCapture.EmitFrame();
        await faultObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Assert(recorder.LastError is not null, "Recorder must retain the pipeline failure.");
        await recorder.StopAsync().ConfigureAwait(false);
        Assert(recorder.State is RecorderState.Idle, "Stop must reset a cleaned faulted recorder.");
    }

    private static RecordingOptions CreateOptions(
        string outputPath = "unused.mkv",
        int framesPerSecond = 60,
        bool includeAudio = true) => new(
        outputPath,
        new VideoCaptureOptions(new CaptureTarget.Monitor(1), framesPerSecond),
        new VideoEncoderSettings(),
        includeAudio ? new AudioCaptureOptions() : null);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeVideoCapture
        : IVideoCapture
    {
        public FakeVideoCapture(int framesPerSecond = 60)
        {
            Format = new VideoFormat(64, 64, framesPerSecond, VideoPixelFormat.Bgra32);
        }

        public event EventHandler<VideoFrame>? FrameArrived;

        public event EventHandler<CaptureFaultedEventArgs>? CaptureFaulted
        {
            add { }
            remove { }
        }

        public VideoFormat Format { get; }

        public void EmitFrame(TimeSpan? timestamp = null) =>
            FrameArrived?.Invoke(this, new VideoFrame(new FakeFrameBuffer(), timestamp ?? TimeSpan.Zero));

        public ValueTask InitializeAsync(VideoCaptureOptions options, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            FrameArrived = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFrameBuffer : IVideoFrameBuffer
    {
        public int Width => 64;

        public int Height => 64;

        public VideoPixelFormat PixelFormat => VideoPixelFormat.Bgra32;

        public void Dispose()
        {
        }
    }

    private sealed class FakeVideoEncoder(bool failEncoding) : IVideoEncoder
    {
        public List<TimeSpan> EncodedTimestamps { get; } = [];

        public TaskCompletionSource FourFramesEncoded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EncodedVideoFormat OutputFormat { get; } = new(
            "V_TEST",
            ReadOnlyMemory<byte>.Empty,
            64,
            64,
            60);

        public ValueTask InitializeAsync(
            VideoFormat inputFormat,
            VideoEncoderSettings settings,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<EncodedVideoFrame> EncodeAsync(
            VideoFrame frame,
            TimeSpan outputTimestamp,
            CancellationToken cancellationToken)
        {
            if (failEncoding)
            {
                throw new InvalidOperationException("Synthetic encoder failure.");
            }

            EncodedTimestamps.Add(outputTimestamp);
            if (EncodedTimestamps.Count >= 4)
            {
                FourFramesEncoded.TrySetResult();
            }

            return ValueTask.FromResult(new EncodedVideoFrame(
                MediaBuffer.CopyFrom([1, 2, 3, 4]),
                outputTimestamp,
                TimeSpan.FromSeconds(1d / 60),
                isKeyFrame: true));
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioCapture(AudioFormat? format = null) : IAudioCapture
    {
        public bool Initialized { get; private set; }

        public bool Started { get; private set; }

        public event EventHandler<AudioPacket>? PacketArrived;

        public event EventHandler<CaptureFaultedEventArgs>? CaptureFaulted
        {
            add { }
            remove { }
        }

        public AudioFormat Format { get; } = format ?? new(48_000, 2, 32, AudioSampleFormat.IeeeFloat);

        public void EmitPacket(TimeSpan? timestamp = null, int sampleCount = 1, byte value = 1)
        {
            var data = new byte[sampleCount * Format.ChannelCount * Format.BitsPerSample / 8];
            Array.Fill(data, value);
            PacketArrived?.Invoke(this, new AudioPacket(
                MediaBuffer.CopyFrom(data), timestamp ?? TimeSpan.Zero,
                TimeSpan.FromSeconds(sampleCount / (double)Format.SampleRate)));
        }

        public ValueTask InitializeAsync(AudioCaptureOptions options, CancellationToken cancellationToken)
        {
            Initialized = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            PacketArrived = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeMuxer : IMediaMuxer
    {
        private int _videoFrames;
        private int _audioPackets;

        public List<byte> AudioData { get; } = [];

        public List<(TimeSpan Timestamp, int Length)> AudioPackets { get; } = [];

        public TaskCompletionSource BothTracksWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource VideoWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MuxerConfiguration? Configuration { get; private set; }

        public bool Finalized { get; private set; }

        public ValueTask OpenAsync(
            string outputPath,
            MuxerConfiguration configuration,
            CancellationToken cancellationToken)
        {
            Configuration = configuration;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteVideoFrameAsync(
            EncodedVideoFrame frame,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _videoFrames);
            VideoWritten.TrySetResult();
            SignalWhenComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAudioPacketAsync(AudioPacket packet, CancellationToken cancellationToken)
        {
            AudioData.AddRange(packet.Buffer.Memory.ToArray());
            AudioPackets.Add((packet.Timestamp, packet.Buffer.Length));
            Interlocked.Increment(ref _audioPackets);
            SignalWhenComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken)
        {
            Finalized = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void SignalWhenComplete()
        {
            if (Volatile.Read(ref _videoFrames) > 0 && Volatile.Read(ref _audioPackets) > 0)
            {
                BothTracksWritten.TrySetResult();
            }
        }
    }
}
