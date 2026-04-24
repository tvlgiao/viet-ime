using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using VietIME.Core.Engines;
using VietIME.Core.Services;

namespace VietIME.Mac.App;

public partial class MainWindow : Window
{
    private readonly MacKeyboardHook? _hook;
    private readonly App _app;
    private bool _initialized = false;
    private readonly UpdateService _updateService = new();
    private UpdateInfo? _lastUpdateInfo;
    private CancellationTokenSource? _downloadCts;

    public MainWindow() : this(null) { }

    public MainWindow(MacKeyboardHook? hook)
    {
        _hook = hook;
        _app = (App)Avalonia.Application.Current!;

        InitializeComponent();

        // Hiện version hiện tại
        txtCurrentVersion.Text = $"v{UpdateService.AppVersion}";
        txtFooterVersion.Text = $"v{UpdateService.AppVersion} — Đỗ Nam — macOS";

        if (_hook != null)
        {
            toggleEnabled.IsChecked = _hook.IsEnabled;
            toggleWarpOnly.IsChecked = _hook.WarpOnlyMode;
            rbTelex.IsChecked = _hook.Engine?.Name == "Telex";
            rbVNI.IsChecked = _hook.Engine?.Name == "VNI";
            UpdateStatus();

            _hook.EnabledChanged += (s, e) =>
            {
                if (IsVisible)
                    Dispatcher.UIThread.InvokeAsync(UpdateStatus);
            };
        }

        _initialized = true;

        // Kiểm tra quyền khi khởi tạo
        CheckPermissions();
    }

    /// <summary>
    /// Cập nhật lại UI khi window được Show() lại từ tray.
    /// Giống RefreshState() trên Windows.
    /// </summary>
    public void RefreshState()
    {
        _initialized = false;

        if (_hook != null)
        {
            toggleEnabled.IsChecked = _hook.IsEnabled;
            toggleWarpOnly.IsChecked = _hook.WarpOnlyMode;
            rbTelex.IsChecked = _hook.Engine?.Name == "Telex";
            rbVNI.IsChecked = _hook.Engine?.Name == "VNI";
            UpdateStatus();
        }

        txtError.IsVisible = false;
        _initialized = true;

        // Auto-check update khi mở settings
        _ = CheckUpdateSilentAsync();
    }

    private void UpdateStatus()
    {
        if (_hook == null) return;

        var enabled = _hook.IsEnabled;
        var engineName = _hook.Engine?.Name ?? "Telex";

        toggleEnabled.IsChecked = enabled;
        txtStatus.Text = enabled ? "Đang bật" : "Đã tắt";
        txtEngine.Text = engineName;

        var successBrush = (ISolidColorBrush)this.FindResource("SuccessBrush")!;
        var dimBrush = (ISolidColorBrush)this.FindResource("TextDimBrush")!;

        statusDot.Fill = enabled ? successBrush : dimBrush;
    }

    /// <summary>
    /// Hiển thị thông báo lỗi (ví dụ: thiếu quyền Input Monitoring).
    /// </summary>
    public void ShowError(string message)
    {
        txtError.Text = message;
        txtError.IsVisible = true;
    }

