using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ISaveVaultService
{
    Task<IReadOnlyList<SaveGameRecord>> ScanGamesAsync(
        IReadOnlyCollection<GameInfo> games,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SaveGameRecord> ScanGameAsync(GameInfo game, CancellationToken cancellationToken = default);
    Task<SaveOperationResult> CreateSnapshotAsync(
        SaveGameRecord game,
        string reason = "手动备份",
        CancellationToken cancellationToken = default);
    Task<SaveOperationResult> RestoreSnapshotAsync(
        SaveGameRecord game,
        SaveSnapshotInfo snapshot,
        CancellationToken cancellationToken = default);
    IReadOnlyList<SaveSnapshotInfo> GetSnapshots(int appId);
    string GetBackupDirectory(int appId);
}
