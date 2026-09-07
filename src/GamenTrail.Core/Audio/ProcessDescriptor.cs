namespace GamenTrail.Core.Audio;

public sealed record ProcessDescriptor(int ProcessId, string ProcessName, string DisplayName);

public interface IProcessProvider
{
    IReadOnlyList<ProcessDescriptor> GetProcesses();
}
