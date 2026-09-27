using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface IGamePlayProfileService
{
    IReadOnlyList<GamePlayProfile> GetProfiles(int appId);
    string? GetActiveProfileId(int appId);
    void SetActiveProfile(int appId, string profileId);
    GamePlayProfile SaveCustomProfile(GamePlayProfile profile);
    void DeleteProfile(int appId, string profileId);
}
