using System.Runtime.InteropServices;
using System.Text;
using VietIME.Core.Engines;
using static VietIME.Linux.LinuxNativeMethods;

namespace VietIME.Linux;

/// <summary>
/// Keyboard hook trên Linux sử dụng evdev (đọc) + uinput (ghi).
/// Tương đương KeyboardHook.cs (Win) và MacKeyboardHook.cs (Mac).
///
/// Chiến lược:
/// - Grab physical keyboard via EVIOCGRAB → chặn events đến hệ thống
/// - Tạo virtual keyboard via uinput → forward/replace events
/// - Đọc event từ evdev → gọi IInputEngine.ProcessKey()
/// - Nếu engine xử lý: gửi backspaces + output qua uinput (clipboard Ctrl+Shift+V cho unicode)
/// - Nếu không: forward event gốc qua uinput
/// </summary>
public class LinuxKeyboardHook : IDisposable
{
    private int _evdevFd = -1;
    private int _uinputFd = -1;
    private Thread? _readThread;
    private volatile bool _running;
    private bool _disposed;

    private IInputEngine? _engine;
    private bool _isEnabled = true;

    // Modifier tracking
    private bool _leftShiftDown;
    private bool _rightShiftDown;
    private bool _leftCtrlDown;
    private bool _rightCtrlDown;
    private bool _leftAltDown;
    private bool _rightAltDown;

    private const int BUFFER_TIMEOUT_MS = 2000;
    private DateTime _lastKeyTime = DateTime.MinValue;
    private DateTime _lastToggleTime = DateTime.MinValue;
    private const int TOGGLE_DEBOUNCE_MS = 300;

    // Delay giữa các synthetic events (µs)
    private const int KEY_DELAY_US = 5000;

