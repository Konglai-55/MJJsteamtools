using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public interface IOpenSteamToolService
{
    bool IsInstalled { get; }
    string? GetSteamPath();
    Task<string?> GetLocalVersionAsync();
    Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync();
    Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default);
    /// <summary>
    /// Ensures OpenSteamTool has a manifest-code fallback in Steam's config/lua
    /// directory. The file is created only when it does not already exist, so
    /// user supplied Lua remains authoritative.
    /// </summary>
    Task<string?> EnsureManifestFallbackAsync(CancellationToken ct = default);
    Task UninstallAsync();
}

public class OpenSteamToolService : IOpenSteamToolService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private const string GitHubLatestUrl = "https://api.github.com/repos/mmxlyo/OpenSteamTool/releases/latest";
    private static readonly string[] ChinaGitHubProxyPrefixes =
    [
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://ghproxy.net/"
    ];
    private static readonly string[] RequiredDlls = ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"];
    private static readonly Dictionary<string, string> EmbeddedVersionMap = new()
    {
        ["115ec256c7c5b066926015a24120cf6e7d9e5a7a5b87441817c2de11cc3f9fec"] = "v1.2.0",
        ["494bc762351b4dc80ca2f36cc005fc89b976f24e6e77c12945229e3e05502e93"] = "v1.3.0",
        ["8d4cb44bc57565e8183b9dab72eda873305c4257e080e29d57bbfda4cc755585"] = "v1.3.1",
        ["6daeef8b0a085c22ca43a6efeceee1f8547c3044573c394ec7cd4945fba13430"] = "v1.3.2",
        ["a1c4ffc819d96d9c397d132cb718aa7d7d44651375845e4bb9258499e643857d"] = "v1.4.0",
        ["550f9edfede4a4403f7aefdd5c4a40fdd92be22135443857fd997b415d7ced1e"] = "v1.4.1",
        ["962f5c7700a0ddde46cd419763ed15f95baf5a4a93525559f7bb6453aa1b1aac"] = "v1.4.2",
        ["d578da0170d18cd8f7cdee36a617a80147bddc8945701e3d5d1f11315d7e36fd"] = "v1.4.3",
        ["9113dce46b7a807e30abc018ee8469f188c51e2d277279c2f15427efc2f52226"] = "v1.4.4",
        ["5ec8351d5949c10c97210759efb5d618741d8414d67ff650e328d036e352c10c"] = "v1.4.5",
        ["cd7266e06d7416d3b02335386c54b909df68e2e0605941f70bb21ed392ee639f"] = "v1.4.6",
        ["9a2c459ad5124eeb48e4a1c7ac9808e5fad6eda54f7df8ad4b2e4465818f50f7"] = "v1.4.6-fix",
        ["09d26118c7cf796cf37562c4cb965d1b213f5866c02ca04fe0fafc4c2f22b0bc"] = "v1.4.7",
    };
    private static readonly byte[] VersionMarker = [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00];
    private const string ManifestFallbackFileName = "000_mjjst_manifest_fallback.lua";
    private const string ManifestFallbackMarker = "MJJSTEAMTOOLS_MANIFEST_FALLBACK_V1";
    private const string ManifestConfig = """
# MJJsteamtools managed defaults. Existing OpenSteamTool configuration is never overwritten.
[manifest]
# wudrm is generally reachable on mainland networks; the Lua fallback tries all mirrors first.
url = "wudrm"
lock_owned_games = false
auto_sync_on_update = true
""";
    private const string ManifestFallbackLua = """
-- MJJSTEAMTOOLS_MANIFEST_FALLBACK_V1
--
-- OpenSteamTool calls this hook before its configured manifest provider.  The
-- endpoints are intentionally tried in order: the first two are HTTPS mirrors,
-- wudrm is useful on mainland networks, and steam.run is a JSON fallback.
-- Return a digit string; manifest ids can exceed Lua's exact integer range.
function fetch_manifest_code(gid)
    local providers = {
        "https://manifest.manifestdex.com/" .. gid,
        "https://manifest.opensteamtool.com/" .. gid,
        "http://gmrc.wudrm.com/manifest/" .. gid,
        "https://manifest.steam.run/api/manifest/" .. gid
    }
    for _, url in ipairs(providers) do
        local body, status = http_get(url, { ["User-Agent"] = "MJJsteamtools/manifest-fallback" })
        if status == 200 and body then
            local code = body:match("^%s*(%d+)%s*$")
            if not code then
                code = body:match('"content"%s*:%s*"(%d+)"')
            end
            if code then return code end
        end
    end
    return nil
end
""";

    public OpenSteamToolService(ISteamPathService steamPathService, IHttpClientProvider httpClientProvider)
    {
        _steamPathService = steamPathService;
        _httpClientProvider = httpClientProvider;
    }

    private static void ConfigureHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MJJsteamtools/2.0");
    }

    public bool IsInstalled => _steamPathService.DetectSteamToolType() == SteamToolType.OpenSteamTool;

    public string? GetSteamPath()
    {
        var path = _steamPathService.DetectSteamPath();
        return !string.IsNullOrEmpty(path) ? path : null;
    }

    public Task<string?> GetLocalVersionAsync()
    {
        var steamPath = GetSteamPath();
        if (steamPath == null) return Task.FromResult<string?>(null);
        var dllPath = Path.Combine(steamPath, "OpenSteamTool.dll");
        if (!File.Exists(dllPath)) return Task.FromResult<string?>(null);

        // 1. 嵌入 SHA256 字典（覆盖 pre-1.4.8 所有官方构建）
        var localHash = ComputeSha256(dllPath);
        if (EmbeddedVersionMap.TryGetValue(localHash, out var embeddedVer))
            return Task.FromResult<string?>(embeddedVer);

        // 2. 二进制标记位解析（覆盖 1.4.8+ 版本）
        try
        {
            var bytes = File.ReadAllBytes(dllPath);
            for (int i = 0; i <= bytes.Length - 12; i++)
            {
                var found = true;
                for (int j = 0; j < VersionMarker.Length; j++)
                {
                    if (bytes[i + j] != VersionMarker[j]) { found = false; break; }
                }
                if (!found) continue;

                var start = i + VersionMarker.Length;
                var end = start;
                while (end < bytes.Length && bytes[end] != 0) end++;
                if (end > start)
                {
                    var ver = Encoding.ASCII.GetString(bytes, start, end - start);
                    if (Regex.IsMatch(ver, @"^v?\d+\.\d+\.\d+"))
                        return Task.FromResult<string?>(ver);
                }
            }
        }
        catch { }

        return Task.FromResult<string?>(null);
    }

    public async Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync()
    {
        string? json = null;
        Exception? lastError = null;
        foreach (var endpoint in BuildApiCandidates())
        {
            try
            {
                json = await _httpClientProvider.SendWithProxyRetryAsync(
                    "open-steam-tool-metadata",
                    TimeSpan.FromSeconds(35),
                    client => client.GetStringAsync(endpoint),
                    ConfigureHeaders);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    // Some public accelerators return an HTML rate-limit page
                    // with status 200. Validate the payload before accepting
                    // that endpoint, otherwise the next mirror is never tried.
                    using var probe = JsonDocument.Parse(json);
                    if (probe.RootElement.TryGetProperty("tag_name", out _)) break;
                    json = null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
            {
                lastError = ex;
            }
        }

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("无法获取 OpenSteamTool 更新信息，请检查网络或代理设置", lastError);

        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "0.0.0";
        var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var htmlUrl)
            ? htmlUrl.GetString() ?? ""
            : $"https://github.com/mmxlyo/OpenSteamTool/releases/tag/{tag}";
        var downloadUrl = "";
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("-Release") && name.EndsWith(".zip"))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    break;
                }
            }
        }
        return (tag, downloadUrl, releaseUrl);
    }

    public async Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        status?.Report("正在下载 OpenSteamTool...");

        var tempZip = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}.zip");
        try
        {
            Exception? lastError = null;
            var candidates = BuildDownloadCandidates(downloadUrl).ToArray();
            for (var i = 0; i < candidates.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = candidates[i];
                try
                {
                    status?.Report($"正在下载 OpenSteamTool… ({i + 1}/{candidates.Length})");
                    await DownloadArchiveAsync(candidate, tempZip, downloadProgress, ct);
                    ct.ThrowIfCancellationRequested();
                    status?.Report("正在校验并安装 DLL…");
                    var extracted = ExtractRequiredDlls(tempZip, steamPath, ct);
                    if (extracted == 0)
                        throw new InvalidDataException("下载文件不是有效的 OpenSteamTool 发布包");

                    // The upstream 1.4.8+ loader supports Lua manifest providers.
                    await EnsureManifestFallbackAsync(ct);
                    status?.Report("安装完成");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsRecoverableDownloadError(ex))
                {
                    lastError = ex;
                    try { File.Delete(tempZip); } catch { }
                    status?.Report($"该下载源失败，正在切换备用源…");
                }
            }

            throw new InvalidOperationException(
                "OpenSteamTool 下载失败，已尝试官方源和中国区加速源。请稍后重试或检查代理设置。",
                lastError);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    private async Task DownloadArchiveAsync(string url, string targetPath, IProgress<int>? progress, CancellationToken ct)
    {
        using var response = await _httpClientProvider.SendWithProxyRetryAsync(
            "open-steam-tool-download",
            TimeSpan.FromSeconds(45),
            client => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead),
            ConfigureHeaders);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? -1;

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(targetPath);
        var buffer = new byte[81920];
        long readBytes = 0;
        int bytesRead;
        while ((bytesRead = await httpStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            readBytes += bytesRead;
            if (totalBytes > 0)
                progress?.Report(Math.Clamp((int)(readBytes * 100 / totalBytes), 0, 100));
        }
    }

    private static int ExtractRequiredDlls(string zipPath, string steamPath, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries
            .Where(entry => RequiredDlls.Contains(Path.GetFileName(entry.Name), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length == 0) return 0;

        var staging = Path.Combine(Path.GetTempPath(), $"MJJsteamtools_ost_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry.Name);
                entry.ExtractToFile(Path.Combine(staging, name), overwrite: true);
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry.Name);
                File.Move(Path.Combine(staging, name), Path.Combine(steamPath, name), overwrite: true);
            }
            return entries.Length;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static bool IsRecoverableDownloadError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException or InvalidDataException;

    private static IEnumerable<string> BuildApiCandidates()
    {
        if (IsChinaUser())
        {
            foreach (var prefix in ChinaGitHubProxyPrefixes)
                yield return prefix + GitHubLatestUrl;
        }
        yield return GitHubLatestUrl;
    }

    private static IEnumerable<string> BuildDownloadCandidates(string downloadUrl)
    {
        if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (IsChinaUser())
            {
                foreach (var prefix in ChinaGitHubProxyPrefixes)
                    yield return prefix + downloadUrl;
            }
        }
        yield return downloadUrl;
    }

    private static bool IsChinaUser()
    {
        try
        {
            var region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            if (region.Equals("CN", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return CultureInfo.CurrentUICulture.Name.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase) ||
               CultureInfo.InstalledUICulture.Name.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> EnsureManifestFallbackAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var steamPath = GetSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath)) return null;

        var luaFolder = Path.Combine(steamPath, "config", "lua");
        Directory.CreateDirectory(luaFolder);
        var fallbackPath = Path.Combine(luaFolder, ManifestFallbackFileName);

        // Never replace an existing file.  A user may have intentionally put a
        // same-named script there; their script must remain untouched.
        if (File.Exists(fallbackPath)) return fallbackPath;

        var tempPath = fallbackPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, ManifestFallbackLua, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, fallbackPath, overwrite: false);

            // OpenSteamTool's built-in provider is manifestdex.  A fresh config
            // opts into the China-friendly provider while keeping the Lua mirror
            // chain as the first choice.  Existing user settings are preserved.
            var configPath = Path.Combine(steamPath, "opensteamtool.toml");
            if (!File.Exists(configPath))
            {
                var configTemp = configPath + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllTextAsync(configTemp, ManifestConfig, new UTF8Encoding(false), ct);
                    File.Move(configTemp, configPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(configPath)) { }
                finally
                {
                    try { if (File.Exists(configTemp)) File.Delete(configTemp); } catch { }
                }
            }
            return fallbackPath;
        }
        catch (IOException) when (File.Exists(fallbackPath))
        {
            // Another MJJsteamtools process won the race to create it.
            return fallbackPath;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public Task UninstallAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        var removed = 0;
        foreach (var dll in RequiredDlls)
        {
            var path = Path.Combine(steamPath, dll);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"无法删除 {dll}，请确保 Steam 已关闭后再试");
                }
            }
        }

        // Remove only the fallback created by this app; never touch a user's
        // other Lua/config files.
        var generatedFallback = Path.Combine(steamPath, "config", "lua", ManifestFallbackFileName);
        try
        {
            if (File.Exists(generatedFallback) &&
                File.ReadAllText(generatedFallback).Contains(ManifestFallbackMarker, StringComparison.Ordinal))
                File.Delete(generatedFallback);
        }
        catch { }
        if (removed == 0)
            throw new InvalidOperationException("未检测到已安装的 OpenSteamTool 文件");
        return Task.CompletedTask;
    }

    // ========== 辅助方法 ==========

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
