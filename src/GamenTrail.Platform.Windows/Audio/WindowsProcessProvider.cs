using System.Diagnostics;
using GamenTrail.Core.Audio;

namespace GamenTrail.Platform.Windows.Audio;

public sealed class WindowsProcessProvider : IProcessProvider
{
    public IReadOnlyList<ProcessDescriptor> GetProcesses()
    {
        var result = new List<ProcessDescriptor>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var processName = process.ProcessName;
                    var title = process.MainWindowTitle;
                    var displayName = string.IsNullOrWhiteSpace(title)
                        ? $"{processName} ({process.Id})"
                        : $"{title} — {processName} ({process.Id})";
                    result.Add(new ProcessDescriptor(process.Id, processName, displayName));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return result
            .OrderBy(static process => process.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
