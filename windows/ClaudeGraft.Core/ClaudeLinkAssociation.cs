using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGraft.Core;

public enum ClaudeLinkHandler { Unknown, Other, Graft }

public static class ClaudeLinkAssociation
{
    private const uint IsProtocol = 0x1000;
    private const uint ProgId = 20;
    private const int NoAssociation = unchecked((int)0x80070483);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint field, string association,
        string verb, [Out] StringBuilder result, ref uint length);

    public static ClaudeLinkHandler Current()
    {
        if (!OperatingSystem.IsWindows()) return ClaudeLinkHandler.Unknown;
        // Ask the shell on every press. Registration only makes Graft a candidate,
        // and Windows can change the elected handler while the manager stays open.
        var result = new StringBuilder(1024);
        uint length = (uint)result.Capacity;
        var status = AssocQueryStringW(IsProtocol, ProgId, "claude", "open", result, ref length);
        return FromResult(status, result.ToString());
    }

    internal static ClaudeLinkHandler FromResult(int status, string progId)
    {
        if (status == NoAssociation) return ClaudeLinkHandler.Other;
        if (status != 0 || string.IsNullOrWhiteSpace(progId)) return ClaudeLinkHandler.Unknown;
        return string.Equals(progId, "ClaudeGraft.Link", StringComparison.OrdinalIgnoreCase)
            ? ClaudeLinkHandler.Graft : ClaudeLinkHandler.Other;
    }
}
