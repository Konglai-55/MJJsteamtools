using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public sealed class DlcManagementService : IDlcManagementService
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public bool IsEnabled(string luaContent, int dlcAppId) =>
        ActiveLineRegex(dlcAppId).IsMatch(luaContent);

    public async Task<DlcToggleResult> SetEnabledAsync(
        string luaPath,
        IReadOnlyCollection<int> dlcAppIds,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (dlcAppIds.Count == 0)
            return new DlcToggleResult(false, 0, "没有可操作的 DLC");
        if (string.IsNullOrWhiteSpace(luaPath) || !File.Exists(luaPath))
            return new DlcToggleResult(false, 0, "当前游戏的 Lua 文件不存在");

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var raw = await File.ReadAllBytesAsync(luaPath, cancellationToken);
            var hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var content = Encoding.UTF8.GetString(raw, hasBom ? 3 : 0, raw.Length - (hasBom ? 3 : 0));
            var updated = content;
            var changed = 0;

            foreach (var dlcAppId in dlcAppIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentlyEnabled = IsEnabled(updated, dlcAppId);
                if (currentlyEnabled == enabled) continue;

                updated = enabled
                    ? EnableDlc(updated, dlcAppId)
                    : DisableDlc(updated, dlcAppId);
                if (IsEnabled(updated, dlcAppId) == enabled)
                    changed++;
            }

            if (changed == 0)
                return new DlcToggleResult(true, 0, "所选 DLC 已经是目标状态");

            var gameAppId = int.TryParse(Path.GetFileNameWithoutExtension(luaPath), out var parsed) ? parsed : 0;
            var backupDirectory = GetBackupDirectory(gameAppId);
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(
                backupDirectory,
                $"{Path.GetFileNameWithoutExtension(luaPath)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.lua");
            File.Copy(luaPath, backupPath, overwrite: false);

            var directory = Path.GetDirectoryName(luaPath)
                            ?? throw new InvalidOperationException("无法确定 Lua 文件目录");
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(luaPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(
                    tempPath,
                    updated,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom),
                    cancellationToken);
                File.Replace(tempPath, luaPath, null, ignoreMetadataErrors: true);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }

            return new DlcToggleResult(
                true,
                changed,
                enabled
                    ? $"已启用 {changed} 个 DLC，重启 Steam 后生效"
                    : $"已禁用 {changed} 个 DLC，重启 Steam 后生效");
        }
        catch (OperationCanceledException)
        {
            return new DlcToggleResult(false, 0, "DLC 操作已取消");
        }
        catch (Exception ex)
        {
            return new DlcToggleResult(false, 0, $"修改 DLC 状态失败：{ex.GetBaseException().Message}");
        }
        finally
        {
            FileLock.Release();
        }
    }

    public string GetBackupDirectory(int gameAppId) =>
        Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Cache",
            "DlcBackups",
            gameAppId > 0 ? gameAppId.ToString() : "Unknown");

    private static string DisableDlc(string content, int dlcAppId)
    {
        var regex = ActiveLineRegex(dlcAppId);
        return regex.Replace(content, match =>
            $"{match.Groups["indent"].Value}-- MJJ:DLC-OFF {dlcAppId} | {match.Groups["body"].Value}");
    }

    private static string EnableDlc(string content, int dlcAppId)
    {
        var disabled = DisabledLineRegex(dlcAppId);
        var updated = disabled.Replace(content, match =>
            $"{match.Groups["indent"].Value}{match.Groups["body"].Value}");
        if (IsActive(updated, dlcAppId)) return updated;

        var newline = updated.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (!updated.EndsWith("\n", StringComparison.Ordinal)) updated += newline;
        return updated
               + $"{newline}-- MJJ:DLC-BEGIN {dlcAppId}{newline}"
               + $"addappid({dlcAppId}){newline}"
               + $"-- MJJ:DLC-END {dlcAppId}{newline}";
    }

    private static bool IsActive(string content, int dlcAppId) =>
        ActiveLineRegex(dlcAppId).IsMatch(content);

    private static Regex ActiveLineRegex(int dlcAppId) => new(
        $@"^(?<indent>[ \t]*)(?<body>add(?:appid|tokenid|token)\s*\(\s*{dlcAppId}(?=\s*[,\)]).*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static Regex DisabledLineRegex(int dlcAppId) => new(
        $@"^(?<indent>[ \t]*)--[ \t]*MJJ:DLC-OFF[ \t]+{dlcAppId}[ \t]*\|[ \t]*(?<body>add(?:appid|tokenid|token)\s*\(\s*{dlcAppId}(?=\s*[,\)]).*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
}
