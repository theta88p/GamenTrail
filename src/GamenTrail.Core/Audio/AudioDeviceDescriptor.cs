namespace GamenTrail.Core.Audio;

public sealed record AudioDeviceDescriptor(string Id, string DisplayName, bool IsDefault);

public interface IAudioDeviceProvider
{
    Task<IReadOnlyList<AudioDeviceDescriptor>> GetRenderDevicesAsync(
        CancellationToken cancellationToken = default);
}
