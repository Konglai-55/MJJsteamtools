using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public sealed record SaveAutoBackupEvent(
    int AppId,
    bool Success,
    string Message,
    DateTimeOffset OccurredAt);

public interface ISaveAutoBackupService : IDisposable
{
    event Action<SaveAutoBackupEvent>? BackupCompleted;

    bool IsEnabled(int appId);
    DateTimeOffset? GetLastBackupTime(int appId);
    void SetEnabled(int appId, bool enabled);
    void Start(Func<IReadOnlyCollection<GameInfo>> gameProvider);
    void Stop();
}
