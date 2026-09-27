using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern;
using Microsoft.Win32;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISteamPathService _steamPathService;
    private readonly ILuaFileManager _luaFileManager;
    private readonly ISettingsService _settingsService;
    private readonly ISteamApiService _steamApiService;
    private readonly IHttpClientProvider _httpClientProvider;
    private AppSettings _settings;

    [ObservableProperty]
    private string _steamPath = string.Empty;

    [ObservableProperty]
    private bool _isAutoRefreshEnabled = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _saveBackupPath = string.Empty;

    [ObservableProperty]
    private int _saveBackupRetention = 20;

    [ObservableProperty]
    private int _selectedCdnIndex;

    [ObservableProperty]
    private bool _isSpeedTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestProgressText))]
    private int _speedTestProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestProgressText))]
    private int _speedTestTotal;

    public string SpeedTestProgressText => $"{SpeedTestProgress}/{SpeedTestTotal}";

    [ObservableProperty]
    private string _selectedBackdrop = "Acrylic10";

    public record BackdropOption(string Display, string Value);

    public List<BackdropOption> BackdropOptions { get; } = new()
    {
        new("亚克力", "Acrylic10"),
        new("云母", "Mica"),
        new("无", "None"),
    };

    public record ProxyModeOption(string Display, string Value);

    public List<ProxyModeOption> ProxyModeOptions { get; } = new()
    {
        new("自动检测", "Auto"),
        new("Windows 系统代理", "System"),
        new("手动 HTTP / SOCKS5", "Manual"),
        new("强制直连", "Direct"),
    };

    public record ThemeOption(string Display, string Value);

    public List<ThemeOption> ThemeOptions { get; } = new()
    {
        new("跟随系统", "System"),
        new("深色模式", "Dark"),
        new("浅色模式", "Light"),
    };

    public List<CdnEndpoint> CdnEndpoints { get; } = CdnEndpoint.Defaults;

    public ObservableCollection<SpeedTestItem> SpeedTestResults { get; } = new();

    public SettingsViewModel(ISteamPathService steamPathService, ILuaFileManager luaFileManager,
        ISettingsService settingsService, ISteamApiService steamApiService,
        IHttpClientProvider httpClientProvider)
    {
        _steamPathService = steamPathService;
        _luaFileManager = luaFileManager;
        _settingsService = settingsService;
        _steamApiService = steamApiService;
        _httpClientProvider = httpClientProvider;
        _settings = settingsService.Load();

        SteamPath = _settings.SteamPath;
        IsAutoRefreshEnabled = _settings.AutoRefreshEnabled;
        IsCardRefreshVisible = _settings.IsCardRefreshVisible;
        IsShowTrainerSections = _settings.ShowTrainerSections;
        IsShowCopyLogButton = _settings.ShowCopyLogButton;
        IsAnonymousUsageStatisticsEnabled = _settings.AnonymousUsageStatisticsEnabled;
        IsDisableCloudForImportedGamesEnabled = _settings.DisableCloudForImportedGames;

        SelectedTheme = _settings.SelectedTheme;
        SelectedCdnIndex = Math.Clamp(_settings.SelectedCdnIndex, 0, CdnEndpoints.Count - 1);
        _selectedBackdrop = _settings.SelectedBackdrop;
        DownloadMode = _settings.DownloadMode;
        KeyFolderPath = _settings.KeyFolderPath;
        ProxyMode = string.IsNullOrWhiteSpace(_settings.ProxyMode) ? "Auto" : _settings.ProxyMode;
        ManualProxy = _settings.ManualProxy;
        SaveBackupPath = _settings.SaveBackupPath;
        SaveBackupRetention = Math.Clamp(_settings.SaveBackupRetention, 1, 200);

        _steamApiService.CdnAutoSwitched += OnCdnAutoSwitched;

        if (string.IsNullOrEmpty(SteamPath))
        {
            var detectedPath = steamPathService.DetectSteamPath();
            SteamPath = detectedPath ?? "未检测到Steam";
        }

    }

    private void OnCdnAutoSwitched(int newIndex)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (newIndex >= 0 && newIndex < CdnEndpoints.Count)
            {
                SelectedCdnIndex = newIndex;
                StatusMessage = $"封面节点已自动切换: {CdnEndpoints[newIndex].Name}";
            }
        });
    }

    private DispatcherTimer? _statusTimer;

    partial void OnStatusMessageChanged(string value)
    {
        _statusTimer?.Stop();
        if (string.IsNullOrEmpty(value)) return;

        _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick -= StatusTimer_Tick;
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        StatusMessage = string.Empty;
    }

    public void Dispose()
    {
        _steamApiService.CdnAutoSwitched -= OnCdnAutoSwitched;
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= StatusTimer_Tick;
            _statusTimer = null;
        }
    }

    partial void OnSelectedCdnIndexChanged(int value)
    {
        _settings.SelectedCdnIndex = value;
        _settingsService.Save(_settings);
        _steamApiService.UpdateCdnPreference(value);
        StatusMessage = $"封面节点已切换: {CdnEndpoints[value].Name}";
    }

    partial void OnProxyModeChanged(string value)
    {
        _settings.ProxyMode = value;
        _settingsService.Save(_settings);
        _httpClientProvider.Reset();
        StatusMessage = value switch
        {
            "System" => "已切换为 Windows 系统代理",
            "Manual" => "已切换为手动代理",
            "Direct" => "已切换为强制直连",
            _ => "已切换为自动检测代理"
        };
    }

    partial void OnManualProxyChanged(string value)
    {
        _settings.ManualProxy = value.Trim();
        _settingsService.Save(_settings);
        _httpClientProvider.Reset();
    }

    partial void OnSelectedThemeChanged(string value)
    {
        _settings.SelectedTheme = value;
        _settingsService.Save(_settings);
        var isLight = false;
        switch (value)
        {
            case "Dark":
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                break;
            case "Light":
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
                isLight = true;
                break;
            default:
                isLight = GetSystemTheme() == ApplicationTheme.Light;
                ThemeManager.Current.ApplicationTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
                break;
        }
        StatusMessage = value switch
        {
            "Dark" => "已切换为深色模式",
            "Light" => "已切换为浅色模式",
            _ => "已跟随系统主题",
        };
    }

    private static ApplicationTheme GetSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1 ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }
        catch { }
        return ApplicationTheme.Dark;
    }

    partial void OnIsCardRefreshVisibleChanged(bool value)
    {
        _settings.IsCardRefreshVisible = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "刷新游戏信息入口已显示" : "刷新游戏信息入口已隐藏";
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        _settings.AutoRefreshEnabled = value;
        _settingsService.Save(_settings);

        if (value)
            _luaFileManager.StartWatching();
        else
            _luaFileManager.StopWatching();
        StatusMessage = value ? "自动监控已开启" : "自动监控已关闭";
    }

    partial void OnSaveBackupPathChanged(string value)
    {
        _settings.SaveBackupPath = value.Trim();
        _settingsService.Save(_settings);
    }

    partial void OnSaveBackupRetentionChanged(int value)
    {
        value = Math.Clamp(value, 1, 200);
        if (_saveBackupRetention != value)
        {
            _saveBackupRetention = value;
            OnPropertyChanged(nameof(SaveBackupRetention));
        }
        _settings.SaveBackupRetention = value;
        _settingsService.Save(_settings);
        StatusMessage = $"每个游戏最多保留 {value} 个存档备份";
    }

    [RelayCommand]
    private void BrowseSaveBackupPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择存档备份目录",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(SaveBackupPath) && Directory.Exists(SaveBackupPath))
            dialog.InitialDirectory = SaveBackupPath;
        if (dialog.ShowDialog() != true) return;
        SaveBackupPath = dialog.FolderName;
        StatusMessage = $"存档备份目录已设置为：{SaveBackupPath}";
    }

    [RelayCommand]
    private void ResetSaveBackupPath()
    {
        SaveBackupPath = string.Empty;
        StatusMessage = "存档备份目录已恢复为应用默认目录";
    }

    partial void OnSelectedBackdropChanged(string value)
    {
        _settings.SelectedBackdrop = value;
        _settingsService.Save(_settings);
        StatusMessage = value switch
        {
            "Acrylic10" => "背景效果已切换为亚克力",
            "Mica" => "背景效果已切换为云母",
            "None" => "背景效果已关闭",
            _ => ""
        };
    }

    [RelayCommand]
    private void BrowseSteamPath()
    {
        var dialog = new OpenFileDialog
        {
            FileName = "steam.exe",
            Filter = "Steam可执行文件|steam.exe",
            Title = "选择Steam安装路径"
        };
        if (dialog.ShowDialog() == true)
        {
            var dir = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(dir))
            {
                SteamPath = dir;
                _steamPathService.SetCustomPath(dir);
                _settings.SteamPath = dir;
                _settingsService.Save(_settings);
                StatusMessage = $"Steam路径已设置为: {dir}";
            }
        }
    }

    [RelayCommand]
    private void ResetSteamPath()
    {
        _steamPathService.SetCustomPath(string.Empty);
        var detectedPath = _steamPathService.DetectSteamPath();
        SteamPath = detectedPath ?? "未检测到Steam";
        _settings.SteamPath = string.Empty;
        _settingsService.Save(_settings);
        StatusMessage = "已重置为自动检测路径";
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            if (!Directory.Exists(cacheDir))
            {
                StatusMessage = "没有需要清理的缓存";
                return;
            }

            var lockedFiles = new List<string>();
            var deletedCount = 0;

            await Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                    catch (IOException)
                    {
                        lockedFiles.Add(Path.GetFileName(file));
                    }
                    catch { }
                }

                foreach (var dir in Directory.GetDirectories(cacheDir))
                {
                    try { Directory.Delete(dir, true); }
                    catch { }
                }

                Directory.CreateDirectory(Path.Combine(cacheDir, "covers"));
            });

            if (lockedFiles.Count > 0)
                StatusMessage = $"缓存已清理(跳过{lockedFiles.Count}个占用文件)";
            else
                StatusMessage = $"缓存已清理(共{deletedCount}个文件)";
        }
        catch (Exception ex) { StatusMessage = $"清理失败: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenLuaFolder()
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (!string.IsNullOrEmpty(luaFolder) && Directory.Exists(luaFolder))
            Process.Start(new ProcessStartInfo { FileName = luaFolder, UseShellExecute = true });
        else StatusMessage = "Lua文件夹不存在";
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        var steamDir = SteamPath;
        if (!string.IsNullOrEmpty(steamDir) && Directory.Exists(steamDir))
            Process.Start(new ProcessStartInfo { FileName = steamDir, UseShellExecute = true });
        else
            StatusMessage = "Steam路径不存在或未设置";
    }

    [RelayCommand]
    private void OpenBinStatsFolder()
    {
        var steamDir = SteamPath;
        if (string.IsNullOrEmpty(steamDir) || !Directory.Exists(steamDir))
        {
            StatusMessage = "Steam路径不存在或未设置";
            return;
        }
        var statsDir = Path.Combine(steamDir, "appcache", "stats");
        Directory.CreateDirectory(statsDir);
        Process.Start(new ProcessStartInfo { FileName = statsDir, UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenCacheFolder()
    {
        var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        if (!Directory.Exists(cacheDir))
            Directory.CreateDirectory(cacheDir);
        Process.Start(new ProcessStartInfo { FileName = cacheDir, UseShellExecute = true });
    }

    [RelayCommand]
    private async Task TestCdnSpeedAsync()
    {
        if (IsSpeedTesting) return;
        IsSpeedTesting = true;
        SpeedTestResults.Clear();
        SpeedTestTotal = CdnEndpoint.Defaults.Count;
        SpeedTestProgress = 0;
        StatusMessage = "正在测试所有CDN节点...";

        try
        {
            var progress = new Progress<(string Name, long LatencyMs, bool IsSuccess)>(result =>
            {
                SpeedTestProgress++;
                SpeedTestResults.Add(new SpeedTestItem
                {
                    Name = result.Name,
                    LatencyMs = result.LatencyMs,
                    IsSuccess = result.IsSuccess
                });
            });

            var results = await _steamApiService.TestCdnSpeedAsync(progress);

            var best = SpeedTestResults.Where(r => r.IsSuccess).OrderBy(r => r.LatencyMs).FirstOrDefault();
            if (best != null)
                StatusMessage = $"测速完成，最快节点: {best.Name} ({best.LatencyMs}ms)";
            else
                StatusMessage = "所有节点均不可达";
        }
        catch (Exception ex)
        {
            StatusMessage = $"测速失败: {ex.Message}";
        }
        finally
        {
            IsSpeedTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestNetworkAsync()
    {
        if (IsNetworkTesting) return;
        if (ProxyMode == "Manual" && !IsValidProxyAddress(ManualProxy))
        {
            StatusMessage = "手动代理地址无效，请填写如 http://127.0.0.1:7890 或 socks5://127.0.0.1:7890";
            return;
        }

        IsNetworkTesting = true;
        StatusMessage = "正在测试清单下载线路...";
        _httpClientProvider.Reset();
        try
        {
            var probes = new[]
            {
                ("SteamCMD", "https://api.steamcmd.net/v1/info/570"),
                ("密钥源", "https://api.993499094.xyz/depotkeys.json"),
                ("远程存储", "https://steamgames554.s3.us-east-1.amazonaws.com/0.zip")
            };
            var results = new List<string>();
            foreach (var (name, url) in probes)
            {
                var reachable = await ProbeEndpointAsync(name, url);
                results.Add($"{name} {(reachable ? "✓" : "✕")}");
            }
            StatusMessage = "线路测试：" + string.Join(" / ", results);
        }
        finally
        {
            IsNetworkTesting = false;
        }
    }

    private async Task<bool> ProbeEndpointAsync(string name, string url)
    {
        try
        {
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                $"network-test-{name}",
                TimeSpan.FromSeconds(12),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, url);
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                });
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidProxyAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var address = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
               && uri.Port > 0
               && uri.Scheme is "http" or "https" or "socks4" or "socks5";
    }

    [ObservableProperty]
    private bool _isCardRefreshVisible = true;

    [ObservableProperty]
    private string _selectedTheme = "System";

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualProxyMode))]
    private string _proxyMode = "Auto";

    public bool IsManualProxyMode => ProxyMode == "Manual";

    [ObservableProperty]
    private string _manualProxy = string.Empty;

    [ObservableProperty]
    private bool _isNetworkTesting;

    [ObservableProperty]
    private string _downloadMode = "DepotKey";

    [ObservableProperty]
    private bool _isShowTrainerSections = true;

    [ObservableProperty]
    private bool _isShowCopyLogButton;

    [ObservableProperty]
    private bool _isAnonymousUsageStatisticsEnabled = true;

    [ObservableProperty]
    private bool _isDisableCloudForImportedGamesEnabled = true;

    partial void OnIsDisableCloudForImportedGamesEnabledChanged(bool value)
    {
        _settings.DisableCloudForImportedGames = value;
        _settingsService.Save(_settings);
        StatusMessage = value
            ? "新入库游戏将默认关闭 Steam 云同步"
            : "新入库游戏将保留 Steam 云同步";
    }

    partial void OnIsAnonymousUsageStatisticsEnabledChanged(bool value)
    {
        _settings.AnonymousUsageStatisticsEnabled = value;
        _settingsService.Save(_settings);
        StatusMessage = value
            ? "匿名使用统计已开启"
            : "匿名使用统计已关闭";
    }

    partial void OnIsShowCopyLogButtonChanged(bool value)
    {
        _settings.ShowCopyLogButton = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "日志复制按钮已显示" : "日志复制按钮已隐藏";
    }

    partial void OnIsShowTrainerSectionsChanged(bool value)
    {
        _settings.ShowTrainerSections = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "修改器推荐栏目已显示" : "修改器推荐栏目已隐藏";
    }

    [ObservableProperty]
    private bool _isServiceInstalled;

    [ObservableProperty]
    private string _keyFolderPath = string.Empty;

    partial void OnDownloadModeChanged(string value)
    {
        _settings.DownloadMode = value;
        _settingsService.Save(_settings);
        StatusMessage = value switch
        {
            "Remote" => "已切换为远程清单仓库",
            "DepotKey" => "已切换为本地缓存仓库 V1",
            "DepotKey2" => "已切换为本地缓存仓库 V2",
            _ => ""
        };
    }

    partial void OnKeyFolderPathChanged(string value)
    {
        _settings.KeyFolderPath = value;
        _settingsService.Save(_settings);
    }
}

public class SpeedTestItem
{
    public string Name { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
    public bool IsSuccess { get; set; }
    public string StatusText => IsSuccess ? $"{LatencyMs}ms" : "失败";
    public string ColorCode => IsSuccess ? LatencyMs switch
    {
        <= 200 => "#4CAF50",
        <= 500 => "#FF9800",
        _ => "#F44336"
    } : "#F44336";
}
