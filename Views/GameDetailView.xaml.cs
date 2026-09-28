using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class GameDetailView : UserControl
{
    private readonly ISteamPathService _steamPathService;
    private readonly ISteamDepotService _steamDepotService;
    private readonly ISteamApiService _steamApiService;
    private readonly ISteamAchievementService _steamAchievementService;
    private readonly IDlcManagementService _dlcManagementService;
    private readonly ISaveVaultService _saveVaultService;
    private readonly ISaveAutoBackupService _saveAutoBackupService;
    private readonly IGamePlayProfileService _gamePlayProfileService;
    private readonly ISteamCloudPreferenceService _steamCloudPreferenceService;
    private readonly ObservableCollection<SteamAchievement> _achievements = new();
    private readonly ObservableCollection<DlcInfo> _dlcs = new();
    private readonly ObservableCollection<DlcInfo> _dlcPreview = new();
    private readonly ObservableCollection<SteamAchievement> _unlockedAchievementPreview = new();
    private readonly ObservableCollection<SteamAchievement> _lockedAchievementPreview = new();
    private readonly ObservableCollection<GamePlayProfile> _playProfiles = new();
    private GameInfo? _game;
    private MainViewModel? _mainViewModel;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _achievementCts;
    private CancellationTokenSource? _saveCts;
    private string? _installPath;
    private bool _isToggling;
    private bool _canManageAchievements;
    private bool _isAchievementOperationRunning;
    private ICollectionView? _achievementView;
    private ICollectionView? _dlcView;
    private bool _canManageDlcs;
    private bool _isDlcOperationRunning;
    private SaveGameRecord? _saveRecord;
    private bool _hasLoadedDlcs;
    private bool _isPlayProfileApplying;
    private bool _sharedTransitionPrepared;

    public event EventHandler? CloseRequested;
    public event Action<int>? OpenSaveVaultRequested;

    /// <summary>Hero image used by HomeView's shared-element transition.</summary>
    public Image SharedCoverImage => DetailCoverImage;

    public GameDetailView(
        ISteamPathService steamPathService,
        ISteamDepotService steamDepotService,
        ISteamApiService steamApiService,
        ISteamAchievementService steamAchievementService,
        IDlcManagementService dlcManagementService,
        ISaveVaultService saveVaultService,
        ISaveAutoBackupService saveAutoBackupService,
        IGamePlayProfileService gamePlayProfileService,
        ISteamCloudPreferenceService steamCloudPreferenceService)
    {
        _steamPathService = steamPathService;
        _steamDepotService = steamDepotService;
        _steamApiService = steamApiService;
        _steamAchievementService = steamAchievementService;
        _dlcManagementService = dlcManagementService;
        _saveVaultService = saveVaultService;
        _saveAutoBackupService = saveAutoBackupService;
        _gamePlayProfileService = gamePlayProfileService;
        _steamCloudPreferenceService = steamCloudPreferenceService;
        InitializeComponent();
        _saveAutoBackupService.BackupCompleted += SaveAutoBackupService_BackupCompleted;
        PlayProfileItems.ItemsSource = _playProfiles;
        DlcPreviewItems.ItemsSource = _dlcPreview;
        AchievementUnlockedPreviewItems.ItemsSource = _unlockedAchievementPreview;
        AchievementLockedPreviewItems.ItemsSource = _lockedAchievementPreview;
    }

    public void ShowGame(GameInfo game, MainViewModel mainViewModel)
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = new CancellationTokenSource();
        _achievementCts?.Cancel();
        _achievementCts?.Dispose();
        _achievementCts = new CancellationTokenSource();
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        _game = game;
        _mainViewModel = mainViewModel;
        DataContext = game;
        SetDlcAdvancedVisible(false, animate: false);
        SetAchievementAdvancedVisible(false, animate: false);
        SetActiveDetailTab(OverviewTabButton);
        SetSaveSectionVisible(false);
        DetailScrollViewer.ScrollToTop();
        if (!_sharedTransitionPrepared)
        {
            MotionBehavior.PlayEntrance(OverviewSection, fromY: 10, pace: AppMotion.Pace.Page);
            MotionBehavior.PlayEntrance(DetailMetricStrip, fromY: 6, order: 1);
        }
        _hasLoadedDlcs = false;
        CreatePlayProfileButton.IsEnabled = false;
        PlayProfileResultBar.Visibility = Visibility.Collapsed;
        RefreshPlayProfiles();
        UpdateLocalDetails();
        UpdateAutoSaveBackupUi();
        UpdateSteamCloudUi();
        _ = LoadDlcSummaryAsync(_detailCts.Token);
        _ = LoadAchievementsAsync(_achievementCts.Token);
        _ = LoadSaveSummaryAsync(_saveCts.Token);
        Dispatcher.BeginInvoke(Focus);
    }

    public void CloseGame()
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;
        _achievementCts?.Cancel();
        _achievementCts?.Dispose();
        _achievementCts = null;
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = null;
        _saveRecord = null;
        _achievements.Clear();
        _unlockedAchievementPreview.Clear();
        _lockedAchievementPreview.Clear();
        AchievementFeaturedPanel.DataContext = null;
        AchievementList.ItemsSource = null;
        _dlcs.Clear();
        _dlcPreview.Clear();
        DlcList.ItemsSource = null;
        _playProfiles.Clear();
        _hasLoadedDlcs = false;
        _game = null;
        _mainViewModel = null;
        DataContext = null;
        ResetSharedTransition();
    }

    /// <summary>
    /// Leaves the detail view in a quiet, measured state while the cover itself
    /// is still travelling above it. This prevents a second cover from flashing.
    /// </summary>
    public void PrepareForSharedTransition()
    {
        _sharedTransitionPrepared = true;
        foreach (var element in SharedTransitionElements())
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 0;
        }
        DetailCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
        DetailCoverImage.Opacity = 0;
    }

    /// <summary>Reveals detail content behind the travelling cover with a short stagger.</summary>
    public void PlaySharedDetailEntrance()
    {
        MotionBehavior.PlayEntrance(DetailHeaderPanel, fromY: -8, pace: AppMotion.Pace.Content, initialOpacity: 0);
        MotionBehavior.PlayEntrance(DetailNavigationBar, fromY: -6, order: 1, pace: AppMotion.Pace.Content, initialOpacity: 0);
        MotionBehavior.PlayEntrance(DetailScrollViewer, fromY: 8, order: 2, pace: AppMotion.Pace.Page, initialOpacity: 0);
        MotionBehavior.PlayEntrance(DetailHeroInfoPanel, fromX: 14, order: 3, pace: AppMotion.Pace.Content, initialOpacity: 0);
        MotionBehavior.PlayEntrance(DetailMetricStrip, fromY: 8, order: 4, pace: AppMotion.Pace.Content, initialOpacity: 0);
        DetailCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
        DetailCoverImage.BeginAnimation(UIElement.OpacityProperty,
            AppMotion.To(1, AppMotion.Pace.Content, from: 0,
                delay: AppMotion.Enabled ? TimeSpan.FromMilliseconds(170) : TimeSpan.Zero));
    }

    /// <summary>Fades the detail chrome quickly so the reverse cover flight stays legible.</summary>
    public void PrepareForSharedTransitionExit()
    {
        foreach (var element in SharedTransitionElements())
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty,
                AppMotion.To(0, AppMotion.Pace.Exit, AppMotion.Curve.Exit));
        }
        DetailCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
        DetailCoverImage.BeginAnimation(UIElement.OpacityProperty,
            AppMotion.To(0, AppMotion.Pace.Exit, AppMotion.Curve.Exit));
    }

    public void CompleteSharedTransition()
    {
        DetailCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
        DetailCoverImage.Opacity = 1;
        _sharedTransitionPrepared = false;
    }

    private IEnumerable<FrameworkElement> SharedTransitionElements()
    {
        yield return DetailHeaderPanel;
        yield return DetailNavigationBar;
        yield return DetailScrollViewer;
        yield return DetailHeroInfoPanel;
        yield return DetailMetricStrip;
    }

    private void ResetSharedTransition()
    {
        _sharedTransitionPrepared = false;
        foreach (var element in SharedTransitionElements())
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        }
        DetailCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
        DetailCoverImage.Opacity = 1;
    }

    private void UpdateLocalDetails()
    {
        if (_game is null) return;

        DepotCountText.Text = $"{_game.Depots.Count} 个";
        ManifestPinStatusText.Text = _game.IsManifestPinned
            ? _game.ManifestSourceIndex == 0 ? "当前安装版" : "Steam 最新版"
            : "未固定";
        TokenStatusText.Text = string.IsNullOrWhiteSpace(_game.Token) ? "未写入" : "已写入";
        DepotItemsControl.ItemsSource = _game.Depots
            .Select(depot => new DepotRow(
                depot.DepotId,
                string.IsNullOrWhiteSpace(depot.ManifestId) ? "—" : depot.ManifestId,
                string.IsNullOrWhiteSpace(depot.Key) ? "缺少" : "已就绪",
                depot.IsPinned ? "已固定" : "—"))
            .ToList();
        EmptyDepotsText.Visibility = _game.Depots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var luaPath = _game.LuaFilePath;
        LuaPathText.Text = string.IsNullOrWhiteSpace(luaPath) ? "未找到 Lua 文件" : luaPath;
        if (!string.IsNullOrWhiteSpace(luaPath) && File.Exists(luaPath))
        {
            var file = new FileInfo(luaPath);
            LuaFileInfoText.Text = $"{FormatBytes(file.Length)} · 修改于 {file.LastWriteTime:yyyy年M月d日 HH:mm}";
        }
        else
        {
            LuaFileInfoText.Text = "文件当前不可用";
        }

        var manifestPath = _steamPathService.FindAppManifest(_game.AppId);
        _installPath = TryResolveInstallPath(manifestPath);
        var isInstalled = !string.IsNullOrWhiteSpace(manifestPath);
        HeroInstallStatusText.Text = isInstalled ? "本地已安装" : "本地未安装";
        InstallStatusText.Text = isInstalled
            ? $"已找到 Steam AppManifest\n{manifestPath}"
            : "没有在 Steam 库目录中找到 AppManifest";
        InstallPathText.Text = string.IsNullOrWhiteSpace(_installPath) ? "未检测到游戏目录" : _installPath;
        OpenInstallFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(_installPath) && Directory.Exists(_installPath);
    }

    private async Task LoadSaveSummaryAsync(CancellationToken cancellationToken)
    {
        if (_game is null) return;
        var game = _game;
        SaveLoadingRing.Visibility = Visibility.Visible;
        SaveLoadingRing.IsActive = true;
        SaveModeText.Text = "正在识别";
        SaveStatusText.Text = "正在识别 Steam userdata 与常见本地存档目录…";
        BackupGameSaveButton.IsEnabled = false;
        OpenFirstSaveLocationButton.IsEnabled = false;
        try
        {
            var record = await _saveVaultService.ScanGameAsync(game, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_game != game) return;
            _saveRecord = record;
            SetSaveSectionVisible(record.HasSaveData);
            SaveLocationCountText.Text = record.Locations.Count.ToString();
            SaveFileCountText.Text = record.FileCount.ToString();
            SaveLastModifiedText.Text = record.LastModifiedText;
            SaveBackupCountText.Text = record.BackupCount.ToString();
            SaveLocationPreviewItems.ItemsSource = record.Locations.Take(3).ToList();
            BackupGameSaveButton.IsEnabled = record.HasSaveData;
            OpenFirstSaveLocationButton.IsEnabled = record.Locations.Count > 0;
            SaveModeText.Text = record.HasSaveData ? "已识别" : "未发现";
            SaveModeText.Foreground = new SolidColorBrush(record.HasSaveData
                ? Color.FromRgb(0x62, 0xE4, 0x8B)
                : Color.FromRgb(0xA8, 0xB3, 0xBE));
            SaveModeBadge.Background = new SolidColorBrush(record.HasSaveData
                ? Color.FromRgb(0x17, 0x3D, 0x30)
                : Color.FromRgb(0x2B, 0x35, 0x40));
            SaveStatusText.Text = record.HasSaveData
                ? $"{record.StatusText} · {record.SizeText} · 修改存档前建议先创建快照"
                : "没有在已知位置发现存档；可在存档保险箱中查看识别范围";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_game == game)
            {
                // A failed scan is not an empty result: keep the section visible
                // so the error and recovery controls remain discoverable.
                SetSaveSectionVisible(true);
                SaveModeText.Text = "识别失败";
                SaveStatusText.Text = $"存档识别失败：{ex.GetBaseException().Message}";
            }
        }
        finally
        {
            if (_game == game)
            {
                SaveLoadingRing.IsActive = false;
                SaveLoadingRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void BackupGameSave_Click(object sender, RoutedEventArgs e)
    {
        if (_saveRecord is null || !_saveRecord.HasSaveData || sender is not Button button) return;
        button.IsEnabled = false;
        SaveLoadingRing.Visibility = Visibility.Visible;
        SaveLoadingRing.IsActive = true;
        SaveStatusText.Text = "正在创建存档快照…";
        try
        {
            var result = await _saveVaultService.CreateSnapshotAsync(
                _saveRecord,
                "游戏详情手动备份",
                _saveCts?.Token ?? CancellationToken.None);
            SaveStatusText.Text = result.Message;
            if (_mainViewModel is not null) _mainViewModel.StatusMessage = result.Message;
            if (result.Success && result.Snapshot is not null)
            {
                _saveRecord.Snapshots.Clear();
                foreach (var snapshot in _saveVaultService.GetSnapshots(_saveRecord.AppId))
                    _saveRecord.Snapshots.Add(snapshot);
                _saveRecord.NotifySummaryChanged();
                SaveBackupCountText.Text = _saveRecord.BackupCount.ToString();
            }
        }
        finally
        {
            SaveLoadingRing.IsActive = false;
            SaveLoadingRing.Visibility = Visibility.Collapsed;
            button.IsEnabled = _saveRecord?.HasSaveData == true;
        }
    }

    private void OpenFirstSaveLocation_Click(object sender, RoutedEventArgs e)
    {
        var path = _saveRecord?.Locations.FirstOrDefault()?.Path;
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenSaveVault_Click(object sender, RoutedEventArgs e)
    {
        if (_game is not null) OpenSaveVaultRequested?.Invoke(_game.AppId);
    }

    private void AutoSaveBackupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || sender is not ToggleButton toggle) return;
        var enabled = toggle.IsChecked == true;
        _saveAutoBackupService.SetEnabled(_game.AppId, enabled);
        UpdateAutoSaveBackupUi();
        if (_mainViewModel is not null)
        {
            _mainViewModel.StatusMessage = enabled
                ? $"已开启《{_game.GameName}》退出后自动备份"
                : $"已关闭《{_game.GameName}》退出后自动备份";
        }
    }

    private void UpdateAutoSaveBackupUi(string? operationMessage = null)
    {
        if (_game is null) return;
        var enabled = _saveAutoBackupService.IsEnabled(_game.AppId);
        AutoSaveBackupToggle.IsChecked = enabled;
        if (!string.IsNullOrWhiteSpace(operationMessage))
        {
            AutoSaveBackupStatusText.Text = operationMessage;
            return;
        }

        var lastBackup = _saveAutoBackupService.GetLastBackupTime(_game.AppId);
        AutoSaveBackupStatusText.Text = !enabled
            ? "已关闭 · 手动备份、已有备份和其他游戏不受影响"
            : lastBackup is not null
                ? $"已开启 · 上次自动备份 {lastBackup.Value:yyyy年M月d日 HH:mm}"
                : "已开启 · MJJST 运行时，游戏退出且存档有变化便自动创建快照";
    }

    private void UpdateSteamCloudUi(string? operationMessage = null)
    {
        if (_game is null) return;

        var state = _steamCloudPreferenceService.GetCurrentAccountState(_game.AppId);
        SteamCloudToggle.IsChecked = state?.IsEnabled ?? false;
        SteamCloudToggle.IsEnabled = state?.IsGlobalEnabled == true;
        if (!string.IsNullOrWhiteSpace(operationMessage))
        {
            SteamCloudStatusText.Text = operationMessage;
            return;
        }

        SteamCloudStatusText.Text = state switch
        {
            null => "无法唯一确定当前 Steam 账号；请先登录 Steam 后重新打开详情页",
            { IsGlobalEnabled: false } => "Steam 全局云同步已关闭 · 此游戏不会请求云存档",
            { IsEnabled: false } => "已关闭 · Steam 不会再为当前账号请求这款游戏的云存档",
            { HasAppOverride: true } => "已开启 · 当前账号会在游戏启动和退出时同步云存档",
            _ => "跟随 Steam 全局设置 · 当前为开启"
        };
    }

    private async void SteamCloudToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || sender is not ToggleButton toggle) return;

        var game = _game;
        var enableCloud = toggle.IsChecked == true;
        var action = enableCloud ? "重新开启" : "关闭";
        var explanation = enableCloud
            ? "重新开启后，Steam 会再次尝试连接这款游戏的云存档；虚拟入库仍可能重新出现云同步错误。"
            : "关闭后，Steam 不再为当前账号同步这款游戏的云存档，相关云错误提示也会停止。本地存档不会被删除，建议同时开启退出游戏后自动备份。";
        var confirmed = await ShowConfirmAsync(
            $"{action}此游戏的 Steam 云同步",
            $"{explanation}\n\n为了防止 Steam 覆盖配置，客户端将正常退出并在设置完成后重新启动。",
            $"{action}并重启 Steam");
        if (!confirmed)
        {
            UpdateSteamCloudUi();
            return;
        }

        toggle.IsEnabled = false;
        SteamCloudProgressRing.Visibility = Visibility.Visible;
        SteamCloudProgressRing.IsActive = true;
        SteamCloudStatusText.Text = $"正在{action}单游戏云同步并安全重启 Steam…";
        try
        {
            var result = await _steamCloudPreferenceService.SetCurrentAccountStateAsync(
                [game.AppId],
                enableCloud,
                _detailCts?.Token ?? CancellationToken.None);
            if (_game != game) return;

            UpdateSteamCloudUi(result.Message);
            if (_mainViewModel is not null)
                _mainViewModel.StatusMessage = result.Message;
        }
        finally
        {
            SteamCloudProgressRing.IsActive = false;
            SteamCloudProgressRing.Visibility = Visibility.Collapsed;
            if (_game == game)
                SteamCloudToggle.IsEnabled = true;
        }
    }

    private void SaveAutoBackupService_BackupCompleted(SaveAutoBackupEvent value)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_game?.AppId != value.AppId) return;
            UpdateAutoSaveBackupUi(value.Message);
            if (!value.Success || _saveRecord is null) return;
            _saveRecord.Snapshots.Clear();
            foreach (var snapshot in _saveVaultService.GetSnapshots(value.AppId))
                _saveRecord.Snapshots.Add(snapshot);
            _saveRecord.NotifySummaryChanged();
            SaveBackupCountText.Text = _saveRecord.BackupCount.ToString();
        });
    }

    private async Task LoadDlcSummaryAsync(CancellationToken cancellationToken)
    {
        if (_game is null) return;
        var game = _game;
        DlcLoadingRing.Visibility = Visibility.Visible;
        DlcLoadingRing.IsActive = true;
        DlcManagerLoadingRing.Visibility = Visibility.Visible;
        DlcManagerLoadingRing.IsActive = true;
        RefreshDlcsButton.IsEnabled = false;
        DlcBatchActions.IsEnabled = false;
        _hasLoadedDlcs = false;
        CreatePlayProfileButton.IsEnabled = false;
        DlcSummaryText.Text = "正在获取…";
        DlcModeText.Text = "正在查询";
        DlcManagementStatusText.Text = "正在读取 DLC 列表与 Lua 注册状态…";
        _canManageDlcs = false;
        _dlcs.Clear();
        _dlcView = null;
        DlcList.ItemsSource = null;
        UpdateDlcSummary();
        UpdateDlcEmptyState();

        try
        {
            var result = await _steamDepotService.QueryAppAsync(game.AppId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_game != game) return;

            if (result is null)
            {
                DlcSummaryText.Text = "暂时无法获取";
                DlcManagementStatusText.Text = "暂时无法获取 DLC 列表";
                DlcModeText.Text = "查询失败";
                UpdateDlcEmptyState();
                return;
            }

            var dlcIds = result.DlcAppIds.Distinct().ToList();
            var total = dlcIds.Count;
            if (total == 0)
            {
                _hasLoadedDlcs = true;
                CreatePlayProfileButton.IsEnabled = true;
                DlcSummaryText.Text = "无关联 DLC";
                DlcModeText.Text = "无 DLC";
                DlcManagementStatusText.Text = "该游戏没有查询到关联 DLC";
                UpdateDlcSummary();
                UpdateDlcEmptyState();
                RefreshPlayProfiles();
                return;
            }

            var canManage = !string.IsNullOrWhiteSpace(game.LuaFilePath) && File.Exists(game.LuaFilePath);
            var luaContent = string.Empty;
            if (canManage)
            {
                try
                {
                    luaContent = await File.ReadAllTextAsync(game.LuaFilePath, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    canManage = false;
                }
            }

            _canManageDlcs = canManage;
            _dlcs.Clear();
            foreach (var dlcId in dlcIds)
            {
                _dlcs.Add(new DlcInfo
                {
                    AppId = dlcId,
                    Name = $"DLC {dlcId}",
                    IsImported = canManage && _dlcManagementService.IsEnabled(luaContent, dlcId),
                    CanToggle = canManage
                });
            }
            _hasLoadedDlcs = true;
            CreatePlayProfileButton.IsEnabled = true;

            _dlcView = CollectionViewSource.GetDefaultView(_dlcs);
            _dlcView.Filter = DlcMatchesFilter;
            DlcList.ItemsSource = _dlcView;
            DlcModeText.Text = canManage ? "可管理" : "只读";
            DlcModeText.Foreground = new SolidColorBrush(canManage
                ? Color.FromRgb(0x62, 0xE4, 0x8B)
                : Color.FromRgb(0xB2, 0xBD, 0xC8));
            DlcModeBadge.Background = new SolidColorBrush(canManage
                ? Color.FromRgb(0x17, 0x3D, 0x30)
                : Color.FromRgb(0x2B, 0x35, 0x40));
            DlcManagementStatusText.Text = canManage
                ? "滑块控制 Lua 是否注册该 DLC；不会修改无法确认归属的共享 Depot"
                : "未找到可写入的 Lua 文件，当前仅可查看 DLC 列表";
            DlcBatchActions.IsEnabled = canManage;
            UpdateDlcSummary();
            UpdateDlcEmptyState();
            UpdateDlcPreview();
            RefreshPlayProfiles();

            var presentationGames = _dlcs.Select(dlc => new GameInfo
            {
                AppId = dlc.AppId,
                GameName = $"AppID: {dlc.AppId}"
            }).ToList();
            _steamApiService.PopulateFromCache(presentationGames);
            ApplyDlcPresentation(presentationGames);

            await _steamApiService.RefreshGameInfoAsync(presentationGames, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_game != game) return;
            ApplyDlcPresentation(presentationGames);
            _dlcView.Refresh();
            RefreshPlayProfiles();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_game == game)
            {
                DlcSummaryText.Text = "获取失败";
                DlcModeText.Text = "查询失败";
                DlcManagementStatusText.Text = $"DLC 查询失败：{ex.GetBaseException().Message}";
            }
        }
        finally
        {
            if (_game == game)
            {
                DlcLoadingRing.IsActive = false;
                DlcLoadingRing.Visibility = Visibility.Collapsed;
                DlcManagerLoadingRing.IsActive = false;
                DlcManagerLoadingRing.Visibility = Visibility.Collapsed;
                RefreshDlcsButton.IsEnabled = true;
            }
        }
    }

    private async Task ReloadDlcsAsync()
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = new CancellationTokenSource();
        await LoadDlcSummaryAsync(_detailCts.Token);
    }

    private bool DlcMatchesFilter(object value)
    {
        if (value is not DlcInfo dlc) return false;
        var query = DlcSearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query)
            && !dlc.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            && !dlc.AppId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        return DlcFilterCombo.SelectedIndex switch
        {
            1 => dlc.IsImported,
            2 => !dlc.IsImported,
            _ => true
        };
    }

    private void DlcSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (EmptyDlcsPanel is null) return;
        _dlcView?.Refresh();
        UpdateDlcEmptyState();
    }

    private void DlcFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmptyDlcsPanel is null) return;
        _dlcView?.Refresh();
        UpdateDlcEmptyState();
    }

    private void UpdateDlcSummary()
    {
        if (DlcManagementCountText is null) return;
        var total = _dlcs.Count;
        var enabled = _dlcs.Count(item => item.IsImported);
        DlcManagementCountText.Text = $"{enabled} / {total} 已启用";
        DlcManagementProgressBar.Maximum = Math.Max(1, total);
        DlcManagementProgressBar.Value = enabled;
        DlcSummaryText.Text = total == 0 ? "无关联 DLC" : $"{enabled} / {total}";
        DlcSummaryText.ToolTip = total == 0
            ? "没有查询到关联 DLC"
            : $"共 {total} 个 DLC，当前 Lua 已启用 {enabled} 个";
        UpdateDlcPreview();
    }

    private void ApplyDlcPresentation(IReadOnlyCollection<GameInfo> presentationGames)
    {
        foreach (var presentation in presentationGames)
        {
            var dlc = _dlcs.FirstOrDefault(item => item.AppId == presentation.AppId);
            if (dlc is null) continue;
            if (!string.IsNullOrWhiteSpace(presentation.GameName)
                && !presentation.GameName.StartsWith("AppID:", StringComparison.OrdinalIgnoreCase))
                dlc.Name = presentation.GameName;
            if (!string.IsNullOrWhiteSpace(presentation.CoverImagePath))
                dlc.CoverImagePath = presentation.CoverImagePath;
        }
        UpdateDlcPreview();
    }

    private void UpdateDlcPreview()
    {
        if (DlcPreviewItems is null) return;
        _dlcPreview.Clear();
        foreach (var dlc in _dlcs.Take(6)) _dlcPreview.Add(dlc);
        var hidden = Math.Max(0, _dlcs.Count - _dlcPreview.Count);
        DlcPreviewCountText.Text = hidden > 0
            ? $"当前展示 6 个 DLC，另有 {hidden} 个可在完整管理中查看"
            : $"共 {_dlcs.Count} 个 DLC";
    }

    private void UpdateDlcEmptyState()
    {
        if (EmptyDlcsPanel is null) return;
        var empty = _dlcView?.IsEmpty != false;
        EmptyDlcsPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshDlcsButton_Click(object sender, RoutedEventArgs e) =>
        await ReloadDlcsAsync();

    private void ToggleDlcManagement_Click(object sender, RoutedEventArgs e) =>
        SetDlcAdvancedVisible(DlcAdvancedListContainer.Visibility != Visibility.Visible);

    private void SetDlcAdvancedVisible(bool visible, bool animate = true)
    {
        if (DlcAdvancedListContainer is null) return;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DlcAdvancedFilterBar.Visibility = visibility;
        DlcBatchActions.Visibility = visibility;
        DlcAdvancedListContainer.Visibility = visibility;
        ToggleDlcManagementButton.Content = visible ? "收起完整管理" : "管理全部 DLC";
        if (visible && animate) AnimateReveal(DlcAdvancedListContainer);
    }

    private async void DlcToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: DlcInfo dlc } toggle) return;
        if (_isDlcOperationRunning)
        {
            toggle.IsChecked = dlc.IsImported;
            return;
        }

        var enabled = toggle.IsChecked == true;
        if (enabled == dlc.IsImported) return;
        var success = await ExecuteDlcOperationAsync(new[] { dlc }, enabled);
        if (!success) toggle.IsChecked = dlc.IsImported;
    }

    private async void EnableAllDlcs_Click(object sender, RoutedEventArgs e) =>
        await ExecuteAllDlcsAsync(enabled: true);

    private async void DisableAllDlcs_Click(object sender, RoutedEventArgs e) =>
        await ExecuteAllDlcsAsync(enabled: false);

    private async Task ExecuteAllDlcsAsync(bool enabled)
    {
        var targets = _dlcs.Where(item => item.IsImported != enabled).ToList();
        if (targets.Count == 0)
        {
            ShowDlcResult(true, enabled ? "当前所有 DLC 均已启用" : "当前所有 DLC 均已禁用");
            return;
        }

        var action = enabled ? "全部启用" : "全部禁用";
        var message = enabled
            ? $"将向 Lua 中启用 {targets.Count} 个 DLC 注册项。操作前会自动备份原文件，是否继续？"
            : $"将从 Lua 中禁用 {targets.Count} 个 DLC 注册项。无法确认归属的共享 Depot 不会被修改，是否继续？";
        var confirmed = await ShowConfirmAsync(action, message, $"确认{action}");
        if (confirmed) await ExecuteDlcOperationAsync(targets, enabled);
    }

    private async Task<bool> ExecuteDlcOperationAsync(IReadOnlyCollection<DlcInfo> targets, bool enabled)
    {
        if (_game is null || !_canManageDlcs || _isDlcOperationRunning || targets.Count == 0)
            return false;

        _isDlcOperationRunning = true;
        DlcBatchActions.IsEnabled = false;
        RefreshDlcsButton.IsEnabled = false;
        foreach (var item in targets)
        {
            item.CanToggle = false;
            item.IsBusy = true;
        }
        ShowDlcResult(true, enabled ? "正在启用所选 DLC…" : "正在禁用所选 DLC…");

        try
        {
            var result = await _dlcManagementService.SetEnabledAsync(
                _game.LuaFilePath,
                targets.Select(item => item.AppId).ToArray(),
                enabled,
                _detailCts?.Token ?? CancellationToken.None);
            ShowDlcResult(result.Success, result.Message);
            if (_mainViewModel is not null) _mainViewModel.StatusMessage = result.Message;
            if (!result.Success) return false;

            foreach (var item in targets) item.IsImported = enabled;
            UpdateDlcSummary();
            _dlcView?.Refresh();
            UpdateDlcEmptyState();
            UpdateLocalDetails();
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowDlcResult(false, "DLC 操作已取消");
            return false;
        }
        finally
        {
            foreach (var item in targets)
            {
                item.IsBusy = false;
                item.CanToggle = _canManageDlcs;
            }
            DlcBatchActions.IsEnabled = _canManageDlcs;
            RefreshDlcsButton.IsEnabled = true;
            _isDlcOperationRunning = false;
        }
    }

    private void OpenDlcBackups_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        var backupPath = _dlcManagementService.GetBackupDirectory(_game.AppId);
        if (!Directory.Exists(backupPath))
        {
            ShowDlcResult(false, "当前游戏还没有 DLC 备份；首次修改前会自动创建");
            return;
        }
        Process.Start(new ProcessStartInfo(backupPath) { UseShellExecute = true });
    }

    private void ShowDlcResult(bool success, string message)
    {
        DlcResultBar.Visibility = Visibility.Visible;
        DlcResultBar.Background = new SolidColorBrush(success
            ? Color.FromRgb(0x17, 0x2A, 0x29)
            : Color.FromRgb(0x2B, 0x20, 0x27));
        DlcResultIcon.Glyph = success ? "\uE73E" : "\uEA39";
        DlcResultIcon.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(0x62, 0xE4, 0x8B)
            : Color.FromRgb(0xFF, 0x78, 0x8C));
        DlcResultText.Text = message;
    }

    private void RefreshPlayProfiles()
    {
        if (_game is null || PlayProfileItems is null) return;

        var totalDlcCount = _dlcs.Count;
        var availableDlcIds = _dlcs.Select(item => item.AppId).ToHashSet();
        _playProfiles.Clear();
        foreach (var profile in _gamePlayProfileService.GetProfiles(_game.AppId))
        {
            profile.DlcSummary = !_hasLoadedDlcs
                ? "等待读取 DLC"
                : totalDlcCount == 0
                    ? "此游戏没有关联 DLC"
                    : profile.Kind switch
                    {
                        GamePlayProfileKind.Vanilla => $"0 / {totalDlcCount} 个 DLC",
                        GamePlayProfileKind.FullDlc => $"{totalDlcCount} / {totalDlcCount} 个 DLC",
                        _ => $"{profile.EnabledDlcAppIds.Count(availableDlcIds.Contains)} / {totalDlcCount} 个 DLC"
                    };
            _playProfiles.Add(profile);
        }
    }

    private async void CreatePlayProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || !_hasLoadedDlcs) return;
        var profile = await ShowPlayProfileEditorAsync(null);
        if (profile is null) return;
        _gamePlayProfileService.SaveCustomProfile(profile);
        RefreshPlayProfiles();
        ShowPlayProfileResult(true, $"方案“{profile.Name}”已保存到本机");
    }

    private async void EditPlayProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GamePlayProfile profile } || profile.IsBuiltIn) return;
        var updated = await ShowPlayProfileEditorAsync(profile);
        if (updated is null) return;
        _gamePlayProfileService.SaveCustomProfile(updated);
        RefreshPlayProfiles();
        ShowPlayProfileResult(true, $"方案“{updated.Name}”已更新");
    }

    private async void DeletePlayProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || sender is not Button { Tag: GamePlayProfile profile } || profile.IsBuiltIn) return;
        var confirmed = await ShowConfirmAsync(
            "删除游玩方案",
            $"确认删除“{profile.Name}”吗？这不会修改当前 DLC 或存档状态。",
            "删除");
        if (!confirmed) return;
        _gamePlayProfileService.DeleteProfile(_game.AppId, profile.Id);
        RefreshPlayProfiles();
        ShowPlayProfileResult(true, $"已删除方案“{profile.Name}”");
    }

    private async void ApplyPlayProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GamePlayProfile profile })
            await ApplyPlayProfileAsync(profile);
    }

    private async void ApplyAndLaunchPlayProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GamePlayProfile profile }) return;
        if (await ApplyPlayProfileAsync(profile)) LaunchWithProfile(profile);
    }

    private async Task<GamePlayProfile?> ShowPlayProfileEditorAsync(GamePlayProfile? existing)
    {
        if (_game is null) return null;

        var nameBox = new TextBox
        {
            Text = existing?.Name ?? $"方案 {_playProfiles.Count(profile => !profile.IsBuiltIn) + 1}",
            MinWidth = 390,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var launchArgumentsBox = new TextBox
        {
            Text = existing?.LaunchArguments ?? string.Empty,
            MinWidth = 390,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = "例如：-novid -windowed"
        };
        var backupCheckBox = new CheckBox
        {
            Content = "应用前自动备份当前存档",
            IsChecked = existing?.BackupSaveBeforeApply ?? true,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var captureDlcCheckBox = new CheckBox
        {
            Content = existing is null ? "记录当前 DLC 开关状态" : "用当前 DLC 状态更新此方案",
            IsChecked = existing is null,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "方案名称",
            FontSize = 11,
            Foreground = TryFindResource("SteamTextTertiaryBrush") as Brush
        });
        content.Children.Add(nameBox);
        content.Children.Add(new TextBlock
        {
            Text = "启动参数（可选）",
            FontSize = 11,
            Margin = new Thickness(0, 13, 0, 0),
            Foreground = TryFindResource("SteamTextTertiaryBrush") as Brush
        });
        content.Children.Add(launchArgumentsBox);
        content.Children.Add(new TextBlock
        {
            Text = "参数只会传给 Steam 启动命令；不会修改创意工坊订阅。",
            FontSize = 10,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = TryFindResource("SteamTextTertiaryBrush") as Brush
        });
        content.Children.Add(backupCheckBox);
        content.Children.Add(captureDlcCheckBox);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "保存当前游玩方案" : "编辑游玩方案",
            Content = content,
            PrimaryButtonText = existing is null ? "保存方案" : "保存更改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var name = nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowPlayProfileResult(false, "方案名称不能为空");
            return null;
        }

        var enabledDlcIds = captureDlcCheckBox.IsChecked == true || existing is null
            ? _dlcs.Where(item => item.IsImported).Select(item => item.AppId).ToList()
            : [.. existing.EnabledDlcAppIds];
        return new GamePlayProfile
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            AppId = _game.AppId,
            Name = name,
            Kind = GamePlayProfileKind.Custom,
            EnabledDlcAppIds = enabledDlcIds,
            LaunchArguments = launchArgumentsBox.Text,
            BackupSaveBeforeApply = backupCheckBox.IsChecked == true
        };
    }

    private async Task<bool> ApplyPlayProfileAsync(GamePlayProfile profile)
    {
        if (_game is null || profile.AppId != _game.AppId || _isPlayProfileApplying) return false;
        var game = _game;
        _isPlayProfileApplying = true;
        PlayProfileItems.IsEnabled = false;
        CreatePlayProfileButton.IsEnabled = false;
        DlcBatchActions.IsEnabled = false;
        ShowPlayProfileResult(true, $"正在应用“{profile.Name}”…");

        try
        {
            if (!_hasLoadedDlcs)
            {
                await ReloadDlcsAsync();
                if (!_hasLoadedDlcs)
                    throw new InvalidOperationException("DLC 状态尚未读取完成，请稍后重试");
            }

            var cancellationToken = _detailCts?.Token ?? CancellationToken.None;
            if (profile.BackupSaveBeforeApply)
            {
                _saveRecord ??= await _saveVaultService.ScanGameAsync(game, cancellationToken);
                if (_saveRecord.HasSaveData)
                {
                    var backupResult = await _saveVaultService.CreateSnapshotAsync(
                        _saveRecord,
                        $"切换游玩方案：{profile.Name}",
                        cancellationToken);
                    if (!backupResult.Success)
                        throw new InvalidOperationException($"存档备份失败，已停止切换：{backupResult.Message}");
                    SaveBackupCountText.Text = _saveVaultService.GetSnapshots(game.AppId).Count.ToString();
                }
            }

            var allDlcIds = _dlcs.Select(item => item.AppId).ToHashSet();
            HashSet<int> desiredDlcIds = profile.Kind switch
            {
                GamePlayProfileKind.Vanilla => new HashSet<int>(),
                GamePlayProfileKind.FullDlc => allDlcIds,
                _ => profile.EnabledDlcAppIds.Where(allDlcIds.Contains).ToHashSet()
            };
            var originalDlcIds = _dlcs.Where(item => item.IsImported).Select(item => item.AppId).ToHashSet();
            var toEnable = desiredDlcIds.Except(originalDlcIds).ToArray();
            var toDisable = originalDlcIds.Except(desiredDlcIds).ToArray();

            if ((toEnable.Length > 0 || toDisable.Length > 0) && !_canManageDlcs)
                throw new InvalidOperationException("当前游戏没有可写入的 Lua 文件，无法切换 DLC 组合");

            var enabledApplied = false;
            try
            {
                if (toEnable.Length > 0)
                {
                    var result = await _dlcManagementService.SetEnabledAsync(
                        game.LuaFilePath, toEnable, true, cancellationToken);
                    if (!result.Success) throw new InvalidOperationException(result.Message);
                    enabledApplied = true;
                }

                if (toDisable.Length > 0)
                {
                    var result = await _dlcManagementService.SetEnabledAsync(
                        game.LuaFilePath, toDisable, false, cancellationToken);
                    if (!result.Success) throw new InvalidOperationException(result.Message);
                }
            }
            catch
            {
                await TryRestoreDlcStateAsync(game.LuaFilePath, toEnable, toDisable, enabledApplied, cancellationToken);
                throw;
            }

            foreach (var dlc in _dlcs) dlc.IsImported = desiredDlcIds.Contains(dlc.AppId);
            _gamePlayProfileService.SetActiveProfile(game.AppId, profile.Id);
            UpdateDlcSummary();
            _dlcView?.Refresh();
            RefreshPlayProfiles();
            var backupText = profile.BackupSaveBeforeApply && _saveRecord?.HasSaveData == true
                ? "，已先备份存档"
                : string.Empty;
            ShowPlayProfileResult(true, $"“{profile.Name}”已应用：启用 {desiredDlcIds.Count} / {allDlcIds.Count} 个 DLC{backupText}");
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowPlayProfileResult(false, "游玩方案切换已取消");
            return false;
        }
        catch (Exception ex)
        {
            ShowPlayProfileResult(false, $"方案应用失败：{ex.GetBaseException().Message}");
            return false;
        }
        finally
        {
            _isPlayProfileApplying = false;
            PlayProfileItems.IsEnabled = true;
            CreatePlayProfileButton.IsEnabled = _hasLoadedDlcs;
            DlcBatchActions.IsEnabled = _canManageDlcs;
        }
    }

    private async Task TryRestoreDlcStateAsync(
        string luaPath,
        IReadOnlyCollection<int> enabledIds,
        IReadOnlyCollection<int> disabledIds,
        bool enabledApplied,
        CancellationToken cancellationToken)
    {
        try
        {
            if (enabledApplied && enabledIds.Count > 0)
                await _dlcManagementService.SetEnabledAsync(luaPath, enabledIds, false, cancellationToken);
            if (disabledIds.Count > 0)
                await _dlcManagementService.SetEnabledAsync(luaPath, disabledIds, true, cancellationToken);
        }
        catch
        {
            // The DLC service keeps file backups. The result bar reports the original failure.
        }
    }

    private void LaunchWithProfile(GamePlayProfile profile)
    {
        if (_game is null) return;
        var steamPath = _steamPathService.GetCustomPath();
        if (string.IsNullOrWhiteSpace(steamPath) || !File.Exists(Path.Combine(steamPath, "steam.exe")))
            steamPath = _steamPathService.DetectSteamPath();
        var steamExe = string.IsNullOrWhiteSpace(steamPath) ? null : Path.Combine(steamPath, "steam.exe");
        if (string.IsNullOrWhiteSpace(steamExe) || !File.Exists(steamExe))
        {
            OpenSteamUri($"steam://run/{_game.AppId}");
            return;
        }

        var launchArguments = profile.LaunchArguments.Replace('\r', ' ').Replace('\n', ' ').Trim();
        Process.Start(new ProcessStartInfo
        {
            FileName = steamExe,
            Arguments = string.IsNullOrWhiteSpace(launchArguments)
                ? $"-applaunch {_game.AppId}"
                : $"-applaunch {_game.AppId} {launchArguments}",
            UseShellExecute = false,
            WorkingDirectory = steamPath
        });
    }

    private void ShowPlayProfileResult(bool success, string message)
    {
        PlayProfileResultBar.Visibility = Visibility.Visible;
        PlayProfileResultBar.Background = new SolidColorBrush(success
            ? Color.FromRgb(0x17, 0x2A, 0x29)
            : Color.FromRgb(0x2B, 0x20, 0x27));
        PlayProfileResultIcon.Glyph = success ? "\uE73E" : "\uEA39";
        PlayProfileResultIcon.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(0x62, 0xE4, 0x8B)
            : Color.FromRgb(0xFF, 0x78, 0x8C));
        PlayProfileResultText.Text = message;
        AnimateReveal(PlayProfileResultBar);
    }

    private async void ImportToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isToggling || _game is null || _mainViewModel is null || sender is not ToggleButton toggle)
            return;
        if (toggle.IsChecked == !_game.IsDisabled)
            return;

        _isToggling = true;
        toggle.IsEnabled = false;
        try
        {
            await _mainViewModel.ToggleGameDisableCommand.ExecuteAsync(_game);
            UpdateLocalDetails();
            if (!_mainViewModel.Games.Contains(_game))
                CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            toggle.IsChecked = !_game.IsDisabled;
            _mainViewModel.StatusMessage = $"切换入库状态失败：{ex.Message}";
        }
        finally
        {
            toggle.IsEnabled = true;
            _isToggling = false;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OverviewTab_Click(object sender, RoutedEventArgs e) =>
        NavigateDetailSection(OverviewSection, OverviewTabButton);

    private void SaveTab_Click(object sender, RoutedEventArgs e) =>
        NavigateDetailSection(SaveManagementSection, SaveTabButton);

    private void ShowSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SetSaveSectionVisible(true);
        NavigateDetailSection(SaveManagementSection, SaveTabButton);
    }

    private void DlcTab_Click(object sender, RoutedEventArgs e) =>
        NavigateDetailSection(DlcManagementSection, DlcTabButton);

    private void AchievementTab_Click(object sender, RoutedEventArgs e) =>
        NavigateDetailSection(AchievementSection, AchievementTabButton);

    private void NavigateDetailSection(FrameworkElement section, Button activeButton)
    {
        SetActiveDetailTab(activeButton);
        section.BringIntoView();
    }

    private void SetActiveDetailTab(Button activeButton)
    {
        if (OverviewTabButton is null) return;
        foreach (var button in new[] { OverviewTabButton, SaveTabButton, DlcTabButton, AchievementTabButton })
            button.Tag = ReferenceEquals(button, activeButton) ? "active" : null;
    }

    private void SetSaveSectionVisible(bool visible)
    {
        SaveManagementSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SaveTabButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && Equals(SaveTabButton.Tag, "active"))
            NavigateDetailSection(OverviewSection, OverviewTabButton);
    }

    private void DlcPlayProfileLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep DLC and schemes adjacent on wide windows; stack at narrow sizes
        // so neither the DLC controls nor profile commands are clipped.
        var stack = e.NewSize.Width < 820;
        DlcPlayProfileLayout.ColumnDefinitions[1].Width = new GridLength(stack ? 0 : 16);
        DlcPlayProfileLayout.ColumnDefinitions[2].Width = new GridLength(stack ? 0 : 288);
        DlcPlayProfileLayout.RowDefinitions[1].Height = stack ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(PlayProfileSection, stack ? 0 : 2);
        Grid.SetRow(PlayProfileSection, stack ? 1 : 0);
        PlayProfileSection.Margin = stack ? new Thickness(0, 16, 0, 0) : new Thickness(0);
        PlayProfileSection.BorderThickness = stack ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        PlayProfileSection.Padding = stack ? new Thickness(0, 16, 0, 0) : new Thickness(16, 0, 0, 0);
    }

    private void HeroMoreActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void GameDetailView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (HeroMoreActionsButton.ContextMenu?.IsOpen == true)
        {
            HeroMoreActionsButton.ContextMenu.IsOpen = false;
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenLibraryButton_Click(object sender, RoutedEventArgs e) =>
        OpenSteamUri($"steam://nav/games/details/{_game?.AppId}");

    private void LaunchGameButton_Click(object sender, RoutedEventArgs e) =>
        OpenSteamUri($"steam://run/{_game?.AppId}");

    private void OpenStoreButton_Click(object sender, RoutedEventArgs e) =>
        OpenSteamUri($"steam://store/{_game?.AppId}");

    private async void QueryDlcButton_Click(object sender, RoutedEventArgs e)
    {
        await ReloadDlcsAsync();
        SetDlcAdvancedVisible(true);
        SetActiveDetailTab(DlcTabButton);
        DlcManagementSection.BringIntoView();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || _mainViewModel is null || sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            await _mainViewModel.RefreshSingleGameCommand.ExecuteAsync(_game);
            UpdateLocalDetails();
            _detailCts?.Cancel();
            _detailCts?.Dispose();
            _detailCts = new CancellationTokenSource();
            await LoadDlcSummaryAsync(_detailCts.Token);
            await ReloadAchievementsAsync();
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = new CancellationTokenSource();
            await LoadSaveSummaryAsync(_saveCts.Token);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void RevealLuaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || string.IsNullOrWhiteSpace(_game.LuaFilePath)) return;
        if (File.Exists(_game.LuaFilePath))
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add("/select,");
            info.ArgumentList.Add(_game.LuaFilePath);
            Process.Start(info);
        }
        else if (Directory.Exists(Path.GetDirectoryName(_game.LuaFilePath)))
        {
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_game.LuaFilePath)!) { UseShellExecute = true });
        }
    }

    private void EditLuaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_game is not null && _mainViewModel is not null)
            _mainViewModel.EditGameCommand.Execute(_game);
    }

    private void OpenInstallFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_installPath) && Directory.Exists(_installPath))
            Process.Start(new ProcessStartInfo(_installPath) { UseShellExecute = true });
    }

    private async void PinLatestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || _mainViewModel is null) return;
        await _mainViewModel.PinToLatestCommand.ExecuteAsync(_game);
        UpdateLocalDetails();
    }

    private async void PinCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || _mainViewModel is null) return;
        await _mainViewModel.PinToCurrentCommand.ExecuteAsync(_game);
        UpdateLocalDetails();
    }

    private async void UnpinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || _mainViewModel is null) return;
        await _mainViewModel.UnpinGameCommand.ExecuteAsync(_game);
        UpdateLocalDetails();
    }

    private async void DeleteLuaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || _mainViewModel is null) return;
        var game = _game;
        await _mainViewModel.DeleteGameCommand.ExecuteAsync(game);
        if (!_mainViewModel.Games.Contains(game))
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReloadAchievementsAsync()
    {
        _achievementCts?.Cancel();
        _achievementCts?.Dispose();
        _achievementCts = new CancellationTokenSource();
        await LoadAchievementsAsync(_achievementCts.Token);
    }

    private async Task LoadAchievementsAsync(CancellationToken cancellationToken)
    {
        if (_game is null) return;
        var game = _game;
        AchievementLoadingRing.Visibility = Visibility.Visible;
        AchievementLoadingRing.IsActive = true;
        RefreshAchievementsButton.IsEnabled = false;
        AchievementBatchActions.IsEnabled = false;
        AchievementStatusText.Text = "正在读取本地成就缓存与 Steam 账号状态…";
        AchievementModeText.Text = "正在读取";
        AchievementModeBadge.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x3E));

        try
        {
            var result = await _steamAchievementService.LoadAsync(game.AppId, _installPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_game != game) return;

            _canManageAchievements = result.CanManage;
            _achievements.Clear();
            foreach (var achievement in result.Achievements)
            {
                achievement.CanManage = result.CanManage;
                _achievements.Add(achievement);
            }

            _achievementView = CollectionViewSource.GetDefaultView(_achievements);
            _achievementView.Filter = AchievementMatchesFilter;
            AchievementList.ItemsSource = _achievementView;
            AchievementStatusText.Text = result.StatusMessage;
            AchievementModeText.Text = result.CanManage ? "本地状态 · 可管理" : "只读缓存";
            AchievementModeText.Foreground = new SolidColorBrush(result.CanManage
                ? Color.FromRgb(0x62, 0xE4, 0x8B)
                : Color.FromRgb(0xB2, 0xBD, 0xC8));
            AchievementModeBadge.Background = new SolidColorBrush(result.CanManage
                ? Color.FromRgb(0x17, 0x3D, 0x30)
                : Color.FromRgb(0x2B, 0x35, 0x40));
            AchievementBatchActions.IsEnabled = result.CanManage;
            UpdateAchievementSummary();
            UpdateAchievementEmptyState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _canManageAchievements = false;
            AchievementModeText.Text = "读取失败";
            AchievementStatusText.Text = $"成就读取失败：{ex.GetBaseException().Message}";
            ShowAchievementResult(false, AchievementStatusText.Text);
        }
        finally
        {
            if (_game == game)
            {
                AchievementLoadingRing.IsActive = false;
                AchievementLoadingRing.Visibility = Visibility.Collapsed;
                RefreshAchievementsButton.IsEnabled = true;
            }
        }
    }

    private bool AchievementMatchesFilter(object value)
    {
        if (value is not SteamAchievement achievement) return false;
        var query = AchievementSearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query)
            && !achievement.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            && !achievement.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            && !achievement.ApiName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        return AchievementFilterCombo.SelectedIndex switch
        {
            1 => !achievement.IsUnlocked,
            2 => achievement.IsUnlocked,
            3 => achievement.IsHidden,
            _ => true
        };
    }

    private void AchievementSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (EmptyAchievementsPanel is null) return;
        _achievementView?.Refresh();
        UpdateAchievementEmptyState();
    }

    private void AchievementFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmptyAchievementsPanel is null) return;
        _achievementView?.Refresh();
        UpdateAchievementEmptyState();
    }

    private void UpdateAchievementSummary()
    {
        var total = _achievements.Count;
        var unlocked = _achievements.Count(item => item.IsUnlocked);
        AchievementCountText.Text = $"{unlocked} / {total} 已解锁";
        AchievementProgressBar.Maximum = Math.Max(1, total);
        AchievementProgressBar.Value = unlocked;
        UpdateAchievementPreview();
    }

    private void UpdateAchievementPreview()
    {
        if (AchievementFeaturedPanel is null) return;
        var unlocked = _achievements
            .Where(item => item.IsUnlocked)
            .OrderByDescending(item => item.UnlockTimeUnix)
            .ThenBy(item => item.Name)
            .ToList();
        var locked = _achievements.Where(item => !item.IsUnlocked).OrderBy(item => item.Name).ToList();

        var featured = unlocked.FirstOrDefault();
        AchievementFeaturedPanel.DataContext = featured;
        AchievementFeaturedPanel.Visibility = featured is null ? Visibility.Collapsed : Visibility.Visible;

        _unlockedAchievementPreview.Clear();
        foreach (var item in unlocked.Skip(featured is null ? 0 : 1).Take(6))
            _unlockedAchievementPreview.Add(item);
        _lockedAchievementPreview.Clear();
        foreach (var item in locked.Take(6)) _lockedAchievementPreview.Add(item);

        var unlockedRemaining = Math.Max(0, unlocked.Count - (featured is null ? 0 : 1) - _unlockedAchievementPreview.Count);
        var lockedRemaining = Math.Max(0, locked.Count - _lockedAchievementPreview.Count);
        AchievementUnlockedRemainingText.Text = $"+{unlockedRemaining}";
        AchievementUnlockedRemainingBadge.Visibility = unlockedRemaining > 0 ? Visibility.Visible : Visibility.Collapsed;
        AchievementLockedRemainingText.Text = $"+{lockedRemaining}";
        AchievementLockedRemainingBadge.Visibility = lockedRemaining > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAchievementEmptyState()
    {
        if (EmptyAchievementsPanel is null) return;
        var empty = _achievementView?.IsEmpty != false;
        EmptyAchievementsPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshAchievementsButton_Click(object sender, RoutedEventArgs e) =>
        await ReloadAchievementsAsync();

    private void ToggleAchievementManagement_Click(object sender, RoutedEventArgs e) =>
        SetAchievementAdvancedVisible(AchievementAdvancedListContainer.Visibility != Visibility.Visible);

    private void SetAchievementAdvancedVisible(bool visible, bool animate = true)
    {
        if (AchievementAdvancedListContainer is null) return;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AchievementAdvancedFilterBar.Visibility = visibility;
        AchievementBatchActions.Visibility = visibility;
        AchievementAdvancedListContainer.Visibility = visibility;
        ToggleAchievementManagementButton.Content = visible ? "收起完整管理" : "查看全部成就";
        if (visible && animate) AnimateReveal(AchievementAdvancedListContainer);
    }

    private async void AchievementActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SteamAchievement achievement } || _isAchievementOperationRunning)
            return;
        var unlock = !achievement.IsUnlocked;
        if (!unlock)
        {
            var confirmed = await ShowConfirmAsync(
                "重锁成就",
                $"确定将“{achievement.Name}”设为未解锁吗？\n\n操作前会自动备份本地成就状态。",
                "确认重锁");
            if (!confirmed) return;
        }
        await ExecuteAchievementOperationAsync(new[] { achievement }, unlock);
    }

    private async void UnlockSelectedAchievements_Click(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedAchievementsAsync(unlocked: true);

    private async void LockSelectedAchievements_Click(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedAchievementsAsync(unlocked: false);

    private async Task ExecuteSelectedAchievementsAsync(bool unlocked)
    {
        var selected = _achievements.Where(item => item.IsSelected && item.IsUnlocked != unlocked).ToList();
        if (selected.Count == 0)
        {
            ShowAchievementResult(false, unlocked ? "请先选择需要解锁的成就" : "请先选择需要重锁的成就");
            return;
        }

        var confirmed = await ShowConfirmAsync(
            unlocked ? "解锁选中成就" : "重锁选中成就",
            $"即将{(unlocked ? "解锁" : "重锁")} {selected.Count} 项成就，并提交到当前 Steam 账号。\n\n操作前会自动备份本地状态，是否继续？",
            unlocked ? "开始解锁" : "开始重锁");
        if (confirmed) await ExecuteAchievementOperationAsync(selected, unlocked);
    }

    private async void UnlockAllAchievements_Click(object sender, RoutedEventArgs e) =>
        await ExecuteAllAchievementsAsync(unlocked: true);

    private async void LockAllAchievements_Click(object sender, RoutedEventArgs e) =>
        await ExecuteAllAchievementsAsync(unlocked: false);

    private async Task ExecuteAllAchievementsAsync(bool unlocked)
    {
        var targets = _achievements.Where(item => item.IsUnlocked != unlocked).ToList();
        if (targets.Count == 0)
        {
            ShowAchievementResult(true, unlocked ? "当前所有成就均已解锁" : "当前所有成就均为未解锁状态");
            return;
        }

        var action = unlocked ? "全部解锁" : "全部重锁";
        var firstConfirm = await ShowConfirmAsync(
            action,
            $"此操作将修改 {targets.Count} 项成就，并同步到当前 Steam 账号。\n\n部分游戏可能会根据服务器数据重新覆盖状态。",
            "继续");
        if (!firstConfirm) return;
        var finalConfirm = await ShowConfirmAsync(
            $"最后确认：{action}",
            $"确认对“{_game?.GameName}”执行{action}吗？操作开始后请勿关闭 Steam。",
            $"确认{action}");
        if (finalConfirm) await ExecuteAchievementOperationAsync(targets, unlocked);
    }

    private async Task ExecuteAchievementOperationAsync(IReadOnlyCollection<SteamAchievement> targets, bool unlocked)
    {
        if (_game is null || !_canManageAchievements || _isAchievementOperationRunning) return;
        _isAchievementOperationRunning = true;
        AchievementBatchActions.IsEnabled = false;
        RefreshAchievementsButton.IsEnabled = false;
        foreach (var item in _achievements) item.CanManage = false;
        foreach (var item in targets) item.IsBusy = true;
        ShowAchievementResult(true, unlocked ? "正在向 Steam 提交解锁状态…" : "正在向 Steam 提交重锁状态…");

        try
        {
            var result = await _steamAchievementService.SetAllAchievementsAsync(
                _game.AppId,
                _installPath,
                targets.Select(item => item.ApiName).ToArray(),
                unlocked,
                _achievementCts?.Token ?? CancellationToken.None);
            ShowAchievementResult(result.Success, result.Message);
            if (result.Success)
                await ReloadAchievementsAsync();
        }
        catch (OperationCanceledException)
        {
            ShowAchievementResult(false, "成就操作已取消");
        }
        finally
        {
            foreach (var item in targets) item.IsBusy = false;
            foreach (var item in _achievements) item.CanManage = _canManageAchievements;
            AchievementBatchActions.IsEnabled = _canManageAchievements;
            RefreshAchievementsButton.IsEnabled = true;
            _isAchievementOperationRunning = false;
        }
    }

    private void OpenAchievementBackups_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        var backupPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "AchievementBackups", _game.AppId.ToString());
        if (!Directory.Exists(backupPath))
        {
            ShowAchievementResult(false, "当前游戏还没有成就备份；首次修改前会自动创建");
            return;
        }
        Process.Start(new ProcessStartInfo(backupPath) { UseShellExecute = true });
    }

    private void ShowAchievementResult(bool success, string message)
    {
        AchievementResultBar.Visibility = Visibility.Visible;
        AchievementResultBar.Background = new SolidColorBrush(success
            ? Color.FromRgb(0x17, 0x2A, 0x29)
            : Color.FromRgb(0x2B, 0x20, 0x27));
        AchievementResultIcon.Glyph = success ? "\uE73E" : "\uEA39";
        AchievementResultIcon.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(0x62, 0xE4, 0x8B)
            : Color.FromRgb(0xFF, 0x78, 0x8C));
        AchievementResultText.Text = message;
    }

    private static async Task<bool> ShowConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 430
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static void AnimateReveal(FrameworkElement element)
    {
        MotionBehavior.PlayEntrance(element, fromY: -6, pace: AppMotion.Pace.Content);
    }

    private static string? TryResolveInstallPath(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            return null;
        try
        {
            var content = File.ReadAllText(manifestPath);
            var match = Regex.Match(content, "\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var steamAppsPath = Path.GetDirectoryName(manifestPath);
            return string.IsNullOrWhiteSpace(steamAppsPath)
                ? null
                : Path.Combine(steamAppsPath, "common", match.Groups[1].Value.Replace("\\\\", "\\"));
        }
        catch
        {
            return null;
        }
    }

    private static void OpenSteamUri(string uri)
    {
        if (uri.EndsWith("/", StringComparison.Ordinal)) return;
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.00} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private sealed record DepotRow(int DepotId, string Manifest, string KeyStatus, string PinStatus);
}
