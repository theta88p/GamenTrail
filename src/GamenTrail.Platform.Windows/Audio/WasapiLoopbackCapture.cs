using System.Diagnostics;
using System.Runtime.InteropServices;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Media;

namespace GamenTrail.Platform.Windows.Audio;

public sealed partial class WasapiLoopbackCapture : IAudioCapture
{
    private const uint ClassContextAll = 23;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint StreamFlagsSourceDefaultQuality = 0x08000000;
    private const uint BufferFlagsSilent = 0x00000002;
    private const uint BufferFlagsTimestampError = 0x00000004;
    private const int RpcChangedMode = unchecked((int)0x80010106);
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private const ushort VariantTypeBlob = 65;
    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid MultimediaDeviceEnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
    private readonly object _sync = new();
    private AudioCaptureOptions? _options;
    private AudioFormat? _format;
    private Thread? _captureThread;
    private ManualResetEvent? _stopEvent;
    private TaskCompletionSource? _startup;
    private long _timestampOriginTicks;

    public event EventHandler<AudioPacket>? PacketArrived;

    public event EventHandler<CaptureFaultedEventArgs>? CaptureFaulted;

    public static bool IsProcessLoopbackSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public AudioFormat Format => _format ?? throw new InvalidOperationException("Audio capture is not initialized.");

    public Exception? LastError { get; private set; }

    public ValueTask InitializeAsync(AudioCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Process ID must be positive.");
        }

        if (options.ProcessId is not null && !IsProcessLoopbackSupported)
        {
            throw new PlatformNotSupportedException(
                "Process loopback capture requires Windows 10 build 20348 or later.");
        }

