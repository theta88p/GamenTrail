using GamenTrail.Core.Audio;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace GamenTrail.Platform.Windows.Audio;

public sealed class WindowsAudioDeviceProvider : IAudioDeviceProvider
{
    public async Task<IReadOnlyList<AudioDeviceDescriptor>> GetRenderDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var selector = MediaDevice.GetAudioRenderSelector();
        var defaultDeviceId = AudioEndpointId.Normalize(
            MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default));
        var devices = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken).ConfigureAwait(false);
        return devices
            .Select(device => new AudioDeviceDescriptor(
                AudioEndpointId.Normalize(device.Id) ?? device.Id,
                device.Name,
                string.Equals(
                    AudioEndpointId.Normalize(device.Id),
                    defaultDeviceId,
                    StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(static device => device.IsDefault)
            .ThenBy(static device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
