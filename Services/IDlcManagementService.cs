namespace SteamLuaManager.Services;

public sealed record DlcToggleResult(bool Success, int ChangedCount, string Message);

public interface IDlcManagementService
{
    bool IsEnabled(string luaContent, int dlcAppId);

    Task<DlcToggleResult> SetEnabledAsync(
        string luaPath,
        IReadOnlyCollection<int> dlcAppIds,
        bool enabled,
        CancellationToken cancellationToken = default);

    string GetBackupDirectory(int gameAppId);
}
