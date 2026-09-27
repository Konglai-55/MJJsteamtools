using System.Windows;
using System.Windows.Controls;

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
