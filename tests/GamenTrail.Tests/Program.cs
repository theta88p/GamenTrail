using System.Text;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Container;
using GamenTrail.Core.Media;
using GamenTrail.Core.Recording;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Container;
using GamenTrail.Platform.Windows.Audio;
using GamenTrail.Platform.Windows.Video;
using GamenTrail.Platform.Windows;
using GamenTrail.App.Services;
using Windows.Graphics.Capture;

AudioCaptureTimelineTests.Run();
if (args.Contains("--auto-input-only", StringComparer.Ordinal))
{
    await using var encoder = new VcmVideoEncoder();
    await encoder.InitializeAsync(new VideoFormat(640, 480, 30, VideoPixelFormat.Bgra32),
        new VideoEncoderSettings(AutoSelectInputFormat: true), CancellationToken.None);
    using var frame = encoder.EncodePixels(new byte[640 * 480 * 4], TimeSpan.Zero);
    Assert(frame.IsKeyFrame && frame.Timestamp == TimeSpan.Zero && frame.Buffer.Length > 0,
        "Automatic input selection must discard its probe and start recording with a keyframe.");
    Console.WriteLine("Automatic input selection passed.");
    return;
}
if (args.Contains("--audio-timeline-only", StringComparer.Ordinal)) return;

if (args.Length == 3 && args[0] == "--external-audio-only")
{
    await ValidateSettingsPersistenceAsync();
    await ExternalAudioTests.RunAsync(args[1], args[2]);
    return;
}
if (args.Contains("--audio-codecs-only", StringComparer.Ordinal))
{
    await ValidateSettingsPersistenceAsync();
    await AudioCodecTests.RunAsync();
    return;
}
if (args.Contains("--preview-only", StringComparer.Ordinal))
{
    await CapturePreviewTests.RunAsync();
    return;
}

if (args.Contains("--codecs-only", StringComparer.Ordinal))
{
    await ValidateSettingsPersistenceAsync();
    await ValidateVideoCodecsAsync();
    await ValidateWindowsGraphicsCaptureInitializationAsync();
    return;
}
var outputPath = Path.Combine(Path.GetTempPath(), $"GamenTrail-{Guid.NewGuid():N}.mkv");
var aviOutputPath = Path.Combine(Path.GetTempPath(), $"GamenTrail-{Guid.NewGuid():N}.avi");
var videoOnlyMkvPath = Path.Combine(Path.GetTempPath(), $"GamenTrail-VideoOnly-{Guid.NewGuid():N}.mkv");
var videoOnlyAviPath = Path.Combine(Path.GetTempPath(), $"GamenTrail-VideoOnly-{Guid.NewGuid():N}.avi");

try
{
    await WriteSampleAsync(outputPath).ConfigureAwait(false);
    ValidateSample(await File.ReadAllBytesAsync(outputPath).ConfigureAwait(false));
    await WriteAviSampleAsync(aviOutputPath).ConfigureAwait(false);
    ValidateAviSample(await File.ReadAllBytesAsync(aviOutputPath).ConfigureAwait(false));
    await WriteVideoOnlySampleAsync(videoOnlyMkvPath).ConfigureAwait(false);
    ValidateVideoOnlyMkv(await File.ReadAllBytesAsync(videoOnlyMkvPath).ConfigureAwait(false));
    await WriteVideoOnlySampleAsync(videoOnlyAviPath).ConfigureAwait(false);
    ValidateVideoOnlyAvi(await File.ReadAllBytesAsync(videoOnlyAviPath).ConfigureAwait(false));
    WindowResizeTests.Run();
    await CapturePreviewTests.RunAsync();
    await RecorderSmokeTests.RunAsync().ConfigureAwait(false);
    await ValidateSettingsPersistenceAsync().ConfigureAwait(false);
    ValidateCaptureBorderPolicy();
    await ValidateProviderCatalogsAsync().ConfigureAwait(false);
    await ValidateWasapiInitializationAsync().ConfigureAwait(false);
    await ValidateEndToEndRecordingAsync().ConfigureAwait(false);
    await ValidateWindowsGraphicsCaptureInitializationAsync().ConfigureAwait(false);
    await ValidateInvalidWindowCaptureAsync().ConfigureAwait(false);
    Console.WriteLine("All GamenTrail smoke tests passed.");
}
finally
{
    File.Delete(outputPath);
    File.Delete(aviOutputPath);
    File.Delete(videoOnlyMkvPath);
    File.Delete(videoOnlyAviPath);
}

