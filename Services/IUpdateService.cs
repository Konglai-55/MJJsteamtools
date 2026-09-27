namespace SteamLuaManager.Services;

public sealed record UpdateReleaseInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string TagName,
    string ReleaseName,
    string ReleaseNotes,
    string InstallerName,
    string InstallerUrl,
    long InstallerSize,
    string InstallerSha256,
    string ReleasePageUrl)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
    public bool HasInstaller => !string.IsNullOrWhiteSpace(InstallerUrl);
}

public sealed record UpdateDownloadProgress(
    long BytesReceived,
    long TotalBytes,
    double BytesPerSecond = 0,
    string Status = "")
{
    public int Percentage => TotalBytes <= 0
        ? 0
        : BytesReceived <= 0
            ? 0
            : (int)Math.Clamp(
                Math.Max(1, (BytesReceived * 100L + TotalBytes - 1) / TotalBytes),
                1,
                100);
}

public interface IUpdateService
{
    Task<UpdateReleaseInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task<string> DownloadInstallerAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