        lock (_sync)
        {
            if (_captureThread is not null)
            {
                throw new InvalidOperationException("Audio capture is already running.");
            }

            var sourceFormat = options.ProcessId is null
                ? QueryMixFormat(options.DeviceId)
                : CreateProcessLoopbackFormat();
            _format = CreateRequestedFormat(sourceFormat, options);
            _options = options;
            LastError = null;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        Task startupTask;
        lock (_sync)
        {
            if (_options is null || _format is null)
            {
                throw new InvalidOperationException("Audio capture is not initialized.");
            }

            if (_captureThread is not null)
            {
                throw new InvalidOperationException("Audio capture is already running.");
            }

            _stopEvent = new ManualResetEvent(initialState: false);
            _startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            startupTask = _startup.Task;
            _captureThread = new Thread(CaptureThreadMain)
            {
                IsBackground = true,
                Name = "GamenTrail WASAPI Loopback",
                Priority = ThreadPriority.AboveNormal,
            };
            _timestampOriginTicks = GetPerformanceCounterTicks();
            _captureThread.Start();
        }

        await startupTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Thread? thread;
        lock (_sync)
        {
            thread = _captureThread;
            _stopEvent?.Set();
        }

        if (thread is not null)
        {
            await Task.Run(thread.Join, cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _stopEvent?.Dispose();
            _stopEvent = null;
            _captureThread = null;
            _startup = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        PacketArrived = null;
        CaptureFaulted = null;
        _options = null;
        _format = null;
    }

    private void CaptureThreadMain()
    {
        var initializeResult = CoInitializeEx(0, 0);
        var uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcChangedMode)
        {
            _startup?.TrySetException(Marshal.GetExceptionForHR(initializeResult) ??
                new InvalidOperationException("COM initialization failed."));
            return;
        }

        object? enumerator = null;
        object? device = null;
        object? audioClientObject = null;
        object? captureClientObject = null;
        AutoResetEvent? audioReady = null;

        try
        {
            var options = _options ?? throw new InvalidOperationException("Audio capture is not initialized.");
            var format = _format ?? throw new InvalidOperationException("Audio capture is not initialized.");
            if (options.ProcessId is { } processId)
            {
                audioClientObject = ActivateProcessAudioClient(processId, options.IncludeProcessTree);
            }
            else
            {
                enumerator = CreateDeviceEnumerator();
                device = GetRenderDevice((IMultimediaDeviceEnumerator)enumerator, options.DeviceId);
                audioClientObject = Activate(device, AudioClientId);
            }

            var audioClient = (IAudioClient)audioClientObject;
            var formatPointer = options.ProcessId is null && !HasFormatOverride(options)
                ? GetMixFormat(audioClient)
                : AllocateWaveFormat(format);
            try
            {
                var streamFlags = StreamFlagsLoopback | StreamFlagsEventCallback;
                if (options.ProcessId is not null || HasFormatOverride(options))
                {
                    streamFlags |= StreamFlagsAutoConvertPcm | StreamFlagsSourceDefaultQuality;
                }

                Marshal.ThrowExceptionForHR(audioClient.Initialize(
                    0,
                    streamFlags,
                    0,
                    0,
                    formatPointer,
                    0));
            }
            finally
            {
                Marshal.FreeCoTaskMem(formatPointer);
            }

            audioReady = new AutoResetEvent(initialState: false);
            Marshal.ThrowExceptionForHR(audioClient.SetEventHandle(audioReady.SafeWaitHandle.DangerousGetHandle()));
            captureClientObject = GetService(audioClient, AudioCaptureClientId);
            var captureClient = (IAudioCaptureClient)captureClientObject;
            Marshal.ThrowExceptionForHR(audioClient.Start());
            _startup?.TrySetResult();

            var waitHandles = new WaitHandle[]
            {
                audioReady,
                _stopEvent ?? throw new InvalidOperationException("Stop event is unavailable."),
            };
            var timeline = new AudioCaptureTimeline(format.SampleRate);
            while (WaitHandle.WaitAny(waitHandles) == 0)
            {
                DrainPackets(captureClient, format, timeline);
            }

            Marshal.ThrowExceptionForHR(audioClient.Stop());
        }
        catch (Exception error)
        {
            LastError = error;
            if (_startup?.Task.IsCompletedSuccessfully == true)
            {
                CaptureFaulted?.Invoke(this, new CaptureFaultedEventArgs(error));
            }
            else
            {
                _startup?.TrySetException(error);
            }
        }
        finally
        {
            audioReady?.Dispose();
            FinalRelease(captureClientObject);
            FinalRelease(audioClientObject);
            FinalRelease(device);
            FinalRelease(enumerator);
            if (uninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private unsafe void DrainPackets(IAudioCaptureClient captureClient, AudioFormat format, AudioCaptureTimeline timeline)
    {
        Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out var packetFrames));
        while (packetFrames > 0)
        {
            nint data = 0;
            uint frames = 0;
            uint flags = 0;
            Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                out data,
                out frames,
                out flags,
                out _,
                out var qpcPosition));

            try
            {
                var byteCount = checked((int)(frames * GetBlockAlignment(format)));
                MediaBuffer buffer;
                if ((flags & BufferFlagsSilent) != 0)
                {
                    buffer = MediaBuffer.CopyFrom(new byte[byteCount]);
                }
                else
                {
                    buffer = MediaBuffer.CopyFrom(new ReadOnlySpan<byte>((void*)data, byteCount));
                }

                var duration = TimeSpan.FromSeconds(frames / (double)format.SampleRate);
                // Count converted PCM frames; endpoint device positions can use a different sample rate.
                var timestamp = timeline.GetTimestamp(frames, flags,
                    checked((long)qpcPosition) - _timestampOriginTicks,
                    GetPerformanceCounterTicks() - _timestampOriginTicks - duration.Ticks);
                PublishPacket(new AudioPacket(buffer, timestamp, duration));
            }
            finally
            {
                Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames));
            }

            Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetFrames));
        }
    }

    private static long GetPerformanceCounterTicks() =>
        checked((long)(Stopwatch.GetTimestamp() * (decimal)TimeSpan.TicksPerSecond / Stopwatch.Frequency));

    private void PublishPacket(AudioPacket packet)
    {
        var handler = PacketArrived;
        if (handler is null)
        {
            packet.Dispose();
            return;
        }

        handler(this, packet);
    }

    private static AudioFormat QueryMixFormat(string? deviceId)
    {
        var initializeResult = CoInitializeEx(0, 0);
        var uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcChangedMode)
        {
            Marshal.ThrowExceptionForHR(initializeResult);
        }

        object? enumerator = null;
        object? device = null;
        object? audioClientObject = null;
        try
        {
            enumerator = CreateDeviceEnumerator();
            device = GetRenderDevice((IMultimediaDeviceEnumerator)enumerator, deviceId);
            audioClientObject = Activate(device, AudioClientId);
            var formatPointer = GetMixFormat((IAudioClient)audioClientObject);
            try
            {
                return ParseWaveFormat(formatPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(formatPointer);
            }
        }
        finally
        {
            FinalRelease(audioClientObject);
            FinalRelease(device);
            FinalRelease(enumerator);
            if (uninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static AudioFormat CreateProcessLoopbackFormat() =>
        new(48_000, 2, 32, AudioSampleFormat.IeeeFloat);

    private static AudioFormat CreateRequestedFormat(AudioFormat source, AudioCaptureOptions options)
    {
        if (!HasFormatOverride(options))
        {
            return source;
        }

        var sampleRate = options.SampleRate ?? source.SampleRate;
        var sampleFormat = options.SampleFormat ?? source.SampleFormat;
        var bitsPerSample = options.BitsPerSample ?? source.BitsPerSample;
        if (sampleRate is not (44_100 or 48_000))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sample rate must be 44100 or 48000 Hz.");
        }

        if ((sampleFormat, bitsPerSample) is not
            (AudioSampleFormat.SignedInteger, 16) and not
            (AudioSampleFormat.IeeeFloat, 32))
        {
            throw new ArgumentException("Only PCM 16-bit and IEEE Float 32-bit audio are supported.", nameof(options));
        }

        return new AudioFormat(sampleRate, options.ChannelCount ?? source.ChannelCount, bitsPerSample, sampleFormat);
    }

    private static bool HasFormatOverride(AudioCaptureOptions options) =>
        options.SampleRate is not null || options.SampleFormat is not null || options.BitsPerSample is not null;

    private static nint AllocateWaveFormat(AudioFormat format)
    {
        var waveFormat = new WaveFormatEx
        {
            FormatTag = format.SampleFormat is AudioSampleFormat.IeeeFloat
                ? WaveFormatIeeeFloat
                : WaveFormatPcm,
            Channels = checked((ushort)format.ChannelCount),
            SamplesPerSecond = checked((uint)format.SampleRate),
            BitsPerSample = checked((ushort)format.BitsPerSample),
            BlockAlign = checked((ushort)GetBlockAlignment(format)),
            AverageBytesPerSecond = checked((uint)(format.SampleRate * GetBlockAlignment(format))),
        };
        var pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatEx>());
        Marshal.StructureToPtr(waveFormat, pointer, fDeleteOld: false);
        return pointer;
    }

    private static object ActivateProcessAudioClient(int processId, bool includeProcessTree)
    {
        var activationParameters = new AudioClientActivationParameters
        {
            ActivationType = 1,
            ProcessLoopbackParameters = new AudioClientProcessLoopbackParameters
            {
                TargetProcessId = checked((uint)processId),
                ProcessLoopbackMode = includeProcessTree ? 0u : 1u,
            },
        };
        var activationData = Marshal.AllocCoTaskMem(Marshal.SizeOf<AudioClientActivationParameters>());
        try
        {
            Marshal.StructureToPtr(activationParameters, activationData, fDeleteOld: false);
            var variant = new PropVariant
            {
                ValueType = VariantTypeBlob,
                Blob = new Blob
                {
                    Size = checked((uint)Marshal.SizeOf<AudioClientActivationParameters>()),
                    Data = activationData,
                },
            };
            using var handler = new ActivateAudioCompletionHandler();
            var audioClientId = AudioClientId;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                ProcessLoopbackDevice,
                in audioClientId,
                in variant,
                handler,
                out var operation));
            try
            {
                return handler.WaitForResult();
            }
            finally
            {
                FinalRelease(operation);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(activationData);
        }
    }

    private static object CreateDeviceEnumerator()
    {
        var type = Type.GetTypeFromCLSID(MultimediaDeviceEnumeratorId, throwOnError: true) ??
            throw new InvalidOperationException("MMDeviceEnumerator is unavailable.");
        return Activator.CreateInstance(type) ??
            throw new InvalidOperationException("MMDeviceEnumerator could not be created.");
    }

    private static IMultimediaDevice GetRenderDevice(IMultimediaDeviceEnumerator enumerator, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out var defaultDevice));
            return defaultDevice;
        }

        Marshal.ThrowExceptionForHR(enumerator.GetDevice(
            AudioEndpointId.Normalize(deviceId) ?? deviceId,
            out var device));
        return device;
    }

    private static object Activate(object device, Guid interfaceId)
    {
        var id = interfaceId;
        Marshal.ThrowExceptionForHR(((IMultimediaDevice)device).Activate(in id, ClassContextAll, 0, out var result));
        return result;
    }

    private static object GetService(IAudioClient audioClient, Guid interfaceId)
    {
        var id = interfaceId;
        Marshal.ThrowExceptionForHR(audioClient.GetService(in id, out var result));
        return result;
    }

    private static nint GetMixFormat(IAudioClient audioClient)
    {
        Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out var format));
        return format;
    }

    private static AudioFormat ParseWaveFormat(nint pointer)
    {
        var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(pointer);
        var sampleFormat = waveFormat.FormatTag switch
        {
            WaveFormatPcm => AudioSampleFormat.SignedInteger,
            WaveFormatIeeeFloat => AudioSampleFormat.IeeeFloat,
            WaveFormatExtensible => ParseExtensibleSubFormat(pointer),
            _ => throw new NotSupportedException($"Unsupported WASAPI mix format tag: {waveFormat.FormatTag}."),
        };

        return new AudioFormat(
            checked((int)waveFormat.SamplesPerSecond),
            waveFormat.Channels,
            waveFormat.BitsPerSample,
            sampleFormat);
    }

    private static AudioSampleFormat ParseExtensibleSubFormat(nint pointer)
    {
        var subFormat = Marshal.PtrToStructure<Guid>(pointer + 24);
        if (subFormat == PcmSubFormat)
        {
            return AudioSampleFormat.SignedInteger;
        }

        if (subFormat == IeeeFloatSubFormat)
        {
            return AudioSampleFormat.IeeeFloat;
        }

        throw new NotSupportedException($"Unsupported WASAPI extensible subformat: {subFormat}.");
    }

    private static uint GetBlockAlignment(AudioFormat format) =>
        checked((uint)(format.ChannelCount * format.BitsPerSample / 8));

    private static void FinalRelease(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParameters
    {
        public uint TargetProcessId;

        public uint ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParameters
    {
        public uint ActivationType;

        public AudioClientProcessLoopbackParameters ProcessLoopbackParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public uint Size;

        public nint Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort ValueType;

        [FieldOffset(8)]
        public Blob Blob;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivateAudioCompletionHandler : IActivateAudioInterfaceCompletionHandler, IDisposable
    {
        private readonly ManualResetEventSlim _completed = new(initialState: false);
        private object? _result;
        private Exception? _error;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(
                    out var activationResult,
                    out var activatedInterface));
                Marshal.ThrowExceptionForHR(activationResult);
                _result = activatedInterface;
            }
            catch (Exception error)
            {
                _error = error;
            }
            finally
            {
                _completed.Set();
            }

            return 0;
        }

        public object WaitForResult()
        {
            _completed.Wait();
            if (_error is not null)
            {
                throw new InvalidOperationException("Process loopback activation failed.", _error);
            }

            return _result ?? throw new InvalidOperationException("Process loopback returned no audio client.");
        }

        public void Dispose() => _completed.Dispose();
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(
            out int activationResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMultimediaDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMultimediaDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMultimediaDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMultimediaDevice
    {
        [PreserveSig]
        int Activate(
            in Guid interfaceId,
            uint classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(uint access, out nint properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            int shareMode,
            uint streamFlags,
            long bufferDuration,
            long periodicity,
            nint format,
            nint sessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint frames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint frames);

        [PreserveSig]
        int IsFormatSupported(int shareMode, nint format, out nint closestMatch);

        [PreserveSig]
        int GetMixFormat(out nint format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(nint eventHandle);

        [PreserveSig]
        int GetService(in Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(
            out nint data,
            out uint frames,
            out uint flags,
            out ulong devicePosition,
            out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint frames);

        [PreserveSig]
        int GetNextPacketSize(out uint frames);
    }

#pragma warning disable SYSLIB1054 // COM interface callbacks are not supported by LibraryImport source generation.
    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        in Guid interfaceId,
        in PropVariant activationParameters,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
#pragma warning restore SYSLIB1054
}