static void ValidateCaptureBorderPolicy()
{
    var monitor = new CaptureTarget.Monitor(1);
    var window = new CaptureTarget.Window(1);
    var region = new CaptureTarget.Region(1, 10, 20, 640, 480);
    Assert(
        WindowsGraphicsCapture.ShouldUseSystemCaptureBorder(new VideoCaptureOptions(monitor, 30, DrawBorder: true)),
        "Monitor capture must use the system capture border when requested.");
    Assert(
        !WindowsGraphicsCapture.ShouldUseSystemCaptureBorder(new VideoCaptureOptions(region, 30, DrawBorder: true)),
        "Region capture must not use the monitor-sized system capture border.");
    Assert(
        !WindowsGraphicsCapture.ShouldUseSystemCaptureBorder(
            new VideoCaptureOptions(window, 30, DrawBorder: true, CaptureOverlappingWindows: true)),
        "Overlapping-window capture must not use the monitor-sized system capture border.");
    Assert(
        WindowsGraphicsCapture.ShouldUseSystemCaptureBorder(new VideoCaptureOptions(window, 30, DrawBorder: true)),
        "Direct window capture must use the system capture border when requested.");
    Assert(
        !WindowsGraphicsCapture.ShouldUseSystemCaptureBorder(new VideoCaptureOptions(monitor, 30, DrawBorder: false)),
        "Capture must not use the system capture border when it is disabled.");
}

static async Task ValidateSettingsPersistenceAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), $"GamenTrail-Settings-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "settings.json");
    try
    {
        var service = new JsonSettingsService(path);
        var expected = new AppSettings
        {
            CaptureMode = "Region",
            MonitorId = "monitor-1",
            WindowId = "window-1",
            WindowWidth = "1920",
            WindowHeight = "1080",
            CodecFourCc = "UMRG",
            VideoPixelFormat = "Yv12",
            CodecStates = new Dictionary<string, byte[]> { ["umrg"] = [1, 2, 3, 255], ["x264"] = [4, 5] },
            FrameRate = 29.97,
            IncludeCursor = false,
            ShowCaptureBorder = true,
            DisableWindowCornerRounding = true,
            CaptureOverlappingWindows = true,
            AudioMode = "Process",
            AudioDeviceId = "audio-1",
            ProcessName = "TestProcess",
            LameExecutablePath = @"C:\Audio Tools\lame.exe",
            FlacExecutablePath = @"C:\Audio Tools\flac.exe",
            Mp3BitRate = 256,
            FlacCompressionLevel = 8,
            AudioCodec = "Pcm16",
            AudioCodecFormats = new Dictionary<string, byte[]> { ["Acm:0001:48000"] = AcmAudioCodec.CreatePcm(48000, 2) },
            AudioSampleRate = 44_100,
            ContainerExtension = "mkv",
            OutputFolder = @"D:\Cap",
            RegionX = "10",
            RegionY = "20",
            RegionWidth = "640",
            RegionHeight = "480",
        };

        await service.SaveAsync(expected).ConfigureAwait(false);
        var actual = await service.LoadAsync().ConfigureAwait(false);
        Assert(actual.CaptureMode == expected.CaptureMode, "Capture mode setting must round-trip.");
        Assert(actual.MonitorId == expected.MonitorId, "Monitor setting must round-trip.");
        Assert(actual.WindowId == expected.WindowId, "Window setting must round-trip.");
        Assert(actual.WindowWidth == expected.WindowWidth && actual.WindowHeight == expected.WindowHeight,
            "Window size settings must round-trip.");
        Assert(actual.CodecStates.Count == expected.CodecStates.Count &&
            expected.CodecStates.All(pair => actual.CodecStates.TryGetValue(pair.Key, out var state) &&
                state.AsSpan().SequenceEqual(pair.Value)), "Per-codec settings must round-trip without changes.");
        Assert(actual.VideoPixelFormat == expected.VideoPixelFormat, "Video pixel format must round-trip.");
        Assert(actual.FrameRate == expected.FrameRate, "Frame rate setting must round-trip.");
        Assert(actual.IncludeCursor == expected.IncludeCursor, "Cursor setting must round-trip.");
        Assert(actual.ShowCaptureBorder == expected.ShowCaptureBorder, "Border setting must round-trip.");
        Assert(actual.DisableWindowCornerRounding == expected.DisableWindowCornerRounding,
            "Window corner setting must round-trip.");
        Assert(actual.CaptureOverlappingWindows == expected.CaptureOverlappingWindows,
            "Overlapping-window capture setting must round-trip.");
        Assert(actual.AudioMode == expected.AudioMode, "Audio mode setting must round-trip.");
        Assert(actual.AudioDeviceId == expected.AudioDeviceId, "Audio device setting must round-trip.");
        Assert(actual.ProcessName == expected.ProcessName, "Process setting must round-trip.");
        Assert(actual.AudioCodecFormats.Count == 1 &&
            actual.AudioCodecFormats["Acm:0001:48000"].AsSpan().SequenceEqual(expected.AudioCodecFormats["Acm:0001:48000"]),
            "Audio codec format settings must round-trip unchanged.");
        Assert(actual.LameExecutablePath == expected.LameExecutablePath && actual.FlacExecutablePath == expected.FlacExecutablePath &&
            actual.Mp3BitRate == 256 && actual.FlacCompressionLevel == 8, "External encoder paths and quality must round-trip.");
        Assert(actual.AudioCodec == expected.AudioCodec, "Audio codec setting must round-trip.");
        Assert(actual.AudioSampleRate == expected.AudioSampleRate, "Audio sample rate setting must round-trip.");
        Assert(actual.ContainerExtension == expected.ContainerExtension, "Container setting must round-trip.");
        Assert(actual.OutputFolder == expected.OutputFolder, "Output folder setting must round-trip.");
        Assert(actual.RegionWidth == expected.RegionWidth, "Region setting must round-trip.");

        var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        Assert(!json.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'),
            "Settings JSON must use CRLF line endings.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static async Task ValidateProviderCatalogsAsync()
{
    var services = new GamenTrailServices();
    Assert(
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) == GamenTrailServices.SupportsProcessLoopback,
        "Process loopback capability must match the Windows version.");
    var monitors = services.CaptureTargets.GetMonitors();
    Assert(monitors.Count > 0, "At least one monitor must be discoverable.");
    Assert(monitors.All(static monitor => monitor.Width > 0 && monitor.Height > 0),
        "Discovered monitors must have positive dimensions.");

    var audioDevices = await services.AudioDevices.GetRenderDevicesAsync().ConfigureAwait(false);
    Assert(audioDevices.Count > 0, "At least one render audio device must be discoverable.");
    Assert(services.Processes.GetProcesses().Count > 0, "At least one process must be discoverable.");
    await ValidateVideoCodecsAsync();
}

static async Task ValidateVideoCodecsAsync()
{
    VideoPixelConverterTests.Run();
    Assert(!VcmVideoEncoder.CanConfigure("~~~~"), "Missing codecs must not expose a settings dialog.");
    foreach (var pixelFormat in Enum.GetValues<VideoEncodingPixelFormat>())
    {
        var codecs = new VcmCodecProvider().GetCodecs(pixelFormat);
        Assert(codecs.Select(static codec => codec.FourCc).Distinct(StringComparer.Ordinal).Count() == codecs.Count,
            "Enumerated codecs must not contain duplicate handlers.");
        Assert(!VcmVideoEncoder.IsCodecAvailable("~~~~", pixelFormat), "An uninstalled codec must not be available.");
        foreach (var codec in codecs)
        {
            Assert(codec.IsAvailable && !string.IsNullOrWhiteSpace(codec.DisplayName),
                "Every listed codec must be available and have a display name.");
            var state = VcmVideoEncoder.GetConfigurationState(codec.FourCc);
            var restored = VcmVideoEncoder.GetConfigurationState(codec.FourCc, state);
            Assert(restored.AsSpan().SequenceEqual(state), "Codec configuration must survive close/reopen.");
            Assert(VcmVideoEncoder.IsCodecAvailable(codec.FourCc, pixelFormat, state),
                "Saved state must be usable when checking format support.");
            var hasDialog = VcmVideoEncoder.CanConfigure(codec.FourCc);
            await using var encoder = new VcmVideoEncoder();
            await encoder.InitializeAsync(
                new VideoFormat(640, 480, 30, VideoPixelFormat.Bgra32),
                new VideoEncoderSettings(codec.FourCc, PixelFormat: pixelFormat, CodecState: state),
                CancellationToken.None);
            Assert(encoder.OutputFormat.CodecPrivate.Length >= 40,
                "Every listed codec must initialize with a valid VCM output format.");
            // Exercise the same conversion/compression path as a captured frame.
            // UtVideo is intra-frame and immediately returns each frame.
            if (codec.DisplayName.StartsWith("UtVideo", StringComparison.Ordinal))
            {
                using var frame = encoder.EncodePixels(new byte[640 * 480 * 4], TimeSpan.Zero);
                Assert(frame.Buffer.Length > 0, "Encoding the selected pixel layout must produce a frame.");
            }
            Console.WriteLine($"{pixelFormat}: {codec.DisplayName} (settings={hasDialog}, state={state.Length} bytes)");
        }

        Console.WriteLine($"Codec catalog tests passed ({pixelFormat}: {codecs.Count} compatible encoders).");
    }
}
static async Task ValidateWasapiInitializationAsync()
{
    var devices = await new WindowsAudioDeviceProvider()
        .GetRenderDevicesAsync()
        .ConfigureAwait(false);
    foreach (var device in devices)
    {
        await using var deviceCapture = new WasapiLoopbackCapture();
        await deviceCapture.InitializeAsync(
            new AudioCaptureOptions(DeviceId: device.Id),
            CancellationToken.None).ConfigureAwait(false);
        Assert(deviceCapture.Format.SampleRate > 0,
            $"WASAPI device '{device.DisplayName}' must expose a valid mix format.");
    }

    await using var capture = new WasapiLoopbackCapture();
    await capture.InitializeAsync(new AudioCaptureOptions(), CancellationToken.None).ConfigureAwait(false);
    Assert(capture.Format.SampleRate > 0, "WASAPI sample rate must be positive.");
    Assert(capture.Format.ChannelCount > 0, "WASAPI channel count must be positive.");
    Assert(capture.Format.BitsPerSample > 0, "WASAPI bit depth must be positive.");
    await capture.StartAsync(CancellationToken.None).ConfigureAwait(false);
    await Task.Delay(100).ConfigureAwait(false);
    await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
    Assert(capture.LastError is null, "WASAPI worker must stop without an error.");

    await using var convertedCapture = new WasapiLoopbackCapture();
    await convertedCapture.InitializeAsync(
        new AudioCaptureOptions(
            SampleRate: 44_100,
            SampleFormat: AudioSampleFormat.SignedInteger,
            BitsPerSample: 16),
        CancellationToken.None).ConfigureAwait(false);
    Assert(convertedCapture.Format.SampleRate == 44_100,
        "WASAPI must apply the selected audio sample rate.");
    Assert(convertedCapture.Format.SampleFormat is AudioSampleFormat.SignedInteger &&
        convertedCapture.Format.BitsPerSample == 16,
        "WASAPI must apply the selected PCM codec format.");
    await convertedCapture.StartAsync(CancellationToken.None).ConfigureAwait(false);
    await Task.Delay(100).ConfigureAwait(false);
    await convertedCapture.StopAsync(CancellationToken.None).ConfigureAwait(false);
    Assert(convertedCapture.LastError is null, "Converted WASAPI capture must stop without an error.");

    await using var processCapture = new WasapiLoopbackCapture();
    await processCapture.InitializeAsync(
        new AudioCaptureOptions(ProcessId: Environment.ProcessId),
        CancellationToken.None).ConfigureAwait(false);
    await processCapture.StartAsync(CancellationToken.None).ConfigureAwait(false);
    await Task.Delay(100).ConfigureAwait(false);
    await processCapture.StopAsync(CancellationToken.None).ConfigureAwait(false);
    Assert(processCapture.LastError is null, "Process loopback worker must stop without an error.");
}

static async Task ValidateEndToEndRecordingAsync()
{
    var codecAvailable = VcmVideoEncoder.IsCodecAvailable("UMRG");
    Console.WriteLine($"UtVideo UMRG available: {codecAvailable}.");
    if (!codecAvailable || !GraphicsCaptureSession.IsSupported())
    {
        return;
    }

    var services = new GamenTrailServices();
    var monitor = services.CaptureTargets.GetMonitors()[0];
    var outputPath = Path.Combine(Path.GetTempPath(), $"GamenTrail-E2E-{Guid.NewGuid():N}.avi");
    try
    {
        await using var recorder = GamenTrailServices.CreateRecorder();
        await recorder.StartAsync(new RecordingOptions(
            outputPath,
            new VideoCaptureOptions(monitor.Target, 10),
            new VideoEncoderSettings(),
            new AudioCaptureOptions())).ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
        await recorder.StopAsync().ConfigureAwait(false);

        var statistics = recorder.GetStatistics();
        Assert(statistics.EncodedVideoFrames > 0, "End-to-end recording must encode video frames.");
        Assert(new FileInfo(outputPath).Length > 0, "End-to-end recording must produce a non-empty AVI.");
        var header = new byte[12];
        await using var stream = File.OpenRead(outputPath);
        var bytesRead = await stream.ReadAsync(header).ConfigureAwait(false);
        Assert(bytesRead == header.Length, "End-to-end AVI header is incomplete.");
        Assert(header.AsSpan(0, 4).SequenceEqual("RIFF"u8),
            "End-to-end output must begin with a RIFF header.");
        Assert(header.AsSpan(8, 4).SequenceEqual("AVI "u8),
            "End-to-end output must use the AVI RIFF form.");

        var file = await File.ReadAllBytesAsync(outputPath).ConfigureAwait(false);
        var videoStreamHeader = FindPattern(file, "strh"u8.ToArray());
        Assert(videoStreamHeader >= 0, "End-to-end AVI is missing its video stream header.");
        var nextHeaderRelative = file.AsSpan(videoStreamHeader + 4).IndexOf("strh"u8);
        Assert(nextHeaderRelative >= 0, "End-to-end AVI is missing its audio stream header.");
        var audioStreamHeader = videoStreamHeader + 4 + nextHeaderRelative;
        var videoScale = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(videoStreamHeader + 28, 4));
        var videoRate = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(videoStreamHeader + 32, 4));
        var videoLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(videoStreamHeader + 40, 4));
        var audioScale = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(audioStreamHeader + 28, 4));
        var audioRate = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(audioStreamHeader + 32, 4));
        var audioLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(audioStreamHeader + 40, 4));
        var videoDuration = videoLength * (double)videoScale / videoRate;
        var audioDuration = audioLength * (double)audioScale / audioRate;
        Assert(Math.Abs(videoDuration - audioDuration) <= (videoScale / (double)videoRate) + 0.01,
            $"End-to-end AVI audio and video durations must agree within one video frame. " +
            $"Video={videoDuration:F6}s ({videoLength} frames), Audio={audioDuration:F6}s ({audioLength} samples), " +
            $"CapturedAudioPackets={statistics.CapturedAudioPackets}, CapturedVideoFrames={statistics.CapturedVideoFrames}.");
    }
    finally
    {
        File.Delete(outputPath);
    }
}

