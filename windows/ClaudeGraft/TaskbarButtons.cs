using System.Runtime.InteropServices;

namespace ClaudeGraft;

/// <summary>
/// Where a button the taskbar draws for itself sits on screen, read through UI
/// Automation: Windows 11's taskbar buttons have no windows of their own.
/// </summary>
// Called off the UI thread only: the strip is a child of the taskbar, so Explorer may
// be waiting on the UI thread at the moment this would wait on Explorer.
internal static class TaskbarButtons
{
    private const int TreeScopeDescendants = 4;
    private const int AutomationIdProperty = 30011;
    private const int BoundingRectangleProperty = 30001;

    private static IUIAutomation? _automation;

    /// The button's left edge in physical pixels, or null where it is not there.
    public static int? LeftOf(nint taskbar, string automationId)
    {
        try
        {
            _automation ??= (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;
            _automation.ElementFromHandle(taskbar, out var bar);
            _automation.CreatePropertyCondition(AutomationIdProperty, automationId, out var condition);
            bar.FindFirst(TreeScopeDescendants, condition, out var button);
            if (button is null) return null;
            button.GetCurrentPropertyValue(BoundingRectangleProperty, out var value);
            // Left, top, width, height; an empty box is a button that is hidden.
            return value is double[] { Length: 4 } r && r[2] > 0 ? (int)Math.Floor(r[0]) : null;
        }
        catch { return null; }
    }

    // Only the slots called are named; the rest hold their places in the vtable.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        void ElementFromHandle(nint hwnd, out IUIAutomationElement element);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
        void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
        void CreateTreeWalker(); void get_ControlViewWalker(); void get_ContentViewWalker(); void get_RawViewWalker();
        void get_RawViewCondition(); void get_ControlViewCondition(); void get_ContentViewCondition();
        void CreateCacheRequest(); void CreateTrueCondition(); void CreateFalseCondition();
        void CreatePropertyCondition(int property, [MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition condition);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId();
        void FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement? found);
        void FindAll(); void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        void GetCurrentPropertyValue(int property, [MarshalAs(UnmanagedType.Struct)] out object value);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }
}
