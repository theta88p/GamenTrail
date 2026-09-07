using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GamenTrail.Core.Media;
using GamenTrail.Core.Video;

namespace GamenTrail.Platform.Windows.Video;

public sealed unsafe partial class VcmVideoEncoder : IVideoEncoder
{
    private const uint IcModeCompress = 1;
    private const uint IcCompressKeyFrame = 1;
    private const uint AviIfKeyFrame = 0x10;
    private const uint IcQualityDefault = uint.MaxValue;
    private const uint BitmapCompressionRgb = 0;
    private const uint IcmCompressGetFormat = 0x4004;
    private const uint IcmCompressGetSize = 0x4005;
    private const uint IcmCompressQuery = 0x4006;
    private const uint IcmCompressBegin = 0x4007;
    private const uint IcmCompress = 0x4008;
    private const uint IcmCompressEnd = 0x4009;

    private readonly D3D11TextureReader _textureReader = new();
    private nint _codec;
    private nint _inputFormat;
    private nint _outputFormat;
    private int _maximumEncodedSize;
    private int _frameNumber;
    private VideoEncodingPixelFormat _pixelFormat;
    private int _captureWidth;
    private int _captureHeight;
    private bool _compressionBegun;
    private VideoFormat? _inputVideoFormat;
    private EncodedVideoFormat? _encodedVideoFormat;

    public EncodedVideoFormat OutputFormat =>
        _encodedVideoFormat ?? throw new InvalidOperationException("Encoder is not initialized.");

