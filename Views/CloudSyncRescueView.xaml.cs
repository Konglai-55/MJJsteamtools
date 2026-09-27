using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public partial class CloudSyncRescueView : UserControl
{
    private readonly ICloudSyncRescueService _rescueService;
    private readonly ISteamCloudPreferenceService _steamCloudPreferenceService;
    private bool _hasLoaded;
    private bool _isBusy;

    public CloudSyncRescueView(
        ICloudSyncRescueService rescueService,
        ISteamCloudPreferenceService steamCloudPreferenceService)
    {
        _rescueService = rescueService;
        _steamCloudPreferenceService = steamCloudPreferenceService;
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<CloudSyncIssue> Issues { get; } = [];
    public Action? BackRequested { get; set; }
    public Action<int>? OpenSaveVaultRequested { get; set; }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hasLoaded)
            return;
        _hasLoaded = true;
        await ScanAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_isBusy)
            return;
        SetBusy(true, "正在读取 Steam 云同步日志并判断异常是否仍然存在…");
        ErrorPanel.Visibility = Visibility.Collapsed;
        HealthyPanel.Visibility = Visibility.Collapsed;
        Issues.Clear();
        try
        {
            var result = await _rescueService.ScanAsync();
            foreach (var issue in result.Issues)
                Issues.Add(issue);

            IssueCountText.Text = result.Issues.Count.ToString();
            ConflictCountText.Text = result.Issues.Sum(issue => issue.ConflictCount).ToString();
            ResolvedCountText.Text = result.ResolvedHistoryCount.ToString();
            HealthyPanel.Visibility = result.Issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusIcon.Glyph = result.Issues.Count == 0 ? "\uE73E" : "\uE7BA";
            StatusIcon.Foreground = result.Issues.Count == 0
                ? new SolidColorBrush(Color.FromRgb(104, 196, 130))
                : new SolidColorBrush(Color.FromRgb(240, 184, 63));
            var logTime = result.LastLogTime == default
                ? "时间未知"
                : result.LastLogTime.ToString("yyyy年M月d日 HH:mm:ss");
            StatusText.Text = result.Issues.Count == 0
                ? $"扫描了 {result.ScannedAppCount} 款游戏的云记录，最后日志：{logTime}。当前状态正常。"
                : $"发现 {result.Issues.Count} 款游戏存在未完成的云同步异常；最后日志：{logTime}。";
        }
        catch (Exception ex)
        {
            IssueCountText.Text = "—";
            ConflictCountText.Text = "—";
            ResolvedCountText.Text = "—";
            ErrorText.Text = ex.GetBaseException().Message;
            ErrorPanel.Visibility = Visibility.Visible;
            StatusIcon.Glyph = "\uE783";
            StatusIcon.Foreground = Brushes.IndianRed;
            StatusText.Text = "检测失败，请确认 Steam 安装路径和日志文件是否可读取。";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: CloudSyncIssue issue })
            return;
        SetBusy(true, $"正在为 {issue.GameName} 创建安全备份…");
        var result = await _rescueService.BackupAsync(issue);
        SetBusy(false, null);
        await ShowMessageAsync(result.Success ? "备份完成" : "备份失败", result.Message, result.BackupPath);
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: CloudSyncIssue issue })
            return;

        var warning = issue.RiskLevel == CloudSyncRiskLevel.High
            ? "检测到可能属于游戏存档的冲突文件。工具会先创建完整安全备份，但 Steam 随后弹出版本选择时仍需你亲自确认。\n\n"
            : string.Empty;
        var confirmed = await ShowConfirmAsync(
            issue.RiskLevel == CloudSyncRiskLevel.High ? "高风险云存档冲突" : "安全重建云同步状态",
            warning +
            "接下来将：\n" +
            "1. 备份冲突文件和该游戏的 Steam 云缓存；\n" +
            "2. 正常退出 Steam；\n" +
            "3. 将该游戏的 remotecache.vdf 改名保留；\n" +
            "4. 重启 Steam，让官方客户端重新核对本地与云端。\n\n" +
            "不会删除本地存档，也不会直接删除云端文件。",
            "备份并继续");
        if (!confirmed)
            return;

        SetBusy(true, $"正在备份并重建 {issue.GameName} 的云同步状态…");
        var result = await _rescueService.ResetSyncMetadataAsync(issue);
        SetBusy(false, null);
        StatusIcon.Glyph = result.Success ? "\uE73E" : "\uE783";
        StatusIcon.Foreground = result.Success
            ? new SolidColorBrush(Color.FromRgb(104, 196, 130))
            : Brushes.IndianRed;
        StatusText.Text = result.Message;
        await ShowMessageAsync(result.Success ? "已交给 Steam 重新同步" : "修复未完成", result.Message, result.BackupPath);
    }

    private void OpenGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CloudSyncIssue issue })
            _rescueService.OpenGameInSteam(issue.AppId);
    }

    private void OpenSaveVaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CloudSyncIssue issue })
            OpenSaveVaultRequested?.Invoke(issue.AppId);
    }

    private async void DisableCloudButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: CloudSyncIssue issue })
            return;

        var confirmed = await ShowConfirmAsync(
            "关闭此游戏的 Steam 云同步",
            $"将为当前 Steam 账号关闭《{issue.GameName}》的单游戏云同步。\n\n" +
            "为了防止 Steam 覆盖配置，客户端会正常退出，写入完成后再自动启动。只影响这款游戏，不会关闭其他游戏或 Steam 的全局云同步。\n\n" +
            "本地存档不会被删除，但关闭后也不会再上传到云端。建议同时开启存档保险箱自动备份。",
            "关闭并重启 Steam");
        if (!confirmed)
            return;

        SetBusy(true, $"正在关闭《{issue.GameName}》的 Steam 云同步…");
        var shouldRescan = false;
        try
        {
            var result = await _steamCloudPreferenceService.SetCurrentAccountStateAsync(
                [issue.AppId],
                enabled: false);
            StatusIcon.Glyph = result.Success ? "\uE73E" : "\uE783";
            StatusIcon.Foreground = result.Success
                ? new SolidColorBrush(Color.FromRgb(104, 196, 130))
                : Brushes.IndianRed;
            StatusText.Text = result.Message;

            await ShowMessageAsync(
                result.Success ? "此游戏的云同步已关闭" : "未能关闭云同步",
                result.Success
                    ? $"{result.Message}\n\nSteam 不会再为《{issue.GameName}》请求云存档；本地存档仍然保留。"
                    : result.Message,
                result.BackupPath);

            shouldRescan = result.Success;
        }
        catch (Exception ex)
        {
            StatusIcon.Glyph = "\uE783";
            StatusIcon.Foreground = Brushes.IndianRed;
            StatusText.Text = $"关闭云同步失败：{ex.GetBaseException().Message}";
            await ShowMessageAsync("未能关闭云同步", StatusText.Text, null);
        }
        finally
        {
            SetBusy(false, null);
        }

        if (shouldRescan)
            await ScanAsync();
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try { _rescueService.OpenLogFolder(); }
        catch (Exception ex) { StatusText.Text = $"无法打开日志目录：{ex.Message}"; }
    }

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try { _rescueService.OpenBackupFolder(); }
        catch (Exception ex) { StatusText.Text = $"无法打开备份目录：{ex.Message}"; }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void SetBusy(bool busy, string? text)
    {
        _isBusy = busy;
        ScanButton.IsEnabled = !busy;
        ScanProgressRing.IsActive = busy;
        ScanProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(text))
            StatusText.Text = text;
    }

    private static async Task<bool> ShowConfirmAsync(string title, string message, string buttonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500
            },
            PrimaryButtonText = buttonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static async Task ShowMessageAsync(string title, string message, string? backupPath)
    {
        var content = string.IsNullOrWhiteSpace(backupPath)
            ? message
            : $"{message}\n\n备份位置：{backupPath}";
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            },
            CloseButtonText = "确定"
        };
        await dialog.ShowAsync();
    }
}
