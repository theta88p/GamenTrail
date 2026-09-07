using System.Runtime.InteropServices;
using System.Buffers.Binary;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Container;
using GamenTrail.Core.Media;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Audio;
using GamenTrail.Platform.Windows.Container;

internal static partial class AudioCodecTests
{
    public static async Task RunAsync()
    {
        foreach (var rate in new[] { 44100, 48000 })
        {
            var formats = AcmAudioCodec.GetFormats(rate);
            Console.WriteLine($"ACM {rate} Hz: {formats.Count} formats");
            foreach (var group in formats.GroupBy(format => BinaryPrimitives.ReadUInt16LittleEndian(format.WaveFormat)))
            {
                var choice = group.OrderByDescending(format => BinaryPrimitives.ReadUInt32LittleEndian(format.WaveFormat.AsSpan(8))).First();
                var pcm = new byte[rate * 4];
                for (var sample = 0; sample < rate; sample++)
                {
                    var value = (short)(Math.Sin(sample * 2 * Math.PI * 440 / rate) * 16000);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 4), value);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 4 + 2), value);
                }
                ValidateDecodedAudio(pcm, rate, choice.WaveFormat);
                foreach (var extension in new[] { "avi", "mkv" })
                {
                    var path = Path.Combine(Path.GetTempPath(), $"GamenTrail-Audio-{Guid.NewGuid():N}.{extension}");
                    try
                    {
                        await using (var muxer = new FileMediaMuxer())
                        {
                            var videoPrivate = new byte[40];
                            BinaryPrimitives.WriteInt32LittleEndian(videoPrivate, 40);
                            "ULRG"u8.CopyTo(videoPrivate.AsSpan(16));
                            await muxer.OpenAsync(path, new MuxerConfiguration(
                                new EncodedVideoFormat("V_MS/VFW/FOURCC", videoPrivate, 640, 480, 30),
                                new AudioFormat(rate, 2, 16, AudioSampleFormat.SignedInteger),
                                TimeSpan.FromMilliseconds(1), choice.WaveFormat), CancellationToken.None);
                            for (var offset = 0; offset < pcm.Length; offset += 1764)
                            {
                                var length = Math.Min(1764, pcm.Length - offset);
                                using var packet = new AudioPacket(MediaBuffer.CopyFrom(pcm.AsSpan(offset, length)),
                                    TimeSpan.FromSeconds(offset / (rate * 4d)), TimeSpan.FromSeconds(length / (rate * 4d)));
                                await muxer.WriteAudioPacketAsync(packet, CancellationToken.None);
                            }
                            await muxer.FinalizeAsync(CancellationToken.None);
                        }
                        var bytes = await File.ReadAllBytesAsync(path);
                        Assert(bytes.AsSpan().IndexOf(choice.WaveFormat) >= 0, "Container must preserve the complete codec format.");
                        Assert(bytes.Length > 1000, "Compressed audio must be written, including the final partial block.");
                        if (extension == "mkv") Assert(bytes.AsSpan().IndexOf("A_MS/ACM"u8) >= 0, "MKV must identify ACM audio.");
                        Console.WriteLine($"{extension}: {choice.DisplayName} OK ({bytes.Length} bytes)");
                    }
                    finally { File.Delete(path); }
                }
            }
        }
        Console.WriteLine("Audio codec tests passed.");
    }

    private static unsafe void ValidateDecodedAudio(byte[] pcm, int rate, byte[] format)
    {
        using var encoded = new MemoryStream();
        using (var encoder = new AcmAudioEncoder(new AudioFormat(rate, 2, 16, AudioSampleFormat.SignedInteger), format))
        {
            for (var offset = 0; offset < pcm.Length; offset += 1764)
            {
                using var packet = encoder.ConvertPacket(pcm.AsSpan(offset, Math.Min(1764, pcm.Length - offset)));
                if (packet is not null) encoded.Write(packet.Buffer.Memory.Span);
            }
            using var tail = encoder.ConvertPacket([], true);
            if (tail is not null) encoded.Write(tail.Buffer.Memory.Span);
        }
        var compressed = encoded.ToArray();
        var decoded = new byte[pcm.Length + rate * 4];
        var destination = AcmAudioCodec.CreatePcm(rate, 2);
        nint stream;
        fixed (byte* inputFormat = format)
        fixed (byte* outputFormat = destination)
            AcmAudioCodec.Check(AcmAudioCodec.StreamOpen(out stream, 0, (nint)inputFormat, (nint)outputFormat, 0, 0, 0, 0), "Decoder open");
        try
        {
            fixed (byte* source = compressed)
            fixed (byte* output = decoded)
            {
                var header = new DecodeHeader
                {
                    Size = (uint)(sizeof(DecodeHeader) - (IntPtr.Size == 4 ? 20 : 0)),
                    Source = (nint)source, SourceLength = (uint)compressed.Length,
                    Destination = (nint)output, DestinationLength = (uint)decoded.Length,
                };
                AcmAudioCodec.Check(Prepare(stream, &header, 0), "Decoder prepare");
                try
                {
                    AcmAudioCodec.Check(Convert(stream, &header, 0x30), "Decoder convert");
                    Assert(header.SourceUsed == compressed.Length, "The decoder must consume all compressed bytes.");
                    Assert(header.DestinationUsed >= pcm.Length && header.DestinationUsed <= pcm.Length + rate * 4 / 10,
                        $"Decoded duration must contain the entire recording with at most one padded block ({header.DestinationUsed}/{pcm.Length}).");
                    double signal = 0, error = 0;
                    for (var i = 0; i < pcm.Length; i += 2)
                    {
                        var original = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i));
                        var actual = BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(i));
                        signal += (double)original * original;
                        error += (double)(original - actual) * (original - actual);
                    }
                    Assert(error == 0 || 10 * Math.Log10(signal / error) > 20, "Decoded tone must retain signal quality and channel/sample ordering.");
                }
                finally { AcmAudioCodec.Check(Unprepare(stream, &header, 0), "Decoder unprepare"); }
            }
        }
        finally { AcmAudioCodec.Check(AcmAudioCodec.StreamClose(stream, 0), "Decoder close"); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct DecodeHeader
    {
        public uint Size, Status;
        public nint User, Source;
        public uint SourceLength, SourceUsed;
        public nint SourceUser, Destination;
        public uint DestinationLength, DestinationUsed;
        public nint DestinationUser;
        public fixed uint Reserved[15];
    }
    [LibraryImport("msacm32.dll", EntryPoint = "acmStreamPrepareHeader")]
    private static unsafe partial uint Prepare(nint stream, DecodeHeader* header, uint flags);
    [LibraryImport("msacm32.dll", EntryPoint = "acmStreamUnprepareHeader")]
    private static unsafe partial uint Unprepare(nint stream, DecodeHeader* header, uint flags);
    [LibraryImport("msacm32.dll", EntryPoint = "acmStreamConvert")]
    private static unsafe partial uint Convert(nint stream, DecodeHeader* header, uint flags);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}