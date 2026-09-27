namespace SteamLuaManager.Services;

public interface IUsageTelemetryService : IDisposable
{
    bool IsConfigured { get; }
    void Start();
    void Stop();
}
