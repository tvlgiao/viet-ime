using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VietIME.Hook;

/// <summary>
/// Win32 API declarations cho keyboard hook và SendInput
/// Các API này cho phép ứng dụng hoạt động system-wide trên tất cả các app Windows
/// </summary>
public static class NativeMethods
{
    #region Constants
    
    // Hook types
    public const int WH_KEYBOARD_LL = 13;
    
    // Keyboard messages
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    
    // Virtual key codes
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12; // Alt key
    public const int VK_BACK = 0x08; // Backspace
    public const int VK_RETURN = 0x0D; // Enter
    public const int VK_SPACE = 0x20;
    public const int VK_TAB = 0x09;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_V = 0x56;
    public const int VK_Z = 0x5A;
    
    // Phím di chuyển con trỏ
    public const uint VK_LEFT = 0x25;
    public const uint VK_UP = 0x26;
    public const uint VK_RIGHT = 0x27;
    public const uint VK_DOWN = 0x28;
    public const uint VK_HOME = 0x24;
    public const uint VK_END = 0x23;
    public const uint VK_PRIOR = 0x21; // Page Up
    public const uint VK_NEXT = 0x22;  // Page Down
    public const uint VK_DELETE = 0x2E;
    
    // Key state flags
    public const int KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const int KEYEVENTF_KEYUP = 0x0002;
    public const int KEYEVENTF_UNICODE = 0x0004;
    public const int KEYEVENTF_SCANCODE = 0x0008;
    
    // Input type
    public const int INPUT_KEYBOARD = 1;
    
    #endregion
    
    #region Delegates
    
    /// <summary>
    /// Delegate cho hook callback
    /// </summary>
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    
    #endregion
    
    #region Structs
    
    /// <summary>
    /// Thông tin về phím được nhấn
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;      // Virtual key code
        public uint scanCode;    // Hardware scan code
        public uint flags;       // Flags
        public uint time;        // Timestamp
        public IntPtr dwExtraInfo;
    }
    
    /// <summary>
    /// Input event cho SendInput - Cần đúng size cho 64-bit Windows
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }
    
    /// <summary>
    /// Union cho các loại input khác nhau - Size = 32 bytes trên 64-bit
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }
    
    /// <summary>
    /// Keyboard input data
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;         // Virtual key code
        public ushort wScan;       // Unicode character or scan code  
        public uint dwFlags;       // Flags
        public uint time;          // Timestamp
        public UIntPtr dwExtraInfo; // Extra info - UIntPtr cho 64-bit
    }
    
    /// <summary>
    /// Mouse input data (cần cho union size đúng)
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
    
    /// <summary>
    /// Hardware input data (cần cho union size đúng)
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
    
    #endregion
    
    #region Imports
    
    /// <summary>
    /// Cài đặt hook vào Windows message queue
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(
        int idHook, 
        LowLevelKeyboardProc lpfn, 
        IntPtr hMod, 
        uint dwThreadId);
    
    /// <summary>
    /// Gỡ bỏ hook
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    
    /// <summary>
    /// Gọi hook tiếp theo trong chuỗi
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr CallNextHookEx(
        IntPtr hhk, 
        int nCode, 
        IntPtr wParam, 
        IntPtr lParam);
    
    /// <summary>
    /// Lấy handle của module
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);
    
    /// <summary>
    /// Gửi input events (keyboard, mouse, etc.)
    /// Đây là API chính để gửi ký tự Unicode đến các ứng dụng
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    
    /// <summary>
    /// Lấy trạng thái của một phím
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);
    
    /// <summary>
    /// Lấy trạng thái async của một phím
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
    
    /// <summary>
    /// Lấy handle của foreground window
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    
    /// <summary>
    /// Lấy thread ID của window
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    
    /// <summary>
    /// Lấy keyboard layout của thread
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);
    
    /// <summary>
    /// Chuyển virtual key thành ký tự
    /// </summary>
    [DllImport("user32.dll")]
    public static extern int ToUnicodeEx(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszBuff,
        int cchBuff,
        uint wFlags,
        IntPtr dwhkl);
    
    /// <summary>
    /// Lấy trạng thái bàn phím
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetKeyboardState(byte[] lpKeyState);
    
    
    /// <summary>
    /// Giải phóng GDI icon handle
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);
    
    #region Clipboard APIs
    
    [DllImport("user32.dll")]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);
    
    [DllImport("user32.dll")]
    public static extern bool CloseClipboard();
    
    [DllImport("user32.dll")]
    public static extern bool EmptyClipboard();
    
    [DllImport("user32.dll")]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    
    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardData(uint uFormat);
    
    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    
    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);
    
    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr hMem);
    
    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalFree(IntPtr hMem);
    
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;
    
    #endregion
    
    #endregion
    
    #region Helper Methods
    
    /// <summary>
    /// Lấy tên process của foreground window
    /// </summary>
    public static string? GetForegroundProcessName()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hWnd, out uint processId);
            if (processId == 0) return null;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName?.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Kiểm tra phím có đang được nhấn không
    /// </summary>
    public static bool IsKeyPressed(int vKey)
    {
        return (GetAsyncKeyState(vKey) & 0x8000) != 0;
    }
    
    /// <summary>
    /// Kiểm tra phím Shift có đang được nhấn không
    /// </summary>
    public static bool IsShiftPressed()
    {
        return IsKeyPressed(VK_SHIFT);
    }
    
    /// <summary>
    /// Kiểm tra phím Ctrl có đang được nhấn không
    /// </summary>
    public static bool IsCtrlPressed()
    {
        return IsKeyPressed(VK_CONTROL);
    }
    
    /// <summary>
    /// Kiểm tra phím Alt có đang được nhấn không
    /// </summary>
    public static bool IsAltPressed()
    {
        return IsKeyPressed(VK_MENU);
    }
    
    /// <summary>
    /// Chuyển virtual key code thành ký tự
    /// Sửa lỗi: Đảm bảo trạng thái Shift/CapsLock được phản ánh chính xác
    /// vì GetKeyboardState trong hook callback có thể không đồng bộ
    /// </summary>
    public static char? VirtualKeyToChar(uint vkCode, uint scanCode, bool? shiftOverride = null)
    {
        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState))
            return null;

        // Dùng shiftOverride nếu có (bắt sớm từ hook callback),
        // fallback sang GetAsyncKeyState (có thể trả về trạng thái cũ khi gõ nhanh)
        if (shiftOverride.HasValue)
        {
            keyboardState[VK_SHIFT] = shiftOverride.Value ? (byte)0x80 : (byte)0;
        }
        else if (IsShiftPressed())
        {
            keyboardState[VK_SHIFT] = 0x80;
        }
        else
        {
            keyboardState[VK_SHIFT] = 0;
        }

        // Đồng bộ CapsLock toggle state
        short capsState = GetKeyState(0x14); // VK_CAPITAL
        if ((capsState & 0x0001) != 0)
        {
            keyboardState[0x14] = 0x01; // Toggle on
        }
        else
        {
            keyboardState[0x14] = 0x00;
        }

        var sb = new System.Text.StringBuilder(2);

        // Lấy keyboard layout của foreground window
        var hWnd = GetForegroundWindow();
        var threadId = GetWindowThreadProcessId(hWnd, out _);
        var layout = GetKeyboardLayout(threadId);

        int result = ToUnicodeEx(vkCode, scanCode, keyboardState, sb, sb.Capacity, 0, layout);

        if (result == 1)
        {
            return sb[0];
        }

        return null;
    }
    
    #endregion
}
