using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using VietIME.Core.Engines;
using VietIME.Linux;

namespace VietIME.Linux.App;

public partial class MainWindow : Window
{
    private readonly LinuxKeyboardHook? _hook;
    private readonly App _app;
    private bool _initialized = false;

    public MainWindow() : this(null) { }

    public MainWindow(LinuxKeyboardHook? hook)
    {
        _hook = hook;
        _app = (App)Avalonia.Application.Current!;

        InitializeComponent();

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
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(UpdateStatus);
            };
        }

        _initialized = true;
    }

    /// <summary>
    /// Cập nhật lại UI khi window được Show() lại từ tray.
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
    /// Hiển thị thông báo lỗi (ví dụ: thiếu quyền /dev/input).
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
        // Nút X chỉ ẩn cửa sổ, không đóng app — giống Windows/Mac
        e.Cancel = true;
        Hide();
    }
}
