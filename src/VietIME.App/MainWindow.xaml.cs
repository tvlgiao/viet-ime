using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VietIME.Core.Engines;
using VietIME.Hook;

namespace VietIME.App;

public partial class MainWindow : Window
{
    private readonly KeyboardHook? _hook;
    private readonly App _app;
    private EventHandler<bool>? _enabledChangedHandler;
    private bool _initialized = false;

    public MainWindow(KeyboardHook? hook = null)
    {
        _hook = hook;
        _app = (App)System.Windows.Application.Current;

        InitializeComponent();

        if (_hook != null)
        {
            toggleEnabled.IsChecked = _hook.IsEnabled;
            toggleWarpOnly.IsChecked = _hook.WarpOnlyMode;
            rbTelex.IsChecked = _hook.Engine?.Name == "Telex";
            rbVNI.IsChecked = _hook.Engine?.Name == "VNI";
            UpdateStatus();

            _enabledChangedHandler = (s, e) =>
            {
                if (IsVisible)
                    Dispatcher.Invoke(UpdateStatus);
            };
            _hook.EnabledChanged += _enabledChangedHandler;
        }

        toggleNotifications.IsChecked = _app.NotificationsEnabled;
        _initialized = true;
    }

    /// <summary>
    /// Cập nhật lại UI khi window được Show() lại từ tray
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

        toggleNotifications.IsChecked = _app.NotificationsEnabled;
        _initialized = true;
    }

    private void UpdateStatus()
    {
        if (_hook == null) return;

        var enabled = _hook.IsEnabled;
        var engineName = _hook.Engine?.Name ?? "Telex";
        var warpOnly = _hook.WarpOnlyMode;

        toggleEnabled.IsChecked = enabled;
        txtStatus.Text = enabled
            ? "Đang bật"
            : (warpOnly ? "Chờ Warp" : "Đã tắt");
        txtEngine.Text = engineName;

        var successBrush = (SolidColorBrush)FindResource("SuccessBrush");
        var dimBrush = (SolidColorBrush)FindResource("TextDimBrush");

        statusDot.Fill = enabled ? successBrush : dimBrush;
    }

    private void ToggleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (_hook != null)
        {
            _hook.IsEnabled = toggleEnabled.IsChecked ?? false;
        }
    }

    private void ToggleNotifications_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _app.NotificationsEnabled = toggleNotifications.IsChecked ?? false;
    }

    private void ToggleWarpOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _hook == null) return;
        _hook.WarpOnlyMode = toggleWarpOnly.IsChecked ?? false;
        _app.SaveSettings();
    }

    private void InputMethod_Changed(object sender, RoutedEventArgs e)
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

        _app.SaveSettings();
        UpdateStatus();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Nút X chỉ ẩn cửa sổ, không đóng app
        e.Cancel = true;
        Hide();
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }
}
