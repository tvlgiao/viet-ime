using System.Runtime.InteropServices;

namespace VietIME.Mac;

/// <summary>
/// macOS native API declarations cho keyboard hook và event handling.
/// Tương đương NativeMethods.cs trên Windows nhưng dùng CoreGraphics thay vì user32.dll.
/// </summary>
public static class MacNativeMethods
{
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AppServicesLib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string AppKitLib = "/System/Library/Frameworks/AppKit.framework/AppKit";

    #region Constants

    // CGEventTapLocation
    public const uint kCGHIDEventTap = 0;           // Hardware level - trước tất cả app
    public const uint kCGSessionEventTap = 1;        // Session level
    public const uint kCGAnnotatedSessionEventTap = 2;

    // CGEventTapPlacement
    public const uint kCGHeadInsertEventTap = 0;     // Đầu chuỗi - nhận event trước
    public const uint kCGTailAppendEventTap = 1;

    // CGEventTapOptions
    public const uint kCGEventTapOptionDefault = 0;  // Có thể modify/block event
    public const uint kCGEventTapOptionListenOnly = 1;

    // CGEventType
    public const uint kCGEventNull = 0;
    public const uint kCGEventLeftMouseDown = 1;
    public const uint kCGEventKeyDown = 10;
    public const uint kCGEventKeyUp = 11;
    public const uint kCGEventFlagsChanged = 12;
    public const uint kCGEventTapDisabledByTimeout = 0xFFFFFFFE;
    public const uint kCGEventTapDisabledByUserInput = 0xFFFFFFFF;

    // CGEventField
    public const uint kCGKeyboardEventKeycode = 9;
    public const uint kCGKeyboardEventAutorepeat = 8;

    // CGEventFlags (bitmask)
    public const ulong kCGEventFlagMaskAlphaShift = 0x00010000; // Caps Lock
    public const ulong kCGEventFlagMaskShift = 0x00020000;
    public const ulong kCGEventFlagMaskControl = 0x00040000;
    public const ulong kCGEventFlagMaskAlternate = 0x00080000; // Option/Alt
    public const ulong kCGEventFlagMaskCommand = 0x00100000;

    // macOS keycodes (khác với Windows virtual key codes)
    public const ushort kVK_Delete = 0x33;        // Backspace
    public const ushort kVK_Return = 0x24;        // Enter
    public const ushort kVK_Tab = 0x30;
    public const ushort kVK_Space = 0x31;
    public const ushort kVK_Escape = 0x35;
    public const ushort kVK_LeftArrow = 0x7B;
    public const ushort kVK_RightArrow = 0x7C;
    public const ushort kVK_DownArrow = 0x7D;
    public const ushort kVK_UpArrow = 0x7E;
    public const ushort kVK_Home = 0x73;
    public const ushort kVK_End = 0x77;
    public const ushort kVK_PageUp = 0x74;
    public const ushort kVK_PageDown = 0x79;
    public const ushort kVK_ForwardDelete = 0x75;
    public const ushort kVK_ANSI_Grave = 0x32;    // ` (backtick/tilde key)
    public const ushort kVK_ANSI_Z = 0x06;        // Z

    // CFRunLoop
    public static readonly IntPtr kCFRunLoopCommonModes;

    #endregion

    #region Static Constructor

    static MacNativeMethods()
    {
        // Lấy kCFRunLoopCommonModes constant
        var handle = NativeLibrary.Load(CoreFoundationLib);
        var ptr = NativeLibrary.GetExport(handle, "kCFRunLoopCommonModes");
        kCFRunLoopCommonModes = Marshal.ReadIntPtr(ptr);
    }

    #endregion

    #region Delegates

    /// <summary>
    /// Callback cho CGEventTap.
    /// Return event để cho đi qua, return IntPtr.Zero để chặn (swallow).
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr CGEventTapCallBack(
        IntPtr proxy,
        uint type,
        IntPtr eventRef,
        IntPtr userInfo);

