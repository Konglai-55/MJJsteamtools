using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ICloudSyncRescueService
{
    string BackupRootPath { get; }
    Task<CloudSyncScanResult> ScanAsync(CancellationToken cancellationToken = default);
    Task<CloudBackupResult> BackupAsync(CloudSyncIssue issue, CancellationToken cancellationToken = default);
    Task<CloudRepairResult> ResetSyncMetadataAsync(CloudSyncIssue issue, CancellationToken cancellationToken = default);
    void OpenLogFolder();
    void OpenBackupFolder();
    void OpenGameInSteam(int appId);
}

public sealed class CloudSyncRescueService : ICloudSyncRescueService
{
    private static readonly Regex LogLineRegex = new(
        @"^\[(?<time>[^\]]+)\].*?\[AppID (?<appId>\d+)\]\s+(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ConflictFileRegex = new(
        @"(?:Sync conflict for file\s+|^(?<prefix>.+?)\s+(?:already marked as conflicting|has local changes but mismatch))(?<file>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PersistingFileRegex = new(
        @"Persisting file\s+(?<path>.+?)\s+to the cloud",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ISteamPathService _steamPathService;
    private readonly ISteamCloudPreferenceService _steamCloudPreferenceService;
    private readonly object _repairHistoryLock = new();

    public CloudSyncRescueService(
        ISteamPathService steamPathService,
        ISteamCloudPreferenceService steamCloudPreferenceService)
    {
        _steamPathService = steamPathService;
        _steamCloudPreferenceService = steamCloudPreferenceService;
        BackupRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DreamPixel",
            "MJJsteamtools",
            "CloudRescueBackups");
    }

    public string BackupRootPath { get; }

