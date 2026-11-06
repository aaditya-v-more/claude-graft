using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ClaudeGraft;

internal static class ShellLinkIdentity
{
    // Windows copies this ID when the shortcut is pinned. A rename, icon change
    // or new launcher build must not turn an existing pin into another profile.
    internal static void Set(string path, Guid shortcutId)
    {
        var link = (IPersistFile)new ShellLink();
        try
        {
            link.Load(path, 2); // STGM_READWRITE
            var store = (IPropertyStore)link;
            var id = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
            var value = new StringVariant
            {
                Type = 31, // VT_LPWSTR
                Text = Marshal.StringToCoTaskMemUni($"ClaudeGraft.Profile.{shortcutId:N}"),
            };
            try
            {
                store.SetValue(ref id, ref value);
                link.Save(path, true);
            }
            finally { Marshal.FreeCoTaskMem(value.Text); }
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct StringVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Text;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out StringVariant value);
        void SetValue(ref PropertyKey key, ref StringVariant value);
        void Commit();
    }
}
