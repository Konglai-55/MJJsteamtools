using System.Reflection;
using System.Windows.Controls;

namespace SteamLuaManager.Views;

public partial class AboutView : UserControl
{
    public string VersionText { get; }
    public Func<Task>? CheckForUpdatesRequested { get; set; }

    public AboutView()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText = version is not null
            ? $"版本 {version.Major}.{version.Minor}.{version.Build}"
            : "版本 1.0.0";
        InitializeComponent();
        DataContext = this;
    }

    public void SetUpdateStatus(string message)
    {
        UpdateStatusText.Text = message;
    }

    private async void CheckUpdateButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CheckForUpdatesRequested is null)
            return;

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在连接 GitHub 检查新版本…";
        try
        {
            await CheckForUpdatesRequested();
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
