using System.Management;
using System.Runtime.Versioning;

namespace ClaudeGraft.Core;

/// <summary>
/// The one impure half of process detection: asking Windows for every running
/// <c>claude.exe</c> and its command line. WMI answers with both in one query,
/// which is what <see cref="ClaudeProcesses"/>'s pure predicates read. A process
/// whose command line cannot be read — access denied, or gone between the query
/// and the read — comes back with an empty one and is simply not matched.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsProcessQuery
{
    public static IReadOnlyList<(int pid, string command)> ClaudeProcesses()
    {
        var found = new List<(int, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'claude.exe'");
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var command = mo["CommandLine"] as string;
                if (string.IsNullOrWhiteSpace(command))
                    throw new IOException("A running Claude process could not be identified. Close it before changing profiles.");
                var pid = Convert.ToInt32(mo["ProcessId"]);
                found.Add((pid, command));
            }
        }
        catch (Exception e)
        {
            throw new IOException("Windows could not verify which Claude profiles are running. No profile was opened or changed.", e);
        }
        return found;
    }
}
