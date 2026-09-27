using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public sealed partial class GitHubUpdateService : IUpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Konglai-55/MJJsteamtools/releases/latest";
    private static readonly TimeSpan DownloadConnectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(25);

    private readonly IHttpClientProvider _httpClientProvider;

    public GitHubUpdateService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    public async Task<UpdateReleaseInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClientProvider.SendWithProxyRetryAsync(
            "github-updates",
            TimeSpan.FromSeconds(15),
            async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;

                var tagName = ReadString(root, "tag_name");
                if (!TryParseVersion(tagName, out var latestVersion))
                    throw new InvalidDataException($"无法识别 GitHub Release 版本号：{tagName}");

                var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
                currentVersion = NormalizeVersion(currentVersion);

                var releasePageUrl = ReadString(root, "html_url");
                var releaseName = ReadString(root, "name");
                var releaseNotes = ReadString(root, "body");
                var installer = FindInstaller(root);

                return new UpdateReleaseInfo(
                    currentVersion,
                    latestVersion,
                    tagName,
                    string.IsNullOrWhiteSpace(releaseName) ? tagName : releaseName,
                    releaseNotes,
                    installer.Name,
                    installer.Url,
                    installer.Size,
                    installer.Sha256,
                    releasePageUrl);
            },
            client =>
            {
                if (!client.DefaultRequestHeaders.UserAgent.Any())
                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"MJJsteamtools-UpdateChecker/{GetApplicationVersion()}");
            });
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!release.HasInstaller)
            throw new InvalidOperationException("此版本没有可用的 MSI 更新包。");

        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MJJsteamtools",
            "Updates");
        Directory.CreateDirectory(updateDirectory);

        var safeName = SanitizeInstallerName(release.InstallerName, release.LatestVersion);
        var destinationPath = Path.Combine(updateDirectory, safeName);
        var partialPath = destinationPath + ".download";
        var sources = BuildDownloadSources(release);
        progress?.Report(new UpdateDownloadProgress(
            GetPartialLength(partialPath, release.InstallerSize),
            release.InstallerSize,
            Status: "正在测速并选择最快更新线路"));
        sources = await OrderDownloadSourcesAsync(sources, cancellationToken);

        Exception? lastError = null;
        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            var source = sources[sourceIndex];
            var attempt = sourceIndex + 1;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existingBytes = GetPartialLength(partialPath, release.InstallerSize);
                var initialStatus = existingBytes > 0
                    ? $"正在通过{source.Name}续传更新包"
                    : $"正在连接{source.Name}（线路 {attempt}/{sources.Count}）";
                progress?.Report(new UpdateDownloadProgress(
                    existingBytes,
                    release.InstallerSize,
                    Status: initialStatus));

                if (release.InstallerSize <= 0 || existingBytes < release.InstallerSize)
                {
                    await _httpClientProvider.SendWithProxyRetryAsync(
                        $"github-update-download-{sourceIndex}",
                        TimeSpan.FromMinutes(30),
                        client => DownloadInstallerAttemptAsync(
                            client,
                            release,
                            source,
                            partialPath,
                            attempt,
                            sources.Count,
                            progress,
                            cancellationToken),
                        client =>
                        {
                            if (!client.DefaultRequestHeaders.UserAgent.Any())
                                client.DefaultRequestHeaders.UserAgent.ParseAdd($"MJJsteamtools-Updater/{GetApplicationVersion()}");
                        });
                }

                var downloadedBytes = new FileInfo(partialPath).Length;
                if (release.InstallerSize > 0 && downloadedBytes != release.InstallerSize)
                    throw new IOException($"更新包大小不完整：已下载 {downloadedBytes} 字节，应为 {release.InstallerSize} 字节。");

                progress?.Report(new UpdateDownloadProgress(
                    downloadedBytes,
                    release.InstallerSize > 0 ? release.InstallerSize : downloadedBytes,
                    Status: "正在校验更新包完整性"));

                var actualHash = await ComputeFileSha256Async(partialPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(release.InstallerSha256) &&
                    !actualHash.Equals(release.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partialPath);
                    throw new InvalidDataException("更新包 SHA-256 校验失败，已丢弃损坏文件。");
                }

                File.Move(partialPath, destinationPath, overwrite: true);
                progress?.Report(new UpdateDownloadProgress(
                    downloadedBytes,
                    downloadedBytes,
                    Status: "更新包下载完成"));
                return destinationPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(partialPath);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= sources.Count)
                    break;

                var partialBytes = GetPartialLength(partialPath, release.InstallerSize);
                var nextSource = sources[sourceIndex + 1];
                progress?.Report(new UpdateDownloadProgress(
                    partialBytes,
                    release.InstallerSize,
                    Status: $"{source.Name}不可用，正在切换到{nextSource.Name}"));
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        TryDelete(partialPath);
        throw new IOException(
            "官方线路和国内加速线路均未能完成更新下载，请稍后重试或使用手动下载。",
            lastError);
    }

    private async Task DownloadInstallerAttemptAsync(
        HttpClient client,
        UpdateReleaseInfo release,
        DownloadSource downloadSource,
        string partialPath,
        int attempt,
        int sourceCount,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existingBytes = GetPartialLength(partialPath, release.InstallerSize);
        if (release.InstallerSize > 0 && existingBytes >= release.InstallerSize)
            return;

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadSource.Url);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (existingBytes > 0)
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(DownloadConnectTimeout);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                connectCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"连接{downloadSource.Name}超时。", ex);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();

            var isResuming = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!isResuming)
                existingBytes = 0;

            var totalBytes = release.InstallerSize > 0
                ? release.InstallerSize
                : existingBytes + (response.Content.Headers.ContentLength ?? 0);
            progress?.Report(new UpdateDownloadProgress(
                existingBytes,
                totalBytes,
                Status: existingBytes > 0 ? "已连接，正在继续下载" : "已连接，等待接收更新数据"));

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                partialPath,
                isResuming ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[128 * 1024];
            var received = existingBytes;
            long speedBytes = 0;
            var firstChunk = true;
            var speedTimer = Stopwatch.StartNew();
            while (true)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCts.CancelAfter(DownloadIdleTimeout);

                int read;
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(), idleCts.Token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("下载服务器已连接，但 25 秒内未收到任何数据。", ex);
                }

                if (read == 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                speedBytes += read;

                if (firstChunk || speedTimer.ElapsedMilliseconds >= 250)
                {
                    var bytesPerSecond = speedBytes / Math.Max(speedTimer.Elapsed.TotalSeconds, 0.001);
                    progress?.Report(new UpdateDownloadProgress(
                        received,
                        totalBytes,
                        bytesPerSecond,
                        $"正在通过{downloadSource.Name}下载（线路 {attempt}/{sourceCount}）"));
                    speedBytes = 0;
                    firstChunk = false;
                    speedTimer.Restart();
                }
            }

            await destination.FlushAsync(cancellationToken);
            var finalSpeed = speedTimer.Elapsed.TotalSeconds > 0
                ? speedBytes / speedTimer.Elapsed.TotalSeconds
                : 0;
            progress?.Report(new UpdateDownloadProgress(
                received,
                totalBytes > 0 ? totalBytes : received,
                finalSpeed,
                "更新包接收完成"));
        }
    }

    private static long GetPartialLength(string partialPath, long expectedBytes)
    {
        if (!File.Exists(partialPath))
            return 0;

        var length = new FileInfo(partialPath).Length;
        if (expectedBytes > 0 && length > expectedBytes)
        {
            TryDelete(partialPath);
            return 0;
        }

        return length;
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private sealed record DownloadSource(string Name, string Url);

    private sealed record DownloadSourceProbe(DownloadSource Source, int OriginalIndex, long? ElapsedMilliseconds);

    private async Task<List<DownloadSource>> OrderDownloadSourcesAsync(
        List<DownloadSource> sources,
        CancellationToken cancellationToken)
    {
        if (sources.Count <= 1)
            return sources;

        var probes = sources.Select((source, index) =>
            ProbeDownloadSourceAsync(source, index, cancellationToken));
        var results = await Task.WhenAll(probes);

        return results
            .OrderBy(result => result.ElapsedMilliseconds.HasValue ? 0 : 1)
            .ThenBy(result => result.ElapsedMilliseconds ?? long.MaxValue)
            .ThenBy(result => result.OriginalIndex)
            .Select(result => result.Source)
            .ToList();
    }

    private async Task<DownloadSourceProbe> ProbeDownloadSourceAsync(
        DownloadSource source,
        int index,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientProvider.GetClient(
                $"github-update-probe-{index}",
                TimeSpan.FromSeconds(6),
                configuredClient =>
                {
                    if (!configuredClient.DefaultRequestHeaders.UserAgent.Any())
                        configuredClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                            $"MJJsteamtools-UpdaterProbe/{GetApplicationVersion()}");
                });

            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromSeconds(6));

            var timer = Stopwatch.StartNew();
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                probeCts.Token);
            response.EnsureSuccessStatusCode();
            timer.Stop();
            return new DownloadSourceProbe(source, index, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new DownloadSourceProbe(source, index, null);
        }
    }

    private static List<DownloadSource> BuildDownloadSources(UpdateReleaseInfo release)
    {
        var sources = new List<DownloadSource>
        {
            new("GitHub 官方线路", release.InstallerUrl)
        };

        // 第三方线路只在 GitHub 已提供 SHA-256 摘要时启用。下载完成后仍以
        // GitHub API 返回的摘要为准校验，任何被修改或损坏的内容都不会安装。
        if (string.IsNullOrWhiteSpace(release.InstallerSha256) ||
            !Uri.TryCreate(release.InstallerUrl, UriKind.Absolute, out var installerUri) ||
            !installerUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return sources;
        }

        sources.Add(new DownloadSource(
            "国内加速线路一",
            $"https://gh-proxy.com/{release.InstallerUrl}"));
        sources.Add(new DownloadSource(
            "国内加速线路二",
            $"https://ghfast.top/{release.InstallerUrl}"));
        return sources;
    }

    private sealed record InstallerAsset(string Name, string Url, long Size, string Sha256);

    private static InstallerAsset FindInstaller(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return new InstallerAsset(string.Empty, string.Empty, 0, string.Empty);

        foreach (var asset in assets.EnumerateArray())
        {
            var name = ReadString(asset, "name");
            if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = ReadString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;
            var digest = ReadString(asset, "digest");
            var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..]
                : string.Empty;
            return new InstallerAsset(name, url, size, sha256);
        }

        return new InstallerAsset(string.Empty, string.Empty, 0, string.Empty);
    }

    private static string SanitizeInstallerName(string name, Version version)
    {
        var fallback = $"MJJsteamtools-Setup-{version.Major}.{version.Minor}.{version.Build}-x64.msi";
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            return fallback;

        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(name.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var match = VersionPattern().Match(value ?? string.Empty);
        if (!match.Success || !Version.TryParse(match.Value, out var parsed))
        {
            version = new Version(0, 0, 0);
            return false;
        }

        version = NormalizeVersion(parsed);
        return true;
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
