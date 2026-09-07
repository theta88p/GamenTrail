using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Media;

namespace GamenTrail.Platform.Windows.Audio;

internal sealed class ExternalAudioEncoder : IDisposable
{
    private readonly Process _process;
    private readonly string _outputPath;
    private readonly Task<string> _errors;
    private readonly string _codec;
    private readonly int _sampleRate;

    public ExternalAudioEncoder(AudioFormat input, ExternalAudioEncoderSettings settings)
    {
        if (input.ChannelCount != 2 || input.BitsPerSample != 16 || input.SampleFormat != AudioSampleFormat.SignedInteger)
            throw new NotSupportedException("外部音声エンコーダーにはPCM 16-bitステレオが必要です。");
        if (settings.Codec is not ("Lame" or "Flac") || !Path.IsPathFullyQualified(settings.ExecutablePath) || !File.Exists(settings.ExecutablePath))
            throw new InvalidOperationException("詳細設定で外部エンコーダーの実行ファイルを登録してください。");
        if (input.SampleRate is not (44100 or 48000)) throw new NotSupportedException("44.1 kHzまたは48 kHzを指定してください。");
        if (!new[] { 96, 128, 160, 192, 224, 256, 320 }.Contains(settings.Mp3BitRate) || settings.FlacCompressionLevel is < 0 or > 8)
            throw new ArgumentException("外部音声エンコーダーの品質設定が不正です。", nameof(settings));
        _codec = settings.Codec;
        _sampleRate = input.SampleRate;
        _outputPath = Path.Combine(Path.GetTempPath(), $"GamenTrail-{Guid.NewGuid():N}.{(_codec == "Lame" ? "mp3" : "flac")}");
        var start = new ProcessStartInfo(settings.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true,
        };
        string[] arguments = _codec == "Lame"
            ? ["--silent", "-r", "--bitwidth", "16", "--signed", "--little-endian", "-s",
                (_sampleRate / 1000d).ToString(CultureInfo.InvariantCulture), "-m", "s", "--cbr", "-b",
                settings.Mp3BitRate.ToString(CultureInfo.InvariantCulture), "-t", "-", _outputPath]
            : ["--silent", "--force-raw-format", "--endian=little", "--sign=signed", "--channels=2", "--bps=16",
                $"--sample-rate={_sampleRate}", $"-{settings.FlacCompressionLevel}", "--blocksize=4096",
                "--no-padding", "--no-seektable", "-o", _outputPath, "-"];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        _process = new Process { StartInfo = start };
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("外部エンコーダーを起動できませんでした。");
        }
        catch { _process.Dispose(); throw; }
        _errors = ReadErrorsAsync(_process.StandardError);
        if (_codec == "Lame")
        {
            var wave = new byte[30];
            BinaryPrimitives.WriteUInt16LittleEndian(wave, 0x55);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(2), 2);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), _sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(8), settings.Mp3BitRate * 125);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(16), 12);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(18), 1);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(20), 0); // ISO padding is specified by each MPEG frame.
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(24), checked((ushort)(144000 * settings.Mp3BitRate / _sampleRate)));
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(26), 1);
            OutputFormat = input with { WaveFormat = wave, CodecId = "A_MPEG/L3" };
        }
        else
        {
            // Valid STREAMINFO with unknown total samples and MD5 for a live recording.
            var header = new byte[42];
            "fLaC"u8.CopyTo(header);
            header[4] = 0x80; header[7] = 34;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 4096);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 4096);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(18), ((ulong)_sampleRate << 44) | (1UL << 41) | (15UL << 36));
            OutputFormat = input with { CodecId = "A_FLAC", CodecPrivate = header };
        }
    }

    public AudioFormat OutputFormat { get; }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        try { await _process.StandardInput.BaseStream.WriteAsync(pcm, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false); }
        catch (IOException error)
        {
            throw new InvalidOperationException("外部音声エンコーダーが入力を受け付けませんでした。実行ファイルと設定を確認してください。", error);
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        _process.StandardInput.Close();
        await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var errors = await _errors.ConfigureAwait(false);
        if (_process.ExitCode != 0) throw new InvalidOperationException($"外部音声エンコーダーが失敗しました ({_process.ExitCode})。{errors}");
    }

    public IEnumerable<AudioPacket> ReadPackets()
    {
        using var stream = new FileStream(_outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (_codec == "Flac")
        {
            var marker = new byte[4];
            stream.ReadExactly(marker);
            if (!marker.AsSpan().SequenceEqual("fLaC"u8)) throw new InvalidDataException("FLACデータが不正です。");
            bool last;
            do
            {
                stream.ReadExactly(marker);
                last = (marker[0] & 0x80) != 0;
                var length = (marker[1] << 16) | (marker[2] << 8) | marker[3];
                if (stream.Position + length > stream.Length) throw new InvalidDataException("FLACメタデータが途切れています。");
                stream.Position += length;
            } while (!last);
        }
        long samples = 0;
        while (stream.Position < stream.Length)
        {
            var bytes = _codec == "Lame" ? ReadMp3Frame(stream) : ReadFlacFrame(stream);
            var count = _codec == "Lame" ? 1152 : FlacBlockSamples(bytes);
            if (count <= 0) throw new InvalidDataException("音声フレームヘッダーが不正です。");
            var packet = new AudioPacket(MediaBuffer.CopyFrom(bytes), TimeSpan.FromSeconds(samples / (double)_sampleRate),
                TimeSpan.FromSeconds(count / (double)_sampleRate));
            samples += count;
            yield return packet;
        }
    }

    private byte[] ReadMp3Frame(Stream stream)
    {
        var header = new byte[4];
        stream.ReadExactly(header);
        if (header[0] != 255 || (header[1] & 0xFE) != 0xFA) throw new InvalidDataException("MPEG-1 Layer III以外の出力です。");
        int[] bitRates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        int[] rates = [44100, 48000, 32000, 0];
        var rate = rates[(header[2] >> 2) & 3];
        var bitRate = bitRates[header[2] >> 4];
        if (rate != _sampleRate || bitRate == 0) throw new InvalidDataException("MP3の出力形式が設定と一致しません。");
        var bytes = new byte[144000 * bitRate / rate + ((header[2] >> 1) & 1)];
        header.CopyTo(bytes, 0);
        stream.ReadExactly(bytes.AsSpan(4));
        return bytes;
    }

    private static byte[] ReadFlacFrame(Stream stream)
    {
        using var frame = new MemoryStream();
        ushort crc = 0;
        var lookahead = new byte[16];
        while (stream.Position < stream.Length)
        {
            var value = (byte)stream.ReadByte();
            frame.WriteByte(value);
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x8005 : 0));
            if (frame.Length > 1024 * 1024) throw new InvalidDataException("FLACフレームが大きすぎます。");
            if (crc != 0 || frame.Length < 8) continue;
            var position = stream.Position;
            if (position == stream.Length) break;
            var count = stream.Read(lookahead);
            stream.Position = position;
            if (FlacBlockSamples(lookahead.AsSpan(0, count)) > 0) break;
        }
        if (crc != 0) throw new InvalidDataException("FLACフレームのCRCが一致しません。");
        return frame.ToArray();
    }

    internal static int FlacBlockSamples(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6 || bytes[0] != 255 || (bytes[1] & 0xFE) != 0xF8 || (bytes[3] & 1) != 0) return 0;
        var code = bytes[2] >> 4;
        var position = 4;
        var first = bytes[position++];
        if (first >= 0x80)
        {
            var length = 0;
            for (var mask = 0x80; (first & mask) != 0 && mask > 0; mask >>= 1) length++;
            if (length is < 2 or > 7 || bytes.Length < position + length) return 0;
            for (var i = 1; i < length; i++) if ((bytes[position++] & 0xC0) != 0x80) return 0;
        }
        if (bytes.Length < position + 4) return 0;
        var samples = code switch { 1 => 192, >= 2 and <= 5 => 576 << (code - 2), 6 => bytes[position++] + 1,
            7 => BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]) + 1, >= 8 => 256 << (code - 8), _ => 0 };
        if (code == 7) position += 2;
        position += (bytes[2] & 15) switch { 12 => 1, 13 or 14 => 2, _ => 0 };
        if (bytes.Length <= position) return 0;
        byte crc = 0;
        for (var i = 0; i <= position; i++)
        {
            crc ^= bytes[i];
            for (var bit = 0; bit < 8; bit++) crc = (byte)((crc << 1) ^ ((crc & 0x80) != 0 ? 7 : 0));
        }
        return crc == 0 ? samples : 0;
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        var tail = string.Empty;
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            tail += new string(buffer, 0, count);
            if (tail.Length > 4096) tail = tail[^4096..];
        }
        return tail;
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } }
        finally { _process.Dispose(); File.Delete(_outputPath); }
    }
}