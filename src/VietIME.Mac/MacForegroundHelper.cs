using System.Runtime.InteropServices;

namespace VietIME.Mac;

/// <summary>
/// Lấy foreground app trên macOS qua NSWorkspace.sharedWorkspace.frontmostApplication.
/// Dùng objc_msgSend P/Invoke.
/// </summary>
public static class MacForegroundHelper
{
    private const string ObjcLib = "/usr/lib/libobjc.dylib";
    private const string AppKitLib = "/System/Library/Frameworks/AppKit.framework/AppKit";

    [DllImport(ObjcLib, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjcLib, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    private static readonly IntPtr NSWorkspaceClass;
    private static readonly IntPtr SharedWorkspaceSel;
    private static readonly IntPtr FrontmostApplicationSel;
    private static readonly IntPtr BundleIdentifierSel;
    private static readonly IntPtr LocalizedNameSel;
    private static readonly IntPtr UTF8StringSel;

    static MacForegroundHelper()
    {
        // Load AppKit để NSWorkspace có sẵn
        try { NativeLibrary.Load(AppKitLib); } catch { /* ignore */ }

        NSWorkspaceClass = objc_getClass("NSWorkspace");
        SharedWorkspaceSel = sel_registerName("sharedWorkspace");
        FrontmostApplicationSel = sel_registerName("frontmostApplication");
        BundleIdentifierSel = sel_registerName("bundleIdentifier");
        LocalizedNameSel = sel_registerName("localizedName");
        UTF8StringSel = sel_registerName("UTF8String");
    }

    /// <summary>
    /// Trả về bundle identifier của app đang foreground (vd "dev.warp.Warp-Stable").
    /// Null nếu không lấy được.
    /// </summary>
    public static string? GetFrontmostBundleId()
    {
        try
        {
            if (NSWorkspaceClass == IntPtr.Zero) return null;

            IntPtr workspace = objc_msgSend(NSWorkspaceClass, SharedWorkspaceSel);
            if (workspace == IntPtr.Zero) return null;

            IntPtr app = objc_msgSend(workspace, FrontmostApplicationSel);
            if (app == IntPtr.Zero) return null;

            IntPtr nsBundleId = objc_msgSend(app, BundleIdentifierSel);
            if (nsBundleId == IntPtr.Zero) return null;

            IntPtr cStr = objc_msgSend(nsBundleId, UTF8StringSel);
            if (cStr == IntPtr.Zero) return null;

            return Marshal.PtrToStringUTF8(cStr);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Kiểm tra foreground app có phải Warp không.
    /// Warp có nhiều variant: dev.warp.Warp-Stable, dev.warp.Warp-Preview, dev.warp.Warp.
    /// </summary>
    public static bool IsWarpForeground()
    {
        var bundleId = GetFrontmostBundleId();
        return bundleId != null && bundleId.StartsWith("dev.warp.Warp", StringComparison.OrdinalIgnoreCase);
    }
}
