using Microsoft.Win32;
using System.Runtime.Versioning;

namespace ClaudeGraft.Core;

public enum ClaudePolicyState { Automatic, Manual, Managed }

public static class ClaudeUpdatePolicy
{
    private const string Policy = @"SOFTWARE\Policies\Claude";
    private const string Value = "disableAutoUpdates";
    private static string Marker => Path.Combine(GraftPaths.OwnData, "owns-claude-update-policy");

    public static ClaudePolicyState Classify(bool otherPolicy, object? userValue, bool owns) =>
        otherPolicy ? ClaudePolicyState.Managed : userValue is null ? ClaudePolicyState.Automatic
        : owns && userValue is int number && number == 1 ? ClaudePolicyState.Manual : ClaudePolicyState.Managed;

    [SupportedOSPlatform("windows")]
    public static ClaudePolicyState Read()
    {
        using var machine = Registry.LocalMachine.OpenSubKey(Policy);
        using var user = Registry.CurrentUser.OpenSubKey(Policy);
        // A configured enforcement window is also an administrator's update policy.
        return Classify(machine?.GetValue(Value) is not null || machine?.GetValue("autoUpdaterEnforcementHours") is not null
            || user?.GetValue("autoUpdaterEnforcementHours") is not null, user?.GetValue(Value), File.Exists(Marker));
    }

    [SupportedOSPlatform("windows")]
    public static void SetManual(bool enabled)
    {
        using var stateLock = StateLock.Acquire();
        if (GraftPaths.ProfilesRootOverride is not null)
            throw new IOException("The isolated test manager cannot change Claude's Windows policy.");
        var state = Read();
        if (state == ClaudePolicyState.Managed)
            throw new IOException("An existing Windows policy controls Claude updates. Contact its administrator.");
        if ((state == ClaudePolicyState.Manual) == enabled) return;
        if (enabled)
        {
            using var user = Registry.CurrentUser.CreateSubKey(Policy, writable: true);
            if (user.GetValue(Value) is not null) throw new IOException("Claude's Windows policy changed. Reopen the dialog.");
            Directory.CreateDirectory(GraftPaths.OwnData);
            // Journal before setting the value so an interrupted enable remains reversible.
            File.WriteAllText(Marker, "1");
            try { user.SetValue(Value, 1, RegistryValueKind.DWord); }
            catch { File.Delete(Marker); throw; }
        }
        else
        {
            using var user = Registry.CurrentUser.OpenSubKey(Policy, writable: true);
            if (user?.GetValue(Value) is int number && number == 1) user.DeleteValue(Value);
            File.Delete(Marker);
        }
    }
}
