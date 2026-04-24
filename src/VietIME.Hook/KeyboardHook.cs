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

    // Warp-only mode: chỉ bật VietIME khi foreground app là Warp.
    // Khi bật, polling thread tự set IsEnabled theo foreground app.
    private bool _warpOnlyMode = false;
    private bool? _lastForegroundIsWarp = null;
    private Timer? _warpWatcherTimer;
    private const int WARP_POLL_INTERVAL_MS = 500;
    private static readonly HashSet<string> _warpProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "warp"
    };

    // Queue phím chờ xử lý khi đang busy
    private readonly ConcurrentQueue<PendingKey> _pendingKeys = new();
    private record PendingKey(char Char, bool IsShift);

    // Lock để tránh race condition với clipboard operations
    private volatile bool _isClipboardBusy = false;
    private CancellationTokenSource? _clipboardRestoreCts = null;
    private readonly object _clipboardLock = new();

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

    /// <summary>
    /// Chỉ bật VietIME khi foreground app là Warp.
    /// Khi bật, polling timer tự update IsEnabled theo foreground:
    ///   foreground = Warp → IsEnabled = true
    ///   foreground ≠ Warp → IsEnabled = false
    /// User vẫn toggle được qua hotkey/UI trong khi ở 1 app; khi chuyển app khác thì state reset theo rule.
    /// </summary>
    public bool WarpOnlyMode
    {
        get => _warpOnlyMode;
        set
        {
            if (_warpOnlyMode == value) return;
            _warpOnlyMode = value;
            if (_warpOnlyMode)
            {
                _lastForegroundIsWarp = null; // Force update ở tick đầu
                StartWarpWatcher();
            }
            else
            {
                StopWarpWatcher();
            }
        }
    }

    private void StartWarpWatcher()
    {
        _warpWatcherTimer?.Dispose();
        _warpWatcherTimer = new Timer(WarpWatcherTick, null, 0, WARP_POLL_INTERVAL_MS);
    }

    private void StopWarpWatcher()
    {
        _warpWatcherTimer?.Dispose();
        _warpWatcherTimer = null;
    }

    private void WarpWatcherTick(object? state)
    {
        if (!_warpOnlyMode) return;
        try
        {
            bool isWarp = IsWarpForeground();
            if (_lastForegroundIsWarp != isWarp)
            {
                _lastForegroundIsWarp = isWarp;
                IsEnabled = isWarp;
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Force sync check Warp foreground ngay lập tức — dùng khi cần UI hiển thị đúng state
    /// mà không phải chờ 500ms tick tiếp theo.
    /// </summary>
    public void ForceWarpCheck()
    {
        if (!_warpOnlyMode) return;
        WarpWatcherTick(null);
    }

    private static bool IsWarpForeground()
    {
        var name = NativeMethods.GetForegroundProcessName();
        return name != null && _warpProcessNames.Contains(name);
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
    /// Xử lý phím đã queue khi đang busy.
    /// Batch tất cả pending keys → xử lý qua engine → gửi 1 lần duy nhất.
    /// Giảm số lần SendInput/clipboard operations từ N xuống 1 → nhanh hơn trên RDP.
    /// </summary>
    private void ProcessPendingKeys()
    {
        if (_engine == null)
        {
            while (_pendingKeys.TryDequeue(out _)) { }
            return;
        }

        if (!_pendingKeys.TryPeek(out _)) return;

        // Lưu trạng thái word đang hiển thị trên màn hình
        // (= engine buffer sau lần gửi trước)
        string screenWord = _engine.GetBuffer();
        int screenWordLen = screenWord.Length;
        bool isClipboard = IsClipboardApp();

        while (_pendingKeys.TryDequeue(out var pending))
        {
            if (_engine == null) break;

            // Non-letter (trừ bracket shortcuts) → word boundary
            bool isWordBoundary = !char.IsLetter(pending.Char)
                                  && pending.Char != '[' && pending.Char != ']';

            if (isWordBoundary)
            {
                // Flush word hiện tại trước
                string currentWord = _engine.GetBuffer();
                if (currentWord != screenWord)
                {
                    if (isClipboard)
                        SendViaClipboard(screenWordLen, currentWord);
                    else
                        SendAtomicReplace(screenWordLen, currentWord);
                }

                // Process non-letter (engine sẽ reset buffer)
                _engine.ProcessKey(pending.Char, pending.IsShift);
                SendCharDirectly(pending.Char);

                // Reset tracking cho word mới
                screenWord = _engine.GetBuffer();
                screenWordLen = screenWord.Length;
                continue;
            }

            // Letter/bracket: engine tự cập nhật buffer nội bộ
            _engine.ProcessKey(pending.Char, pending.IsShift);
        }

        // Flush word cuối cùng - chỉ gửi nếu có thay đổi
        string finalWord = _engine.GetBuffer();
        if (finalWord != screenWord)
        {
            if (isClipboard)
                SendViaClipboard(screenWordLen, finalWord);
            else
                SendAtomicReplace(screenWordLen, finalWord);
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
    /// FIX: Không restore clipboard để tránh race condition khi gõ nhanh
    /// </summary>
    private void SendViaClipboard(int backspaceCount, string text)
    {
        _isSendingInput = true;
        try
        {
            // Cancel pending restore nếu có (vì có input mới)
            lock (_clipboardLock)
            {
                _clipboardRestoreCts?.Cancel();
                _clipboardRestoreCts = null;
            }

            // 1. Gửi backspace qua SendInput
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
                Thread.Sleep(2);
            }

            if (text.Length == 0) return;

            // 2. Backup clipboard cũ (chỉ backup 1 lần, không backup lại nếu đang có pending restore)
            string? oldClipboard = null;
            lock (_clipboardLock)
            {
                if (!_isClipboardBusy)
                {
                    _isClipboardBusy = true;
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
                }
            }

            // 3. Set clipboard mới với text cần paste
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    NativeMethods.EmptyClipboard();
                    var bytes = (text.Length + 1) * 2;
                    var hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hGlobal != IntPtr.Zero)
                    {
                        var pGlobal = NativeMethods.GlobalLock(hGlobal);
                        if (pGlobal != IntPtr.Zero)
                        {
                            Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                            Marshal.WriteInt16(pGlobal, text.Length * 2, 0);
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

            // 5. Restore clipboard sau delay dài hơn (500ms) và có thể cancel
            if (oldClipboard != null)
            {
                var cts = new CancellationTokenSource();
                lock (_clipboardLock)
                {
                    _clipboardRestoreCts = cts;
                }

                var clipboardToRestore = oldClipboard;
                Task.Delay(500, cts.Token).ContinueWith(_ =>
                {
                    if (cts.Token.IsCancellationRequested) return;

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
                    catch { /* Bỏ qua lỗi */ }
                    finally
                    {
                        lock (_clipboardLock)
                        {
                            _isClipboardBusy = false;
                            if (_clipboardRestoreCts == cts)
                                _clipboardRestoreCts = null;
                        }
                    }
                }, TaskScheduler.Default);
            }
            else
            {
                // Không có clipboard cũ cần restore
                lock (_clipboardLock)
                {
                    _isClipboardBusy = false;
                }
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
                // Giảm từ 10ms → 1ms cho nhanh hơn trên RDP
                Thread.Sleep(1);
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
        if (vkCode != NativeMethods.VK_Z) return false;
        if (!NativeMethods.IsCtrlPressed() || !NativeMethods.IsAltPressed()) return false;

        // Ctrl + Alt + Shift + Z → luôn TẮT VietIME (nhường Unikey hoặc để gõ tiếng Anh)
        if (NativeMethods.IsShiftPressed())
        {
            TryDisable();
            return true; // Chặn phím để không gửi Z ra app
        }

        // Ctrl + Alt + Z → toggle bật/tắt VietIME
        return TryToggle();
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

    private void TryDisable()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastToggleTime).TotalMilliseconds < TOGGLE_DEBOUNCE_MS) return;
        _lastToggleTime = now;
        if (_isEnabled) IsEnabled = false;
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
    protected virtual void Dispose(bool disposing) { if (!_disposed) { StopWarpWatcher(); Uninstall(); _disposed = true; } }
    ~KeyboardHook() { Dispose(false); }
}
