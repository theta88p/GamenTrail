using System.Buffers.Binary;
using System.Runtime.InteropServices;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Media;

namespace GamenTrail.Platform.Windows.Audio;

public sealed record AcmAudioFormat(string Id, string DisplayName, byte[] WaveFormat);

/// <summary>ACM format enumeration and the Windows audio format chooser.</summary>
public static unsafe partial class AcmAudioCodec
{
    private const uint ConvertFormats = 0x00100000;
    private const uint SameSampleRate = 0x00040000;

    public static IReadOnlyList<AcmAudioFormat> GetFormats(int sampleRate)
    {
        var formats = new List<AcmAudioFormat>();
        var source = CreatePcm(sampleRate, 2);
        var buffer = new byte[GetMaximumFormatSize()];
        source.CopyTo(buffer, 0);
        Exception? failure = null;
        FormatCallback callback = (_, details, _, _) =>
        {
            try
            {
                var bytes = CopyFormat(details->Format);
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
                if (tag is not (1 or 3) && CanEncode(source, bytes))
                    formats.Add(Describe(bytes, new string(details->Description, 0, 128).TrimEnd('\0')));
            }
            catch (Exception error) { failure = error; return 0; }
            return 1;
        };
        fixed (byte* data = buffer)
        {
            var details = new FormatDetails { Size = (uint)sizeof(FormatDetails), Format = (nint)data, FormatSize = (uint)buffer.Length };
            Check(FormatEnum(0, &details, callback, 0, ConvertFormats | SameSampleRate), "音声コーデックを列挙できませんでした");
        }
        GC.KeepAlive(callback);
        if (failure is not null) throw failure;
        return formats.DistinctBy(static format => format.Id).OrderBy(static format => format.DisplayName, StringComparer.CurrentCulture).ToArray();
    }

    public static byte[]? Configure(nint owner, int sampleRate, ReadOnlyMemory<byte> current)
    {
        if (owner == 0) throw new ArgumentException("A parent window is required.", nameof(owner));
        var source = CreatePcm(sampleRate, 2);
        var output = new byte[Math.Max(GetMaximumFormatSize(), current.Length)];
        (current.IsEmpty ? source.AsMemory() : current).CopyTo(output);
        fixed (byte* input = source)
        fixed (byte* data = output)
        fixed (char* title = "音声コーデックの設定")
        {
            var choose = new FormatChoose
            {
                Size = (uint)sizeof(FormatChoose), Style = 0x40, Owner = owner,
                Format = (nint)data, FormatSize = (uint)output.Length, Title = (nint)title,
                EnumFlags = ConvertFormats | SameSampleRate, EnumFormat = (nint)input,
            };
            var result = FormatChooseDialog(&choose);
            if (result == 515) return null; // ACMERR_CANCELED
            Check(result, "音声コーデックの設定画面を開けませんでした");
            var selected = CopyFormat((nint)data);
            if (!CanEncode(source, selected)) throw new NotSupportedException("選択した音声形式では録音できません。");
            return selected;
        }
    }

