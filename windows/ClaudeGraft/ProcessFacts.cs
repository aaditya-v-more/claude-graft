using System.Runtime.InteropServices;

namespace ClaudeGraft;

/// <summary>
/// One process's name and start time, asked of that process alone. Process.GetProcessById
/// reads a snapshot of every process on the machine for its name, and run for each window
/// on screen from the UI thread it held the window from loading.
/// </summary>
internal static class ProcessFacts
{
    public static bool IsClaude(int pid) =>
        Path.GetFileNameWithoutExtension(ImagePath(pid) ?? "").Equals("claude", StringComparison.OrdinalIgnoreCase);

    public static string? ImagePath(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(handle); }
    }

    public static DateTime StartTime(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return default;
        try { return GetProcessTimes(handle, out var created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created).ToLocalTime() : default; }
        finally { CloseHandle(handle); }
    }

    private const uint QueryLimitedInformation = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, char[] name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
}