static async Task ValidateWindowsGraphicsCaptureInitializationAsync()
{
    if (!GraphicsCaptureSession.IsSupported())
    {
        return;
    }

    var monitor = NativeMethods.MonitorFromPoint(default, 2);
    Assert(monitor != 0, "Could not resolve the primary monitor handle.");

    await using var capture = new WindowsGraphicsCapture();
    const int regionWidth = 320;
    const int regionHeight = 240;
    await capture.InitializeAsync(
        new VideoCaptureOptions(
            new CaptureTarget.Region(monitor, 16, 16, regionWidth, regionHeight),
            60),
        CancellationToken.None).ConfigureAwait(false);
    Assert(capture.Format.Width == regionWidth, "Region capture width must match the requested width.");
    Assert(capture.Format.Height == regionHeight, "Region capture height must match the requested height.");

    var firstFrame = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    capture.FrameArrived += OnFrameArrived;
    await capture.StartAsync(CancellationToken.None).ConfigureAwait(false);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var frame = await firstFrame.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    Assert(frame.Buffer is WindowsCaptureFrameBuffer, "Capture must return a GPU-backed frame.");
    Assert(frame.Buffer.Width >= regionWidth, "Captured source must contain the requested region width.");
    Assert(frame.Buffer.Height >= regionHeight, "Captured source must contain the requested region height.");
    Assert(frame.Timestamp >= TimeSpan.Zero, "Captured frame timestamp must not be negative.");

    using (var textureReader = new D3D11TextureReader())
    using (var pixels = textureReader.Read(
               (WindowsCaptureFrameBuffer)frame.Buffer,
               capture.Format.Width,
               capture.Format.Height))
    {
        Assert(
            pixels.Length == checked(capture.Format.Width * capture.Format.Height * 4),
            "D3D11 readback must return tightly packed BGRA32 pixels.");
    }

    using (var reader = new D3D11TextureReader())
    using (var padded = reader.Read((WindowsCaptureFrameBuffer)frame.Buffer,
        capture.Format.Width + 1, capture.Format.Height + 1, capture.Format.Width, capture.Format.Height))
    {
        var rowBytes = (capture.Format.Width + 1) * 4;
        Assert(padded.Memory.Span[..rowBytes].IndexOfAnyExcept((byte)0) < 0,
            "Bottom padding must be black, not pixels outside the capture region.");
        for (var row = 0; row <= capture.Format.Height; row++)
            Assert(padded.Memory.Span.Slice(row * rowBytes + capture.Format.Width * 4, 4).IndexOfAnyExcept((byte)0) < 0,
                "Right padding must be black, not pixels outside the capture region.");
    }

    foreach (var pixelFormat in Enum.GetValues<VideoEncodingPixelFormat>())
    {
        var codec = new VcmCodecProvider().GetCodecs(pixelFormat)
            .FirstOrDefault(item => item.DisplayName.StartsWith("UtVideo", StringComparison.Ordinal));
        if (codec is null) continue;
        await using var encoder = new VcmVideoEncoder();
        await encoder.InitializeAsync(capture.Format,
            new VideoEncoderSettings(codec.FourCc, PixelFormat: pixelFormat),
            CancellationToken.None).ConfigureAwait(false);
        using var encodedFrame = await encoder.EncodeAsync(frame, frame.Timestamp, CancellationToken.None).ConfigureAwait(false);
        Assert(encodedFrame.Buffer.Length > 0, "Captured RGB/YUV frames must compress successfully.");
        Console.WriteLine($"Capture to {pixelFormat} compression passed.");
    }
    capture.FrameArrived -= OnFrameArrived;
    await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);

    void OnFrameArrived(object? sender, VideoFrame frame)
    {
        if (!firstFrame.TrySetResult(frame))
        {
            frame.Dispose();
        }
    }
}

