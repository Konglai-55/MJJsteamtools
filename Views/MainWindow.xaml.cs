using System.Diagnostics;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using SteamLuaManager.Controls;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    private readonly string[] _navOrder = ["Home", "ScriptDownload", "Extraction", "SaveVault", "FamilyLibrary", "Trainer", "UtilityTools", "Settings", "About"];
    private string _prevTag = "Home";

    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly ISteamPathService _steamPathService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly ScriptDownloadViewModel _scriptDownloadViewModel;
    private readonly ExtractionViewModel _extractionViewModel;
    private readonly TrainerViewModel _trainerViewModel;
    private readonly HomeView _homeView;
    private readonly SaveVaultView _saveVaultView;
    private readonly FamilyLibraryView _familyLibraryView;
    private readonly SettingsView _settingsView;
    private readonly ScriptDownloadView _scriptDownloadView;
    private readonly ExtractionView _extractionView;
    private readonly TrainerView _trainerView;
    private readonly UtilityToolsView _utilityToolsView;
    private readonly SteamStorageManagerView _steamStorageManagerView;
    private readonly CloudSyncRescueView _cloudSyncRescueView;
    private readonly AboutView _aboutView;
    private readonly SteamStorageService _steamStorageService;
    private readonly IOpenSteamToolService _openSteamToolService;
    private readonly IUpdateService _updateService;
    private readonly ISaveAutoBackupService _saveAutoBackupService;
    private bool _isCheckingForUpdates;
    private CancellationTokenSource? _updateDownloadCts;
    private CancellationTokenSource? _kernelCts;
    private int? _pendingSaveVaultAppId;
    private bool _isPaneOpen = true;
    private DateTime _compactIndicatorTrackingUntil;
    private bool _compactIndicatorTracking;

    private FrameworkElement? _accountMenuTrigger;
    private int _accountMenuTransitionVersion;

    public MainWindow(MainViewModel viewModel, SettingsViewModel settingsViewModel, ScriptDownloadViewModel scriptDownloadViewModel, ExtractionViewModel extractionViewModel, TrainerViewModel trainerViewModel, ISettingsService settingsService, ISteamPathService steamPathService, IOpenSteamToolService openSteamToolService, IUpdateService updateService, ISteamDepotService steamDepotService, ISteamApiService steamApiService, ISteamAchievementService steamAchievementService, IDlcManagementService dlcManagementService, ISaveVaultService saveVaultService, ISaveAutoBackupService saveAutoBackupService, IFamilyLibraryService familyLibraryService, ICloudSyncRescueService cloudSyncRescueService, ISteamCloudPreferenceService steamCloudPreferenceService, IGamePlayProfileService gamePlayProfileService)
    {
        InitializeComponent();
        _openSteamToolService = openSteamToolService;
        _updateService = updateService;
        _saveAutoBackupService = saveAutoBackupService;
        _viewModel = viewModel;
        _settingsViewModel = settingsViewModel;
        _scriptDownloadViewModel = scriptDownloadViewModel;
        _extractionViewModel = extractionViewModel;
        _trainerViewModel = trainerViewModel;
        _settingsService = settingsService;
        _steamPathService = steamPathService;
        _steamStorageService = new SteamStorageService(_steamPathService, steamApiService);
        DataContext = _viewModel;

        var iconUri = new Uri("pack://application:,,,/Assets/brand-app.png");
        var decoder = BitmapDecoder.Create(iconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bestFrame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
        Icon = bestFrame;

        _homeView = new HomeView(_steamPathService, steamDepotService, steamApiService, steamAchievementService, dlcManagementService, saveVaultService, saveAutoBackupService, gamePlayProfileService, steamCloudPreferenceService) { DataContext = _viewModel };
        _saveVaultView = new SaveVaultView(saveVaultService, saveAutoBackupService, _viewModel);
        _familyLibraryView = new FamilyLibraryView(familyLibraryService);
        _familyLibraryView.ImportRequested = FamilyLibraryView_ImportRequested;
        _homeView.OpenSaveVaultRequested += HomeView_OpenSaveVaultRequested;
        _settingsView = new SettingsView { DataContext = settingsViewModel };
        _scriptDownloadView = new ScriptDownloadView { DataContext = scriptDownloadViewModel };
        _extractionView = new ExtractionView { DataContext = extractionViewModel };
        _trainerView = new TrainerView { DataContext = trainerViewModel };
        _utilityToolsView = new UtilityToolsView
        {
            ClearDownloadCacheRequested = ClearSteamDownloadCacheAsync,
            OpenStorageManagerRequested = OpenSteamStorageManager,
            OpenCloudSyncRescueRequested = OpenCloudSyncRescue
        };
        _steamStorageManagerView = new SteamStorageManagerView(_steamStorageService)
        {
            BackRequested = ReturnToUtilityTools,
            CleanupRequested = SteamStorageManagerView_CleanupRequested,
            DetailDeleteRequested = SteamStorageManagerView_DetailDeleteRequested
        };
        _cloudSyncRescueView = new CloudSyncRescueView(cloudSyncRescueService, steamCloudPreferenceService)
        {
            BackRequested = ReturnToUtilityTools,
            OpenSaveVaultRequested = HomeView_OpenSaveVaultRequested
        };
        _aboutView = new AboutView();
        _aboutView.CheckForUpdatesRequested = () => CheckForUpdatesAsync(showNoUpdateMessage: true);
        ContentTransition.Content = _homeView;
        _settingsView.InstallOpenSteamToolRequested += SettingsView_InstallOpenSteamToolRequested;
        _settingsView.UninstallOpenSteamToolRequested += SettingsView_UninstallOpenSteamToolRequested;
        _settingsView.UpdateOpenSteamToolStatus(_openSteamToolService.IsInstalled);

        settingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
        Closed += MainWindow_Closed;
    }

    private void SettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsViewModel svm) return;
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.SelectedBackdrop):
                UpdateBackdrop(svm.SelectedBackdrop);
                break;
            case nameof(SettingsViewModel.IsCardRefreshVisible):
                _viewModel.IsCardRefreshVisible = svm.IsCardRefreshVisible;
                break;
            case nameof(SettingsViewModel.SelectedTheme):
                UpdateBackdropTheme(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
                UpdateBackdrop(svm.SelectedBackdrop);
                break;
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;
        _settingsView.InstallOpenSteamToolRequested -= SettingsView_InstallOpenSteamToolRequested;
        _settingsView.UninstallOpenSteamToolRequested -= SettingsView_UninstallOpenSteamToolRequested;
        _updateDownloadCts?.Cancel();
        _updateDownloadCts?.Dispose();
        _updateDownloadCts = null;
        _saveAutoBackupService.Dispose();
        if (_viewModel is IDisposable viewModelDisposable)
            viewModelDisposable.Dispose();
        if (_settingsViewModel is IDisposable settingsViewModelDisposable)
            settingsViewModelDisposable.Dispose();
    }

    private void RefreshTitle()
    {
        var status = _steamPathService.DetectSteamToolType() switch
        {
            SteamToolType.OpenSteamTool => "使用 OpenSteamTool 内核",
            SteamToolType.SteamTools => "检测到不适配的 SteamTools",
            _ => "未安装 OpenSteamTool"
        };
        Title = $"MJJsteamtools - {status}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LoadedCommand.CanExecute(null))
            await _viewModel.LoadedCommand.ExecuteAsync(null);

        _saveAutoBackupService.Start(() => _viewModel.AllGames.ToArray());

        RefreshTitle();
        RefreshTopAccountDisplay();

        var settings = _settingsService.Load();
        UpdateBackdropTheme(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
        UpdateBackdrop(settings.SelectedBackdrop);
        _settingsViewModel.SelectedBackdrop = settings.SelectedBackdrop;
        _settingsView.UpdateOpenSteamToolStatus(_openSteamToolService.IsInstalled);

        switch (_viewModel.OpenSteamToolStatus)
        {
            case "未安装 OpenSteamTool":
                await ShowModernDialogAsync(
                    "未安装 OpenSteamTool",
                    "未检测到 OpenSteamTool，本软件目前仅适配 OpenSteamTool。\n\n" +
                    "请确保已在 Steam 目录中正确安装 OpenSteamTool 后再使用。\n\n" +
                    "可在「设置 → 基本设置 → OpenSteamTool 管理」中进行安装。");
                break;

            case "检测到不适配的 SteamTools":
                await ShowModernDialogAsync(
                    "不适配的 SteamTools",
                    "检测到 SteamTools（闭源），该内核与本软件不适配。\n\n" +
                    "本软件目前仅适配 OpenSteamTool（开源内核）。\n" +
                    "请卸载 SteamTools 后安装 OpenSteamTool 再使用。");
                break;
        }

        HomeItem.IsSelected = true;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _ = CheckForUpdatesAsync(showNoUpdateMessage: false);
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        RefreshTopAccountDisplay();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (_settingsViewModel.SelectedTheme != "System") return;

        Dispatcher.Invoke(() =>
        {
            var isLight = GetSystemIsLightTheme();
            ThemeManager.Current.ApplicationTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
            UpdateBackdropTheme(isLight);
        });
    }

    private static bool GetSystemIsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1;
        }
        catch { }
        return false;
    }

    private async Task ShowModernDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ShowModernConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task CheckForUpdatesAsync(bool showNoUpdateMessage)
    {
        if (_isCheckingForUpdates)
            return;

        _isCheckingForUpdates = true;
        try
        {
            var release = await _updateService.CheckForUpdatesAsync();
            if (release is null)
            {
                _aboutView.SetUpdateStatus("GitHub 仓库暂未发布 Release");
                if (showNoUpdateMessage)
                    await ShowModernDialogAsync("检查更新", "仓库中暂时没有已发布的 Release。");
                return;
            }

            if (!release.IsUpdateAvailable)
            {
                _aboutView.SetUpdateStatus($"已是最新版本 · {FormatVersion(release.CurrentVersion)}");
                if (showNoUpdateMessage)
                    await ShowModernDialogAsync("已是最新版本", $"当前版本：{FormatVersion(release.CurrentVersion)}\n最新版本：{release.TagName}");
                return;
            }

            _aboutView.SetUpdateStatus($"发现新版本 · {release.TagName}");
            await ShowUpdateAvailableDialogAsync(release);
        }
        catch (Exception ex)
        {
            _aboutView.SetUpdateStatus("检查失败，请确认网络或代理设置");
            if (showNoUpdateMessage)
                await ShowModernDialogAsync("检查更新失败", $"无法连接 GitHub。请检查网络或在设置中配置代理后重试。\n\n{ex.Message}");
        }
        finally
        {
            _isCheckingForUpdates = false;
        }
    }

    private async Task ShowUpdateAvailableDialogAsync(UpdateReleaseInfo release)
    {
        var notes = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "此版本没有填写更新说明。"
            : release.ReleaseNotes.Trim();
        if (notes.Length > 1600)
            notes = notes[..1600] + "\n…";

        var content = new StackPanel { MaxWidth = 480 };
        content.Children.Add(new TextBlock
        {
            Text = $"{FormatVersion(release.CurrentVersion)}  →  {release.TagName}",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("SteamAccentBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        content.Children.Add(new TextBlock
        {
            Text = notes,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("SteamTextSecondaryBrush")
        });

        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(release.ReleaseName) ? "发现新版本" : release.ReleaseName,
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = release.HasInstaller ? "立即更新" : "查看发布页",
            CloseButtonText = "稍后提醒",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        if (!release.HasInstaller)
        {
            if (!string.IsNullOrWhiteSpace(release.ReleasePageUrl))
                Process.Start(new ProcessStartInfo(release.ReleasePageUrl) { UseShellExecute = true });
            return;
        }

        await DownloadAndInstallUpdateAsync(release);
    }

    private async Task DownloadAndInstallUpdateAsync(UpdateReleaseInfo release)
    {
        _updateDownloadCts?.Dispose();
        _updateDownloadCts = new CancellationTokenSource();
        ShowKernelOverlay($"正在下载 {release.TagName} 更新…");
        KernelOverlayHint.Text = "下载完成后将自动校验、安装并重新启动";
        KernelOverlayHint.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (value.BytesReceived <= 0)
                {
                    KernelOverlayProgressBar.Visibility = Visibility.Collapsed;
                    KernelOverlayPercent.Visibility = Visibility.Collapsed;
                    KernelOverlayRing.Visibility = Visibility.Visible;
                    KernelOverlayRing.IsActive = true;
                    KernelOverlayStatus.Text = string.IsNullOrWhiteSpace(value.Status)
                        ? $"正在连接 {release.TagName} 更新服务器…"
                        : value.Status;
                }
                else
                {
                    UpdateKernelOverlayProgress(value.Percentage);
                    var speedText = value.BytesPerSecond > 0
                        ? $"  ·  {FormatBytes((long)value.BytesPerSecond)}/s"
                        : string.Empty;
                    var statusText = string.IsNullOrWhiteSpace(value.Status)
                        ? $"正在下载 {release.TagName} 更新"
                        : value.Status;
                    KernelOverlayStatus.Text =
                        $"{statusText}…  {FormatBytes(value.BytesReceived)} / {FormatBytes(value.TotalBytes)}{speedText}";
                }

                KernelOverlayHint.Text = value.Status.Contains("重试", StringComparison.Ordinal) ||
                                         value.Status.Contains("切换", StringComparison.Ordinal)
                    ? "当前线路不可用，正在自动切换；已经下载的部分会继续使用"
                    : "下载完成后将自动校验、安装并重新启动";
            });

            var installerPath = await _updateService.DownloadInstallerAsync(
                release,
                progress,
                _updateDownloadCts.Token);

            KernelCancelButton.Visibility = Visibility.Collapsed;
            KernelOverlayStatus.Text = "更新包校验通过，正在启动自动安装…";
            KernelOverlayHint.Text = "应用将自动关闭，安装完成后会重新打开";
            UpdateKernelOverlayProgress(100);

            await Task.Delay(500);
            LaunchInstallerAndRestart(installerPath);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            _aboutView.SetUpdateStatus("已取消更新下载");
            HideKernelOverlay();
        }
        catch (Exception ex)
        {
            _aboutView.SetUpdateStatus("自动更新失败，可稍后重试");
            HideKernelOverlay();
            await ShowModernDialogAsync("自动更新失败", $"更新包下载或校验失败。请检查网络与代理设置后重试。\n\n{ex.Message}");
        }
        finally
        {
            _updateDownloadCts?.Dispose();
            _updateDownloadCts = null;
        }
    }

    private static void LaunchInstallerAndRestart(string installerPath)
    {
        var installedExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MJJsteamtools",
            "MJJsteamtools.exe");
        var logPath = Path.Combine(Path.GetDirectoryName(installerPath)!, "update-install.log");
        var targetPid = Environment.ProcessId;

        static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);
        var escapedInstaller = EscapePowerShell(installerPath);
        var escapedExecutable = EscapePowerShell(installedExe);
        var escapedLog = EscapePowerShell(logPath);
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Wait-Process -Id {{targetPid}} -Timeout 60
            $arguments = '/i "{{escapedInstaller}}" /passive /norestart /L*v "{{escapedLog}}"'
            $installer = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $arguments -Wait -PassThru
            if ($installer.ExitCode -eq 0 -or $installer.ExitCode -eq 1641 -or $installer.ExitCode -eq 3010) {
                Remove-Item -LiteralPath '{{escapedInstaller}}' -Force
            }
            if (Test-Path -LiteralPath '{{escapedExecutable}}') {
                Start-Process -FilePath '{{escapedExecutable}}'
            }
            """;
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("无法启动 Windows Installer 更新进程。");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "未知";
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024d * 1024d):0.0} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private static string FormatVersion(Version version) =>
        $"v{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    private void ShowKernelOverlay(string status)
    {
        KernelOverlayGrid.Visibility = Visibility.Visible;
        KernelOverlayStatus.Text = status;
        KernelOverlayHint.Visibility = Visibility.Collapsed;
        KernelOverlayProgressBar.Visibility = Visibility.Collapsed;
        KernelOverlayPercent.Visibility = Visibility.Collapsed;
        KernelOverlayRing.Visibility = Visibility.Visible;
        KernelOverlayRing.IsActive = true;
        KernelCancelButton.Visibility = Visibility.Visible;
    }

    private void UpdateKernelOverlayProgress(int percent)
    {
        KernelOverlayRing.IsActive = false;
        KernelOverlayRing.Visibility = Visibility.Collapsed;
        KernelOverlayProgressBar.Visibility = Visibility.Visible;
        KernelOverlayPercent.Visibility = Visibility.Visible;
        KernelOverlayProgressBar.Value = percent;
        KernelOverlayPercent.Text = $"{percent}%";
    }

    private void ShowKernelDownloadHint()
    {
        KernelOverlayHint.Visibility = Visibility.Visible;
    }

    private void HideKernelOverlay()
    {
        KernelOverlayRing.IsActive = false;
        KernelOverlayGrid.Visibility = Visibility.Collapsed;
    }

    private void KernelCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _kernelCts?.Cancel();
        _updateDownloadCts?.Cancel();
    }

    public void UpdateBackdrop(string backdropTypeName)
    {
        if (!Enum.TryParse<BackdropType>(backdropTypeName, true, out var backdropType))
            return;

        WindowHelper.SetSystemBackdropType(this, backdropType);

        if (backdropType == BackdropType.None)
        {
            var isLight = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light;
            Background = isLight
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
        }
        else
        {
            Background = null;
        }
    }

    private void UpdateBackdropTheme(bool isLight)
    {
        if (isLight)
        {
            BackdropHelper.RemoveDarkMode(this);
            WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xF0, 0xF5, 0xF5, 0xF5));
        }
        else
        {
            WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E));
            BackdropHelper.ApplyDarkMode(this);
        }
        UpdateOverlayBackground();
    }

    private void UpdateOverlayBackground()
    {
        var panelBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x3C, 0x52));
        AccountSubmenu.Background = panelBrush;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        // Prefer a three-card search layout on desktop, but never let the
        // initial window extend beyond the current monitor's work area.
        var workArea = SystemParameters.WorkArea;
        if (Width > workArea.Width)
        {
            Width = Math.Max(MinWidth, workArea.Width);
            Left = workArea.Left + (workArea.Width - Width) / 2;
        }
        if (Height > workArea.Height)
        {
            Height = Math.Max(MinHeight, workArea.Height);
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }

        try
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            var cornerPreference = DwmCornerRound;
            _ = DwmSetWindowAttribute(windowHandle, DwmWindowCornerPreference,
                ref cornerPreference, Marshal.SizeOf<int>());
        }
        catch
        {
            // Windows 10 等不支持该属性的系统继续使用 WindowChrome 圆角。
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        MaximizeButtonGlyph.Glyph = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (AccountSubmenu.Visibility != Visibility.Visible) return;

        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source == AccountSubmenu || source == TopAccountButton)
                return;
            source = VisualTreeHelper.GetParent(source);
        }
        AccountSubmenu.Visibility = Visibility.Collapsed;
    }

    private void NavView_SelectionChanged(object sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        if (tag != "ScriptDownload")
        {
            _scriptDownloadViewModel.LogLines.Clear();
            _scriptDownloadViewModel.SearchResults.Clear();
            _scriptDownloadViewModel.StatusMessage = "";
        }
        if (tag != "Extraction")
        {
            _extractionViewModel.LogLines.Clear();
            _extractionViewModel.StatusMessage = "";
        }
        if (tag != "Settings")
        {
            _settingsViewModel.SpeedTestResults.Clear();
            _settingsViewModel.StatusMessage = "";
        }
        var prevIndex = Array.IndexOf(_navOrder, _prevTag);
        var newIndex = Array.IndexOf(_navOrder, tag);
        if (prevIndex >= 0 && newIndex >= 0)
        {
            if (newIndex > prevIndex)
            {
                ContentTransition.Transition = TransitionType.Down;
            }
            else if (newIndex < prevIndex)
            {
                ContentTransition.Transition = TransitionType.Up;
            }
        }
        _prevTag = tag;

        SwitchView(tag);
        UpdateCollapsedNavigationVisuals(animate: false);
    }

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        BouncePaneToggleTab();
        _isPaneOpen = !_isPaneOpen;
        NavView.IsPaneOpen = _isPaneOpen;
        UpdateCollapsedNavigationVisuals(animate: true);
        RefreshResponsiveContentLayout();

        var targetX = _isPaneOpen ? 0 : -(NavView.OpenPaneLength - NavView.CompactPaneLength);
        var shadowX = (_isPaneOpen ? NavView.OpenPaneLength : NavView.CompactPaneLength) - 1;
        if (AppMotion.Enabled)
        {
            PaneToggleTransform.BeginAnimation(TranslateTransform.XProperty,
                AppMotion.To(targetX, AppMotion.Pace.Content, AppMotion.Curve.InOut));
            NavigationPaneShadowTransform.BeginAnimation(TranslateTransform.XProperty,
                AppMotion.To(shadowX, AppMotion.Pace.Content, AppMotion.Curve.InOut));
        }
        else
        {
            PaneToggleTransform.BeginAnimation(TranslateTransform.XProperty, null);
            NavigationPaneShadowTransform.BeginAnimation(TranslateTransform.XProperty, null);
            PaneToggleTransform.X = targetX;
            NavigationPaneShadowTransform.X = shadowX;
        }
        PaneToggleGlyph.Glyph = _isPaneOpen ? "\uE76B" : "\uE76C";
        PaneToggleButton.ToolTip = _isPaneOpen ? "收起侧边栏" : "展开侧边栏";
        if (!_isPaneOpen && AppMotion.Enabled)
            TrackCompactIndicatorThroughTransition();
    }

    private void BouncePaneToggleTab()
    {
        PaneToggleButton.ApplyTemplate();
        if (PaneToggleButton.Template.FindName("PaneToggleBackground", PaneToggleButton) is not Border tab)
            return;

        // Template Freezables can be sealed by WPF. Install a per-instance
        // transform before animating so the click feedback never crashes input.
        var transform = new TranslateTransform();
        tab.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        if (!AppMotion.Enabled)
            return;

        // Move only the protruding tab, not the pane or its layout slot.
        // Its sidebar-colored surface stays unchanged throughout the press.
        var bounce = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(5,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(85)), AppMotion.Ease(AppMotion.Curve.Enter)));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(AppMotion.Duration(AppMotion.Pace.Content)), AppMotion.Ease(AppMotion.Curve.Settle)));
        transform.BeginAnimation(TranslateTransform.XProperty, bounce, HandoffBehavior.SnapshotAndReplace);
    }

    private void RefreshResponsiveContentLayout()
    {
        void Refresh()
        {
            NavigationShell.InvalidateMeasure();
            NavigationShell.InvalidateArrange();
            NavView.InvalidateMeasure();
            NavView.InvalidateArrange();
            NavigationShell.UpdateLayout();
            NavView.UpdateLayout();
            ContentTransition.UpdateLayout();
            StorageManagerHost.UpdateLayout();
            _homeView.RefreshResponsiveLayout();
        }

        // Run once after the NavigationView has accepted IsPaneOpen, then once
        // after its 220 ms visual-state transition has settled.
        Dispatcher.BeginInvoke(DispatcherPriority.Render, Refresh);
        var settleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = AppMotion.EffectiveDuration(AppMotion.Pace.Content) + TimeSpan.FromMilliseconds(40)
        };
        settleTimer.Tick += (_, _) =>
        {
            settleTimer.Stop();
            Refresh();
        };
        settleTimer.Start();
    }

    private static Duration PaneTransitionDuration => new(AppMotion.EffectiveDuration(AppMotion.Pace.Content));
    // The NavigationView template leaves a small left inset in compact mode.
    // Shift the rendered glyph, not its layout slot, so every icon's visible
    // pixels sit on the compact pane centerline without changing row spacing.
    private const double CompactIconCenterOffset = -6d;

    private static IEasingFunction CreatePaneTransitionEase() => AppMotion.Ease(AppMotion.Curve.InOut);

    private void TrackCompactIndicatorThroughTransition()
    {
        // The icon moves for the full pane animation. Reading its coordinates
        // only at click time leaves the underline at the icon's old position.
        _compactIndicatorTrackingUntil = DateTime.UtcNow.AddMilliseconds(260);
        if (_compactIndicatorTracking)
            return;

        _compactIndicatorTracking = true;
        CompositionTarget.Rendering += TrackCompactIndicatorOnRender;
    }

    private void TrackCompactIndicatorOnRender(object? sender, EventArgs e)
    {
        if (_isPaneOpen || DateTime.UtcNow >= _compactIndicatorTrackingUntil)
        {
            CompositionTarget.Rendering -= TrackCompactIndicatorOnRender;
            _compactIndicatorTracking = false;
            if (!_isPaneOpen)
                PositionCollapsedSelectionIndicator(animate: false);
            return;
        }

        PositionCollapsedSelectionIndicator(animate: false);
    }

    private void AnimateNavigationItemMargin(NavigationViewItem item, Thickness target, bool animate)
    {
        item.BeginAnimation(FrameworkElement.MarginProperty, null);
        if (!animate || !AppMotion.Enabled)
        {
            item.Margin = target;
            return;
        }

        var animation = new ThicknessAnimation
        {
            To = target,
            Duration = PaneTransitionDuration,
            EasingFunction = CreatePaneTransitionEase(),
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            item.BeginAnimation(FrameworkElement.MarginProperty, null);
            item.Margin = target;
        };
        item.BeginAnimation(FrameworkElement.MarginProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateCompactIconAlignment(bool animate)
    {
        var targetX = _isPaneOpen ? 0d : CompactIconCenterOffset;
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>()
                     .Concat(NavView.FooterMenuItems.OfType<NavigationViewItem>()))
        {
            if (item.Icon is not FrameworkElement icon)
                continue;

            if (icon.RenderTransform is not TranslateTransform translate)
            {
                translate = new TranslateTransform();
                icon.RenderTransform = translate;
            }

            translate.BeginAnimation(TranslateTransform.XProperty, null);
            if (!animate || !AppMotion.Enabled)
            {
                translate.X = targetX;
                continue;
            }

            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                To = targetX,
                Duration = PaneTransitionDuration,
                EasingFunction = CreatePaneTransitionEase()
            }, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void HideCollapsedSelectionIndicator(bool animate)
    {
        animate &= AppMotion.Enabled;
        if (!animate || CollapsedSelectionIndicator.Visibility != Visibility.Visible)
        {
            CollapsedSelectionIndicator.BeginAnimation(UIElement.OpacityProperty, null);
            CollapsedSelectionIndicator.Opacity = 1;
            CollapsedSelectionIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = PaneTransitionDuration,
            EasingFunction = CreatePaneTransitionEase(),
            FillBehavior = FillBehavior.HoldEnd
        };
        fade.Completed += (_, _) =>
        {
            CollapsedSelectionIndicator.BeginAnimation(UIElement.OpacityProperty, null);
            CollapsedSelectionIndicator.Opacity = 1;
            CollapsedSelectionIndicator.Visibility = Visibility.Collapsed;
        };
        CollapsedSelectionIndicator.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void PositionCollapsedSelectionIndicator(bool animate)
    {
        animate &= AppMotion.Enabled;
        if (NavView.SelectedItem is not NavigationViewItem selected || !selected.IsLoaded)
        {
            HideCollapsedSelectionIndicator(animate: false);
            return;
        }

        try
        {
            double left;
            double top;
            if (selected.Icon is FrameworkElement icon && icon.IsLoaded && icon.ActualWidth > 0 && icon.ActualHeight > 0)
            {
                // Anchor the underline to the rendered icon itself.  This keeps it
                // centered even when the compact pane has a border or template inset.
                var iconPoint = icon.TransformToAncestor(NavigationShell)
                    .Transform(new Point(icon.ActualWidth / 2, icon.ActualHeight));
                left = iconPoint.X - CollapsedSelectionIndicator.Width / 2;
                top = iconPoint.Y + 3;
            }
            else
            {
                var itemPoint = selected.TransformToAncestor(NavigationShell).Transform(new Point(0, 0));
                left = (NavView.CompactPaneLength - CollapsedSelectionIndicator.Width) / 2;
                top = itemPoint.Y + Math.Max(0, selected.ActualHeight / 2 + 10);
            }

            var oldLeft = Canvas.GetLeft(CollapsedSelectionIndicator);
            var oldTop = Canvas.GetTop(CollapsedSelectionIndicator);
            var wasVisible = CollapsedSelectionIndicator.Visibility == Visibility.Visible;
            var canAnimate = animate && CollapsedSelectionIndicator.Visibility == Visibility.Visible
                && !double.IsNaN(oldLeft) && !double.IsNaN(oldTop);

            CollapsedSelectionIndicator.Visibility = Visibility.Visible;
            if (animate && !wasVisible)
            {
                CollapsedSelectionIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                CollapsedSelectionIndicator.Opacity = 0;
                CollapsedSelectionIndicator.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
                {
                    To = 1,
                    Duration = PaneTransitionDuration,
                    EasingFunction = CreatePaneTransitionEase()
                }, HandoffBehavior.SnapshotAndReplace);
            }
            if (canAnimate)
            {
                CollapsedSelectionIndicator.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
                {
                    To = left,
                    Duration = PaneTransitionDuration,
                    EasingFunction = CreatePaneTransitionEase()
                });
                CollapsedSelectionIndicator.BeginAnimation(Canvas.TopProperty, new DoubleAnimation
                {
                    To = top,
                    Duration = PaneTransitionDuration,
                    EasingFunction = CreatePaneTransitionEase()
                });
            }
            else
            {
                CollapsedSelectionIndicator.BeginAnimation(Canvas.LeftProperty, null);
                CollapsedSelectionIndicator.BeginAnimation(Canvas.TopProperty, null);
                Canvas.SetLeft(CollapsedSelectionIndicator, left);
                Canvas.SetTop(CollapsedSelectionIndicator, top);
            }
        }
        catch (InvalidOperationException)
        {
            HideCollapsedSelectionIndicator(animate: false);
        }
    }

    private void UpdateCollapsedNavigationVisuals(bool animate = false)
    {
        LibraryNavHeader.Visibility = _isPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        ManagementNavHeader.Visibility = _isPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        // Child entries use the same inset as primary entries while compact so
        // their icons remain visible instead of being clipped by the pane edge.
        var compactMargin = new Thickness(6, 1, 6, 1);
        var expandedMargin = new Thickness(36, 0, 6, 0);
        AnimateNavigationItemMargin(ScriptDownloadItem, _isPaneOpen ? expandedMargin : compactMargin, animate);
        AnimateNavigationItemMargin(ExtractionItem, _isPaneOpen ? expandedMargin : compactMargin, animate);
        AnimateCompactIconAlignment(animate);

        if (_isPaneOpen)
        {
            NavView.Resources["NavigationViewSelectionIndicatorWidth"] = 4d;
            NavView.Resources["NavigationViewSelectionIndicatorHeight"] = 20d;
            HideCollapsedSelectionIndicator(animate);
            return;
        }

        NavView.Resources["NavigationViewSelectionIndicatorWidth"] = 0d;
        NavView.Resources["NavigationViewSelectionIndicatorHeight"] = 0d;
        PositionCollapsedSelectionIndicator(animate);
    }

    private void SwitchView(string tag)
    {
        CloseSteamStorageManagerHost();

        CurrentPageTitleText.Text = tag switch
        {
            "Home" => "游戏库",
            "SaveVault" => "存档保险箱",
            "FamilyLibrary" => "家庭库",
            "ScriptDownload" => "搜索入库",
            "Extraction" => "Lua 提取",
            "Trainer" => "游戏修改器",
            "UtilityTools" => "实用工具",
            "Settings" => "设置",
            "About" => "关于",
            _ => "游戏库"
        };

        UserControl? newView = tag switch
        {
            "Home" => _homeView,
            "SaveVault" => _saveVaultView,
            "FamilyLibrary" => _familyLibraryView,
            "Settings" => _settingsView,
            "ScriptDownload" => _scriptDownloadView,
            "Extraction" => _extractionView,
            "Trainer" => _trainerView,
            "UtilityTools" => _utilityToolsView,
            "About" => _aboutView,
            _ => null
        };

        if (newView is null || newView == ContentTransition.Content) return;
        ContentTransition.Content = newView;

        if (tag == "Trainer")
        {
            _ = _trainerViewModel.LoadSectionsCommand.ExecuteAsync(null);
            _trainerViewModel.LoadDownloadedTrainersCommand.Execute(null);
        }
        else if (tag == "SaveVault")
        {
            var openAppId = _pendingSaveVaultAppId;
            _pendingSaveVaultAppId = null;
            _ = _saveVaultView.RefreshAsync(openAppId);
        }
    }

    private void OpenSteamStorageManager()
    {
        CurrentPageTitleText.Text = "空间管家";
        StorageManagerHost.Content = _steamStorageManagerView;
        StorageManagerHost.Visibility = Visibility.Visible;
        ContentTransition.Visibility = Visibility.Collapsed;
    }

    private void OpenCloudSyncRescue()
    {
        CurrentPageTitleText.Text = "云同步急救";
        StorageManagerHost.Content = _cloudSyncRescueView;
        StorageManagerHost.Visibility = Visibility.Visible;
        ContentTransition.Visibility = Visibility.Collapsed;
    }

    private void ReturnToUtilityTools()
    {
        CloseSteamStorageManagerHost();
        CurrentPageTitleText.Text = "实用工具";
        if (ContentTransition.Content != _utilityToolsView)
            ContentTransition.Content = _utilityToolsView;
    }

    private void CloseSteamStorageManagerHost()
    {
        if (StorageManagerHost is null || StorageManagerHost.Visibility != Visibility.Visible)
            return;

        if (StorageManagerHost.Content is SteamStorageManagerView storageView)
            storageView.CancelActiveScan(updateStatus: false);
        StorageManagerHost.Content = null;
        StorageManagerHost.Visibility = Visibility.Collapsed;
        ContentTransition.Visibility = Visibility.Visible;
    }

    private void HomeView_OpenSaveVaultRequested(int appId)
    {
        _pendingSaveVaultAppId = appId;
        if (ContentTransition.Content == _saveVaultView)
        {
            _pendingSaveVaultAppId = null;
            _ = _saveVaultView.OpenGameAsync(appId);
            return;
        }
        NavView.SelectedItem = SaveVaultItem;
    }

    private async Task FamilyLibraryView_ImportRequested(Models.FamilyGameInfo game)
    {
        NavView.SelectedItem = ScriptDownloadItem;
        var importGame = new ScriptDownloadViewModel.FoundGame(
            game.AppId,
            game.Name,
            game.Name,
            game.CoverImage,
            string.Empty,
            string.Empty);
        await _scriptDownloadViewModel.DownloadGameCommand.ExecuteAsync(importGame);
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            await _viewModel.HandleDropAsync(files);
        }
    }

    // ========== 顶栏 Steam 快捷操作 ==========

    private async void StartSteamButton_Click(object sender, RoutedEventArgs e)
    {
        await LaunchSteamAsync();
    }

    private async void RestartSteamButton_Click(object sender, RoutedEventArgs e)
    {
        KillSteamProcesses();
        await LaunchSteamAsync();
    }

    private async Task LaunchSteamAsync(string? arguments = null)
    {
        try
        {
            var path = _steamPathService.GetCustomPath();
            if (string.IsNullOrWhiteSpace(path))
                path = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(path))
            {
                await ShowModernDialogAsync("提示", "未检测到 Steam 安装路径，请先在设置页面配置");
                return;
            }

            var exePath = System.IO.Path.Combine(path, "steam.exe");
            if (!System.IO.File.Exists(exePath))
            {
                await ShowModernDialogAsync("提示", $"未找到 steam.exe：{exePath}");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"启动 Steam 失败：{ex.Message}");
        }
    }

    private async Task<bool> ClearSteamDownloadCacheAsync()
    {
        void SetStatus(string message)
        {
            _utilityToolsView.SetActionStatus(message);
            _steamStorageManagerView.SetActionStatus(message);
        }

        try
        {
            var path = _steamPathService.GetCustomPath();
            if (string.IsNullOrWhiteSpace(path))
                path = _steamPathService.DetectSteamPath();
            var exePath = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.Combine(path, "steam.exe");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(exePath))
            {
                SetStatus("未检测到 Steam，请先在设置中配置安装路径。");
                await ShowModernDialogAsync("未检测到 Steam", "未找到 Steam 安装目录，请先在设置页面配置 Steam 路径。");
                return false;
            }

            var confirmed = await ShowModernConfirmAsync(
                "清除 Steam 下载缓存？",
                "这个操作不会删除已安装的游戏，但会关闭 Steam、清除下载缓存并重新启动。\n\n" +
                "清理完成后需要重新登录 Steam。请先保存进度并退出正在运行的游戏。",
                "清除并重启");
            if (!confirmed)
            {
                SetStatus("已取消清除下载缓存。");
                return false;
            }

            SetStatus("正在安全关闭 Steam…");
            if (!await TryShutdownSteamAsync(exePath, path))
            {
                SetStatus("Steam 未能自动退出，请关闭游戏和 Steam 后重试。");
                await ShowModernDialogAsync(
                    "Steam 仍在运行",
                    "Steam 未能在 20 秒内正常退出。请先关闭正在运行的游戏并完全退出 Steam，然后再次清除缓存。");
                return false;
            }

            SetStatus("正在由 Steam 清除下载缓存并重新启动…");
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-flushconfig",
                WorkingDirectory = path,
                UseShellExecute = true
            });
            SetStatus("清理命令已交给 Steam，Steam 将重新启动并要求登录。");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("清除下载缓存失败。");
            await ShowModernDialogAsync("清理失败", $"无法完成 Steam 下载缓存清理：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> SteamStorageManagerView_DetailDeleteRequested(SteamStorageDetailItem item)
    {
        if (item.CategoryKey == "games")
        {
            var confirmed = await ShowModernConfirmAsync(
                "通过 Steam 卸载游戏？",
                $"将打开 Steam 的卸载确认：\n\n{item.Name}\n{item.AppIdText}\n当前占用 {item.SizeText}\n\n" +
                "MJJsteamtools 不会直接删除游戏目录，避免留下清单或下载状态异常。",
                "打开卸载确认");
            if (!confirmed)
                return false;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://uninstall/{item.AppId}",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                await ShowModernDialogAsync("无法打开 Steam", ex.Message);
            }
            return false;
        }

        var isWorkshop = item.CategoryKey == "workshop";
        var confirmedDelete = await ShowModernConfirmAsync(
            isWorkshop ? "删除这款游戏的工坊内容？" : "清理这款游戏的着色器缓存？",
            $"{item.Name}\n{item.AppIdText}\n将删除 {item.SizeText}\n\n目录：{item.Path}\n\n" +
            (isWorkshop
                ? "如果仍订阅这些创意工坊项目，Steam 之后可能重新下载。"
                : "缓存可重新生成；下次进入游戏时可能需要重新编译并出现短暂卡顿。"),
            isWorkshop ? "确认删除" : "确认清理");
        if (!confirmedDelete)
            return false;

        var shouldRestartSteam = !isWorkshop && IsSteamRunning();
        if (shouldRestartSteam)
        {
            var steamPath = _steamPathService.GetCustomPath();
            if (string.IsNullOrWhiteSpace(steamPath))
                steamPath = _steamPathService.DetectSteamPath();
            var exePath = string.IsNullOrWhiteSpace(steamPath) ? string.Empty : Path.Combine(steamPath, "steam.exe");
            if (string.IsNullOrWhiteSpace(steamPath) || !File.Exists(exePath))
            {
                await ShowModernDialogAsync("未检测到 Steam", "无法安全关闭 Steam，请先在设置页面配置 Steam 路径。");
                return false;
            }
            _steamStorageManagerView.SetActionStatus("正在安全关闭 Steam…");
            if (!await TryShutdownSteamAsync(exePath, steamPath))
            {
                await ShowModernDialogAsync("Steam 仍在运行", "请关闭正在运行的游戏并完全退出 Steam，然后重试。");
                return false;
            }
        }

        try
        {
            var deletedBytes = await _steamStorageService.DeleteDetailItemAsync(item);
            if (shouldRestartSteam)
                await LaunchSteamAsync();
            await ShowModernDialogAsync(
                isWorkshop ? "工坊内容已删除" : "着色器缓存已清理",
                $"已释放 {SteamStorageService.FormatBytes(deletedBytes)}。\n\n{item.Name}");
            return true;
        }
        catch (Exception ex)
        {
            if (shouldRestartSteam)
                await LaunchSteamAsync();
            await ShowModernDialogAsync("操作失败", $"无法处理该目录：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> SteamStorageManagerView_CleanupRequested(SteamStorageCategory category)
    {
        if (category.CleanupKey == "download-cache")
            return await ClearSteamDownloadCacheAsync();
        if (category.CleanupKey != "shader-cache")
            return false;

        var confirmed = await ShowModernConfirmAsync(
            "清理全部着色器缓存？",
            $"当前着色器缓存占用 {category.SizeText}。这些文件可以重新生成，但清理后首次进入游戏可能会重新下载或编译，并出现短暂卡顿。\n\n" +
            "Steam 将暂时关闭，清理完成后自动重新启动。",
            "清理并重启");
        if (!confirmed)
            return false;

        var path = _steamPathService.GetCustomPath();
        if (string.IsNullOrWhiteSpace(path))
            path = _steamPathService.DetectSteamPath();
        var exePath = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.Combine(path, "steam.exe");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(exePath))
        {
            await ShowModernDialogAsync("未检测到 Steam", "未找到 Steam 安装目录，请先在设置页面配置 Steam 路径。");
            return false;
        }

        _steamStorageManagerView.SetActionStatus("正在安全关闭 Steam…");
        if (!await TryShutdownSteamAsync(exePath, path))
        {
            _steamStorageManagerView.SetActionStatus("Steam 未能自动退出，未执行清理。");
            await ShowModernDialogAsync("Steam 仍在运行", "请关闭正在运行的游戏并完全退出 Steam，然后重试。");
            return false;
        }

        try
        {
            _steamStorageManagerView.SetActionStatus("正在清理着色器缓存…");
            var clearedBytes = await _steamStorageService.ClearShaderCachesAsync();
            await LaunchSteamAsync();
            _steamStorageManagerView.SetActionStatus($"着色器缓存已清理，释放 {SteamStorageService.FormatBytes(clearedBytes)}。");
            await ShowModernDialogAsync("清理完成", $"已释放 {SteamStorageService.FormatBytes(clearedBytes)} 着色器缓存，Steam 已重新启动。");
            return true;
        }
        catch (Exception ex)
        {
            await LaunchSteamAsync();
            _steamStorageManagerView.SetActionStatus("着色器缓存清理失败。");
            await ShowModernDialogAsync("清理失败", $"无法清理着色器缓存：{ex.Message}");
            return false;
        }
    }

    private static async Task<bool> TryShutdownSteamAsync(string exePath, string steamPath)
    {
        if (!IsSteamRunning())
            return true;

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "-shutdown",
            WorkingDirectory = steamPath,
            UseShellExecute = true
        });
        var shutdownTimeout = Stopwatch.StartNew();
        while (IsSteamRunning() && shutdownTimeout.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(250);
        return !IsSteamRunning();
    }

    private static bool IsSteamRunning()
    {
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                        return true;
                }
                catch
                {
                    // 进程可能在枚举后立即退出，继续检查其余实例。
                }
            }
        }

        return false;
    }

    private static void KillSteamProcesses()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("steam"))
            {
                if (proc.Id != 0)
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                proc.Dispose();
            }
        }
        catch { }
    }

    // ========== Steam 账号切换 ==========

    private class SteamAccount
    {
        public string SteamId { get; set; } = "";
        public string AccountName { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string AvatarHash { get; set; } = "";
        public string? AvatarPath { get; set; }
        public bool MostRecent { get; set; }
        public bool IsCurrent { get; set; }
        public bool IsSeparator { get; set; }

        public static SteamAccount Separator() => new() { IsSeparator = true };
    }

    private List<SteamAccount>? _cachedAccounts;

    private void TopAccountButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (AccountSubmenu.Visibility == Visibility.Visible)
        {
            AccountSubmenu.Visibility = Visibility.Collapsed;
            return;
        }

        _accountMenuTrigger = TopAccountButton;
        ShowAccountSubmenu(TopAccountButton);
    }

    private void TopAccountButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (AccountSubmenu.Visibility == Visibility.Visible)
            _ = DelayedHideSubmenuAsync(AccountSubmenu, TopAccountButton);
    }

    private void RefreshTopAccountDisplay()
    {
        var accounts = ParseLoginUsersVdf();
        if (accounts == null || accounts.Count == 0)
        {
            _cachedAccounts = null;
            SetTopAccountDisplay(null);
            return;
        }

        AttachAvatarPaths(accounts);
        var current = ResolveCurrentSteamAccount(accounts);
        foreach (var account in accounts)
            account.IsCurrent = ReferenceEquals(account, current);

        _cachedAccounts = accounts;
        SetTopAccountDisplay(current);
    }

    private void AttachAvatarPaths(IEnumerable<SteamAccount> accounts)
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return;

        foreach (var account in accounts)
        {
            var avatarPath = System.IO.Path.Combine(steamPath, "config", "avatarcache", $"{account.SteamId}.png");
            account.AvatarPath = System.IO.File.Exists(avatarPath) ? avatarPath : null;
        }
    }

    private static SteamAccount? ResolveCurrentSteamAccount(List<SteamAccount> accounts)
    {
        try
        {
            using var activeKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var rawActiveUser = activeKey?.GetValue("ActiveUser");
            if (rawActiveUser != null && uint.TryParse(rawActiveUser.ToString(), out var activeUser) && activeUser != 0)
            {
                var active = accounts.FirstOrDefault(account =>
                    ulong.TryParse(account.SteamId, out var steamId64)
                    && unchecked((uint)(steamId64 & uint.MaxValue)) == activeUser);
                if (active != null) return active;
            }
        }
        catch { }

        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var autoLoginUser = steamKey?.GetValue("AutoLoginUser") as string;
            if (!string.IsNullOrWhiteSpace(autoLoginUser))
            {
                var autoLogin = accounts.FirstOrDefault(account =>
                    account.AccountName.Equals(autoLoginUser, StringComparison.OrdinalIgnoreCase));
                if (autoLogin != null) return autoLogin;
            }
        }
        catch { }

        return accounts.FirstOrDefault(account => account.MostRecent) ?? accounts[0];
    }

    private void SetTopAccountDisplay(SteamAccount? account)
    {
        TopAccountAvatar.Source = null;
        if (account == null)
        {
            TopAccountName.Text = "未登录 Steam";
            TopAccountButton.ToolTip = "未检测到本地 Steam 账号";
            return;
        }

        TopAccountName.Text = account.PersonaName;
        TopAccountButton.ToolTip = $"{account.PersonaName}\n账号：{account.AccountName}\nSteamID：{account.SteamId}";
        if (string.IsNullOrEmpty(account.AvatarPath)) return;

        try
        {
            var avatar = new BitmapImage();
            avatar.BeginInit();
            avatar.CacheOption = BitmapCacheOption.OnLoad;
            avatar.UriSource = new Uri(account.AvatarPath, UriKind.Absolute);
            avatar.EndInit();
            avatar.Freeze();
            TopAccountAvatar.Source = avatar;
        }
        catch { }
    }

    private void AccountSubmenu_MouseEnter(object sender, MouseEventArgs e)
    {
        // 鼠标进入子菜单，保持显示
    }

    private void AccountSubmenu_MouseLeave(object sender, MouseEventArgs e)
    {
        // 鼠标离开子菜单，延迟隐藏
        if (_accountMenuTrigger != null)
            _ = DelayedHideSubmenuAsync(AccountSubmenu, _accountMenuTrigger);
    }

    private async Task DelayedHideSubmenuAsync(Border submenu, FrameworkElement trigger)
    {
        var requestVersion = _accountMenuTransitionVersion;
        await Task.Delay(200);
        if (requestVersion != _accountMenuTransitionVersion) return;
        if (submenu.Visibility != Visibility.Visible) return;
        if (submenu.IsMouseOver || trigger.IsMouseOver) return;
        var closeVersion = ++_accountMenuTransitionVersion;
        await Task.Delay(MotionBehavior.PlayExit(submenu, toY: -6));
        if (closeVersion != _accountMenuTransitionVersion) return;
        submenu.Visibility = Visibility.Collapsed;
        submenu.BeginAnimation(UIElement.OpacityProperty, null);
        submenu.Opacity = 1;
    }

    private void ShowAccountSubmenu(FrameworkElement trigger)
    {
        _accountMenuTransitionVersion++;
        RefreshTopAccountDisplay();

        var displayAccounts = new List<SteamAccount>();
        var accounts = _cachedAccounts ?? [];
        for (var i = 0; i < accounts.Count; i++)
        {
            if (i > 0)
                displayAccounts.Add(SteamAccount.Separator());
            displayAccounts.Add(accounts[i]);
        }

        AccountList.ItemsSource = displayAccounts;
        var anchor = trigger.TranslatePoint(new Point(0, trigger.ActualHeight), OverlayCanvas);
        var submenuWidth = AccountSubmenu.Width;
        var left = anchor.X + trigger.ActualWidth - submenuWidth;
        left = Math.Max(8, Math.Min(OverlayCanvas.ActualWidth - submenuWidth - 8, left));
        var top = anchor.Y + 6;
        Canvas.SetLeft(AccountSubmenu, left);
        Canvas.SetTop(AccountSubmenu, top);
        AccountSubmenu.RenderTransformOrigin = new Point(1, 0);
        var maxHeight = Math.Max(120, OverlayCanvas.ActualHeight - top - 12);
        AccountSubmenu.MaxHeight = maxHeight;
        AccountScrollViewer.MaxHeight = Math.Max(56, maxHeight - 58);
        AccountScrollViewer.ScrollToTop();
        AccountSubmenu.Visibility = Visibility.Visible;
        MotionBehavior.PlayEntrance(AccountSubmenu, fromY: -8, pace: AppMotion.Pace.Feedback);
    }

    private async void AccountItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is SteamAccount acc)
        {
            e.Handled = true;
            _accountMenuTransitionVersion++;
            AccountSubmenu.Visibility = Visibility.Collapsed;
            await SwitchSteamAccountAsync(acc);
        }
    }

    private void AccountMenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
            b.Background = (Brush)Application.Current.FindResource("SystemControlHighlightListMediumBrush");
    }

    private void AccountMenuItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
            b.ClearValue(Border.BackgroundProperty);
    }

    private async void DeleteAccountButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: SteamAccount account })
            return;

        AccountSubmenu.Visibility = Visibility.Collapsed;
        var confirmed = await ShowModernConfirmAsync(
            "删除账号记录",
            $"确定从本机 Steam 账号列表移除“{account.PersonaName}”吗？\n\n" +
            "此操作只删除本地登录记录，不会删除 Steam 账号、游戏或云存档。为避免 Steam 重新写回记录，正在运行的 Steam 将被关闭。",
            "删除记录");
        if (!confirmed)
            return;

        try
        {
            KillSteamProcesses();
            var steamPath = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                throw new InvalidOperationException("未检测到 Steam 安装路径");

            var vdfPath = System.IO.Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!System.IO.File.Exists(vdfPath))
                throw new FileNotFoundException("未找到 Steam 账号记录文件", vdfPath);

            var content = await System.IO.File.ReadAllTextAsync(vdfPath);
            var blockPattern =
                $"(?ms)^[ \\t]*\\\"{Regex.Escape(account.SteamId)}\\\"[ \\t]*(?:\\r?\\n)?[ \\t]*\\{{.*?^[ \\t]*\\}}[ \\t]*(?:\\r?\\n)?";
            var updated = new Regex(blockPattern).Replace(content, string.Empty, 1);
            if (updated == content)
                throw new InvalidOperationException("未能在 Steam 账号记录中找到该账号");

            System.IO.File.Copy(vdfPath, vdfPath + ".mjjsteamtools.bak", overwrite: true);
            await System.IO.File.WriteAllTextAsync(vdfPath, updated, new UTF8Encoding(false));

            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true))
            {
                var autoLoginUser = key?.GetValue("AutoLoginUser") as string;
                if (autoLoginUser?.Equals(account.AccountName, StringComparison.OrdinalIgnoreCase) == true)
                    key?.SetValue("AutoLoginUser", string.Empty, RegistryValueKind.String);
            }

            RefreshTopAccountDisplay();
            await ShowModernDialogAsync("记录已删除", $"已移除“{account.PersonaName}”的本地登录记录。\n需要使用该账号时，可通过“登录新账号”重新登录。");
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("删除失败", $"无法删除账号记录：{ex.Message}");
        }
    }

    private async void NewSteamAccountButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        AccountSubmenu.Visibility = Visibility.Collapsed;
        var confirmed = await ShowModernConfirmAsync(
            "登录新账号",
            "Steam 将退出当前账号并打开登录界面。现有账号记录不会被删除，登录信息也不会经过本工具。",
            "打开登录界面");
        if (!confirmed)
            return;

        try
        {
            KillSteamProcesses();
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true))
                key?.SetValue("AutoLoginUser", string.Empty, RegistryValueKind.String);

            SetTopAccountDisplay(null);
            await LaunchSteamAsync("-login");
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("启动失败", $"无法打开 Steam 登录界面：{ex.Message}");
        }
    }

    private async Task SwitchSteamAccountAsync(SteamAccount target)
    {
        try
        {
            KillSteamProcesses();
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true))
            {
                if (key == null)
                {
                    await ShowModernDialogAsync("错误", "无法打开注册表 Steam 项");
                    return;
                }
                key.SetValue("AutoLoginUser", target.AccountName, RegistryValueKind.String);
            }
            if (_cachedAccounts != null)
            {
                foreach (var account in _cachedAccounts)
                    account.IsCurrent = account.AccountName.Equals(target.AccountName, StringComparison.OrdinalIgnoreCase);
            }
            target.IsCurrent = true;
            SetTopAccountDisplay(target);
            await LaunchSteamAsync();
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"切换账号失败：{ex.Message}");
        }
    }

    private List<SteamAccount>? ParseLoginUsersVdf()
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return null;

        var vdfPath = System.IO.Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!System.IO.File.Exists(vdfPath)) return null;

        try
        {
            var content = System.IO.File.ReadAllText(vdfPath);
            var accounts = new List<SteamAccount>();

            foreach (Match blockMatch in Regex.Matches(content, "\\\"(\\d+)\\\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline))
            {
                var body = blockMatch.Groups["body"].Value;
                var accountName = GetVdfValue(body, "AccountName");
                var personaName = GetVdfValue(body, "PersonaName");
                if (string.IsNullOrEmpty(accountName))
                    continue;

                accounts.Add(new SteamAccount
                {
                    SteamId = blockMatch.Groups[1].Value,
                    AccountName = accountName,
                    PersonaName = string.IsNullOrEmpty(personaName) ? accountName : personaName,
                    AvatarHash = GetVdfValue(body, "AvatarHash"),
                    MostRecent = GetVdfValue(body, "MostRecent") == "1"
                });
            }
            return accounts;
        }
        catch { return null; }
    }

    private static string GetVdfValue(string block, string key)
    {
        var match = Regex.Match(block, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]*)\\\"");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    // ========== OpenSteamTool 内核管理 ==========

    private async void SettingsView_InstallOpenSteamToolRequested(object? sender, EventArgs e)
    {
        await InstallKernelAsync();
        _settingsView.UpdateOpenSteamToolStatus(_openSteamToolService.IsInstalled);
    }

    private async void SettingsView_UninstallOpenSteamToolRequested(object? sender, EventArgs e)
    {
        await UninstallKernelAsync();
        _settingsView.UpdateOpenSteamToolStatus(_openSteamToolService.IsInstalled);
    }

    private async Task InstallKernelAsync()
    {
        if (_openSteamToolService.IsInstalled)
        {
            var confirmed = await ShowModernConfirmAsync(
                "确认安装",
                "OpenSteamTool 已安装，是否仍要重新安装？这将覆盖现有文件。",
                "重新安装");
            if (!confirmed) return;
        }

        try
        {
            var (version, downloadUrl, _) = await _openSteamToolService.GetRemoteInfoAsync();
            if (string.IsNullOrEmpty(downloadUrl))
            {
                await ShowModernDialogAsync("错误", "无法获取最新版本下载链接");
                return;
            }

            ShowKernelOverlay("正在下载 OpenSteamTool...");
            _kernelCts = new CancellationTokenSource();
            try
            {
                var status = new Progress<string>(msg => KernelOverlayStatus.Text = msg);
                var progress = new Progress<int>(pct => UpdateKernelOverlayProgress(pct));
                ShowKernelDownloadHint();
                await _openSteamToolService.InstallAsync(downloadUrl, status, progress, _kernelCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _kernelCts?.Cancel();
                _kernelCts?.Dispose();
                _kernelCts = null;
                HideKernelOverlay();
            }

            await ShowModernDialogAsync("安装完成", $"OpenSteamTool {version} 安装成功！\n请重启 Steam 后生效。");
            RefreshTitle();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"安装失败：{ex.Message}");
        }
    }

    private async Task UninstallKernelAsync()
    {
        if (!_openSteamToolService.IsInstalled)
        {
            await ShowModernDialogAsync("提示", "未检测到已安装的 OpenSteamTool。");
            return;
        }

        var confirmed = await ShowModernConfirmAsync(
            "确认卸载",
            "确定要卸载 OpenSteamTool 吗？\n这将删除以下文件：\n• dwmapi.dll\n• xinput1_4.dll\n• OpenSteamTool.dll",
            "卸载");
        if (!confirmed) return;

        try
        {
            await _openSteamToolService.UninstallAsync();
            await ShowModernDialogAsync("卸载完成", "OpenSteamTool 已卸载。\n重启 Steam 后生效。");
            RefreshTitle();
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"卸载失败：{ex.Message}");
        }
    }

}
