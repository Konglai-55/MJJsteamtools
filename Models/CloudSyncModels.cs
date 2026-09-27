using System.IO;

namespace SteamLuaManager.Models;

public enum CloudSyncIssueKind
{
    Conflict,
    Network,
    Permission,
    Quota,
    Unknown
}

public enum CloudSyncRiskLevel
{
    Low,
    Medium,
    High
}

public sealed class CloudConflictFile
{
    public required string RelativePath { get; init; }
    public string? LocalPath { get; init; }
    public CloudSyncRiskLevel RiskLevel { get; init; }
    public required string TypeText { get; init; }
    public string DisplayPath => RelativePath.Replace('/', '\\');
    public bool HasLocalPath => !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);
}

public sealed class CloudSyncIssue
{
    public int AppId { get; init; }
    public required string GameName { get; init; }
    public CloudSyncIssueKind Kind { get; init; }
    public CloudSyncRiskLevel RiskLevel { get; init; }
    public DateTime LastErrorTime { get; init; }
    public required string LastError { get; init; }
    public required string Summary { get; init; }
    public required string SuggestedAction { get; init; }
    public required IReadOnlyList<CloudConflictFile> ConflictFiles { get; init; }
    public bool IsImportedGame { get; init; }
    public bool IsRebound { get; init; }
    public int ConflictCount => ConflictFiles.Count;
    public bool HasConflictFiles => ConflictCount > 0;
    public bool CanResetMetadata => Kind == CloudSyncIssueKind.Conflict && !IsRebound;
    public bool ShouldRecommendDisableCloud => IsImportedGame || IsRebound;
    public bool ShouldOfferSaveVault => IsImportedGame || IsRebound;
    public string AppIdText => $"AppID: {AppId}";
    public string LastErrorTimeText => LastErrorTime == default
        ? "时间未知"
        : LastErrorTime.ToString("yyyy年M月d日 HH:mm:ss");
    public string RiskText => RiskLevel switch
    {
        CloudSyncRiskLevel.High => "高风险 · 可能包含存档",
        CloudSyncRiskLevel.Medium => "需要确认",
        _ => "低风险 · 设置文件"
    };
    public string RiskColor => RiskLevel switch
    {
        CloudSyncRiskLevel.High => "#F06A6A",
        CloudSyncRiskLevel.Medium => "#F0B83F",
        _ => "#68C482"
    };
    public string KindText => Kind switch
    {
        CloudSyncIssueKind.Conflict => "文件冲突",
        CloudSyncIssueKind.Network => "网络异常",
        CloudSyncIssueKind.Permission => "权限异常",
        CloudSyncIssueKind.Quota => "云空间异常",
        _ => "同步异常"
    };
    public string SourceText => IsImportedGame ? "检测到一键入库配置" : "Steam 库游戏";
    public string ReboundText => IsRebound ? "修复后再次报错" : string.Empty;
}

public sealed class CloudSyncScanResult
{
    public required string SteamPath { get; init; }
    public required string LogPath { get; init; }
    public required IReadOnlyList<CloudSyncIssue> Issues { get; init; }
    public int ResolvedHistoryCount { get; init; }
    public int ScannedAppCount { get; init; }
    public DateTime LastLogTime { get; init; }
}

public sealed record CloudBackupResult(bool Success, string Message, string? BackupPath = null);

public sealed record CloudRepairResult(bool Success, string Message, string? BackupPath = null);
