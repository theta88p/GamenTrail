namespace GamenTrail.Core.Audio;

public sealed record ExternalAudioEncoderSettings(string Codec, string ExecutablePath, int Mp3BitRate = 192, int FlacCompressionLevel = 5);