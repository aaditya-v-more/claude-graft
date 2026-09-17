using System.Runtime.InteropServices;

namespace ClaudeGraft.Core;

public static class WindowsCommandLine
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static string[] Parse(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return Array.Empty<string>();
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) throw new IOException("Windows could not read a process command line.");
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }

    public static string? Option(string[] arguments, string name)
    {
        for (var i = 1; i < arguments.Length; i++)
        {
            if (arguments[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return arguments[i][(name.Length + 1)..];
            if (arguments[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i + 1 < arguments.Length ? arguments[i + 1] : "";
        }
        return null;
    }
}
