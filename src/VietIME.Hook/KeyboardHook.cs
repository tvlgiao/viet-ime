using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VietIME.Core.Engines;

namespace VietIME.Hook;

/// <summary>
/// Quản lý keyboard hook - bắt tất cả phím trên toàn hệ thống
///
/// Chiến lược v8 - ATOMIC SENDINPUT:
/// - Gộp backspace + unicode chars vào 1 lần SendInput duy nhất (atomic, không race condition)
/// - Không dùng clipboard, không Thread.Sleep
/// - Background thread gửi output → hook callback return ngay → không block gõ phím
/// - Queue phím khi busy → xử lý đúng thứ tự sau khi output gửi xong
/// </summary>
public class KeyboardHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IInputEngine? _engine;
    private bool _isEnabled = true;
    private bool _disposed = false;
    private volatile bool _isSendingInput = false;
    private volatile bool _isBusySending = false;

    private static readonly UIntPtr VIET_IME_MARKER = new(0x56494D45);

    // Danh sách process cần dùng clipboard mode (Warp IDE, etc.)
    private static readonly HashSet<string> _clipboardApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "warp"
    };

    private const int BUFFER_TIMEOUT_MS = 2000;
    private DateTime _lastKeyTime = DateTime.MinValue;
    private DateTime _lastToggleTime = DateTime.MinValue;
    private const int TOGGLE_DEBOUNCE_MS = 300;

    // Queue phím chờ xử lý khi đang busy
    private readonly ConcurrentQueue<PendingKey> _pendingKeys = new();
    private record PendingKey(char Char, bool IsShift);

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public event EventHandler<bool>? EnabledChanged;
    public event EventHandler<string>? Error;
    public event Action<string>? DebugLog;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                _engine?.Reset();
                EnabledChanged?.Invoke(this, value);
            }
        }
    }

    public IInputEngine? Engine
    {
        get => _engine;
        set
        {
            _engine?.Reset();
            _engine = value;
        }
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;

        if (curModule == null)
        {
            Error?.Invoke(this, "Không thể lấy module handle");
            return;
        }

        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _proc,
            NativeMethods.GetModuleHandle(curModule.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Error?.Invoke(this, $"Không thể cài đặt hook. Error code: {error}");
        }
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode < 0 || _isSendingInput)
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            bool isShiftPressed = NativeMethods.IsShiftPressed(); // Bắt NGAY trước khi bị mất

            if ((ulong)hookStruct.dwExtraInfo == (ulong)VIET_IME_MARKER)
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            if (msg != NativeMethods.WM_KEYDOWN && msg != NativeMethods.WM_SYSKEYDOWN)
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            if (HandleToggleHotkey(hookStruct.vkCode))
                return (IntPtr)1;

            if (!_isEnabled || _engine == null)
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            if (NativeMethods.IsCtrlPressed() || NativeMethods.IsAltPressed())
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            if (HandleSpecialKey(hookStruct.vkCode))
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            // Timeout check
            var now = DateTime.UtcNow;
            if (_lastKeyTime != DateTime.MinValue &&
                (now - _lastKeyTime).TotalMilliseconds > BUFFER_TIMEOUT_MS)
            {
                _engine.Reset();
            }
            _lastKeyTime = now;

            char? ch = NativeMethods.VirtualKeyToChar(hookStruct.vkCode, hookStruct.scanCode, isShiftPressed);
            if (!ch.HasValue)
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            // Nếu đang busy gửi output, queue phím lại
            if (_isBusySending)
            {
                _pendingKeys.Enqueue(new PendingKey(ch.Value, isShiftPressed));
                return (IntPtr)1; // Chặn phím - sẽ xử lý sau
            }

            // Xử lý ký tự qua engine
            var result = _engine.ProcessKey(ch.Value, isShiftPressed);

            DebugLog?.Invoke($"Key '{ch.Value}': Handled={result.Handled}, Output='{result.OutputText}', BS={result.BackspaceCount}");

            if (result.Handled && result.OutputText != null)
            {
                // Gửi output trên background thread → hook return ngay → không block gõ phím
                _isBusySending = true;
                int bs = result.BackspaceCount;
                string text = result.OutputText;

                Task.Run(() =>
                {
                    try
                    {
                        SendAtomicReplace(bs, text);
                    }
                    finally
                    {
                        ProcessPendingKeys();       // Xử lý queue TRƯỚC
                        _isBusySending = false;     // Mở khóa SAU → tránh race condition
                    }
                });

                return (IntPtr)1; // Chặn phím gốc
            }

            // Engine không xử lý → phím đi qua bình thường
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            DebugLog?.Invoke($"Error: {ex.Message}");
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
    }

    /// <summary>
    /// Xử lý phím đã queue khi đang busy
    /// Chạy trên background thread sau khi output gửi xong
    /// </summary>
    private void ProcessPendingKeys()
    {
        while (_pendingKeys.TryDequeue(out var pending))
        {
            if (_engine == null) break;

            var result = _engine.ProcessKey(pending.Char, pending.IsShift);

            if (result.Handled && result.OutputText != null)
            {
                SendAtomicReplace(result.BackspaceCount, result.OutputText);
            }
            else
            {
                // Engine không xử lý → gửi ký tự gốc
                SendCharDirectly(pending.Char);
            }
        }
    }

    /// <summary>
    /// Kiểm tra foreground app có cần dùng clipboard mode không
    /// </summary>
    private bool IsClipboardApp()
    {
        var processName = NativeMethods.GetForegroundProcessName();
        return processName != null && _clipboardApps.Contains(processName);
    }

    /// <summary>
    /// Gửi text qua clipboard: backspace → copy text → Ctrl+V → restore clipboard
    /// Dùng cho các app không hỗ trợ SendInput KEYEVENTF_UNICODE (Warp IDE, etc.)
    /// </summary>
    private void SendViaClipboard(int backspaceCount, string text)
    {
        _isSendingInput = true;
        try
        {
            // 1. Gửi backspace qua SendInput (giống SendAtomicReplace)
            if (backspaceCount > 0)
            {
                var bsInputs = new NativeMethods.INPUT[backspaceCount * 2];
                for (int i = 0; i < backspaceCount; i++)
                {
                    bsInputs[i * 2] = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_KEYBOARD,
                        u = new NativeMethods.INPUTUNION
                        {
                            ki = new NativeMethods.KEYBDINPUT
                            {
                                wVk = (ushort)NativeMethods.VK_BACK,
                                wScan = 0,
                                dwFlags = 0,
                                time = 0,
                                dwExtraInfo = VIET_IME_MARKER
                            }
                        }
                    };
                    bsInputs[i * 2 + 1] = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_KEYBOARD,
                        u = new NativeMethods.INPUTUNION
                        {
                            ki = new NativeMethods.KEYBDINPUT
                            {
                                wVk = (ushort)NativeMethods.VK_BACK,
                                wScan = 0,
                                dwFlags = (uint)NativeMethods.KEYEVENTF_KEYUP,
                                time = 0,
                                dwExtraInfo = VIET_IME_MARKER
                            }
                        }
                    };
                }
                NativeMethods.SendInput((uint)bsInputs.Length, bsInputs, Marshal.SizeOf<NativeMethods.INPUT>());
                // Giảm delay từ 10ms xuống 3ms - tối ưu cho Warp terminal
                Thread.Sleep(3);
            }

            if (text.Length == 0) return;

            // 2. Backup clipboard cũ
            string? oldClipboard = null;
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                    if (hData != IntPtr.Zero)
                    {
                        var pData = NativeMethods.GlobalLock(hData);
                        if (pData != IntPtr.Zero)
                        {
                            oldClipboard = Marshal.PtrToStringUni(pData);
                            NativeMethods.GlobalUnlock(hData);
                        }
                    }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            // 3. Set clipboard mới với text cần paste
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    NativeMethods.EmptyClipboard();
                    var bytes = (text.Length + 1) * 2; // UTF-16 + null terminator
                    var hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hGlobal != IntPtr.Zero)
                    {
                        var pGlobal = NativeMethods.GlobalLock(hGlobal);
                        if (pGlobal != IntPtr.Zero)
                        {
                            Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                            Marshal.WriteInt16(pGlobal, text.Length * 2, 0); // null terminator
                            NativeMethods.GlobalUnlock(hGlobal);
                        }
                        NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hGlobal);
                    }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            // 4. Simulate Ctrl+V
            var pasteInputs = new NativeMethods.INPUT[4];
            // Ctrl down
            pasteInputs[0] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)NativeMethods.VK_CONTROL,
                        wScan = 0, dwFlags = 0, time = 0,
                        dwExtraInfo = VIET_IME_MARKER
                    }
                }
            };
            // V down
            pasteInputs[1] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)NativeMethods.VK_V,
                        wScan = 0, dwFlags = 0, time = 0,
                        dwExtraInfo = VIET_IME_MARKER
                    }
                }
            };
            // V up
            pasteInputs[2] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)NativeMethods.VK_V,
                        wScan = 0,
                        dwFlags = (uint)NativeMethods.KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = VIET_IME_MARKER
                    }
                }
            };
            // Ctrl up
            pasteInputs[3] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)NativeMethods.VK_CONTROL,
                        wScan = 0,
                        dwFlags = (uint)NativeMethods.KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = VIET_IME_MARKER
                    }
                }
            };
            NativeMethods.SendInput(4, pasteInputs, Marshal.SizeOf<NativeMethods.INPUT>());

            // 5. Restore clipboard cũ - ASYNC để không block gõ phím (giảm từ 50ms xuống 0ms)
            if (oldClipboard != null)
            {
                var clipboardToRestore = oldClipboard;
                Task.Delay(30).ContinueWith(_ =>
                {
                    try
                    {
                        if (NativeMethods.OpenClipboard(IntPtr.Zero))
                        {
                            try
                            {
                                NativeMethods.EmptyClipboard();
                                var bytes = (clipboardToRestore.Length + 1) * 2;
                                var hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes);
                                if (hGlobal != IntPtr.Zero)
                                {
                                    var pGlobal = NativeMethods.GlobalLock(hGlobal);
                                    if (pGlobal != IntPtr.Zero)
                                    {
                                        Marshal.Copy(clipboardToRestore.ToCharArray(), 0, pGlobal, clipboardToRestore.Length);
                                        Marshal.WriteInt16(pGlobal, clipboardToRestore.Length * 2, 0);
                                        NativeMethods.GlobalUnlock(hGlobal);
                                    }
                                    NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hGlobal);
                                }
                            }
                            finally
                            {
                                NativeMethods.CloseClipboard();
                            }
                        }
                    }
                    catch { /* Bỏ qua lỗi khôi phục clipboard */ }
                });
            }

            DebugLog?.Invoke($"SendViaClipboard: bs={backspaceCount}, text='{text}'");
        }
        finally
        {
            _isSendingInput = false;
        }
    }

    /// <summary>
    /// Gửi backspace + unicode text qua SendInput.
    /// Tách thành 2 batch: backspace trước, unicode sau, với delay nhỏ ở giữa.
    /// Một số terminal/app không xử lý tốt khi trộn VK events và KEYEVENTF_UNICODE
    /// events trong cùng 1 SendInput call.
    /// </summary>
    private void SendAtomicReplace(int backspaceCount, string text)
    {
        // Clipboard mode cho các app không hỗ trợ KEYEVENTF_UNICODE (Warp IDE, etc.)
        if (IsClipboardApp())
        {
            SendViaClipboard(backspaceCount, text);
            return;
        }

        _isSendingInput = true;
        try
        {
            // 1. Gửi backspace (VK_BACK) - batch riêng
            if (backspaceCount > 0)
            {
                var bsInputs = new NativeMethods.INPUT[backspaceCount * 2];
                for (int i = 0; i < backspaceCount; i++)
                {
                    bsInputs[i * 2] = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_KEYBOARD,
                        u = new NativeMethods.INPUTUNION
                        {
                            ki = new NativeMethods.KEYBDINPUT
                            {
                                wVk = (ushort)NativeMethods.VK_BACK,
                                wScan = 0,
                                dwFlags = 0,
                                time = 0,
                                dwExtraInfo = VIET_IME_MARKER
                            }
                        }
                    };
                    bsInputs[i * 2 + 1] = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_KEYBOARD,
                        u = new NativeMethods.INPUTUNION
                        {
                            ki = new NativeMethods.KEYBDINPUT
                            {
                                wVk = (ushort)NativeMethods.VK_BACK,
                                wScan = 0,
                                dwFlags = (uint)NativeMethods.KEYEVENTF_KEYUP,
                                time = 0,
                                dwExtraInfo = VIET_IME_MARKER
                            }
                        }
                    };
                }
                NativeMethods.SendInput((uint)bsInputs.Length, bsInputs, Marshal.SizeOf<NativeMethods.INPUT>());

                // Delay nhỏ để app kịp xử lý backspace trước khi nhận unicode
                Thread.Sleep(10);
            }

            // 2. Gửi unicode text - batch riêng
            if (text.Length > 0)
            {
                var textInputs = new NativeMethods.INPUT[text.Length * 2];
                for (int i = 0; i < text.Length; i++)
                {
                    ushort unicode = text[i];
                    textInputs[i * 2] = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_KEYBOARD,
                        u = new NativeMethods.INPUTUNION
                        {
                            ki = new NativeMethods.KEYBDINPUT
                            {
                                wVk = 0,
                                wScan = unicode,
                                dwFlags = (uint)NativeMethods.KEYEVENTF_UNICODE,
                                time = 0,
                                dwExtraInfo = VIET_IME_MARKER
                            }
                        }
                    };
                    textInputs[i * 2 + 1] = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_KEYBOARD,
                        u = new NativeMethods.INPUTUNION
                        {
                            ki = new NativeMethods.KEYBDINPUT
                            {
                                wVk = 0,
                                wScan = unicode,
                                dwFlags = (uint)(NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP),
                                time = 0,
                                dwExtraInfo = VIET_IME_MARKER
                            }
                        }
                    };
                }
                NativeMethods.SendInput((uint)textInputs.Length, textInputs, Marshal.SizeOf<NativeMethods.INPUT>());
            }

            DebugLog?.Invoke($"SendAtomicReplace: bs={backspaceCount}, text='{text}'");
        }
        finally
        {
            _isSendingInput = false;
        }
    }

    /// <summary>
    /// Gửi 1 ký tự trực tiếp (cho phím đã bị chặn nhưng engine không xử lý)
    /// </summary>
    private void SendCharDirectly(char ch)
    {
        // Clipboard mode cho các app không hỗ trợ KEYEVENTF_UNICODE
        if (IsClipboardApp())
        {
            SendViaClipboard(0, ch.ToString());
            return;
        }

        _isSendingInput = true;
        try
        {
            ushort unicode = ch;
            var inputs = new NativeMethods.INPUT[2];
            inputs[0] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0, wScan = unicode,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE,
                        time = 0, dwExtraInfo = VIET_IME_MARKER
                    }
                }
            };
            inputs[1] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0, wScan = unicode,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
                        time = 0, dwExtraInfo = VIET_IME_MARKER
                    }
                }
            };
            NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        }
        finally
        {
            _isSendingInput = false;
        }
    }

    private bool HandleToggleHotkey(uint vkCode)
    {
        // Ctrl + ` (backtick/tilde key) - toggle bật/tắt
        if (vkCode == 0xC0 && NativeMethods.IsCtrlPressed() && !NativeMethods.IsShiftPressed())
            return TryToggle();

        // Ctrl + Shift (bất kỳ phím Shift nào) - luôn TẮT VietIME
        // Dùng khi Unikey chạy song song: người dùng ấn Ctrl+Shift để chuyển layout → tắt VietIME
        if ((vkCode == 0xA0 || vkCode == 0xA1) && NativeMethods.IsCtrlPressed()) // VK_LSHIFT hoặc VK_RSHIFT
            return TryDisable();
        if (vkCode == 0xA2 || vkCode == 0xA3) // VK_LCONTROL hoặc VK_RCONTROL
        {
            if (NativeMethods.IsShiftPressed())
                return TryDisable();
        }

        return false;
    }

    private bool TryToggle()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastToggleTime).TotalMilliseconds < TOGGLE_DEBOUNCE_MS)
            return true;
        _lastToggleTime = now;
        IsEnabled = !IsEnabled;
        return true;
    }

    private bool TryDisable()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastToggleTime).TotalMilliseconds < TOGGLE_DEBOUNCE_MS)
            return false; // Không chặn phím, để Ctrl+Shift đi qua cho Unikey
        _lastToggleTime = now;
        if (_isEnabled)
        {
            IsEnabled = false;
        }
        return false; // Không chặn phím - để Ctrl+Shift đi qua cho hệ thống/Unikey xử lý
    }

    private bool HandleSpecialKey(uint vkCode)
    {
        switch (vkCode)
        {
            case NativeMethods.VK_SPACE:
            case NativeMethods.VK_RETURN:
            case NativeMethods.VK_TAB:
            case NativeMethods.VK_ESCAPE:
                _engine?.Reset();
                return true;
            case NativeMethods.VK_BACK:
                _engine?.ProcessBackspace();
                return true;
            case NativeMethods.VK_LEFT:
            case NativeMethods.VK_RIGHT:
            case NativeMethods.VK_UP:
            case NativeMethods.VK_DOWN:
            case NativeMethods.VK_HOME:
            case NativeMethods.VK_END:
            case NativeMethods.VK_PRIOR:
            case NativeMethods.VK_NEXT:
            case NativeMethods.VK_DELETE:
                _engine?.Reset();
                return true;
            default:
                return false;
        }
    }

    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
    protected virtual void Dispose(bool disposing) { if (!_disposed) { Uninstall(); _disposed = true; } }
    ~KeyboardHook() { Dispose(false); }
}
