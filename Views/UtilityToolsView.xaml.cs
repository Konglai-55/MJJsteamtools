using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public partial class UtilityToolsView : UserControl
{
    public Func<Task>? ClearDownloadCacheRequested { get; set; }
    public Action? OpenStorageManagerRequested { get; set; }
    public Action? OpenCloudSyncRescueRequested { get; set; }

    public UtilityToolsView()
    {
        InitializeComponent();
    }

    private void OpenManifestCache_Click(object sender, RoutedEventArgs e)
    {
        var paths = App.ServiceProvider?.GetRequiredService<ISteamPathService>();
        if (paths is null) return;
        new ManifestCacheWindow(paths) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OpenStorageManagerButton_Click(object sender, RoutedEventArgs e)
    {
        OpenStorageManagerRequested?.Invoke();
    }

    private void OpenCloudSyncRescueButton_Click(object sender, RoutedEventArgs e)
    {
        OpenCloudSyncRescueRequested?.Invoke();
    }

    private async void ClearDownloadCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (ClearDownloadCacheRequested is null)
            return;

        ClearDownloadCacheButton.IsEnabled = false;
        ActionStatusText.Text = "正在唤起 Steam 清除缓存确认窗口…";
        ActionStatusText.Visibility = Visibility.Visible;

        try
        {
            await ClearDownloadCacheRequested();
        }
        finally
        {
            ClearDownloadCacheButton.IsEnabled = true;
        }
    }

    public void SetActionStatus(string message)
    {
        ActionStatusText.Text = message;
        ActionStatusText.Visibility = Visibility.Visible;
    }
}
