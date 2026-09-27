using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class SteamAchievement : ObservableObject
{
    public required string ApiName { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;
    public string LockedIconUrl { get; init; } = string.Empty;
    public bool IsHidden { get; init; }
    public string StatId { get; init; } = string.Empty;
    public int BitIndex { get; init; } = -1;

    [ObservableProperty]
    private bool _isUnlocked;

    [ObservableProperty]
    private uint _unlockTimeUnix;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _canManage;

    public string ActiveIconUrl => IsUnlocked || string.IsNullOrWhiteSpace(LockedIconUrl)
        ? IconUrl
        : LockedIconUrl;

    public string StateText => IsUnlocked ? "已解锁" : "未解锁";

    public string UnlockTimeText => IsUnlocked && UnlockTimeUnix > 0
        ? DateTimeOffset.FromUnixTimeSeconds(UnlockTimeUnix).LocalDateTime.ToString("yyyy年M月d日 HH:mm")
        : string.Empty;

    partial void OnIsUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(ActiveIconUrl));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(UnlockTimeText));
    }

    partial void OnUnlockTimeUnixChanged(uint value) =>
        OnPropertyChanged(nameof(UnlockTimeText));
}

public sealed record AchievementLoadResult(
    IReadOnlyList<SteamAchievement> Achievements,
    bool CanManage,
    bool HasLiveState,
    string StatusMessage);

public sealed record AchievementOperationResult(bool Success, string Message);

public sealed record AchievementHelperRequest(
    int AppId,
    string? InstallPath,
    string[] ApiNames,
    bool Unlocked);