    public ValueTask InitializeAsync(VideoFormat inputFormat, VideoEncoderSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.AutoSelectInputFormat)
        {
            InitializeCore(inputFormat, settings, cancellationToken);
            return ValueTask.CompletedTask;
        }
        var failures = new List<string>();
        foreach (var format in Enum.GetValues<VideoEncodingPixelFormat>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = settings with { PixelFormat = format, AutoSelectInputFormat = false };
            try
            {
                InitializeCore(inputFormat, candidate, cancellationToken);
                var size = checked(OutputFormat.Width * OutputFormat.Height * 4);
                using (var probe = EncodePixels(new byte[size], TimeSpan.Zero)) { }
                // Discard the probe and restart so recording begins with a fresh keyframe.
                InitializeCore(inputFormat, candidate, cancellationToken);
                return ValueTask.CompletedTask;
            }
            catch (VcmCodecException error)
            {
                ResetCodec();
                failures.Add($"{format}: {error.Message}");
            }
        }
        throw new VcmCodecException("対応する入力形式で圧縮を開始できませんでした。 " + string.Join(" / ", failures));
    }

    private void InitializeCore(
        VideoFormat inputFormat,
        VideoEncoderSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateInputFormat(inputFormat);
        cancellationToken.ThrowIfCancellationRequested();
        ResetCodec();
        _captureWidth = inputFormat.Width;
        _captureHeight = inputFormat.Height;
        var dimensions = VideoPixelConverter.AlignDimensions(inputFormat.Width, inputFormat.Height, settings.PixelFormat);
        inputFormat = inputFormat with { Width = dimensions.Width, Height = dimensions.Height };
        _pixelFormat = settings.PixelFormat;

        _codec = IcOpen(ToFourCc("vidc"), ToFourCc(settings.CodecFourCc), IcModeCompress);
        if (_codec == 0)
        {
            throw new VcmCodecException($"VCM codec '{settings.CodecFourCc}' is not installed or cannot encode.");
        }

        try
        {
            RestoreCodecState(_codec, settings.CodecState);
            var inputHeader = CreateInputFormat(inputFormat, settings.PixelFormat);
            _inputFormat = Marshal.AllocHGlobal(Marshal.SizeOf<BitmapInfoHeader>());
            Marshal.StructureToPtr(inputHeader, _inputFormat, fDeleteOld: false);

            ThrowForCodecError(
                IcSendMessage(_codec, IcmCompressQuery, _inputFormat, 0),
                $"The codec does not accept {settings.PixelFormat} input at {inputFormat.Width}x{inputFormat.Height}.");

            var outputFormatSize = checked((int)IcSendMessage(_codec, IcmCompressGetFormat, _inputFormat, 0));
            if (outputFormatSize < Marshal.SizeOf<BitmapInfoHeader>())
            {
                throw new VcmCodecException("The codec returned an invalid output format size.");
            }

            _outputFormat = Marshal.AllocHGlobal(outputFormatSize);
            ThrowForCodecError(
                IcSendMessage(_codec, IcmCompressGetFormat, _inputFormat, _outputFormat),
                "The codec did not return an output format.");

            _maximumEncodedSize = checked((int)IcSendMessage(
                _codec,
                IcmCompressGetSize,
                _inputFormat,
                _outputFormat));
            if (_maximumEncodedSize <= 0)
            {
                throw new VcmCodecException("The codec returned an invalid maximum frame size.");
            }

            ThrowForCodecError(
                IcSendMessage(_codec, IcmCompressBegin, _inputFormat, _outputFormat),
                "The codec failed to begin compression.");
            _compressionBegun = true;
            _frameNumber = 0;
            _inputVideoFormat = inputFormat;

            var codecPrivate = new byte[outputFormatSize];
            Marshal.Copy(_outputFormat, codecPrivate, 0, codecPrivate.Length);
            _encodedVideoFormat = new EncodedVideoFormat(
                "V_MS/VFW/FOURCC",
                codecPrivate,
                inputFormat.Width,
                inputFormat.Height,
                inputFormat.FramesPerSecond);
            return;
        }
        catch
        {
            ResetCodec();
            throw;
        }
    }

    public ValueTask<EncodedVideoFrame> EncodeAsync(
        VideoFrame frame,
        TimeSpan outputTimestamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        if (frame.Buffer is not WindowsCaptureFrameBuffer windowsFrame)
        {
            throw new ArgumentException("VCM encoding requires a Windows Graphics Capture frame.", nameof(frame));
        }

        var inputFormat = _inputVideoFormat ?? throw new InvalidOperationException("Encoder is not initialized.");
        if (!_compressionBegun || _codec == 0 || _inputFormat == 0 || _outputFormat == 0)
        {
            throw new InvalidOperationException("Encoder is not initialized.");
        }

        using var rawFrame = _textureReader.Read(windowsFrame, inputFormat.Width, inputFormat.Height, _captureWidth, _captureHeight);
        return ValueTask.FromResult(EncodePixels(rawFrame.Memory, outputTimestamp));
    }

    internal EncodedVideoFrame EncodePixels(ReadOnlyMemory<byte> bottomUpBgra, TimeSpan outputTimestamp)
    {
        var inputFormat = _inputVideoFormat ?? throw new InvalidOperationException("Encoder is not initialized.");
        if (bottomUpBgra.Length != checked(inputFormat.Width * inputFormat.Height * 4))
            throw new ArgumentException("Unexpected capture buffer size.", nameof(bottomUpBgra));
        using var convertedFrame = _pixelFormat == VideoEncodingPixelFormat.Bgra32
            ? null
            : MemoryPool<byte>.Shared.Rent(VideoPixelConverter.GetBufferSize(inputFormat.Width, inputFormat.Height, _pixelFormat));
        if (convertedFrame is not null)
        {
            VideoPixelConverter.Convert(bottomUpBgra.Span, convertedFrame.Memory.Span,
                inputFormat.Width, inputFormat.Height, _pixelFormat);
        }

        IMemoryOwner<byte>? outputOwner = MemoryPool<byte>.Shared.Rent(_maximumEncodedSize);
        try
        {
            using var inputPin = (convertedFrame is null ? bottomUpBgra : convertedFrame.Memory).Pin();
            using var outputPin = outputOwner.Memory.Pin();
            uint chunkId = 0;
            uint outputFlags = 0;
            var compress = new IcCompress
            {
                Flags = IcCompressKeyFrame,
                OutputFormat = _outputFormat,
                Output = (nint)outputPin.Pointer,
                InputFormat = _inputFormat,
                Input = (nint)inputPin.Pointer,
                ChunkId = &chunkId,
                OutputFlags = &outputFlags,
                FrameNumber = _frameNumber,
                Quality = IcQualityDefault,
            };

            ThrowForCodecError(
                IcSendMessage(_codec, IcmCompress, (nint)(&compress), (nint)sizeof(IcCompress)),
                $"The codec failed to compress frame {_frameNumber}.");

            var outputHeader = Marshal.PtrToStructure<BitmapInfoHeader>(_outputFormat);
            var encodedLength = checked((int)outputHeader.ImageSize);
            if (encodedLength <= 0 || encodedLength > _maximumEncodedSize)
            {
                throw new VcmCodecException("The codec returned an invalid encoded frame size.");
            }

            _frameNumber++;
            var mediaBuffer = new MediaBuffer(outputOwner, encodedLength);
            outputOwner = null;
            var encodedFrame = new EncodedVideoFrame(
                mediaBuffer,
                outputTimestamp,
                TimeSpan.FromSeconds(1d / inputFormat.FramesPerSecond),
                isKeyFrame: (outputFlags & AviIfKeyFrame) != 0);
            return encodedFrame;
        }
        finally
        {
            outputOwner?.Dispose();
        }
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        ResetCodec();
        _textureReader.Dispose();
        return ValueTask.CompletedTask;
    }

    public static bool IsCodecAvailable(string codecFourCc, VideoEncodingPixelFormat pixelFormat = VideoEncodingPixelFormat.Bgra32,
        ReadOnlyMemory<byte> state = default)
    {
        var codec = IcOpen(ToFourCc("vidc"), ToFourCc(codecFourCc), IcModeCompress);
        if (codec == 0)
        {
            return false;
        }

        try
        {
            RestoreCodecState(codec, state);
            // Probe the exact layout supplied by the converter.
            var input = CreateInputFormat(new VideoFormat(640, 480, 30, VideoPixelFormat.Bgra32), pixelFormat);
            return IcSendMessage(codec, IcmCompressQuery, (nint)(&input), 0) == 0;
        }
        finally
        {
            var closeResult = IcClose(codec);
            Debug.Assert(closeResult == 0, "VCM codec close failed.");
        }
    }

    private void ResetCodec()
    {
        if (_compressionBegun && _codec != 0)
        {
            IcSendMessage(_codec, IcmCompressEnd, 0, 0);
        }

        _compressionBegun = false;
        if (_codec != 0)
        {
            var closeResult = IcClose(_codec);
            Debug.Assert(closeResult == 0, "VCM codec close failed.");
            _codec = 0;
        }

        Free(ref _inputFormat);
        Free(ref _outputFormat);
        _maximumEncodedSize = 0;
        _frameNumber = 0;
        _inputVideoFormat = null;
        _encodedVideoFormat = null;
    }

    private static BitmapInfoHeader CreateInputFormat(VideoFormat format, VideoEncodingPixelFormat pixelFormat) => new()
    {
        Size = checked((uint)Marshal.SizeOf<BitmapInfoHeader>()),
        Width = format.Width,
        Height = format.Height,
        Planes = 1,
        BitCount = pixelFormat switch
        {
            VideoEncodingPixelFormat.Bgra32 => 32,
            VideoEncodingPixelFormat.Bgr24 => 24,
            VideoEncodingPixelFormat.Yuy2 => 16,
            VideoEncodingPixelFormat.Yv12 or VideoEncodingPixelFormat.Nv12 => 12,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat)),
        },
        Compression = pixelFormat switch
        {
            VideoEncodingPixelFormat.Yuy2 => ToFourCc("YUY2"),
            VideoEncodingPixelFormat.Yv12 => ToFourCc("YV12"),
            VideoEncodingPixelFormat.Nv12 => ToFourCc("NV12"),
            _ => BitmapCompressionRgb,
        },
        ImageSize = checked((uint)VideoPixelConverter.GetBufferSize(format.Width, format.Height, pixelFormat)),
    };

    private static void ValidateInputFormat(VideoFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(format.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(format.Height, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(format.FramesPerSecond, 0);

        if (format.PixelFormat is not VideoPixelFormat.Bgra32)
        {
            throw new NotSupportedException("VCM currently accepts BGRA32 capture frames only.");
        }
    }

    private static uint ToFourCc(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 4 || value.Any(static character => character > 0x7F))
        {
            throw new ArgumentException("A FourCC must contain exactly four ASCII characters.", nameof(value));
        }

        return value[0] | ((uint)value[1] << 8) | ((uint)value[2] << 16) | ((uint)value[3] << 24);
    }

    private static void ThrowForCodecError(nint result, string message)
    {
        if (result != 0)
        {
            throw new VcmCodecException($"{message} VCM error: {result}.");
        }
    }

    private static void Free(ref nint memory)
    {
        if (memory != 0)
        {
            Marshal.FreeHGlobal(memory);
            memory = 0;
        }
    }

    [LibraryImport("msvfw32.dll", EntryPoint = "ICOpen")]
    private static partial nint IcOpen(uint type, uint handler, uint mode);

    [LibraryImport("msvfw32.dll", EntryPoint = "ICClose")]
    private static partial uint IcClose(nint codec);

    [LibraryImport("msvfw32.dll", EntryPoint = "ICSendMessage")]
    private static partial nint IcSendMessage(nint codec, uint message, nint parameter1, nint parameter2);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IcCompress
    {
        public uint Flags;
        public nint OutputFormat;
        public nint Output;
        public nint InputFormat;
        public nint Input;
        public uint* ChunkId;
        public uint* OutputFlags;
        public int FrameNumber;
        public uint FrameSize;
        public uint Quality;
        public nint PreviousFormat;
        public nint Previous;
    }
}

public sealed class VcmCodecException : Exception
{
    public VcmCodecException(string message)
        : base(message)
    {
    }
}
