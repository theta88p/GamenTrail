namespace GamenTrail.Platform.Windows.Container;

internal static class MatroskaElement
{
    public const uint Ebml = 0x1A45DFA3;
    public const uint EbmlVersion = 0x4286;
    public const uint EbmlReadVersion = 0x42F7;
    public const uint EbmlMaxIdLength = 0x42F2;
    public const uint EbmlMaxSizeLength = 0x42F3;
    public const uint DocType = 0x4282;
    public const uint DocTypeVersion = 0x4287;
    public const uint DocTypeReadVersion = 0x4285;
    public const uint Segment = 0x18538067;
    public const uint Info = 0x1549A966;
    public const uint TimestampScale = 0x2AD7B1;
    public const uint Duration = 0x4489;
    public const uint MuxingApp = 0x4D80;
    public const uint WritingApp = 0x5741;
    public const uint Tracks = 0x1654AE6B;
    public const uint TrackEntry = 0xAE;
    public const uint TrackNumber = 0xD7;
    public const uint TrackUid = 0x73C5;
    public const uint TrackType = 0x83;
    public const uint FlagLacing = 0x9C;
    public const uint CodecId = 0x86;
    public const uint CodecPrivate = 0x63A2;
    public const uint DefaultDuration = 0x23E383;
    public const uint Video = 0xE0;
    public const uint PixelWidth = 0xB0;
    public const uint PixelHeight = 0xBA;
    public const uint Audio = 0xE1;
    public const uint SamplingFrequency = 0xB5;
    public const uint Channels = 0x9F;
    public const uint BitDepth = 0x6264;
    public const uint Cluster = 0x1F43B675;
    public const uint Timestamp = 0xE7;
    public const uint SimpleBlock = 0xA3;
    public const uint Cues = 0x1C53BB6B;
    public const uint CuePoint = 0xBB;
    public const uint CueTime = 0xB3;
    public const uint CueTrackPositions = 0xB7;
    public const uint CueTrack = 0xF7;
    public const uint CueClusterPosition = 0xF1;
}
