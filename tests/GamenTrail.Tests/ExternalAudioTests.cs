using System.Buffers.Binary;
using System.Diagnostics;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Container;
using GamenTrail.Core.Media;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Audio;
using GamenTrail.Platform.Windows.Container;

internal static class ExternalAudioTests
{
    public static async Task RunAsync(string lamePath, string flacPath)
    {
        foreach (var rate in new[] { 44100, 48000 })
        foreach (var codec in new[] { "Lame", "Flac" })
        {
            var settings = new ExternalAudioEncoderSettings(codec, codec == "Lame" ? lamePath : flacPath);
            var format = new AudioFormat(rate, 2, 16, AudioSampleFormat.SignedInteger);
            var pcm = new byte[(rate + 137) * 4];
            for (var i = 0; i < pcm.Length / 4; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(Math.Sin(i * Math.PI * 880 / rate) * 16000));
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(Math.Sin(i * Math.PI * 1320 / rate) * 12000));
            }
            using (var encoder = new ExternalAudioEncoder(format, settings))
            {
                await encoder.WriteAsync(pcm, CancellationToken.None);
                await encoder.CompleteAsync(CancellationToken.None);
                var samples = 0d;
                using var reconstructed = new MemoryStream();
                if (codec == "Flac") reconstructed.Write(encoder.OutputFormat.CodecPrivate.Span);
                foreach (var packet in encoder.ReadPackets())
                {
                    using (packet)
                    {
                        samples += packet.Duration.TotalSeconds * rate;
                        reconstructed.Write(packet.Buffer.Memory.Span);
                    }
                }
                if (Math.Abs(samples - pcm.Length / 4d) > (codec == "Flac" ? 1 : 2304))
                    throw new InvalidOperationException($"Encoded duration mismatch: {codec} {samples}.");
                if (codec == "Flac")
                {
                    var input = Path.Combine(Path.GetTempPath(), $"GamenTrail-test-{Guid.NewGuid():N}.flac");
                    var output = input + ".raw";
                    try
                    {
                        await File.WriteAllBytesAsync(input, reconstructed.ToArray());
                        var start = new ProcessStartInfo(flacPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                        foreach (var arg in new[] { "-d", "--silent", "--force-raw-format", "--endian=little", "--sign=signed", "-o", output, input }) start.ArgumentList.Add(arg);
                        using var process = Process.Start(start)!;
                        var errors = process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                        if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
                        var decoded = await File.ReadAllBytesAsync(output);
                        if (!decoded.AsSpan().SequenceEqual(pcm)) throw new InvalidOperationException("FLAC must decode bit-exactly.");
                    }
                    finally { File.Delete(input); File.Delete(output); }
                }
            }
            foreach (var extension in new[] { "avi", "mkv" })
            {
                var path = Path.Combine(Path.GetTempPath(), $"GamenTrail-external-{Guid.NewGuid():N}.{extension}");
                try
                {
                    await using (var muxer = new FileMediaMuxer())
                    {
                        var bitmap = new byte[40];
                        BinaryPrimitives.WriteInt32LittleEndian(bitmap, 40);
                        "ULRG"u8.CopyTo(bitmap.AsSpan(16));
                        await muxer.OpenAsync(path, new MuxerConfiguration(
                            new EncodedVideoFormat("V_MS/VFW/FOURCC", bitmap, 640, 480, 30),
                            format, TimeSpan.FromMilliseconds(1), ExternalEncoder: settings), CancellationToken.None);
                        using var audio = new AudioPacket(MediaBuffer.CopyFrom(pcm), TimeSpan.Zero, TimeSpan.FromSeconds(pcm.Length / (rate * 4d)));
                        await muxer.WriteAudioPacketAsync(audio, CancellationToken.None);
                        await muxer.FinalizeAsync(CancellationToken.None);
                    }
                    var bytes = await File.ReadAllBytesAsync(path);
                    if (bytes.Length < 1000 || extension == "mkv" && bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(codec == "Lame" ? "A_MPEG/L3" : "A_FLAC")) < 0)
                        throw new InvalidOperationException("External audio container is invalid.");
                    if (codec == "Flac" && extension == "avi") await ValidateFlacAviAsync(bytes, pcm, rate, flacPath);
                    Console.WriteLine($"{codec} {rate} Hz -> {extension}: passed");
                }
                finally { File.Delete(path); }
            }
        }
        Console.WriteLine("External audio tests passed.");
    }
    private static async Task ValidateFlacAviAsync(byte[] avi, byte[] pcm, int rate, string flacPath)
    {
        var chunks = ReadChunks(avi, 0, avi.Length).ToArray();
        var header = chunks.Single(chunk => chunk.Id == "strh" && chunk.Data.Span[..4].SequenceEqual("auds"u8)).Data;
        var format = chunks.Single(chunk => chunk.Id == "strf" && chunk.Data.Length == 52 &&
            BinaryPrimitives.ReadUInt16LittleEndian(chunk.Data.Span) == 0xF1AC).Data;
        var audio = chunks.Where(chunk => chunk.Id == "01wb").ToArray();
        var scale = BinaryPrimitives.ReadUInt32LittleEndian(header.Span[20..]);
        var timeRate = BinaryPrimitives.ReadUInt32LittleEndian(header.Span[24..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.Span[32..]);
        if (scale != 4096 || timeRate != rate || length != audio.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Span[44..]) != 0)
            throw new InvalidOperationException("AVI FLAC must use variable-size packets and a frame-based clock.");
        if (audio.Length != (pcm.Length / 4 + 4095) / 4096)
            throw new InvalidOperationException("AVI must contain each FLAC frame, including the final partial block.");
        var duration = length * (double)scale / timeRate;
        if (duration < pcm.Length / (rate * 4d) || duration - pcm.Length / (rate * 4d) >= 4096d / rate)
            throw new InvalidOperationException("AVI FLAC duration must be within the final frame boundary.");
        var index = chunks.Single(chunk => chunk.Id == "indx" && chunk.Data.Span.Slice(8, 4).SequenceEqual("01wb"u8)).Data;
        var entries = BinaryPrimitives.ReadUInt32LittleEndian(index.Span[4..]);
        uint indexedFrames = 0;
        for (var entry = 0; entry < entries; entry++)
            indexedFrames += BinaryPrimitives.ReadUInt32LittleEndian(index.Span[(24 + entry * 16 + 12)..]);
        if (indexedFrames != audio.Length) throw new InvalidOperationException("OpenDML audio index durations must count FLAC frames.");

        var input = Path.Combine(Path.GetTempPath(), $"GamenTrail-avi-flac-{Guid.NewGuid():N}.flac");
        var output = input + ".raw";
        try
        {
            using var extracted = new MemoryStream();
            extracted.Write("fLaC"u8);
            extracted.Write(new byte[] { 0x80, 0, 0, 34 });
            extracted.Write(format.Span.Slice(18, 34));
            foreach (var frame in audio) extracted.Write(frame.Data.Span);
            await File.WriteAllBytesAsync(input, extracted.ToArray());
            var start = new ProcessStartInfo(flacPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-d", "--silent", "--force-raw-format", "--endian=little", "--sign=signed", "-o", output, input })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
            if (!(await File.ReadAllBytesAsync(output)).AsSpan().SequenceEqual(pcm))
                throw new InvalidOperationException("FLAC extracted from AVI must decode to the exact original PCM.");
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    private static IEnumerable<(string Id, ReadOnlyMemory<byte> Data)> ReadChunks(byte[] bytes, int start, int end)
    {
        while (start + 8 <= end)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, start, 4);
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start + 4)));
            var payload = start + 8;
            if (size < 0 || size > end - payload) throw new InvalidDataException("Invalid RIFF chunk.");
            if (id is "RIFF" or "LIST")
            {
                foreach (var child in ReadChunks(bytes, payload + 4, payload + size)) yield return child;
            }
            else yield return (id, bytes.AsMemory(payload, size));
            start = checked(payload + size + (size & 1));
        }
    }}