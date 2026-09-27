using System.IO;
using System.Text.Json;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class AppSettings
{
    public string SteamPath { get; set; } = string.Empty;
    public bool AutoRefreshEnabled { get; set; } = true;
    public int SelectedCdnIndex { get; set; }
    public string SelectedViewMode { get; set; } = "卡片";
    public string SelectedBackdrop { get; set; } = "Acrylic10";
    public string DownloadMode { get; set; } = "DepotKey";
    public string KeyFolderPath { get; set; } = string.Empty;
    public string ProxyMode { get; set; } = "Auto";
    public string ManualProxy { get; set; } = string.Empty;
    public bool IsCardRefreshVisible { get; set; } = true;
    public string SelectedTheme { get; set; } = "System";
    public bool ShowTrainerSections { get; set; } = true;
    public bool ShowCopyLogButton { get; set; }
    public bool AnonymousUsageStatisticsEnabled { get; set; } = true;
    public bool DisableCloudForImportedGames { get; set; } = true;
    public string SaveBackupPath { get; set; } = string.Empty;
    public int SaveBackupRetention { get; set; } = 20;
    public List<TrainerBinding> TrainerBindings { get; set; } = new();
    public List<int> FavoriteAppIds { get; set; } = new();
}

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
    event Action<AppSettings>? SettingsChanged;
}

public class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsFilePath;

    public SettingsService()
    {
        _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                if (string.IsNullOrWhiteSpace(settings.DownloadMode))
                    settings.DownloadMode = "DepotKey";
                return settings;
            }
        }
        catch { }
        return new AppSettings();
    }

    public event Action<AppSettings>? SettingsChanged;

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
            SettingsChanged?.Invoke(settings);
        }
        catch { }
    }
}
