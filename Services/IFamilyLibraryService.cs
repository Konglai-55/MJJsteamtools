using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface IFamilyLibraryService
{
    bool HasSession { get; }
    void SetAccessToken(string accessToken);
    void ClearSession();
    Task<FamilyLibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default);
    IReadOnlySet<int> GetLocallyRunningAppIds(IEnumerable<FamilyGameInfo> games);
}