    private void ToggleEnabled_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (_hook != null)
        {
            _hook.IsEnabled = toggleEnabled.IsChecked ?? false;
        }
    }

    private void ToggleWarpOnly_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_initialized || _hook == null) return;
        _hook.WarpOnlyMode = toggleWarpOnly.IsChecked ?? false;
    }

    private void InputMethod_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_initialized || _hook == null) return;

        if (rbTelex.IsChecked == true)
        {
            _hook.Engine = new TelexEngine();
        }
        else if (rbVNI.IsChecked == true)
        {
            _hook.Engine = new VniEngine();
        }

        UpdateStatus();
    }

    private void BtnMinimize_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void BtnExit_Click(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.TryShutdown();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Nút X chỉ ẩn cửa sổ, không đóng app - giống Windows
        e.Cancel = true;
        Hide();
    }

    // ═══════════════════════════════════════════
    // Permission handlers
    // ═══════════════════════════════════════════

    /// <summary>
    /// Kiểm tra quyền Input Monitoring + Accessibility, hiện/ẩn Permission Card.
    /// </summary>
    public void CheckPermissions()
    {
        bool hasListen = MacPermissionHelper.HasInputMonitoringPermission();
        bool hasPost = MacPermissionHelper.HasPostEventPermission();

        var successBrush = (ISolidColorBrush)this.FindResource("SuccessBrush")!;
        var errorBrush = (ISolidColorBrush)this.FindResource("ErrorBrush")!;

        dotInputMonitoring.Fill = hasListen ? successBrush : errorBrush;
        dotAccessibility.Fill = hasPost ? successBrush : errorBrush;

        panelPermission.IsVisible = !hasListen || !hasPost;
    }

    private void BtnOpenInputMonitoring_Click(object? sender, RoutedEventArgs e)
    {
        // Mở System Settings → Privacy & Security → Input Monitoring
        OpenUrl("x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent");
    }

    private void BtnOpenAccessibility_Click(object? sender, RoutedEventArgs e)
    {
        // Mở System Settings → Privacy & Security → Accessibility
        OpenUrl("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
    }

    private void BtnRecheckPermission_Click(object? sender, RoutedEventArgs e)
    {
        bool hasListen = MacPermissionHelper.HasInputMonitoringPermission();
        bool hasPost = MacPermissionHelper.HasPostEventPermission();

        CheckPermissions();

        if (hasListen && hasPost)
        {
            // Đủ quyền → khởi động lại app để install hook
            RestartApp();
        }
    }

    private void RestartApp()
    {
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath == null) return;

        // Tìm .app bundle
        var appBundlePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exePath)!, "..", ".."));
        if (appBundlePath.EndsWith(".app"))
        {
            // Khởi động lại qua `open`
            var pid = Process.GetCurrentProcess().Id;
            var script = $"while kill -0 {pid} 2>/dev/null; do sleep 0.3; done; open \"{appBundlePath}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        else
        {
            // Chạy trực tiếp
            Process.Start(exePath);
        }

        // Thoát app hiện tại
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.TryShutdown(0);
        }
        Environment.Exit(0);
    }

    // ═══════════════════════════════════════════
    // Update handlers
    // ═══════════════════════════════════════════

    /// <summary>
    /// Auto-check update (gọi từ RefreshState/startup), không hiện lỗi nếu thất bại.
    /// </summary>
    public async Task CheckUpdateSilentAsync()
    {
        try
        {
            var info = await _updateService.CheckForUpdateAsync();
            _lastUpdateInfo = info;
            await Dispatcher.UIThread.InvokeAsync(() => ShowUpdateResult(info, silent: true));
        }
        catch { /* silent */ }
    }

    private async void BtnCheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        HideAllUpdatePanels();
        panelChecking.IsVisible = true;
        btnCheckUpdate.IsEnabled = false;

        try
        {
            var info = await _updateService.CheckForUpdateAsync(force: true);
            _lastUpdateInfo = info;
            ShowUpdateResult(info, silent: false);
        }
        catch (Exception ex)
        {
            HideAllUpdatePanels();
            txtUpdateError.Text = $"Lỗi: {ex.Message}";
            txtUpdateError.IsVisible = true;
        }
        finally
        {
            btnCheckUpdate.IsEnabled = true;
        }
    }

    private void ShowUpdateResult(UpdateInfo info, bool silent)
    {
        HideAllUpdatePanels();

        if (info.Error != null && !silent)
        {
            txtUpdateError.Text = $"Không thể kiểm tra: {info.Error}";
            txtUpdateError.IsVisible = true;
            return;
        }

        if (info.HasUpdate)
        {
            txtNewVersion.Text = $"v{info.LatestVersion}";
            txtReleaseNotes.Text = TruncateNotes(info.ReleaseNotes, 120);
            panelUpdateAvailable.IsVisible = true;
        }
        else if (!silent)
        {
            panelUpToDate.IsVisible = true;
        }
    }

    private async void BtnInstallUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastUpdateInfo?.DmgDownloadUrl == null)
        {
            // Fallback: mở browser
            OpenUrl(_lastUpdateInfo?.ReleaseUrl ?? "https://github.com/donamvn/viet-ime/releases/latest");
            return;
        }

        // Bắt đầu download
        HideAllUpdatePanels();
        panelDownloading.IsVisible = true;
        btnCheckUpdate.IsEnabled = false;

        var dmgPath = Path.Combine(Path.GetTempPath(), "VietIME-update.dmg");

        try
        {
            _downloadCts = new CancellationTokenSource();

            await _updateService.DownloadFileAsync(
                _lastUpdateInfo.DmgDownloadUrl,
                dmgPath,
                onProgress: (downloaded, total) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (total > 0)
                        {
                            double pct = (double)downloaded / total * 100;
                            progressDownload.Value = pct;
                            txtDownloadProgress.Text = $"{pct:F0}% ({downloaded / 1024 / 1024}MB / {total / 1024 / 1024}MB)";
                        }
                        else
                        {
                            txtDownloadProgress.Text = $"{downloaded / 1024}KB";
                        }
                    });
                },
                ct: _downloadCts.Token);

            // Download xong → install
            HideAllUpdatePanels();
            panelInstalling.IsVisible = true;

            await Task.Run(() => InstallUpdate(dmgPath));
        }
        catch (OperationCanceledException)
        {
            HideAllUpdatePanels();
            txtUpdateError.Text = "Đã huỷ tải xuống.";
            txtUpdateError.IsVisible = true;
        }
        catch (Exception ex)
        {
            HideAllUpdatePanels();
            txtUpdateError.Text = $"Lỗi cập nhật: {ex.Message}";
            txtUpdateError.IsVisible = true;
        }
        finally
        {
            btnCheckUpdate.IsEnabled = true;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void BtnViewRelease_Click(object? sender, RoutedEventArgs e)
    {
        OpenUrl(_lastUpdateInfo?.ReleaseUrl ?? "https://github.com/donamvn/viet-ime/releases/latest");
    }

    /// <summary>
    /// Cài đặt bản cập nhật từ DMG:
    /// 1. Mount DMG
    /// 2. Tìm VietIME.app trong volume
    /// 3. Tạo shell script: đợi app thoát → xoá app cũ → copy app mới → unmount → launch
    /// 4. Chạy script + thoát app hiện tại
    /// </summary>
    private void InstallUpdate(string dmgPath)
    {
        // Xác định đường dẫn .app hiện tại
        // Process.MainModule.FileName trỏ vào VietIME bên trong .app/Contents/MacOS/
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath == null)
            throw new InvalidOperationException("Không xác định được đường dẫn app hiện tại.");

        // Tìm .app bundle: lên 3 cấp từ Contents/MacOS/VietIME
        var appBundlePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exePath)!, "..", ".."));
        if (!appBundlePath.EndsWith(".app"))
        {
            // Fallback: nếu không chạy từ .app bundle, mở browser
            Dispatcher.UIThread.Post(() =>
            {
                HideAllUpdatePanels();
                txtUpdateError.Text = "Không thể tự cập nhật khi chạy ngoài .app bundle. Vui lòng tải về thủ công.";
                txtUpdateError.IsVisible = true;
            });
            OpenUrl(_lastUpdateInfo?.ReleaseUrl ?? "https://github.com/donamvn/viet-ime/releases/latest");
            return;
        }

        var pid = Process.GetCurrentProcess().Id;

        // Tạo update script (dùng verbatim string + Replace để tránh conflict với bash $VAR)
        var scriptPath = Path.Combine(Path.GetTempPath(), "vietime-update.sh");
        var script = @"#!/bin/bash