static async Task ValidateInvalidWindowCaptureAsync()
{
    if (!GraphicsCaptureSession.IsSupported())
    {
        return;
    }

    await using var capture = new WindowsGraphicsCapture();
    try
    {
        await capture.InitializeAsync(
            new VideoCaptureOptions(new CaptureTarget.Window(1), 30),
            CancellationToken.None).ConfigureAwait(false);
        throw new InvalidOperationException("An invalid window handle must not initialize capture.");
    }
    catch (InvalidOperationException error) when (
        error.Message.Contains("no longer available", StringComparison.Ordinal))
    {
    }
}

static async Task WriteSampleAsync(string outputPath)
{
    var videoFormat = new EncodedVideoFormat(
        "V_MS/VFW/FOURCC",
        CreateBitmapInfoHeader(1920, 1080),
        1920,
        1080,
        60);
    var audioFormat = new AudioFormat(48_000, 2, 32, AudioSampleFormat.IeeeFloat);
    var configuration = new MuxerConfiguration(videoFormat, audioFormat, TimeSpan.FromMilliseconds(1));

    await using var writer = new FileMediaMuxer();
    await writer.OpenAsync(outputPath, configuration, CancellationToken.None).ConfigureAwait(false);

    using var videoBuffer = MediaBuffer.CopyFrom([0x10, 0x20, 0x30, 0x40]);
    using var videoFrame = new EncodedVideoFrame(
        videoBuffer,
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1d / 60),
        isKeyFrame: true);
    await writer.WriteVideoFrameAsync(videoFrame, CancellationToken.None).ConfigureAwait(false);

    using var audioBuffer = MediaBuffer.CopyFrom(new byte[384]);
    using var audioPacket = new AudioPacket(
        audioBuffer,
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1));
    await writer.WriteAudioPacketAsync(audioPacket, CancellationToken.None).ConfigureAwait(false);
    await writer.FinalizeAsync(CancellationToken.None).ConfigureAwait(false);
}

