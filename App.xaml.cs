using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Text.Json;
using iNKORE.UI.WPF.Modern;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;
using SteamLuaManager.Views;

namespace SteamLuaManager;

public partial class App : Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }
    private IUsageTelemetryService? _usageTelemetryService;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 3 && e.Args[0].Equals("--achievement-helper", StringComparison.OrdinalIgnoreCase))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunAchievementHelperAsync(e.Args[1], e.Args[2]);
            return;
        }

        // 全局未处理异常日志
        var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(logPath, args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(logPath, args.Exception);
            // Never swallow a render/layout exception. Marking it handled makes
            // WPF retry the same broken layout forever and looks like a frozen UI.
            args.Handled = false;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(logPath, args.Exception);
            args.SetObserved();
        };

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();

        // Steam 客户端采用统一深色外观。固定主题可避免 Fluent 控件和
        // Steam 自定义色板在浅色系统下出现一半深色、一半浅色的混搭。
        ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;

        try {
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            WriteCrashLog(logPath, ex);
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                mainWindow.UpdateBackdrop("None");
                mainWindow.Show();
            }
            catch (Exception fallbackEx)
            {
                WriteCrashLog(logPath, fallbackEx);
                MessageBox.Show($"程序窗口无法显示，请检查 crash.log。`n`n{fallbackEx.Message}", "MJJsteamtools", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }

        _usageTelemetryService = ServiceProvider.GetRequiredService<IUsageTelemetryService>();
        _usageTelemetryService.Start();

        var autoLaunch = ServiceProvider.GetRequiredService<ITrainerAutoLaunchService>();
        autoLaunch.Start();
        mainWindow.Closed += (_, _) => autoLaunch.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                // Keep the OpenSteamTool manifest provider resilient.  This is
                // deliberately best-effort and never blocks the UI startup.
                var openSteamTool = ServiceProvider.GetRequiredService<IOpenSteamToolService>();
                if (openSteamTool.IsInstalled)
                    await openSteamTool.EnsureManifestFallbackAsync();

                var depotService = ServiceProvider.GetRequiredService<ISteamDepotService>();
                await depotService.EnsureAllSourcesAsync();
            }
            catch { }
        });

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _usageTelemetryService?.Dispose();
        _usageTelemetryService = null;
        base.OnExit(e);
    }

    private static void WriteCrashLog(string path, Exception? exception)
    {
        try
        {
            if (exception is null)
            {
                System.IO.File.WriteAllText(path, "Unknown unhandled exception.");
                return;
            }

            var root = exception.GetBaseException();
            System.IO.File.WriteAllText(
                path,
                $"{root.GetType().FullName}: {root.Message}{Environment.NewLine}{root.StackTrace}");
        }
        catch
        {
            // Crash logging must not throw while handling another exception.
        }
    }

    private async Task RunAchievementHelperAsync(string requestPath, string resultPath)
    {
        AchievementOperationResult result;
        try
        {
            var json = await System.IO.File.ReadAllTextAsync(requestPath);
            var request = JsonSerializer.Deserialize<AchievementHelperRequest>(json)
                          ?? throw new System.IO.InvalidDataException("成就操作请求无效");
            var achievementService = new SteamAchievementService(new SteamPathService());
            result = await achievementService.SetAllAchievementsDirectAsync(
                request.AppId,
                request.InstallPath,
                request.ApiNames,
                request.Unlocked);
        }
        catch (Exception ex)
        {
            result = new AchievementOperationResult(false, $"辅助程序执行失败：{ex.GetBaseException().Message}");
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result));
        }
        catch
        {
        }
        Shutdown(result.Success ? 0 : 1);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISteamPathService, SteamPathService>();
        services.AddSingleton<ILuaFileManager, LuaFileManager>();
        services.AddSingleton<ISteamApiService, SteamApiService>();
        services.AddSingleton<ISteamManifestService, SteamManifestService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IHttpClientProvider, HttpClientProvider>();
        services.AddSingleton<IUsageTelemetryService, UsageTelemetryService>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
        services.AddSingleton<ISteamDepotService, SteamDepotService>();
        services.AddSingleton<ISteamAchievementService, SteamAchievementService>();
        services.AddSingleton<IDlcManagementService, DlcManagementService>();
        services.AddSingleton<ISaveVaultService, SaveVaultService>();
        services.AddSingleton<ISaveAutoBackupService, SaveAutoBackupService>();
        services.AddSingleton<IGamePlayProfileService, GamePlayProfileService>();
        services.AddSingleton<IFamilyLibraryService, FamilyLibraryService>();
        services.AddSingleton<IOpenSteamToolService, OpenSteamToolService>();
        services.AddSingleton<ITrainerService, TrainerService>();
		services.AddSingleton<ITrainerAutoLaunchService, TrainerAutoLaunchService>();
		services.AddSingleton<ICloudSyncRescueService, CloudSyncRescueService>();
		services.AddSingleton<ISteamCloudPreferenceService, SteamCloudPreferenceService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ScriptDownloadViewModel>();
        services.AddTransient<ExtractionViewModel>();
        services.AddTransient<TrainerViewModel>();
        services.AddTransient<MainWindow>();
    }
}
