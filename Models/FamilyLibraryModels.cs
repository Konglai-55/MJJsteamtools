using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public sealed class FamilyLibrarySnapshot
{
    public List<FamilyMemberInfo> Members { get; init; } = [];
    public List<FamilyGameInfo> Games { get; init; } = [];
    public string DataScopeText { get; init; } = string.Empty;
    public string FamilyName { get; init; } = string.Empty;
    public bool RequiresConnection { get; init; }
}

public sealed class FamilyMemberInfo
{
    public string SteamId { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
    public string PersonaName { get; init; } = string.Empty;
    public string AvatarPath { get; init; } = string.Empty;
    public bool IsMostRecent { get; init; }
    public int RecentPlaytimeMinutes { get; init; }
    public int TotalPlaytimeMinutes { get; init; }
    public string RoleText { get; init; } = "家庭成员";
    public string DetailText { get; init; } = string.Empty;
    public string SecondaryText { get; init; } = string.Empty;
    public string TertiaryText { get; init; } = string.Empty;
}

public partial class FamilyGameInfo : ObservableObject
{
    public int AppId { get; init; }
    public string InstallPath { get; init; } = string.Empty;
    public long LastPlayedUnix { get; init; }
    public int RecentPlaytimeMinutes { get; init; }
    public int TotalPlaytimeMinutes { get; init; }
    public bool IsInstalled { get; init; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _coverImage = string.Empty;

    [ObservableProperty]
    private string _ownerDisplay = "归属需在 Steam 确认";

    [ObservableProperty]
    private string _ownerEvidence = "未在本机找到可靠的许可证归属缓存";

    [ObservableProperty]
    private string _shareStatus = "正在确认共享资格";

    [ObservableProperty]
    private string _shareStatusKind = "Unknown";

    [ObservableProperty]
    private string _availability = "远程占用需由 Steam 确认";

    [ObservableProperty]
    private bool _isRunningLocally;

    public string AppIdText => $"AppID: {AppId}";
    public string InstallStatusText => IsInstalled ? "已安装" : "本机有游玩记录";
    public string PlaytimeText => TotalPlaytimeMinutes <= 0
        ? "暂无本机时长"
        : $"本机记录 {(TotalPlaytimeMinutes / 60d):0.#} 小时";
}
