using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class SaveVaultView : UserControl
{
    private readonly ISaveVaultService _saveVaultService;
    private readonly ISaveAutoBackupService _saveAutoBackupService;
    private readonly MainViewModel _mainViewModel;
    private readonly ObservableCollection<SaveGameRecord> _records = new();
    private ICollectionView? _recordsView;
    private CancellationTokenSource? _scanCts;
    private SaveGameRecord? _selectedRecord;
    private bool _isScanning;
    private bool _isBatchBackingUp;

    public SaveVaultView(
        ISaveVaultService saveVaultService,
        ISaveAutoBackupService saveAutoBackupService,
        MainViewModel mainViewModel)
    {
        _saveVaultService = saveVaultService;
        _saveAutoBackupService = saveAutoBackupService;
        _mainViewModel = mainViewModel;
        InitializeComponent();
        _saveAutoBackupService.BackupCompleted += SaveAutoBackupService_BackupCompleted;
    }

    public async Task RefreshAsync(int? openAppId = null)
    {
        if (_isScanning) return;
        _isScanning = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        ScanProgressRing.Visibility = Visibility.Visible;
        ScanProgressRing.IsActive = true;
        ScanStatusText.Text = "正在识别存档位置…";

        try
        {
            var games = _mainViewModel.AllGames.Count > 0
                ? _mainViewModel.AllGames.ToList()
                : _mainViewModel.Games.ToList();
            var progress = new Progress<(int Completed, int Total)>(value =>
                ScanStatusText.Text = $"正在扫描 {value.Completed} / {value.Total}");
            var records = await _saveVaultService.ScanGamesAsync(games, progress, _scanCts.Token);
            _records.Clear();
            foreach (var record in records
                         .OrderByDescending(item => item.HasSaveData)
                         .ThenByDescending(item => item.LastModified)
                         .ThenBy(item => item.GameName))
                _records.Add(record);

            _recordsView = CollectionViewSource.GetDefaultView(_records);
            _recordsView.Filter = RecordMatchesFilter;
            GameList.ItemsSource = _recordsView;
            UpdateMetrics();
            UpdateEmptyState();
            ScanStatusText.Text = $"扫描完成 · {_records.Count(item => item.HasSaveData)} 个游戏有存档";
            if (openAppId is int appId)
            {
                var record = _records.FirstOrDefault(item => item.AppId == appId);
                if (record is not null) ShowDetail(record);
            }
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "扫描已取消";
        }
        catch (Exception ex)
        {
            ScanStatusText.Text = $"扫描失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            ScanProgressRing.IsActive = false;
            ScanProgressRing.Visibility = Visibility.Collapsed;
            _isScanning = false;
        }
    }

    public async Task OpenGameAsync(int appId)
    {
        var record = _records.FirstOrDefault(item => item.AppId == appId);
        if (record is null)
        {
            await RefreshAsync(appId);
            return;
        }
        ShowDetail(record);
    }

    private bool RecordMatchesFilter(object value)
    {
        if (value is not SaveGameRecord record) return false;
        var query = SearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query)
            && !record.GameName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            && !record.AppId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;
        return FilterCombo.SelectedIndex switch
        {
            0 => record.HasSaveData,
            2 => record.BackupCount > 0,
            3 => !record.HasSaveData,
            _ => true
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (EmptyPanel is null) return;
        _recordsView?.Refresh();
        UpdateEmptyState();
    }

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmptyPanel is null) return;
        _recordsView?.Refresh();
        UpdateEmptyState();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void BackupGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SaveGameRecord record }) return;
        await BackupRecordAsync(record);
    }

    private void ViewGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SaveGameRecord record }) ShowDetail(record);
    }

    private void ShowDetail(SaveGameRecord record)
    {
        _selectedRecord = record;
        DetailPanel.DataContext = record;
        NoSnapshotsText.Visibility = record.Snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoSlotPreviewText.Visibility = record.Slots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var structured = record.Slots.Where(item => item.HasStructuredData).ToList();
        var raw = record.Slots.Where(item => !item.HasStructuredData).ToList();
        StructuredSlotItems.ItemsSource = structured;
        RawSlotItems.ItemsSource = raw;
        UnparsedSaveNotice.Visibility = raw.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RawFilesPanel.Visibility = Visibility.Collapsed;
        ToggleRawFilesButton.Content = "查看原始文件";
        LocationListPanel.Visibility = Visibility.Collapsed;
        ToggleLocationsButton.Content = "查看存档文件夹";
        UpdateAutoSaveBackupUi();
        OverviewPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        Reveal(DetailPanel);
    }

    private void BackToOverview_Click(object sender, RoutedEventArgs e)
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        OverviewPanel.Visibility = Visibility.Visible;
        Reveal(OverviewPanel);
    }

    private void VaultAutoSaveBackupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is null || sender is not System.Windows.Controls.Primitives.ToggleButton toggle)
            return;
        var enabled = toggle.IsChecked == true;
        _saveAutoBackupService.SetEnabled(_selectedRecord.AppId, enabled);
        UpdateAutoSaveBackupUi();
        ScanStatusText.Text = enabled
            ? $"已开启《{_selectedRecord.GameName}》退出后自动备份"
            : $"已关闭《{_selectedRecord.GameName}》退出后自动备份";
    }

    private void UpdateAutoSaveBackupUi(string? operationMessage = null)
    {
        if (_selectedRecord is null) return;
        var enabled = _saveAutoBackupService.IsEnabled(_selectedRecord.AppId);
        VaultAutoSaveBackupToggle.IsChecked = enabled;
        if (!string.IsNullOrWhiteSpace(operationMessage))
        {
            VaultAutoSaveBackupStatusText.Text = operationMessage;
            return;
        }

        var lastBackup = _saveAutoBackupService.GetLastBackupTime(_selectedRecord.AppId);
        VaultAutoSaveBackupStatusText.Text = !enabled
            ? "已关闭 · 手动备份、已有备份和其他游戏不受影响"
            : lastBackup is not null
                ? $"已开启 · 上次自动备份 {lastBackup.Value:yyyy年M月d日 HH:mm}"
                : "已开启 · MJJST 运行时，游戏退出且存档有变化便自动创建快照";
    }

    private void SaveAutoBackupService_BackupCompleted(SaveAutoBackupEvent value)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var record = _records.FirstOrDefault(item => item.AppId == value.AppId);
            if (record is not null && value.Success)
            {
                record.Snapshots.Clear();
                foreach (var snapshot in _saveVaultService.GetSnapshots(value.AppId))
                    record.Snapshots.Add(snapshot);
                record.NotifySummaryChanged();
                record.OperationStatus = value.Message;
                UpdateMetrics();
            }

            if (_selectedRecord?.AppId == value.AppId)
            {
                UpdateAutoSaveBackupUi(value.Message);
                NoSnapshotsText.Visibility = _selectedRecord.Snapshots.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        });
    }

    private void ToggleRawFiles_Click(object sender, RoutedEventArgs e)
    {
        var show = RawFilesPanel.Visibility != Visibility.Visible;
        RawFilesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleRawFilesButton.Content = show ? "收起原始文件" : "查看原始文件";
        if (show) Reveal(RawFilesPanel);
    }

    private void ToggleLocations_Click(object sender, RoutedEventArgs e)
    {
        var show = LocationListPanel.Visibility != Visibility.Visible;
        LocationListPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleLocationsButton.Content = show ? "收起存档文件夹" : "查看存档文件夹";
        if (show) Reveal(LocationListPanel);
    }

    private async void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is not null) await BackupRecordAsync(_selectedRecord);
    }

    private async Task<bool> BackupRecordAsync(SaveGameRecord record, string reason = "手动备份")
    {
        if (record.IsBusy || !record.HasSaveData) return false;
        record.IsBusy = true;
        record.OperationStatus = "正在创建存档快照…";
        try
        {
            var result = await _saveVaultService.CreateSnapshotAsync(record, reason, _scanCts?.Token ?? CancellationToken.None);
            record.OperationStatus = result.Message;
            if (!result.Success || result.Snapshot is null) return false;
            record.Snapshots.Clear();
            foreach (var snapshot in _saveVaultService.GetSnapshots(record.AppId))
                record.Snapshots.Add(snapshot);
            record.NotifySummaryChanged();
            NoSnapshotsText.Visibility = Visibility.Collapsed;
            UpdateMetrics();
            return true;
        }
        finally
        {
            record.IsBusy = false;
        }
    }

    private async void BackupAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchBackingUp) return;
        var targets = _records.Where(item => item.HasSaveData).ToList();
        if (targets.Count == 0)
        {
            ScanStatusText.Text = "当前没有识别到可备份的存档";
            return;
        }
        var confirmed = await ShowConfirmAsync(
            "备份全部游戏",
            $"将为 {targets.Count} 个游戏创建存档快照。正在运行的游戏可能有文件被占用，是否继续？",
            "开始备份");
        if (!confirmed) return;

        _isBatchBackingUp = true;
        var success = 0;
        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                ScanStatusText.Text = $"正在备份 {index + 1} / {targets.Count} · {targets[index].GameName}";
                if (await BackupRecordAsync(targets[index], "批量手动备份")) success++;
            }
            ScanStatusText.Text = $"批量备份完成 · 成功 {success} / {targets.Count}";
        }
        finally
        {
            _isBatchBackingUp = false;
        }
    }

    private async void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is null || sender is not Button { Tag: SaveSnapshotInfo snapshot }) return;
        var confirmed = await ShowConfirmAsync(
            "恢复存档快照",
            $"即将把“{_selectedRecord.GameName}”恢复到 {snapshot.CreatedText}。\n\n请先退出游戏；当前状态会自动备份，恢复不会删除快照中不存在的额外文件。",
            "继续恢复");
        if (!confirmed) return;
        var finalConfirm = await ShowConfirmAsync(
            "最后确认",
            "Steam Cloud 可能在游戏启动时提示本地与云端冲突。请优先选择刚恢复的本地文件，确认继续吗？",
            "确认恢复");
        if (!finalConfirm) return;

        var record = _selectedRecord;
        record.IsBusy = true;
        record.OperationStatus = "正在创建回滚备份并恢复文件…";
        try
        {
            var result = await _saveVaultService.RestoreSnapshotAsync(record, snapshot, _scanCts?.Token ?? CancellationToken.None);
            record.OperationStatus = result.Message;
            ScanStatusText.Text = result.Message;
            if (result.Success) await RefreshSelectedRecordAsync(record.AppId);
        }
        finally
        {
            record.IsBusy = false;
        }
    }

    private async Task RefreshSelectedRecordAsync(int appId)
    {
        var game = _mainViewModel.AllGames.FirstOrDefault(item => item.AppId == appId)
                   ?? _mainViewModel.Games.FirstOrDefault(item => item.AppId == appId);
        if (game is null) return;
        var refreshed = await _saveVaultService.ScanGameAsync(game, _scanCts?.Token ?? CancellationToken.None);
        var index = _records.ToList().FindIndex(item => item.AppId == appId);
        if (index >= 0) _records[index] = refreshed;
        _recordsView?.Refresh();
        UpdateMetrics();
        ShowDetail(refreshed);
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SaveLocationInfo location } && Directory.Exists(location.Path))
            Process.Start(new ProcessStartInfo(location.Path) { UseShellExecute = true });
    }

    private void OpenSelectedBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is null) return;
        OpenDirectory(_saveVaultService.GetBackupDirectory(_selectedRecord.AppId));
    }

    private void OpenBackupRoot_Click(object sender, RoutedEventArgs e)
    {
        var gameDirectory = _saveVaultService.GetBackupDirectory(0);
        OpenDirectory(Path.GetDirectoryName(gameDirectory)!);
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void UpdateMetrics()
    {
        var detected = _records.Where(item => item.HasSaveData).ToList();
        DetectedGamesText.Text = detected.Count.ToString();
        SaveFilesText.Text = detected.Sum(item => item.FileCount).ToString();
        SnapshotCountText.Text = _records.Sum(item => item.BackupCount).ToString();
        SaveSizeText.Text = FormatBytes(detected.Sum(item => item.TotalBytes));
    }

    private void UpdateEmptyState()
    {
        EmptyPanel.Visibility = _recordsView?.IsEmpty != false ? Visibility.Visible : Visibility.Collapsed;
    }

    private static async Task<bool> ShowConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static void Reveal(FrameworkElement element)
    {
        element.Opacity = 0;
        var transform = new TranslateTransform(12, 0);
        element.RenderTransform = transform;
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d * 1024d):0.00} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.00} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }
}