static async Task WriteAviSampleAsync(string outputPath)
{
    var videoFormat = new EncodedVideoFormat(
        "V_MS/VFW/FOURCC",
        CreateBitmapInfoHeader(320, 240),
        320,
        240,
        30);
    var audioFormat = new AudioFormat(48_000, 2, 32, AudioSampleFormat.IeeeFloat);
    var configuration = new MuxerConfiguration(videoFormat, audioFormat, TimeSpan.FromMilliseconds(1));

    await using var writer = new FileMediaMuxer();
    await writer.OpenAsync(outputPath, configuration, CancellationToken.None).ConfigureAwait(false);

    using var keyBuffer = MediaBuffer.CopyFrom([0x10, 0x20, 0x30, 0x40]);
    using var keyFrame = new EncodedVideoFrame(
        keyBuffer,
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1d / 30),
        isKeyFrame: true);
    await writer.WriteVideoFrameAsync(keyFrame, CancellationToken.None).ConfigureAwait(false);

    using var audioBuffer = MediaBuffer.CopyFrom(new byte[384]);
    using var audioPacket = new AudioPacket(
        audioBuffer,
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(1));
    await writer.WriteAudioPacketAsync(audioPacket, CancellationToken.None).ConfigureAwait(false);

    using var deltaBuffer = MediaBuffer.CopyFrom([0x50, 0x60, 0x70, 0x80]);
    using var deltaFrame = new EncodedVideoFrame(
        deltaBuffer,
        TimeSpan.FromSeconds(1d / 30),
        TimeSpan.FromSeconds(1d / 30),
        isKeyFrame: false);
    await writer.WriteVideoFrameAsync(deltaFrame, CancellationToken.None).ConfigureAwait(false);
    await writer.FinalizeAsync(CancellationToken.None).ConfigureAwait(false);
}

