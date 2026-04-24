using System.Collections.Concurrent;
using VietIME.Core.Engines;

namespace VietIME.Mac;

/// <summary>
/// Quản lý keyboard hook trên macOS - bắt tất cả phím trên toàn hệ thống.
/// Tương đương KeyboardHook.cs trên Windows nhưng dùng CGEventTap thay vì SetWindowsHookEx.
///
/// Chiến lược:
/// - CGEventTap bắt kCGEventKeyDown → lấy unicode char → gọi IInputEngine.ProcessKey()
/// - Nếu engine xử lý: chặn event gốc, gửi backspace + output text qua CGEventPost
/// - Background thread gửi output → callback return nhanh → tránh bị macOS disable tap
/// - Queue phím khi busy → xử lý đúng thứ tự sau khi output gửi xong
/// </summary>
public class MacKeyboardHook : IDisposable
{
    private IntPtr _tapPort = IntPtr.Zero;
    private IntPtr _runLoopSource = IntPtr.Zero;
    private IntPtr _runLoop = IntPtr.Zero;
    private IntPtr _eventSource = IntPtr.Zero;
    private Thread? _tapThread;
    private MacNativeMethods.CGEventTapCallBack? _callback; // Giữ reference tránh GC

    private IInputEngine? _engine;
    private bool _isEnabled = true;
    private bool _disposed = false;
    private volatile bool _isSendingInput = false;
    private volatile bool _isBusySending = false;

    // Marker để nhận diện input từ chính VietIME (tương đương VIET_IME_MARKER 0x56494D45 trên Windows)
    // Dùng custom userData field trên CGEvent
    private const long VIME_MARKER_VALUE = 0x56494D45;
    // CGEventField cho user data (field 43 = kCGEventSourceUserData)
    private const uint kCGEventSourceUserData = 43;

    private const int BUFFER_TIMEOUT_MS = 2000;
    private DateTime _lastKeyTime = DateTime.MinValue;
    private DateTime _lastToggleTime = DateTime.MinValue;
    private const int TOGGLE_DEBOUNCE_MS = 300;

    // Warp-only mode: chỉ bật VietIME khi foreground app là Warp
    private bool _warpOnlyMode = false;
    private bool? _lastForegroundIsWarp = null;
    private Timer? _warpWatcherTimer;
    private const int WARP_POLL_INTERVAL_MS = 500;

    // Queue phím chờ xử lý khi đang busy
    private readonly ConcurrentQueue<PendingKey> _pendingKeys = new();
    private record PendingKey(char Char, bool IsShift);

