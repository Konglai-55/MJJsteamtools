using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public sealed record CachedManifest(string Name, string Source, string Sha256, long Bytes,
    DateTime ImportedUtc, string CachePath)
{
    public string SizeText => $"{Bytes / 1024d:N1} KB";
}

/// <summary>Independent, user-local archive. Never changes Steam files, Lua, or credentials.</summary>
public sealed class ManifestCacheService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string CacheDirectory { get; }
    public ManifestCacheService(string? directory = null) => CacheDirectory = Path.GetFullPath(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MJJsteamtools", "ManifestCache"));

    public IReadOnlyList<CachedManifest> ReadEntries()
    {
        if (!Directory.Exists(CacheDirectory)) return [];
        var result = new List<CachedManifest>();
        foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<CachedManifest>(File.ReadAllText(file));
                if (item is null || !Regex.IsMatch(item.Sha256, "^[A-F0-9]{64}$")) continue;
                var path = Path.Combine(CacheDirectory, item.Sha256 + Path.GetExtension(item.Name));
                if (File.Exists(path)) result.Add(item with { CachePath = path });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(x => x.ImportedUtc).ToArray();
    }

    public async Task<bool> ImportAsync(string path, string source, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 8 || info.Length > 64 * 1024 * 1024)
                throw new InvalidDataException("清单为空或超过 64 MB。");
            var data = await File.ReadAllBytesAsync(path, ct);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Format detection is not an authenticity check; the digest records exact bytes.
            if (ext == ".manifest")
            {
                if (BitConverter.ToUInt32(data, 0) != 0x71F617D0)
                    throw new InvalidDataException("文件不是 Steam 二进制清单。");
            }
            else if (ext == ".txt")
            {
                var header = System.Text.Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(1024, data.Length)));
                if (!header.StartsWith("Content Manifest for Depot ", StringComparison.Ordinal) ||
                    !header.Contains("Manifest ID / date", StringComparison.Ordinal))
                    throw new InvalidDataException("文件不是 DepotDownloader 导出的文本清单。");
            }
            else throw new InvalidDataException("请选择 .manifest 或 DepotDownloader 的清单 .txt 文件。");
            var hash = Convert.ToHexString(SHA256.HashData(data));
            Directory.CreateDirectory(CacheDirectory);
            var metadata = Path.Combine(CacheDirectory, hash + ".json");
            if (File.Exists(metadata) && ReadEntries().Any(x => x.Sha256 == hash)) return false;
            var cachedPath = Path.Combine(CacheDirectory, hash + ext);
            await File.WriteAllBytesAsync(cachedPath, data, ct);
            var item = new CachedManifest(Path.GetFileName(path), source, hash, data.Length, DateTime.UtcNow, cachedPath);
            var temporary = metadata + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(item), ct);
            File.Move(temporary, metadata, true);
            return true;
        }
        finally { _gate.Release(); }
    }

    public static ProcessStartInfo CreateFetchCommand(string executable, int appId, string username, string output)
    {
        if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("请输入 Steam 登录账号名。");
        if (!Path.GetFileName(executable).Equals("DepotDownloader.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable)) throw new ArgumentException("请选择 DepotDownloader.exe。");
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = false,
            WorkingDirectory = Path.GetFullPath(output)
        };
        foreach (var arg in new[] { "-app", appId.ToString(), "-username", username.Trim(),
            "-manifest-only", "-dir", Path.GetFullPath(output), "-loginid", "17352701" })
            start.ArgumentList.Add(arg);
        return start;
    }

    public async Task<int> FetchAsync(string executable, int appId, string username, CancellationToken ct)
    {
        var output = Path.Combine(CacheDirectory, "sessions", Guid.NewGuid().ToString("N"));
        var start = CreateFetchCommand(executable, appId, username, output);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        using var process = Process.Start(start) ?? throw new IOException("下载器启动失败。");
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        if (process.ExitCode != 0) throw new IOException($"下载器返回错误 {process.ExitCode}，请查看登录窗口中的信息。");
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(output, "manifest_*.txt", SearchOption.AllDirectories))
            if (await ImportAsync(file, $"Steam 账号获取 · AppID {appId}", ct)) count++;
        if (count == 0) throw new IOException("没有生成新的清单，请确认账号权限和下载器窗口中的结果。");
        return count;
    }
}