    public static AcmAudioFormat Describe(byte[] bytes, string? description = null)
    {
        ValidateFormat(bytes);
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var name = tag switch
        {
            1 => "PCM", 2 => "Microsoft ADPCM", 3 => "IEEE Float", 6 => "A-Law",
            7 => "μ-Law", 0x11 => "IMA ADPCM", 0x31 => "GSM 6.10", 0x55 => "MP3",
            _ => $"ACM 0x{tag:X4}",
        };
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
        var bitRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) * 8d / 1000;
        return new(Convert.ToHexString(bytes), $"{name} · {rate} Hz · {channels} ch · {bitRate:0.#} kbps" +
            (string.IsNullOrWhiteSpace(description) ? "" : $" ({description})"), bytes);
    }

    public static byte[] CreatePcm(int sampleRate, int channels)
    {
        var bytes = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), checked((ushort)channels));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checked((uint)(sampleRate * channels * 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), checked((ushort)(channels * 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 16);
        return bytes;
    }

    public static bool CanEncode(byte[] source, byte[] destination)
    {
        ValidateFormat(destination);
        fixed (byte* input = source)
        fixed (byte* output = destination)
            return StreamOpen(out _, 0, (nint)input, (nint)output, 0, 0, 0, 1) == 0;
    }

    public static void ValidateFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 18 || bytes.Length != 18 + BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) == 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) == 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]) == 0)
            throw new InvalidDataException("音声形式の設定データが不正です。");
    }

    private static byte[] CopyFormat(nint pointer)
    {
        var length = 18 + (ushort)Marshal.ReadInt16(pointer, 16);
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return bytes;
    }

    private static int GetMaximumFormatSize()
    {
        Check(Metrics(0, 50, out var size), "音声形式の情報を取得できませんでした");
        return checked((int)Math.Max(18, size));
    }

    internal static void Check(uint result, string message)
    {
        if (result != 0) throw new InvalidOperationException($"{message} (ACM: {result})");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FormatCallback(nint driver, FormatDetails* details, nint instance, uint support);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FormatDetails
    {
        public uint Size, Index, Tag, Support;
        public nint Format;
        public uint FormatSize;
        public fixed char Description[128];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FormatChoose
    {
        public uint Size, Style;
        public nint Owner, Format;
        public uint FormatSize;
        public nint Title;
        public fixed char TagDescription[48];
        public fixed char FormatDescription[128];
        public nint Name;
        public uint NameLength, EnumFlags;
        public nint EnumFormat, Instance, Template, CustomData, Hook;
    }

    [DllImport("msacm32.dll", EntryPoint = "acmFormatEnumW", ExactSpelling = true)]
    private static extern uint FormatEnum(nint driver, FormatDetails* details, FormatCallback callback, nint instance, uint flags);
    [LibraryImport("msacm32.dll", EntryPoint = "acmFormatChooseW")]
    private static partial uint FormatChooseDialog(FormatChoose* choose);
    [LibraryImport("msacm32.dll", EntryPoint = "acmMetrics")]
    private static partial uint Metrics(nint objectHandle, uint metric, out uint value);
    [LibraryImport("msacm32.dll", EntryPoint = "acmStreamOpen")]
    internal static partial uint StreamOpen(out nint stream, nint driver, nint source, nint destination, nint filter, nint callback, nint instance, uint flags);
    [LibraryImport("msacm32.dll", EntryPoint = "acmStreamClose")]
    internal static partial uint StreamClose(nint stream, uint flags);
}

/// <summary>Buffers incomplete input blocks and flushes the final ACM block at stop.</summary>
internal sealed unsafe partial class AcmAudioEncoder : IDisposable
{
    private nint _stream;
    private byte[] _pending = [];
    private bool _started;
    private long _outputBytes;
    private long _inputBytes;
    private readonly int _inputBlockSize;
    private readonly int _averageBytesPerSecond;

    public AcmAudioEncoder(AudioFormat input, byte[] destination)
    {
        if (input.SampleFormat != AudioSampleFormat.SignedInteger || input.BitsPerSample != 16)
            throw new NotSupportedException("ACM録音にはPCM 16-bit入力が必要です。");
        AcmAudioCodec.ValidateFormat(destination);
        var source = AcmAudioCodec.CreatePcm(input.SampleRate, input.ChannelCount);
        fixed (byte* src = source)
        fixed (byte* dst = destination)
            AcmAudioCodec.Check(AcmAudioCodec.StreamOpen(out _stream, 0, (nint)src, (nint)dst, 0, 0, 0, 0), "音声エンコーダーを開始できませんでした");
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(destination);
        _inputBlockSize = tag is 2 or 0x11 or 0x31 && destination.Length >= 20
            ? BinaryPrimitives.ReadUInt16LittleEndian(destination.AsSpan(18)) * input.ChannelCount * 2
            : input.ChannelCount * 2;
        _averageBytesPerSecond = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(destination.AsSpan(8)));
        OutputFormat = new AudioFormat(
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(destination.AsSpan(4))),
            BinaryPrimitives.ReadUInt16LittleEndian(destination.AsSpan(2)),
            BinaryPrimitives.ReadUInt16LittleEndian(destination.AsSpan(14)),
            AudioSampleFormat.SignedInteger, destination);
    }

    public AudioFormat OutputFormat { get; }

    public AudioPacket? ConvertPacket(ReadOnlySpan<byte> source, bool final = false)
    {
        var input = new byte[checked(_pending.Length + source.Length)];
        _pending.CopyTo(input, 0);
        source.CopyTo(input.AsSpan(_pending.Length));
        if (input.Length == 0) return null;
        if (final && _inputBlockSize > 0)
        {
            var padding = (int)((_inputBlockSize - (_inputBytes + input.Length) % _inputBlockSize) % _inputBlockSize);
            if (padding > 0) Array.Resize(ref input, checked(input.Length + padding));
        }
        // Retain a complete PCM frame so END always has a non-empty source buffer.
        var convertLength = final ? input.Length : Math.Max(0, input.Length - 4);
        if (convertLength == 0) { _pending = input; return null; }
        AcmAudioCodec.Check(StreamSize(_stream, checked((uint)Math.Max(input.Length, 65536)), out var required, 0), "音声バッファを確保できませんでした");
        var output = new byte[checked((int)required)];
        fixed (byte* src = input)
        fixed (byte* dst = output)
        {
            var header = new StreamHeader
            {
                Size = (uint)(sizeof(StreamHeader) - (IntPtr.Size == 4 ? 20 : 0)), Source = (nint)src, SourceLength = (uint)convertLength,
                Destination = (nint)dst, DestinationLength = (uint)output.Length,
            };
            AcmAudioCodec.Check(Prepare(_stream, &header, 0), "音声変換を準備できませんでした");
            try
            {
                var flags = (final ? 0x20u : 4u) | (_started ? 0u : 0x10u);
                AcmAudioCodec.Check(Convert(_stream, &header, flags), "音声を圧縮できませんでした");
                _started = true;
                if (header.SourceUsed > input.Length || header.DestinationUsed > output.Length)
                    throw new InvalidDataException("ACM returned invalid buffer lengths.");
                _inputBytes += header.SourceUsed;
                _pending = input.AsSpan((int)header.SourceUsed).ToArray();
                if (final && _pending.Length != 0)
                    throw new InvalidDataException("音声の末尾を完全に圧縮できませんでした。");
                if (header.DestinationUsed == 0) return null;
                var timestamp = TimeSpan.FromSeconds(_outputBytes / (double)_averageBytesPerSecond);
                _outputBytes += header.DestinationUsed;
                return new AudioPacket(MediaBuffer.CopyFrom(output.AsSpan(0, (int)header.DestinationUsed)),
                    timestamp, TimeSpan.FromSeconds(header.DestinationUsed / (double)_averageBytesPerSecond));
            }
            finally { AcmAudioCodec.Check(Unprepare(_stream, &header, 0), "音声変換バッファを解放できませんでした"); }
        }
    }

    public void Dispose()
    {
        if (_stream == 0) return;
        var stream = _stream;
        _stream = 0;
        AcmAudioCodec.Check(AcmAudioCodec.StreamClose(stream, 0), "音声エンコーダーを終了できませんでした");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct StreamHeader
    {
        public uint Size, Status;
        public nint User, Source;
        public uint SourceLength, SourceUsed;
        public nint SourceUser, Destination;
        public uint DestinationLength, DestinationUsed;
        public nint DestinationUser;
        public fixed uint Reserved[15]; // Win64 ACM header
    }
    [LibraryImport("msacm32.dll", EntryPoint = "acmStreamSize")]
    private static partial uint StreamSize(nint stream, uint input, out uint output, uint flags);
    [LibraryImport("msacm32.dll", EntryPoint = "acmStreamPrepareHeader")]
    private static partial uint Prepare(nint stream, StreamHeader* header, uint flags);
    [LibraryImport("msacm32.dll", EntryPoint = "acmStreamUnprepareHeader")]
    private static partial uint Unprepare(nint stream, StreamHeader* header, uint flags);
    [LibraryImport("msacm32.dll", EntryPoint = "acmStreamConvert")]
    private static partial uint Convert(nint stream, StreamHeader* header, uint flags);
}