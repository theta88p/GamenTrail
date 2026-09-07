namespace GamenTrail.Platform.Windows.Audio;

internal static class AudioEndpointId
{
    private const string DeviceInterfaceMarker = "MMDEVAPI#";

    public static string? Normalize(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return deviceId;
        }

        var endpointStart = deviceId.IndexOf(
            DeviceInterfaceMarker,
            StringComparison.OrdinalIgnoreCase);
        if (endpointStart < 0)
        {
            return deviceId;
        }

        endpointStart += DeviceInterfaceMarker.Length;
        var interfaceClassStart = deviceId.IndexOf(
            "#{",
            endpointStart,
            StringComparison.OrdinalIgnoreCase);
        return interfaceClassStart > endpointStart
            ? deviceId[endpointStart..interfaceClassStart]
            : deviceId;
    }
}