    public Task<CloudSyncScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ScanCore(cancellationToken), cancellationToken);
    }

    private CloudSyncScanResult ScanCore(CancellationToken cancellationToken)
    {
        var steamPath = GetSteamPath();
        var logPath = Path.Combine(steamPath, "logs", "cloud_log.txt");
        if (!File.Exists(logPath))
            throw new FileNotFoundException("没有找到 Steam 云同步日志。请先启动一次 Steam 后再扫描。", logPath);

        var eventsByApp = new Dictionary<int, List<CloudLogEvent>>();
        using var stream = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = LogLineRegex.Match(line);
            if (!match.Success ||
                !int.TryParse(match.Groups["appId"].Value, out var appId))
                continue;

            _ = DateTime.TryParse(match.Groups["time"].Value, out var time);
            if (!eventsByApp.TryGetValue(appId, out var events))
            {
                events = [];
                eventsByApp[appId] = events;
            }

            events.Add(new CloudLogEvent(time, match.Groups["message"].Value.Trim()));
        }

        var issues = new List<CloudSyncIssue>();
        var resolvedHistoryCount = 0;
        var lastLogTime = DateTime.MinValue;

        foreach (var (appId, events) in eventsByApp)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (events.Count == 0)
                continue;

            lastLogTime = events.Max(item => item.Time > lastLogTime ? item.Time : lastLogTime);
            var lastSuccessIndex = events.FindLastIndex(item => IsSuccessfulTerminalEvent(item.Message));
            var lastErrorIndex = events.FindLastIndex(item => IsErrorEvent(item.Message));
            if (lastErrorIndex < 0)
                continue;
            if (lastSuccessIndex > lastErrorIndex)
            {
                resolvedHistoryCount++;
                continue;
            }

            var activeEvents = events.Skip(Math.Max(0, lastSuccessIndex + 1)).ToList();
            var errorEvents = activeEvents.Where(item => IsErrorEvent(item.Message)).ToList();
            if (errorEvents.Count == 0)
                continue;

            // A historical error remains in cloud_log.txt even after the user
            // disables Cloud for that game. Do not keep reporting it as active.
            var cloudState = _steamCloudPreferenceService.GetCurrentAccountState(appId);
            if (cloudState is { IsEnabled: false })
            {
                resolvedHistoryCount++;
                continue;
            }

            var conflictPaths = ExtractConflictPaths(activeEvents);
            var persistedPaths = ExtractPersistedPaths(activeEvents);
            var files = conflictPaths
                .Select(path => CreateConflictFile(path, persistedPaths))
                .ToList();
            var kind = DetermineIssueKind(errorEvents, files);
            var risk = files.Count == 0
                ? CloudSyncRiskLevel.Medium
                : files.Max(file => file.RiskLevel);
            var lastError = errorEvents[^1];
            var gameName = ResolveGameName(appId);
            var isImportedGame = HasOneClickImportConfig(steamPath, appId);
            var lastRepairTime = GetLastRepairTime(steamPath, appId);
            var isRebound = lastRepairTime.HasValue && lastError.Time > lastRepairTime.Value;

            issues.Add(new CloudSyncIssue
            {
                AppId = appId,
                GameName = gameName,
                Kind = kind,
                RiskLevel = risk,
                LastErrorTime = lastError.Time,
                LastError = SimplifyError(lastError.Message),
                Summary = BuildSummary(kind, files.Count),
                SuggestedAction = BuildSuggestion(kind, risk, isImportedGame, isRebound),
                ConflictFiles = files,
                IsImportedGame = isImportedGame,
                IsRebound = isRebound
            });
        }

        return new CloudSyncScanResult
        {
            SteamPath = steamPath,
            LogPath = logPath,
            Issues = issues.OrderByDescending(issue => issue.LastErrorTime).ToList(),
            ResolvedHistoryCount = resolvedHistoryCount,
            ScannedAppCount = eventsByApp.Count,
            LastLogTime = lastLogTime
        };
    }

    public async Task<CloudBackupResult> BackupAsync(
        CloudSyncIssue issue,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = await Task.Run(() => BackupCore(issue, cancellationToken), cancellationToken);
            return new CloudBackupResult(true, "安全备份已创建，可以继续处理云同步问题。", path);
        }
        catch (Exception ex)
        {
            return new CloudBackupResult(false, $"创建备份失败：{ex.GetBaseException().Message}");
        }
    }

    public async Task<CloudRepairResult> ResetSyncMetadataAsync(
        CloudSyncIssue issue,
        CancellationToken cancellationToken = default)
    {
        if (!issue.CanResetMetadata)
            return new CloudRepairResult(false, "该问题不是文件冲突，不适合重建同步状态。请先处理网络或权限问题。");

        var backup = await BackupAsync(issue, cancellationToken);
        if (!backup.Success || string.IsNullOrWhiteSpace(backup.BackupPath))
            return new CloudRepairResult(false, backup.Message);

        try
        {
            var steamPath = GetSteamPath();
            if (!await RequestSteamShutdownAsync(steamPath, cancellationToken))
                return new CloudRepairResult(false, "Steam 没有完全退出。请手动退出 Steam 后再次点击修复。", backup.BackupPath);

            var renamedCount = 0;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var appDataPath in FindAppUserDataPaths(steamPath, issue.AppId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cachePath = Path.Combine(appDataPath, "remotecache.vdf");
                if (!File.Exists(cachePath))
                    continue;

                var renamedPath = cachePath + $".mjjst-rescue-{stamp}.bak";
                File.Move(cachePath, renamedPath, overwrite: false);
                renamedCount++;
            }

            if (renamedCount == 0)
                return new CloudRepairResult(false, "没有找到该游戏的本地云同步状态文件，未执行修改。备份已经保留。", backup.BackupPath);

            RecordRepair(issue.AppId, DateTime.Now);

            StartSteam(steamPath);
            await Task.Delay(2500, cancellationToken);
            OpenGameInSteam(issue.AppId);
            return new CloudRepairResult(
                true,
                "本地云同步状态已安全重建。Steam 会重新核对本地与云端文件；若出现版本选择，请确认时间后再决定保留本地或云端。",
                backup.BackupPath);
        }
        catch (Exception ex)
        {
            return new CloudRepairResult(false, $"修复未完成：{ex.GetBaseException().Message}。原文件备份仍然保留。", backup.BackupPath);
        }
    }

    public void OpenLogFolder()
    {
        var path = Path.Combine(GetSteamPath(), "logs");
        OpenPath(path);
    }

    public void OpenBackupFolder()
    {
        Directory.CreateDirectory(BackupRootPath);
        OpenPath(BackupRootPath);
    }

    public void OpenGameInSteam(int appId)
    {
        Process.Start(new ProcessStartInfo($"steam://nav/games/details/{appId}") { UseShellExecute = true });
    }

    private string BackupCore(CloudSyncIssue issue, CancellationToken cancellationToken)
    {
        var steamPath = GetSteamPath();
        Directory.CreateDirectory(BackupRootPath);
        var safeName = string.Join("_", issue.GameName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var backupPath = Path.Combine(
            BackupRootPath,
            $"{DateTime.Now:yyyyMMdd-HHmmss}_{issue.AppId}_{safeName}");
        Directory.CreateDirectory(backupPath);

        var copiedFiles = new List<string>();
        var conflictRoot = Path.Combine(backupPath, "conflict-files");
        var fileIndex = 0;
        foreach (var file in issue.ConflictFiles.Where(file => file.HasLocalPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileIndex++;
            var targetDir = Path.Combine(conflictRoot, fileIndex.ToString("D2"));
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, Path.GetFileName(file.LocalPath!));
            File.Copy(file.LocalPath!, targetPath, overwrite: true);
            copiedFiles.Add(file.LocalPath!);
        }

        var userDataRoot = Path.Combine(backupPath, "steam-userdata");
        var accountIndex = 0;
        foreach (var appDataPath in FindAppUserDataPaths(steamPath, issue.AppId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            accountIndex++;
            CopyDirectory(appDataPath, Path.Combine(userDataRoot, $"account-{accountIndex}"), cancellationToken);
        }

        var report = new
        {
            createdAt = DateTime.Now,
            issue.AppId,
            issue.GameName,
            issue.KindText,
            issue.RiskText,
            issue.LastError,
            conflictFiles = issue.ConflictFiles.Select(file => new
            {
                file.RelativePath,
                file.LocalPath,
                file.TypeText
            }),
            copiedFiles
        };
        File.WriteAllText(
            Path.Combine(backupPath, "rescue-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        return backupPath;
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            CopyDirectory(directory, Path.Combine(destination, info.Name), cancellationToken);
        }
    }

    private static async Task<bool> RequestSteamShutdownAsync(string steamPath, CancellationToken cancellationToken)
    {
        if (!IsSteamRunning())
            return true;

        var steamExe = Path.Combine(steamPath, "steam.exe");
        if (!File.Exists(steamExe))
            return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = steamExe,
            Arguments = "-shutdown",
            WorkingDirectory = steamPath,
            UseShellExecute = true
        });

        var timeout = Stopwatch.StartNew();
        while (IsSteamRunning() && timeout.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(250, cancellationToken);
        return !IsSteamRunning();
    }

    private static bool IsSteamRunning()
    {
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                        return true;
                }
                catch
                {
                }
            }
        }
        return false;
    }

    private static void StartSteam(string steamPath)
    {
        var steamExe = Path.Combine(steamPath, "steam.exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = steamExe,
            WorkingDirectory = steamPath,
            UseShellExecute = true
        });
    }

    private string GetSteamPath()
    {
        var path = _steamPathService.GetCustomPath();
        if (string.IsNullOrWhiteSpace(path))
            path = _steamPathService.DetectSteamPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.Combine(path, "steam.exe")))
            throw new DirectoryNotFoundException("未检测到有效的 Steam 安装路径，请先在设置中配置。");
        return path;
    }

    private string ResolveGameName(int appId)
    {
        var manifestPath = _steamPathService.FindAppManifest(appId);
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            return $"Steam 游戏 {appId}";

        try
        {
            var content = File.ReadAllText(manifestPath);
            var match = Regex.Match(content, "\\\"name\\\"\\s+\\\"(?<name>[^\\\"]+)\\\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["name"].Value : $"Steam 游戏 {appId}";
        }
        catch
        {
            return $"Steam 游戏 {appId}";
        }
    }

    private static List<string> ExtractConflictPaths(IEnumerable<CloudLogEvent> events)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events)
        {
            var message = item.Message;
            string? path = null;
            const string directPrefix = "Sync conflict for file ";
            var directIndex = message.IndexOf(directPrefix, StringComparison.OrdinalIgnoreCase);
            if (directIndex >= 0)
                path = message[(directIndex + directPrefix.Length)..].Trim();
            else
            {
                var markedIndex = message.IndexOf(" already marked as conflicting", StringComparison.OrdinalIgnoreCase);
                if (markedIndex > 0)
                    path = message[..markedIndex].Trim();
                var mismatchIndex = message.IndexOf(" has local changes but mismatch", StringComparison.OrdinalIgnoreCase);
                if (mismatchIndex > 0)
                    path = message[..mismatchIndex].Trim();
            }

            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path.Trim('"'));
        }
        return paths.ToList();
    }

    private static List<string> ExtractPersistedPaths(IEnumerable<CloudLogEvent> events)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events)
        {
            var match = PersistingFileRegex.Match(item.Message);
            if (match.Success)
                paths.Add(match.Groups["path"].Value.Trim().Trim('"'));
        }
        return paths.ToList();
    }

    private static CloudConflictFile CreateConflictFile(string relativePath, IReadOnlyList<string> persistedPaths)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var localPath = persistedPaths.FirstOrDefault(path =>
            path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
        localPath ??= persistedPaths.FirstOrDefault(path =>
            Path.GetFileName(path).Equals(Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase));
        var risk = ClassifyFileRisk(relativePath);
        return new CloudConflictFile
        {
            RelativePath = relativePath,
            LocalPath = localPath,
            RiskLevel = risk.Level,
            TypeText = risk.Text
        };
    }

    private static (CloudSyncRiskLevel Level, string Text) ClassifyFileRisk(string path)
    {
        var value = path.Replace('\\', '/').ToLowerInvariant();
        var fileName = Path.GetFileName(value);
        if (fileName.Contains("setting") || fileName.Contains("config") ||
            fileName.Contains("binding") || fileName.Contains("preference") ||
            fileName.Contains("input") || fileName.Contains("option"))
            return (CloudSyncRiskLevel.Low, "设置文件");

        if (value.Contains("/save") || value.Contains("/profile") ||
            fileName.Contains("save") || fileName.Contains("progress") ||
            fileName.Contains("career") || fileName.Contains("checkpoint") ||
            Path.GetExtension(fileName) is ".sav" or ".save")
            return (CloudSyncRiskLevel.High, "可能是游戏存档");

        return (CloudSyncRiskLevel.Medium, "云同步文件");
    }

    private static CloudSyncIssueKind DetermineIssueKind(
        IReadOnlyList<CloudLogEvent> errors,
        IReadOnlyCollection<CloudConflictFile> files)
    {
        if (files.Count > 0 || errors.Any(item => item.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase)))
            return CloudSyncIssueKind.Conflict;
        if (errors.Any(item => item.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)))
            return CloudSyncIssueKind.Quota;
        if (errors.Any(item => item.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                              item.Message.Contains("permission", StringComparison.OrdinalIgnoreCase)))
            return CloudSyncIssueKind.Permission;
        if (errors.Any(item => item.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                              item.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                              item.Message.Contains("HTTP", StringComparison.OrdinalIgnoreCase)))
            return CloudSyncIssueKind.Network;
        return CloudSyncIssueKind.Unknown;
    }

    private static bool IsSuccessfulTerminalEvent(string message)
    {
        return message.Contains("Successfully synced", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Upload complete, result OK", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Download complete, result OK", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsErrorEvent(string message)
    {
        return message.Contains("Sync conflict", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("marked as conflicting", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("creating a conflict", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("failed due to conflicts", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Failed to begin", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("No Connection", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               (message.Contains("HTTP", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSummary(CloudSyncIssueKind kind, int conflictCount) => kind switch
    {
        CloudSyncIssueKind.Conflict => conflictCount > 0
            ? $"Steam 检测到 {conflictCount} 个本地文件与云端版本不一致。"
            : "Steam 的本地与云端文件状态发生冲突。",
        CloudSyncIssueKind.Network => "Steam 与云存储节点连接失败，文件本身不一定损坏。",
        CloudSyncIssueKind.Permission => "Steam 无法读取或写入本地云文件。",
        CloudSyncIssueKind.Quota => "该游戏的 Steam 云空间或文件数量可能已达到限制。",
        _ => "Steam 未能完成这款游戏的云同步。"
    };

    private static string BuildSuggestion(
        CloudSyncIssueKind kind,
        CloudSyncRiskLevel risk,
        bool isImportedGame,
        bool isRebound)
    {
        if (isRebound)
            return "该游戏在重建同步状态后再次报错，不建议继续重复修复。请关闭此游戏的 Steam 云同步，并改用存档保险箱保护本地进度。";
        if (isImportedGame && kind == CloudSyncIssueKind.Conflict)
            return "检测到本地一键入库配置。如果账号没有这款游戏的稳定云许可，建议关闭单游戏云同步并加入存档保险箱；确认拥有正常许可时也可以先安全重建一次。";

        return kind switch
        {
        CloudSyncIssueKind.Conflict when risk == CloudSyncRiskLevel.High =>
            "先创建安全备份，再重建同步状态。Steam 弹出版本选择时，请比较存档时间后决定。",
        CloudSyncIssueKind.Conflict =>
            "建议先备份，再重建本地同步状态，让 Steam 重新弹出本地/云端版本选择。",
        CloudSyncIssueKind.Network =>
            "先检查网络和系统代理，再重新登录 Steam；无需删除任何存档或缓存文件。",
        CloudSyncIssueKind.Permission =>
            "检查文件是否只读、被安全软件占用，以及 Steam 是否有目录写入权限。",
        CloudSyncIssueKind.Quota =>
            "在 Steam 云远程存储页面检查容量，确认后再删除不需要的云文件。",
        _ => "打开 Steam 云日志查看详细信息，修复前不要覆盖本地文件。"
        };
    }

    private static bool HasOneClickImportConfig(string steamPath, int appId)
    {
        var luaRoot = Path.Combine(steamPath, "config", "lua");
        return File.Exists(Path.Combine(luaRoot, $"{appId}.lua")) ||
               File.Exists(Path.Combine(luaRoot, "Disable", $"{appId}.lua"));
    }

    private DateTime? GetLastRepairTime(string steamPath, int appId)
    {
        DateTime? latest = null;
        lock (_repairHistoryLock)
        {
            try
            {
                var history = LoadRepairHistory();
                latest = history.Entries
                    .Where(entry => entry.AppId == appId)
                    .Select(entry => (DateTime?)entry.RepairedAt)
                    .Max();
            }
            catch
            {
            }
        }

        foreach (var appDataPath in FindAppUserDataPaths(steamPath, appId))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             appDataPath,
                             "remotecache.vdf.mjjst-rescue-*.bak",
                             SearchOption.TopDirectoryOnly))
                {
                    var match = Regex.Match(
                        Path.GetFileName(path),
                        @"mjjst-rescue-(?<stamp>\d{8}-\d{6})\.bak$",
                        RegexOptions.IgnoreCase);
                    if (!match.Success ||
                        !DateTime.TryParseExact(
                            match.Groups["stamp"].Value,
                            "yyyyMMdd-HHmmss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out var parsed))
                        continue;
                    if (!latest.HasValue || parsed > latest.Value)
                        latest = parsed;
                }
            }
            catch
            {
            }
        }
        return latest;
    }

    private void RecordRepair(int appId, DateTime repairedAt)
    {
        lock (_repairHistoryLock)
        {
            Directory.CreateDirectory(BackupRootPath);
            var history = LoadRepairHistory();
            history.Entries.RemoveAll(entry => entry.AppId == appId);
            history.Entries.Add(new CloudRepairHistoryEntry { AppId = appId, RepairedAt = repairedAt });
            File.WriteAllText(
                RepairHistoryPath,
                JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private CloudRepairHistory LoadRepairHistory()
    {
        try
        {
            if (File.Exists(RepairHistoryPath))
            {
                var history = JsonSerializer.Deserialize<CloudRepairHistory>(File.ReadAllText(RepairHistoryPath));
                if (history != null)
                    return history;
            }
        }
        catch
        {
        }
        return new CloudRepairHistory();
    }

    private string RepairHistoryPath => Path.Combine(BackupRootPath, "repair-history.json");

    private static string SimplifyError(string message)
    {
        if (message.Contains("failed due to conflicts", StringComparison.OrdinalIgnoreCase))
            return "同步被文件冲突阻止";
        if (message.Contains("No Connection", StringComparison.OrdinalIgnoreCase))
            return "无法连接 Steam 云存储节点";
        if (message.Contains("Sync conflict for file", StringComparison.OrdinalIgnoreCase))
            return "本地与云端文件版本冲突";
        return message.Length > 150 ? message[..150] + "…" : message;
    }

    private static IEnumerable<string> FindAppUserDataPaths(string steamPath, int appId)
    {
        var root = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(root))
            yield break;
        foreach (var accountPath in Directory.EnumerateDirectories(root))
        {
            var appPath = Path.Combine(accountPath, appId.ToString());
            if (Directory.Exists(appPath))
                yield return appPath;
        }
    }

    private static void OpenPath(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private sealed record CloudLogEvent(DateTime Time, string Message);

    private sealed class CloudRepairHistory
    {
        public List<CloudRepairHistoryEntry> Entries { get; set; } = [];
    }

    private sealed class CloudRepairHistoryEntry
    {
        public int AppId { get; set; }
        public DateTime RepairedAt { get; set; }
    }
}
