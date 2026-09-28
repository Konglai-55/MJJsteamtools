using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SteamLuaManager.Controls;

namespace SteamLuaManager.Views;

public partial class SettingsView : UserControl
{
    private bool _firstLoad = true;

    public event EventHandler? InstallOpenSteamToolRequested;
    public event EventHandler? UninstallOpenSteamToolRequested;

    public SettingsView()
    {
        InitializeComponent();
    }

    public void UpdateOpenSteamToolStatus(bool isInstalled)
    {
        OpenSteamToolStatusText.Text = isInstalled ? "已安装" : "未安装";
        OpenSteamToolStatusText.Foreground = (Brush)FindResource(
            isInstalled ? "SteamGreenBrush" : "SteamTextSecondaryBrush");
        OpenSteamToolStatusDot.Fill = (Brush)FindResource(
            isInstalled ? "SteamGreenBrush" : "SteamTextTertiaryBrush");
        InstallOpenSteamToolButton.IsEnabled = !isInstalled;
        UninstallOpenSteamToolButton.IsEnabled = isInstalled;
    }

    private void InstallOpenSteamToolButton_Click(object sender, RoutedEventArgs e)
    {
        InstallOpenSteamToolRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UninstallOpenSteamToolButton_Click(object sender, RoutedEventArgs e)
    {
        UninstallOpenSteamToolRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_firstLoad)
        {
            _firstLoad = false;
            return;
        }

        if (sender is TabControl tc &&
            tc.Template.FindName("ContentArea", tc) is FrameworkElement content)
            MotionBehavior.PlayEntrance(content, fromY: 6, pace: AppMotion.Pace.Content);
    }
}