    public MacKeyboardHook()
    {
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
    /// Chỉ bật VietIME khi foreground app là Warp (bundle id dev.warp.Warp*).
    /// Tương đương WarpOnlyMode trên Windows.
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
                _lastForegroundIsWarp = null;
                _warpWatcherTimer?.Dispose();
                _warpWatcherTimer = new Timer(WarpWatcherTick, null, 0, WARP_POLL_INTERVAL_MS);
            }
            else
            {
                _warpWatcherTimer?.Dispose();
                _warpWatcherTimer = null;
            }
        }
    }

    private void WarpWatcherTick(object? state)
    {
        if (!_warpOnlyMode) return;
        try
        {
            bool isWarp = MacForegroundHelper.IsWarpForeground();
            if (_lastForegroundIsWarp != isWarp)
            {
                _lastForegroundIsWarp = isWarp;
                IsEnabled = isWarp;
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Cài đặt keyboard hook (CGEventTap).
    /// Tương đương KeyboardHook.Install() trên Windows.
    /// </summary>
    public void Install()
    {
        if (_tapPort != IntPtr.Zero) return;

        // Kiểm tra quyền trước
        Console.WriteLine("[Hook] Kiểm tra quyền...");
        bool hasListen = MacPermissionHelper.HasInputMonitoringPermission();
        bool hasPost = MacPermissionHelper.HasPostEventPermission();
        Console.WriteLine($"[Hook] Input Monitoring: {hasListen}, Accessibility (Post Event): {hasPost}");

        if (!hasListen || !hasPost)
        {
            Console.WriteLine("[Hook] Thiếu quyền - yêu cầu...");
            MacPermissionHelper.RequestAllPermissions();

            // Kiểm tra lại
            hasListen = MacPermissionHelper.HasInputMonitoringPermission();
            hasPost = MacPermissionHelper.HasPostEventPermission();

            var permError = MacPermissionHelper.CheckAllPermissions();
            if (permError != null)
            {
                Console.WriteLine($"[Hook] {permError}");
                Error?.Invoke(this, permError);
                return;
            }
        }
        Console.WriteLine("[Hook] Quyền OK, tạo CGEventTap...");

        // Tạo event source cho output (state 0 = kCGEventSourceStateCombinedSessionState)
        // Không dùng Private (1) vì synthetic events cần được hệ thống nhận dạng
        _eventSource = MacNativeMethods.CGEventSourceCreate(0);
        if (_eventSource == IntPtr.Zero)
        {
            Console.WriteLine("[Hook] WARNING: CGEventSourceCreate failed, sử dụng null source");
        }

        // Giữ callback reference để tránh GC (giống Windows giữ delegate LowLevelKeyboardProc)
        _callback = HookCallback;

        // Tạo event tap trên background thread
        _tapThread = new Thread(RunEventTap)
        {
            Name = "VietIME-EventTap",
            IsBackground = true
        };
        _tapThread.Start();
    }

    /// <summary>
    /// Chạy event tap loop trên dedicated thread.
    /// CGEventTap cần run loop riêng để nhận events.
    /// </summary>
    private void RunEventTap()
    {
        // Bắt kCGEventKeyDown + kCGEventFlagsChanged (để detect modifier keys)
        ulong eventMask = MacNativeMethods.CGEventMaskBit(MacNativeMethods.kCGEventKeyDown)
                        | MacNativeMethods.CGEventMaskBit(MacNativeMethods.kCGEventFlagsChanged);

        _tapPort = MacNativeMethods.CGEventTapCreate(
            MacNativeMethods.kCGHIDEventTap,
            MacNativeMethods.kCGHeadInsertEventTap,
            MacNativeMethods.kCGEventTapOptionDefault,
            eventMask,
            _callback!,
            IntPtr.Zero);

        if (_tapPort == IntPtr.Zero)
        {
            Error?.Invoke(this, "Không thể tạo CGEventTap. Kiểm tra quyền Input Monitoring.");
            return;
        }

        _runLoopSource = MacNativeMethods.CFMachPortCreateRunLoopSource(
            IntPtr.Zero, _tapPort, 0);

        if (_runLoopSource == IntPtr.Zero)
        {
            Error?.Invoke(this, "Không thể tạo RunLoop source.");
            return;
        }

        _runLoop = MacNativeMethods.CFRunLoopGetCurrent();
        MacNativeMethods.CFRunLoopAddSource(
            _runLoop, _runLoopSource, MacNativeMethods.kCFRunLoopCommonModes);

        MacNativeMethods.CGEventTapEnable(_tapPort, true);

        DebugLog?.Invoke("CGEventTap installed thành công");

        // Blocking - chạy run loop cho đến khi bị stop
        MacNativeMethods.CFRunLoopRun();
    }

    /// <summary>
    /// Gỡ bỏ hook.
    /// Tương đương KeyboardHook.Uninstall() trên Windows.
    /// </summary>
    public void Uninstall()
    {
        if (_tapPort != IntPtr.Zero)
        {
            MacNativeMethods.CGEventTapEnable(_tapPort, false);

            if (_runLoop != IntPtr.Zero)
            {
                MacNativeMethods.CFRunLoopStop(_runLoop);
            }

            if (_runLoopSource != IntPtr.Zero)
            {
                MacNativeMethods.CFRelease(_runLoopSource);
                _runLoopSource = IntPtr.Zero;
            }

            MacNativeMethods.CFRelease(_tapPort);
            _tapPort = IntPtr.Zero;
        }

        if (_eventSource != IntPtr.Zero)
        {
            MacNativeMethods.CFRelease(_eventSource);
            _eventSource = IntPtr.Zero;
        }

        _tapThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _tapThread = null;
        _runLoop = IntPtr.Zero;
    }

    /// <summary>
    /// CGEventTap callback - tương đương HookCallback trên Windows.
    /// Return eventRef để cho event đi qua, IntPtr.Zero để chặn.
    /// </summary>
    private IntPtr HookCallback(IntPtr proxy, uint type, IntPtr eventRef, IntPtr userInfo)
    {
        try
        {
            // macOS tự disable tap nếu callback chậm → re-enable
            if (type == MacNativeMethods.kCGEventTapDisabledByTimeout)
            {
                DebugLog?.Invoke("CGEventTap bị disable do timeout - re-enabling...");
                if (_tapPort != IntPtr.Zero)
                {
                    MacNativeMethods.CGEventTapEnable(_tapPort, true);
                }
                return eventRef;
            }

            // Với kCGAnnotatedSessionEventTap, synthetic events của VietIME
            // không đi qua tap này nữa → chỉ cần marker check như safety net
            long markerValue = MacNativeMethods.CGEventGetIntegerValueField(eventRef, kCGEventSourceUserData);
            if (markerValue == VIME_MARKER_VALUE)
                return eventRef;

            // kCGEventFlagsChanged: chỉ forward, không xử lý (hotkey mới dựa trên keydown Z)
            if (type == MacNativeMethods.kCGEventFlagsChanged)
                return eventRef;

            // Chỉ xử lý kCGEventKeyDown từ đây
            if (type != MacNativeMethods.kCGEventKeyDown)
                return eventRef;

            ulong flags = MacNativeMethods.CGEventGetFlags(eventRef);
            bool isShiftPressed = MacNativeMethods.IsShiftPressed(flags);

            // Xử lý hotkey toggle
            ushort keycode = (ushort)MacNativeMethods.CGEventGetIntegerValueField(
                eventRef, MacNativeMethods.kCGKeyboardEventKeycode);

            if (HandleToggleHotkey(keycode, flags))
                return IntPtr.Zero; // Chặn hotkey

            if (!_isEnabled || _engine == null)
                return eventRef;

            // Bỏ qua khi Command/Control/Option đang nhấn (shortcuts)
            if (MacNativeMethods.IsCommandPressed(flags) ||
                MacNativeMethods.IsControlPressed(flags) ||
                MacNativeMethods.IsOptionPressed(flags))
                return eventRef;

            // Xử lý phím đặc biệt (Space, Enter, Backspace, arrows...)
            if (HandleSpecialKey(keycode))
                return eventRef;

            // Timeout check - giống Windows
            var now = DateTime.UtcNow;
            if (_lastKeyTime != DateTime.MinValue &&
                (now - _lastKeyTime).TotalMilliseconds > BUFFER_TIMEOUT_MS)
            {
                _engine.Reset();
            }
            _lastKeyTime = now;

            // Lấy unicode char từ event
            char? ch = MacNativeMethods.GetCharFromEvent(eventRef);
            if (!ch.HasValue)
                return eventRef;

            // Nếu đang busy gửi output, queue phím lại
            if (_isBusySending)
            {
                _pendingKeys.Enqueue(new PendingKey(ch.Value, isShiftPressed));
                return IntPtr.Zero; // Chặn phím - sẽ xử lý sau
            }

            // Xử lý ký tự qua engine
            var result = _engine.ProcessKey(ch.Value, isShiftPressed);

            DebugLog?.Invoke($"Key '{ch.Value}' (0x{keycode:X2}): Handled={result.Handled}, Output='{result.OutputText}', BS={result.BackspaceCount}");

            if (result.Handled && result.OutputText != null)
            {
                // Gửi output trên background thread → callback return ngay → tránh bị timeout
                _isBusySending = true;
                int bs = result.BackspaceCount;
                string text = result.OutputText;

                Task.Run(() =>
                {
                    try
                    {
                        SendReplace(bs, text);
                        // Đợi app xử lý xong events trước khi gửi pending keys
                        Thread.Sleep(20);
                    }
                    finally
                    {
                        ProcessPendingKeys();       // Xử lý queue TRƯỚC
                        _isBusySending = false;     // Mở khóa SAU
                    }
                });

                return IntPtr.Zero; // Chặn phím gốc
            }

            // Engine không xử lý → phím đi qua bình thường
            return eventRef;
        }
        catch (Exception ex)
        {
            DebugLog?.Invoke($"Error: {ex.Message}");
            return eventRef;
        }
    }

    /// <summary>
    /// Xử lý phím đã queue khi đang busy.
    /// Giống ProcessPendingKeys() trên Windows.
    /// </summary>
    private void ProcessPendingKeys()
    {
        while (_pendingKeys.TryDequeue(out var pending))
        {
            if (_engine == null) break;

            var result = _engine.ProcessKey(pending.Char, pending.IsShift);

            if (result.Handled && result.OutputText != null)
            {
                SendReplace(result.BackspaceCount, result.OutputText);
                Thread.Sleep(20); // Đợi app xử lý xong trước khi gửi tiếp
            }
            else
            {
                // Engine không xử lý → gửi ký tự gốc
                SendCharDirectly(pending.Char);
            }
        }
    }

    /// <summary>
    /// Gửi backspace + unicode text qua CGEvent.
    /// Tương đương SendAtomicReplace trên Windows.
    /// Tách thành 2 batch: backspace trước, unicode sau, với delay nhỏ ở giữa.
    /// </summary>
    private void SendReplace(int backspaceCount, string text)
    {
        _isSendingInput = true;
        try
        {
            DebugLog?.Invoke($"SendReplace START: bs={backspaceCount}, text='{text}'");

            // 1. Gửi backspace
            for (int i = 0; i < backspaceCount; i++)
            {
                SendKeyEvent(MacNativeMethods.kVK_Delete, true);
                Thread.Sleep(1);
                SendKeyEvent(MacNativeMethods.kVK_Delete, false);
                Thread.Sleep(1);
            }

            if (backspaceCount > 0 && text.Length > 0)
            {
                Thread.Sleep(10); // Delay để app kịp xử lý backspace
            }

            // 2. Gửi unicode text - từng ký tự
            foreach (char c in text)
            {
                SendUnicodeChar(c);
                Thread.Sleep(2); // Delay giữa các ký tự
            }

            DebugLog?.Invoke($"SendReplace DONE: bs={backspaceCount}, text='{text}'");
        }
        catch (Exception ex)
        {
            DebugLog?.Invoke($"SendReplace ERROR: {ex.Message}");
        }
        finally
        {
            _isSendingInput = false;
        }
    }

    /// <summary>
    /// Gửi 1 ký tự trực tiếp (cho phím đã bị chặn nhưng engine không xử lý).
    /// Tương đương SendCharDirectly trên Windows.
    /// </summary>
    private void SendCharDirectly(char ch)
    {
        _isSendingInput = true;
        try
        {
            SendUnicodeChar(ch);
        }
        finally
        {
            _isSendingInput = false;
        }
    }

    /// <summary>
    /// Gửi một keyboard event (key down hoặc key up).
    /// Dùng cho Backspace và các phím đặc biệt.
    /// Post tại kCGAnnotatedSessionEventTap để bypass tap của chính mình (tránh re-entrancy).
    /// </summary>
    private void SendKeyEvent(ushort keycode, bool keyDown)
    {
        var evt = MacNativeMethods.CGEventCreateKeyboardEvent(_eventSource, keycode, keyDown);
        if (evt != IntPtr.Zero)
        {
            // Đánh dấu event từ VietIME
            MacNativeMethods.CGEventSetIntegerValueField(evt, kCGEventSourceUserData, VIME_MARKER_VALUE);
            // Post tại AnnotatedSession → bypass event tap → không bị re-entrancy
            MacNativeMethods.CGEventPost(MacNativeMethods.kCGAnnotatedSessionEventTap, evt);
            MacNativeMethods.CFRelease(evt);
        }
        else
        {
            DebugLog?.Invoke($"FAILED: CGEventCreateKeyboardEvent keycode=0x{keycode:X2} keyDown={keyDown}");
        }
    }

    /// <summary>
    /// Gửi một ký tự unicode qua CGEvent.
    /// Tương đương SendInput(KEYEVENTF_UNICODE) trên Windows.
    /// Tạo keyboard event → set unicode string (ushort[] = UniChar) → post tại AnnotatedSession.
    /// </summary>
    private void SendUnicodeChar(char c)
    {
        // Dùng ushort[] thay cho char[] để match UniChar (uint16_t) chính xác
        var unicodeChars = new ushort[] { (ushort)c };

        // Key down
        var keyDown = MacNativeMethods.CGEventCreateKeyboardEvent(_eventSource, 0, true);
        if (keyDown != IntPtr.Zero)
        {
            MacNativeMethods.CGEventKeyboardSetUnicodeString(keyDown, (nuint)1, unicodeChars);
            MacNativeMethods.CGEventSetIntegerValueField(keyDown, kCGEventSourceUserData, VIME_MARKER_VALUE);
            MacNativeMethods.CGEventPost(MacNativeMethods.kCGAnnotatedSessionEventTap, keyDown);
            MacNativeMethods.CFRelease(keyDown);
        }
        else
        {
            DebugLog?.Invoke($"FAILED: CGEventCreateKeyboardEvent for char '{c}' (0x{(int)c:X4})");
            return;
        }

        // Key up - KHÔNG set unicode string (chỉ keyDown mới tạo ký tự,
        // set unicode trên keyUp khiến một số terminal xử lý thành ký tự thứ 2 → double char)
        var keyUp = MacNativeMethods.CGEventCreateKeyboardEvent(_eventSource, 0, false);
        if (keyUp != IntPtr.Zero)
        {
            MacNativeMethods.CGEventSetIntegerValueField(keyUp, kCGEventSourceUserData, VIME_MARKER_VALUE);
            MacNativeMethods.CGEventPost(MacNativeMethods.kCGAnnotatedSessionEventTap, keyUp);
            MacNativeMethods.CFRelease(keyUp);
        }
    }

    /// <summary>
    /// Xử lý hotkey toggle cho kCGEventKeyDown.
    /// Cmd + Opt + Z         → toggle bật/tắt
    /// Cmd + Opt + Shift + Z → luôn TẮT VietIME (nhường bộ gõ khác)
    /// </summary>
    private bool HandleToggleHotkey(ushort keycode, ulong flags)
    {
        if (keycode != MacNativeMethods.kVK_ANSI_Z) return false;
        if (!MacNativeMethods.IsCommandPressed(flags)) return false;
        if (!MacNativeMethods.IsOptionPressed(flags)) return false;

        if (MacNativeMethods.IsShiftPressed(flags))
        {
            TryDisable();
            return true; // chặn phím
        }

        return TryToggle();
    }

    private bool TryToggle()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastToggleTime).TotalMilliseconds < TOGGLE_DEBOUNCE_MS) return true;
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


    /// <summary>
    /// Xử lý phím đặc biệt - tương đương HandleSpecialKey trên Windows.
    /// </summary>
    private bool HandleSpecialKey(ushort keycode)
    {
        switch (keycode)
        {
            case MacNativeMethods.kVK_Space:
            case MacNativeMethods.kVK_Return:
            case MacNativeMethods.kVK_Tab:
            case MacNativeMethods.kVK_Escape:
                _engine?.Reset();
                return true;

            case MacNativeMethods.kVK_Delete: // Backspace
                _engine?.ProcessBackspace();
                return true;

            case MacNativeMethods.kVK_LeftArrow:
            case MacNativeMethods.kVK_RightArrow:
            case MacNativeMethods.kVK_UpArrow:
            case MacNativeMethods.kVK_DownArrow:
            case MacNativeMethods.kVK_Home:
            case MacNativeMethods.kVK_End:
            case MacNativeMethods.kVK_PageUp:
            case MacNativeMethods.kVK_PageDown:
            case MacNativeMethods.kVK_ForwardDelete:
                _engine?.Reset();
                return true;

            default:
                return false;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _warpWatcherTimer?.Dispose();
            _warpWatcherTimer = null;
            Uninstall();
            _disposed = true;
        }
    }

    ~MacKeyboardHook()
    {
        Dispose(false);
    }
}