static async Task WriteVideoOnlySampleAsync(string outputPath)
{
    var videoFormat = new EncodedVideoFormat(
        "V_MS/VFW/FOURCC",
        CreateBitmapInfoHeader(320, 240),
        320,
        240,
        30);
    var configuration = new MuxerConfiguration(
        videoFormat,
        Audio: null,
        TimecodeScale: TimeSpan.FromMilliseconds(1));

    await using var writer = new FileMediaMuxer();
    await writer.OpenAsync(outputPath, configuration, CancellationToken.None).ConfigureAwait(false);
    using var buffer = MediaBuffer.CopyFrom([0x10, 0x20, 0x30, 0x40]);
    using var frame = new EncodedVideoFrame(
        buffer,
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1d / 30),
        isKeyFrame: true);
    await writer.WriteVideoFrameAsync(frame, CancellationToken.None).ConfigureAwait(false);
    await writer.FinalizeAsync(CancellationToken.None).ConfigureAwait(false);
}

static byte[] CreateBitmapInfoHeader(int width, int height)
{
    var result = new byte[40];
    BitConverter.GetBytes(40).CopyTo(result, 0);
    BitConverter.GetBytes(width).CopyTo(result, 4);
    BitConverter.GetBytes(height).CopyTo(result, 8);
    BitConverter.GetBytes((ushort)1).CopyTo(result, 12);
    BitConverter.GetBytes((ushort)32).CopyTo(result, 14);
    Encoding.ASCII.GetBytes("ULRG").CopyTo(result, 16);
    return result;
}

static void ValidateSample(byte[] file)
{
    AssertStartsWith(file, [0x1A, 0x45, 0xDF, 0xA3], "EBML header");
    AssertContains(file, Encoding.ASCII.GetBytes("matroska"), "Matroska document type");
    AssertContains(file, Encoding.ASCII.GetBytes("V_MS/VFW/FOURCC"), "video codec ID");
    AssertContains(file, Encoding.ASCII.GetBytes("A_PCM/FLOAT/IEEE"), "audio codec ID");
    AssertContains(file, [0x1F, 0x43, 0xB6, 0x75], "Cluster");
    AssertContains(file, [0x1C, 0x53, 0xBB, 0x6B], "Cues");

    var simpleBlockCount = CountPattern(file, [0xA3]);
    Assert(simpleBlockCount >= 2, "Expected both a video and an audio SimpleBlock.");

    var clusterOffset = FindPattern(file, [0x1F, 0x43, 0xB6, 0x75]);
    var clusterSize = file.AsSpan(clusterOffset + 4, 8);
    Assert(clusterSize[0] == 0x01, "Cluster size must use an eight-byte VINT.");
    Assert(!clusterSize[1..].ToArray().All(static value => value == byte.MaxValue),
        "Cluster size must be patched during finalization.");

    var durationOffset = FindPattern(file, [0x44, 0x89]);
    Assert(durationOffset >= 0, "Missing Duration.");
    Assert(file[durationOffset + 2] == 0x88, "Duration must contain an eight-byte float.");
    var durationBits = BinaryPrimitives.ReadInt64BigEndian(file.AsSpan(durationOffset + 3, 8));
    var duration = BitConverter.Int64BitsToDouble(durationBits);
    Assert(duration > 16 && duration < 17, "Duration must be patched in timestamp-scale units.");
}