    #endregion

    #region CGEvent - Core APIs

    /// <summary>
    /// Tạo event tap - bắt keyboard events toàn hệ thống.
    /// Tương đương SetWindowsHookEx(WH_KEYBOARD_LL) trên Windows.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern IntPtr CGEventTapCreate(
        uint tap,               // kCGHIDEventTap
        uint place,             // kCGHeadInsertEventTap
        uint options,           // kCGEventTapOptionDefault
        ulong eventsOfInterest, // Bitmask các event type cần bắt
        CGEventTapCallBack callback,
        IntPtr userInfo);

    /// <summary>
    /// Bật/tắt event tap.
    /// QUAN TRỌNG: Phải gọi với enable=true khi nhận kCGEventTapDisabledByTimeout.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.U1)] bool enable);

    /// <summary>
    /// Gửi event vào hệ thống.
    /// Tương đương SendInput trên Windows.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern void CGEventPost(uint tap, IntPtr eventRef);

    /// <summary>
    /// Tạo keyboard event mới.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern IntPtr CGEventCreateKeyboardEvent(
        IntPtr source,          // null = default
        ushort virtualKey,
        [MarshalAs(UnmanagedType.U1)] bool keyDown);

    /// <summary>
    /// Lấy giá trị integer field từ event.
    /// Dùng để lấy keycode: CGEventGetIntegerValueField(event, kCGKeyboardEventKeycode)
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern long CGEventGetIntegerValueField(IntPtr eventRef, uint field);

    /// <summary>
    /// Set giá trị integer field cho event.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern void CGEventSetIntegerValueField(IntPtr eventRef, uint field, long value);

    /// <summary>
    /// Lấy flags (modifier keys) từ event.
    /// Tương đương GetAsyncKeyState(VK_SHIFT) trên Windows.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern ulong CGEventGetFlags(IntPtr eventRef);

    /// <summary>
    /// Set flags cho event.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern void CGEventSetFlags(IntPtr eventRef, ulong flags);

    /// <summary>
    /// Lấy unicode string từ keyboard event.
    /// Tương đương ToUnicodeEx trên Windows.
    /// UniCharCount = unsigned long = 8 bytes trên 64-bit macOS → dùng nuint.
    /// UniChar = uint16_t → dùng ushort[] (không dùng char[] vì CharSet.Ansi marshal char thành 1 byte).
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern void CGEventKeyboardGetUnicodeString(
        IntPtr eventRef,
        nuint maxStringLength,
        out nuint actualStringLength,
        [Out] ushort[] unicodeString);

    /// <summary>
    /// Set unicode string cho keyboard event.
    /// Dùng để gửi ký tự tiếng Việt.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern void CGEventKeyboardSetUnicodeString(
        IntPtr eventRef,
        nuint stringLength,
        [In] ushort[] unicodeString);

    /// <summary>
    /// Tạo event source mới.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern IntPtr CGEventSourceCreate(int stateID);

    /// <summary>
    /// Set extra info trên event source - dùng làm marker nhận diện input từ VietIME.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern void CGEventSourceSetLocalEventsSuppressionInterval(IntPtr source, double seconds);

    /// <summary>
    /// Lấy flags từ event source (toàn cục).
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern ulong CGEventSourceFlagsState(int stateID);

    #endregion

    #region Permission APIs

    /// <summary>
    /// Kiểm tra quyền Input Monitoring KHÔNG hiện dialog.
    /// Return true nếu đã có quyền.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern bool CGPreflightListenEventAccess();

    /// <summary>
    /// Yêu cầu quyền Input Monitoring - hiện dialog hệ thống nếu chưa có quyền.
    /// Return true nếu đã có quyền.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern bool CGRequestListenEventAccess();

    /// <summary>
    /// Kiểm tra quyền post event KHÔNG hiện dialog.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern bool CGPreflightPostEventAccess();

    /// <summary>
    /// Yêu cầu quyền post event.
    /// </summary>
    [DllImport(CoreGraphicsLib)]
    public static extern bool CGRequestPostEventAccess();

    #endregion

    #region CoreFoundation - RunLoop

    /// <summary>
    /// Tạo run loop source từ Mach port (CFMachPort từ CGEventTapCreate).
    /// </summary>
    [DllImport(CoreFoundationLib)]
    public static extern IntPtr CFMachPortCreateRunLoopSource(
        IntPtr allocator,   // IntPtr.Zero = default
        IntPtr port,        // từ CGEventTapCreate
        long order);        // 0

    /// <summary>
    /// Lấy run loop hiện tại.
    /// </summary>
    [DllImport(CoreFoundationLib)]
    public static extern IntPtr CFRunLoopGetCurrent();

    /// <summary>
    /// Thêm source vào run loop.
    /// </summary>
    [DllImport(CoreFoundationLib)]
    public static extern void CFRunLoopAddSource(
        IntPtr runLoop,
        IntPtr source,
        IntPtr mode);       // kCFRunLoopCommonModes

    /// <summary>
    /// Xóa source khỏi run loop.
    /// </summary>
    [DllImport(CoreFoundationLib)]
    public static extern void CFRunLoopRemoveSource(
        IntPtr runLoop,
        IntPtr source,
        IntPtr mode);

    /// <summary>
    /// Chạy run loop (blocking).
    /// </summary>
    [DllImport(CoreFoundationLib)]
    public static extern void CFRunLoopRun();

    /// <summary>
    /// Dừng run loop.
    /// </summary>
    [DllImport(CoreFoundationLib)]
    public static extern void CFRunLoopStop(IntPtr runLoop);

    /// <summary>
    /// Release CFType object.
    /// </summary>
    [DllImport(CoreFoundationLib)]
    public static extern void CFRelease(IntPtr obj);

    /// <summary>
    /// Kiểm tra Mach port có valid không.
    /// </summary>
    [DllImport(CoreFoundationLib)]
    public static extern bool CFMachPortIsValid(IntPtr port);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Tạo event mask cho một loại event.
    /// Dùng: CGEventMaskBit(kCGEventKeyDown) | CGEventMaskBit(kCGEventKeyUp)
    /// </summary>
    public static ulong CGEventMaskBit(uint eventType)
    {
        return 1UL << (int)eventType;
    }

    /// <summary>
    /// Kiểm tra Shift có đang được nhấn từ flags.
    /// </summary>
    public static bool IsShiftPressed(ulong flags)
    {
        return (flags & kCGEventFlagMaskShift) != 0;
    }

    /// <summary>
    /// Kiểm tra Command (⌘) có đang được nhấn từ flags.
    /// </summary>
    public static bool IsCommandPressed(ulong flags)
    {
        return (flags & kCGEventFlagMaskCommand) != 0;
    }

    /// <summary>
    /// Kiểm tra Control có đang được nhấn từ flags.
    /// </summary>
    public static bool IsControlPressed(ulong flags)
    {
        return (flags & kCGEventFlagMaskControl) != 0;
    }

    /// <summary>
    /// Kiểm tra Option/Alt có đang được nhấn từ flags.
    /// </summary>
    public static bool IsOptionPressed(ulong flags)
    {
        return (flags & kCGEventFlagMaskAlternate) != 0;
    }

    /// <summary>
    /// Lấy unicode char từ keyboard event.
    /// Tương đương NativeMethods.VirtualKeyToChar() trên Windows.
    /// </summary>
    public static char? GetCharFromEvent(IntPtr eventRef)
    {
        var buffer = new ushort[4];
        CGEventKeyboardGetUnicodeString(eventRef, (nuint)4, out nuint actualLength, buffer);

        if (actualLength >= 1)
        {
            return (char)buffer[0];
        }
        return null;
    }

    #endregion
}