# VietIME auto-update script
# Đợi app hiện tại thoát
while kill -0 __PID__ 2>/dev/null; do sleep 0.5; done

# Mount DMG
MOUNT_POINT=$(hdiutil attach ""__DMG_PATH__"" -nobrowse -noverify -noautoopen 2>/dev/null | grep '/Volumes/' | awk '{print substr($0, index($0, ""/Volumes/""))}')
if [ -z ""$MOUNT_POINT"" ]; then
    echo 'Lỗi: Không mount được DMG'
    exit 1
fi

# Tìm .app trong volume
NEW_APP=$(find ""$MOUNT_POINT"" -maxdepth 1 -name '*.app' -type d | head -1)
if [ -z ""$NEW_APP"" ]; then
    echo 'Lỗi: Không tìm thấy .app trong DMG'
    hdiutil detach ""$MOUNT_POINT"" -quiet
    exit 1
fi

# Xoá app cũ và copy app mới
rm -rf ""__APP_PATH__""
cp -R ""$NEW_APP"" ""__APP_PATH__""

# Unmount DMG
hdiutil detach ""$MOUNT_POINT"" -quiet

# Xoá DMG tạm
rm -f ""__DMG_PATH__""

# Launch app mới
open ""__APP_PATH__""

# Xoá chính script này
rm -f ""__SCRIPT_PATH__""
"
            .Replace("__PID__", pid.ToString())
            .Replace("__DMG_PATH__", dmgPath)
            .Replace("__APP_PATH__", appBundlePath)
            .Replace("__SCRIPT_PATH__", scriptPath);

        File.WriteAllText(scriptPath, script);

        // Chạy script
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = scriptPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Đặt executable permission
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            Arguments = $"+x \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })?.WaitForExit(2000);

        Process.Start(psi);

        // Thoát app hiện tại để script có thể replace
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.TryShutdown(0);
            }
            Environment.Exit(0);
        });
    }

    private void HideAllUpdatePanels()
    {
        panelChecking.IsVisible = false;
        panelUpToDate.IsVisible = false;
        panelUpdateAvailable.IsVisible = false;
        panelDownloading.IsVisible = false;
        panelInstalling.IsVisible = false;
        txtUpdateError.IsVisible = false;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private static string TruncateNotes(string? notes, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        // Lấy dòng đầu có nội dung
        var lines = notes.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join(" ", lines).Trim();
        return text.Length > maxLen ? text[..maxLen] + "..." : text;
    }
}
