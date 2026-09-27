using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ISteamAchievementService
{
    Task<AchievementLoadResult> LoadAsync(
        int appId,
        string? installPath,
        CancellationToken cancellationToken = default);

    Task<AchievementOperationResult> SetAchievementAsync(
        int appId,
        string? installPath,
        string apiName,
        bool unlocked,
        CancellationToken cancellationToken = default);

    Task<AchievementOperationResult> SetAllAchievementsAsync(
        int appId,
        string? installPath,
        IReadOnlyCollection<string> apiNames,
        bool unlocked,
        CancellationToken cancellationToken = default);
}