static void ValidateAviSample(byte[] file)
{
    AssertStartsWith(file, "RIFF"u8.ToArray(), "RIFF header");
    Assert(file.AsSpan(8, 4).SequenceEqual("AVI "u8), "AVI form type is missing.");
    AssertContains(file, "hdrl"u8.ToArray(), "AVI header list");
    AssertContains(file, "movi"u8.ToArray(), "AVI movie list");
    AssertContains(file, "idx1"u8.ToArray(), "AVI 1.0 index");
    AssertContains(file, "indx"u8.ToArray(), "OpenDML super index");
    AssertContains(file, "ix00"u8.ToArray(), "OpenDML video index");
    AssertContains(file, "ix01"u8.ToArray(), "OpenDML audio index");
    AssertContains(file, "ULRG"u8.ToArray(), "UtVideo handler");

    var videoStreamHeaderOffset = FindPattern(file, "strh"u8.ToArray());
    Assert(videoStreamHeaderOffset >= 0, "Missing video stream header.");
    var videoScale = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(videoStreamHeaderOffset + 28, 4));
    var videoRate = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(videoStreamHeaderOffset + 32, 4));
    Assert(videoRate == 30 && videoScale == 1,
        "AVI video rate must remain at the configured constant frame rate.");

    var idx1Offset = FindPattern(file, "idx1"u8.ToArray());
    Assert(idx1Offset >= 0, "Missing idx1 chunk.");
    var indexSize = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(idx1Offset + 4, 4));
    Assert(indexSize == 48, "idx1 must contain two video entries and one audio entry.");

    var firstVideoFlags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(idx1Offset + 12, 4));
    Assert((firstVideoFlags & 0x10) != 0, "The first AVI video frame must be indexed as a keyframe.");
    var secondVideoOffset = idx1Offset + 8 + 32;
    Assert(file.AsSpan(secondVideoOffset, 4).SequenceEqual("00dc"u8),
        "The third idx1 entry must be the second video frame.");
    var secondVideoFlags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(secondVideoOffset + 4, 4));
    Assert((secondVideoFlags & 0x10) == 0, "A delta frame must not be indexed as a keyframe.");
}

static void ValidateVideoOnlyMkv(byte[] file)
{
    AssertContains(file, "V_MS/VFW/FOURCC"u8.ToArray(), "video-only MKV video codec ID");
    Assert(file.AsSpan().IndexOf("A_PCM"u8) < 0, "Video-only MKV must not contain an audio track.");
}

static void ValidateVideoOnlyAvi(byte[] file)
{
    var mainHeaderOffset = FindPattern(file, "avih"u8.ToArray());
    Assert(mainHeaderOffset >= 0, "Video-only AVI is missing its main header.");
    var streamCount = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(mainHeaderOffset + 32, 4));
    Assert(streamCount == 1, "Video-only AVI must declare exactly one stream.");
    Assert(file.AsSpan().IndexOf("auds"u8) < 0, "Video-only AVI must not contain an audio stream header.");
    Assert(file.AsSpan().IndexOf("01wb"u8) < 0, "Video-only AVI must not contain audio chunks.");
}

static void AssertStartsWith(byte[] source, byte[] expected, string name)
{
    Assert(source.AsSpan().StartsWith(expected), $"Missing {name}.");
}

static void AssertContains(byte[] source, byte[] expected, string name)
{
    Assert(FindPattern(source, expected) >= 0, $"Missing {name}.");
}

static int CountPattern(byte[] source, byte[] expected)
{
    var count = 0;
    var offset = 0;

    while (offset <= source.Length - expected.Length)
    {
        var match = source.AsSpan(offset).IndexOf(expected);
        if (match < 0)
        {
            break;
        }

        count++;
        offset += match + expected.Length;
    }

    return count;
}

static int FindPattern(byte[] source, byte[] expected) => source.AsSpan().IndexOf(expected);

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativePoint(int X, int Y);

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(NativePoint point, uint flags);
}