    // Warp-only mode: chỉ bật VietIME khi foreground app là Warp
    private bool _warpOnlyMode = false;
    private bool? _lastForegroundIsWarp = null;
    private System.Threading.Timer? _warpWatcherTimer;
    private const int WARP_POLL_INTERVAL_MS = 500;

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
    /// Chỉ bật VietIME khi foreground app là Warp. Dựa vào xdotool để lấy window class.
    /// Cần cài: sudo apt install xdotool
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
                _warpWatcherTimer = new System.Threading.Timer(WarpWatcherTick, null, 0, WARP_POLL_INTERVAL_MS);
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
            bool isWarp = IsWarpForeground();
            if (_lastForegroundIsWarp != isWarp)
            {
                _lastForegroundIsWarp = isWarp;
                IsEnabled = isWarp;
            }
        }
        catch { /* ignore */ }
    }

    private static bool IsWarpForeground()
    {
        try
        {
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xdotool",
                    Arguments = "getactivewindow getwindowclassname",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(500);
            return output.Contains("warp", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool IsShiftPressed => _leftShiftDown || _rightShiftDown;
    private bool IsCtrlPressed => _leftCtrlDown || _rightCtrlDown;
    private bool IsAltPressed => _leftAltDown || _rightAltDown;

    /// <summary>
    /// Tìm keyboard device trong /dev/input/event*.
    /// Hỗ trợ cả máy thật và VM (VMware, VirtualBox, QEMU, UTM).
    /// </summary>
    public static string? FindKeyboardDevice(Action<string>? log = null)
    {
        string? bestMatch = null;
        string? fallbackMatch = null;
        int openFailCount = 0;

        for (int i = 0; i < 64; i++)
        {
            string path = $"/dev/input/event{i}";
            int fd = open(path, O_RDONLY | O_NONBLOCK);
            if (fd < 0)
            {
                openFailCount++;
                continue;
            }

            try
            {
                // Đọc tên device
                string name = "";
                byte[] nameBuf = new byte[256];
                if (ioctl(fd, EVIOCGNAME(256), nameBuf) >= 0)
                {
                    name = Encoding.UTF8.GetString(nameBuf).TrimEnd('\0');
                }
                string nameLower = name.ToLower();

                log?.Invoke($"  {path}: \"{name}\"");

                // Bỏ qua virtual devices (do chính VietIME tạo)
                if (nameLower.Contains("vietime"))
                {
                    log?.Invoke($"    → Bỏ qua (VietIME virtual device)");
                    continue;
                }
                // Bỏ qua mouse, touchpad, etc.
                if (nameLower.Contains("mouse") || nameLower.Contains("touchpad") ||
                    nameLower.Contains("trackpad") || nameLower.Contains("trackpoint") ||
                    nameLower.Contains("finger"))
                {
                    log?.Invoke($"    → Bỏ qua (mouse/touchpad)");
                    continue;
                }

                // Kiểm tra device có EV_KEY capability
                byte[] evBits = new byte[4];
                if (ioctl(fd, EVIOCGBIT(0, evBits.Length), evBits) >= 0)
                {
                    bool hasKey = (evBits[0] & (1 << EV_KEY)) != 0;
                    if (!hasKey)
                    {
                        log?.Invoke($"    → Bỏ qua (không có EV_KEY)");
                        continue;
                    }
                }

                // Kiểm tra key capabilities
                byte[] keyBits = new byte[(KEY_MAX / 8) + 1];
                if (ioctl(fd, EVIOCGBIT(EV_KEY, keyBits.Length), keyBits) >= 0)
                {
                    // Đếm letter keys có sẵn
                    int letterCount = 0;
                    for (ushort k = KEY_A; k <= KEY_Z; k++)
                    {
                        if ((keyBits[k / 8] & (1 << (k % 8))) != 0)
                            letterCount++;
                    }

                    // Kiểm tra có Enter + Space (tín hiệu keyboard cơ bản)
                    bool hasEnter = (keyBits[KEY_ENTER / 8] & (1 << (KEY_ENTER % 8))) != 0;
                    bool hasSpace = (keyBits[KEY_SPACE / 8] & (1 << (KEY_SPACE % 8))) != 0;

                    log?.Invoke($"    → Letters: {letterCount}/26, Enter: {hasEnter}, Space: {hasSpace}");

                    // Best match: đủ 26 chữ cái
                    if (letterCount == 26)
                    {
                        log?.Invoke($"    ✓ Keyboard match (full letters)");
                        bestMatch ??= path;
                    }
                    // Fallback: có ít nhất 20 letters + Enter + Space
                    // (VM keyboard có thể không report đủ 26)
                    else if (letterCount >= 20 && hasEnter && hasSpace)
                    {
                        log?.Invoke($"    ✓ Keyboard fallback match (VM compatible)");
                        fallbackMatch ??= path;
                    }
                    // Fallback 2: device tên chứa "keyboard" hoặc "kbd"
                    else if ((nameLower.Contains("keyboard") || nameLower.Contains("kbd") ||
                              nameLower.Contains("at translated")) && hasEnter)
                    {
                        log?.Invoke($"    ✓ Keyboard fallback match (by name)");
                        fallbackMatch ??= path;
                    }
                }
            }
            finally
            {
                close(fd);
            }
        }

        if (bestMatch == null && fallbackMatch == null && openFailCount > 10)
        {
            log?.Invoke($"\n⚠ Không thể mở {openFailCount} devices. Có thể thiếu quyền.");
            log?.Invoke($"  Chạy: sudo usermod -aG input $USER && logout");
        }

        var result = bestMatch ?? fallbackMatch;
        if (result != null)
            log?.Invoke($"\n→ Sử dụng: {result}");
        else
            log?.Invoke($"\n✗ Không tìm thấy keyboard device nào phù hợp.");

        return result;
    }

    /// <summary>
    /// Cài đặt hook: mở evdev, grab, tạo uinput device.
    /// </summary>
    public void Install(string? devicePath = null)
    {
        devicePath ??= FindKeyboardDevice(DebugLog);
        if (devicePath == null)
        {
            Error?.Invoke(this, "Không tìm thấy keyboard device. Kiểm tra quyền: sudo usermod -aG input $USER && logout\nNếu dùng VM (VMware/VirtualBox): đảm bảo VM có keyboard device.");
            return;
        }

        DebugLog?.Invoke($"Sử dụng keyboard: {devicePath}");

        // 1. Mở evdev device
        _evdevFd = open(devicePath, O_RDONLY);
        if (_evdevFd < 0)
        {
            Error?.Invoke(this, $"Không thể mở {devicePath}. Cần quyền: sudo usermod -aG input $USER && logout");
            return;
        }

        // 2. Tạo uinput virtual keyboard
        if (!CreateUinputDevice())
        {
            close(_evdevFd);
            _evdevFd = -1;
            return;
        }

        // Delay nhỏ để uinput device sẵn sàng
        Thread.Sleep(200);

        // 3. Grab physical keyboard (chặn events đến hệ thống)
        if (ioctl(_evdevFd, EVIOCGRAB, 1) < 0)
        {
            Error?.Invoke(this, "Không thể grab keyboard device.");
            DestroyUinputDevice();
            close(_evdevFd);
            _evdevFd = -1;
            return;
        }

        // 4. Bắt đầu đọc events trên background thread
        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            Name = "VietIME-EvdevReader",
            IsBackground = true
        };
        _readThread.Start();

        DebugLog?.Invoke("Hook installed thành công (evdev + uinput)");
    }

    /// <summary>
    /// Tạo virtual keyboard device qua /dev/uinput.
    /// </summary>
    private bool CreateUinputDevice()
    {
        _uinputFd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (_uinputFd < 0)
        {
            Error?.Invoke(this, "Không thể mở /dev/uinput. Cần quyền: sudo usermod -aG input $USER");
            return false;
        }

        // Đăng ký event types
        ioctl(_uinputFd, UI_SET_EVBIT, EV_KEY);
        ioctl(_uinputFd, UI_SET_EVBIT, EV_SYN);
        ioctl(_uinputFd, UI_SET_EVBIT, EV_REP);

        // Đăng ký tất cả key codes
        for (int i = 0; i <= KEY_MAX; i++)
        {
            ioctl(_uinputFd, UI_SET_KEYBIT, i);
        }

        // Setup device info
        var setup = new UinputSetup
        {
            Id = new InputId
            {
                BusType = 0x03, // BUS_USB
                Vendor = 0x1234,
                Product = 0x5678,
                Version = 1
            },
            Name = "VietIME Virtual Keyboard",
            FfEffectsMax = 0
        };

        if (ioctl(_uinputFd, UI_DEV_SETUP, ref setup) < 0)
        {
            Error?.Invoke(this, "UI_DEV_SETUP failed");
            close(_uinputFd);
            _uinputFd = -1;
            return false;
        }

        if (ioctl(_uinputFd, UI_DEV_CREATE, 0) < 0)
        {
            Error?.Invoke(this, "UI_DEV_CREATE failed");
            close(_uinputFd);
            _uinputFd = -1;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Vòng lặp đọc events từ evdev.
    /// </summary>
    private void ReadLoop()
    {
        var ev = new InputEvent();
        int eventSize = Marshal.SizeOf<InputEvent>();

        while (_running)
        {
            nint bytesRead = read(_evdevFd, ref ev, eventSize);

            if (bytesRead < eventSize)
            {
                Thread.Sleep(1);
                continue;
            }

            ProcessEvent(ref ev);
        }
    }

    /// <summary>
    /// Xử lý một event từ evdev.
    /// </summary>
    private void ProcessEvent(ref InputEvent ev)
    {
        // Forward non-key events (SYN, etc.) trực tiếp
        if (ev.Type != EV_KEY)
        {
            WriteUinput(ref ev);
            return;
        }

        // Track modifier keys
        UpdateModifiers(ev.Code, ev.Value);

        // Forward modifier key events trực tiếp
        if (IsModifierKey(ev.Code))
        {
            WriteUinput(ref ev);
            return;
        }

        // Chỉ xử lý key press (không xử lý release và repeat)
        if (ev.Value != KEY_STATE_PRESS)
        {
            WriteUinput(ref ev);
            return;
        }

        // Kiểm tra toggle hotkey:
        //   Ctrl+Alt+Z         → toggle bật/tắt
        //   Ctrl+Alt+Shift+Z   → luôn TẮT VietIME
        if (ev.Code == KEY_Z && IsCtrlPressed && IsAltPressed)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastToggleTime).TotalMilliseconds > TOGGLE_DEBOUNCE_MS)
            {
                _lastToggleTime = now;
                if (IsShiftPressed)
                {
                    if (_isEnabled)
                    {
                        IsEnabled = false;
                        DebugLog?.Invoke("Ctrl+Alt+Shift+Z: TẮT");
                    }
                }
                else
                {
                    IsEnabled = !IsEnabled;
                    DebugLog?.Invoke($"Ctrl+Alt+Z: {(_isEnabled ? "BẬT" : "TẮT")}");
                }
            }
            // Không forward phím hotkey
            return;
        }

        // Nếu tắt hoặc có Ctrl/Alt → forward
        if (!_isEnabled || _engine == null || IsCtrlPressed || IsAltPressed)
        {
            WriteUinput(ref ev);
            return;
        }

        // Handle special keys
        if (HandleSpecialKey(ev.Code))
        {
            WriteUinput(ref ev);
            return;
        }

        // Timeout check
        var utcNow = DateTime.UtcNow;
        if (_lastKeyTime != DateTime.MinValue &&
            (utcNow - _lastKeyTime).TotalMilliseconds > BUFFER_TIMEOUT_MS)
        {
            _engine.Reset();
        }
        _lastKeyTime = utcNow;

        // Chuyển key code sang ký tự
        char? ch = KeyCodeToChar(ev.Code, IsShiftPressed);
        if (!ch.HasValue)
        {
            WriteUinput(ref ev);
            return;
        }

        // Gọi engine xử lý
        var result = _engine.ProcessKey(ch.Value, IsShiftPressed);

        DebugLog?.Invoke($"Key '{ch.Value}': Handled={result.Handled}, Output='{result.OutputText}', BS={result.BackspaceCount}");

        if (result.Handled && result.OutputText != null)
        {
            // Gửi backspaces + output qua clipboard (Ctrl+Shift+V cho unicode)
            SendReplace(result.BackspaceCount, result.OutputText);
            // KHÔNG forward key gốc
            return;
        }

        // Engine không xử lý → forward key gốc
        WriteUinput(ref ev);
    }

    /// <summary>
    /// Gửi backspaces + output text qua uinput.
    /// Dùng xdotool/xclip cho unicode characters (evdev chỉ gửi keycodes).
    /// </summary>
    private void SendReplace(int backspaceCount, string text)
    {
        // 1. Gửi backspaces
        for (int i = 0; i < backspaceCount; i++)
        {
            SendKey(KEY_BACKSPACE, false);
            Thread.Sleep(2);
        }

        Thread.Sleep(5);

        // 2. Gửi text qua clipboard (Ctrl+Shift+V) cho unicode support
        // evdev/uinput chỉ gửi được key codes (ASCII), không gửi được unicode trực tiếp
        // Nên dùng clipboard method giống Windows terminal mode
        bool hasUnicode = false;
        foreach (char c in text)
        {
            if (c > 127)
            {
                hasUnicode = true;
                break;
            }
        }

        if (hasUnicode)
        {
            SendViaClipboard(text);
        }
        else
        {
            // ASCII text → gửi trực tiếp qua key events
            foreach (char c in text)
            {
                var mapping = CharToKeyCode(c);
                if (mapping.HasValue)
                {
                    SendKey(mapping.Value.keyCode, mapping.Value.needShift);
                    Thread.Sleep(2);
                }
            }
        }
    }

    /// <summary>
    /// Gửi text qua clipboard: xclip + Ctrl+Shift+V.
    /// Hoạt động trên mọi terminal và GUI app.
    /// </summary>
    private void SendViaClipboard(string text)
    {
        try
        {
            // Lưu clipboard cũ
            string? oldClipboard = null;
            try
            {
                var readProc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "xclip",
                        Arguments = "-selection clipboard -o",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                readProc.Start();
                oldClipboard = readProc.StandardOutput.ReadToEnd();
                readProc.WaitForExit(100);
            }
            catch { /* ignore */ }

            // Set clipboard mới
            var setProc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xclip",
                    Arguments = "-selection clipboard",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            setProc.Start();
            setProc.StandardInput.Write(text);
            setProc.StandardInput.Close();
            setProc.WaitForExit(100);

            Thread.Sleep(10);

            // Gửi Ctrl+Shift+V (paste trong terminal)
            // Thử Ctrl+Shift+V trước (terminal), fallback Ctrl+V (GUI apps)
            SendKeyCombo(KEY_LEFTCTRL, KEY_LEFTSHIFT, KEY_V);

            Thread.Sleep(20);

            // Khôi phục clipboard cũ
            if (oldClipboard != null)
            {
                try
                {
                    var restoreProc = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "xclip",
                            Arguments = "-selection clipboard",
                            RedirectStandardInput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    restoreProc.Start();
                    restoreProc.StandardInput.Write(oldClipboard);
                    restoreProc.StandardInput.Close();
                    restoreProc.WaitForExit(100);
                }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            DebugLog?.Invoke($"Clipboard error: {ex.Message}. Cần cài: sudo apt install xclip");
        }
    }

    /// <summary>
    /// Gửi một phím qua uinput (press + release + SYN).
    /// </summary>
    private void SendKey(ushort keyCode, bool withShift)
    {
        if (withShift)
            WriteKeyEvent(KEY_LEFTSHIFT, KEY_STATE_PRESS);

        WriteKeyEvent(keyCode, KEY_STATE_PRESS);
        WriteSyn();
        WriteKeyEvent(keyCode, KEY_STATE_RELEASE);
        WriteSyn();

        if (withShift)
            WriteKeyEvent(KEY_LEFTSHIFT, KEY_STATE_RELEASE);

        WriteSyn();
    }

    /// <summary>
    /// Gửi combo Ctrl+Shift+Key.
    /// </summary>
    private void SendKeyCombo(ushort mod1, ushort mod2, ushort key)
    {
        WriteKeyEvent(mod1, KEY_STATE_PRESS);
        WriteSyn();
        WriteKeyEvent(mod2, KEY_STATE_PRESS);
        WriteSyn();
        WriteKeyEvent(key, KEY_STATE_PRESS);
        WriteSyn();

        WriteKeyEvent(key, KEY_STATE_RELEASE);
        WriteSyn();
        WriteKeyEvent(mod2, KEY_STATE_RELEASE);
        WriteSyn();
        WriteKeyEvent(mod1, KEY_STATE_RELEASE);
        WriteSyn();
    }

    private void WriteKeyEvent(ushort code, int value)
    {
        var ev = new InputEvent
        {
            Type = EV_KEY,
            Code = code,
            Value = value
        };
        WriteUinput(ref ev);
    }

    private void WriteSyn()
    {
        var ev = new InputEvent
        {
            Type = EV_SYN,
            Code = SYN_REPORT,
            Value = 0
        };
        WriteUinput(ref ev);
    }

    private void WriteUinput(ref InputEvent ev)
    {
        if (_uinputFd < 0) return;
        write(_uinputFd, ref ev, Marshal.SizeOf<InputEvent>());
    }

    // ═══════════════════════════════════════════
    // Modifier tracking & special keys
    // ═══════════════════════════════════════════

    private void UpdateModifiers(ushort code, int value)
    {
        bool down = value != KEY_STATE_RELEASE;
        switch (code)
        {
            case KEY_LEFTSHIFT: _leftShiftDown = down; break;
            case KEY_RIGHTSHIFT: _rightShiftDown = down; break;
            case KEY_LEFTCTRL: _leftCtrlDown = down; break;
            case KEY_RIGHTCTRL: _rightCtrlDown = down; break;
            case KEY_LEFTALT: _leftAltDown = down; break;
            case KEY_RIGHTALT: _rightAltDown = down; break;
        }
    }

    private static bool IsModifierKey(ushort code) =>
        code is KEY_LEFTSHIFT or KEY_RIGHTSHIFT
            or KEY_LEFTCTRL or KEY_RIGHTCTRL
            or KEY_LEFTALT or KEY_RIGHTALT
            or KEY_CAPSLOCK;

    private bool HandleSpecialKey(ushort code)
    {
        switch (code)
        {
            case KEY_BACKSPACE:
                _engine?.ProcessBackspace();
                return true;

            case KEY_ENTER:
            case KEY_TAB:
            case KEY_ESC:
            case KEY_HOME:
            case KEY_END:
            case KEY_LEFT:
            case KEY_RIGHT:
            case KEY_UP:
            case KEY_DOWN:
            case KEY_PAGEUP:
            case KEY_PAGEDOWN:
            case KEY_DELETE:
                _engine?.Reset();
                return true;
        }
        return false;
    }

    // ═══════════════════════════════════════════
    // Cleanup
    // ═══════════════════════════════════════════

    public void Uninstall()
    {
        _running = false;
        _readThread?.Join(1000);

        // Ungrab keyboard
        if (_evdevFd >= 0)
        {
            ioctl(_evdevFd, EVIOCGRAB, 0);
            close(_evdevFd);
            _evdevFd = -1;
        }

        DestroyUinputDevice();
    }

    private void DestroyUinputDevice()
    {
        if (_uinputFd >= 0)
        {
            ioctl(_uinputFd, UI_DEV_DESTROY, 0);
            close(_uinputFd);
            _uinputFd = -1;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _warpWatcherTimer?.Dispose();
            _warpWatcherTimer = null;
            Uninstall();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
