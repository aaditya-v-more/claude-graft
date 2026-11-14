using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeGraft.Core;

[SupportedOSPlatform("windows")]
public static class WindowsPackageTrust
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        public uint Size;
        public nint Path, Handle, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public nint File;
        public uint StateAction;
        public nint State, UrlReference;
        public uint ProviderFlags, UiContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);

    public static void Verify(string path)
    {
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = Marshal.StringToCoTaskMemUni(path) };
        var buffer = Marshal.AllocCoTaskMem((int)file.Size);
        try
        {
            Marshal.StructureToPtr(file, buffer, false);
            var trust = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, // WTD_UI_NONE
                UnionChoice = 1, File = buffer, ProviderFlags = 0x80 }; // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            var result = WinVerifyTrust(-1, ref action, ref trust);
            if (result != 0) throw new IOException($"Windows could not verify the update signature (0x{result:X8}).");
        }
        finally { Marshal.FreeCoTaskMem(buffer); Marshal.FreeCoTaskMem(file.Path); }
    }
}
