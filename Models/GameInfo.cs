using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class GameInfo : ObservableObject
{
    [ObservableProperty]
    private int _appId;

    [ObservableProperty]
    private string _luaFilePath = string.Empty;

    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _coverImagePath = string.Empty;

    [ObservableProperty]
    private DateTime _luaFileTime;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _isManifestPinned;

    [ObservableProperty]
    private bool _isDisabled;

    [ObservableProperty]
    private int _manifestSourceIndex;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallSizeText))]
    private long _installSizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallSizeText))]
    private long _requiredStorageBytes;

    public string InstallSizeText => InstallSizeBytes > 0
        ? $"占用 {FormatSize(InstallSizeBytes)}"
        : RequiredStorageBytes > 0
            ? $"约需 {FormatSize(RequiredStorageBytes)}"
            : "大小未知";

    public ObservableCollection<DepotInfo> Depots { get; set; } = new();

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024 * 1024):0.##} TB";
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.#} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):0.#} MB";
        return $"{Math.Max(1, bytes / 1024d):0.#} KB";
    }
}
