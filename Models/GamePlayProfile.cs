using System.Text.Json.Serialization;

namespace SteamLuaManager.Models;

public enum GamePlayProfileKind
{
    Vanilla,
    FullDlc,
    Custom
}

public sealed class GamePlayProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public GamePlayProfileKind Kind { get; set; } = GamePlayProfileKind.Custom;
    public List<int> EnabledDlcAppIds { get; set; } = [];
    public string LaunchArguments { get; set; } = string.Empty;
    public bool BackupSaveBeforeApply { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsBuiltIn => Kind != GamePlayProfileKind.Custom;

    [JsonIgnore]
    public bool CanDelete => !IsBuiltIn;

    [JsonIgnore]
    public bool IsActive { get; set; }

    [JsonIgnore]
    public string TypeText => Kind switch
    {
        GamePlayProfileKind.Vanilla => "纯净模式",
        GamePlayProfileKind.FullDlc => "完整内容",
        _ => "自定义"
    };

    [JsonIgnore]
    public string DlcSummary { get; set; } = "等待读取 DLC";

    [JsonIgnore]
    public string LaunchSummary => string.IsNullOrWhiteSpace(LaunchArguments)
        ? "默认启动参数"
        : LaunchArguments;
}
