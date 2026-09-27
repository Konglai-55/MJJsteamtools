using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public sealed class SaveVaultService : ISaveVaultService
{
    private const int MaxFilesPerLocation = 50000;
    private static readonly string[] SaveDirectoryNames =
    [
        "save", "saves", "savedata", "savegame", "savegames", "saved games",
        "profiles", "profile", "userdata"
    ];
    private static readonly HashSet<string> PreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sav", ".save", ".dat", ".bin", ".slot", ".profile", ".json", ".xml", ".ini", ".cfg", ".txt"
    };
    private static readonly Dictionary<string, string> PreviewFieldLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playername"] = "角色名", ["charactername"] = "角色名", ["profilename"] = "角色名",
        ["level"] = "等级", ["playerlevel"] = "等级", ["characterlevel"] = "等级",
        ["chapter"] = "章节", ["chaptername"] = "章节", ["mission"] = "任务",
        ["difficulty"] = "难度", ["gamemode"] = "模式",
        ["playtime"] = "游戏时间", ["playtimesec"] = "游戏时间", ["playtimeseconds"] = "游戏时间",
        ["money"] = "金钱", ["currency"] = "金钱", ["gold"] = "金钱", ["credits"] = "金钱",
        ["progress"] = "进度", ["completion"] = "完成度", ["percentage"] = "完成度",
        ["newgameplus"] = "周目", ["ngplus"] = "周目"
    };

    private readonly ISteamPathService _steamPathService;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SaveVaultService(ISteamPathService steamPathService, ISettingsService settingsService)
    {
        _steamPathService = steamPathService;
        _settingsService = settingsService;
    }

    public async Task<IReadOnlyList<SaveGameRecord>> ScanGamesAsync(
        IReadOnlyCollection<GameInfo> games,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = games.DistinctBy(game => game.AppId).ToList();
        var results = new SaveGameRecord[source.Count];
        using var gate = new SemaphoreSlim(4, 4);
        var completed = 0;
        await Task.WhenAll(source.Select(async (game, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[index] = await ScanGameAsync(game, cancellationToken);
            }
            finally
            {
                gate.Release();
                var value = Interlocked.Increment(ref completed);
                progress?.Report((value, source.Count));
            }
        }));
        return results;
    }

    public Task<SaveGameRecord> ScanGameAsync(GameInfo game, CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanGameCore(game, cancellationToken), cancellationToken);

    public IReadOnlyList<SaveSnapshotInfo> GetSnapshots(int appId)
    {
        var directory = GetBackupDirectory(appId);
        if (!Directory.Exists(directory)) return [];
        var snapshots = new List<SaveSnapshotInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var file = new FileInfo(path);
                var manifest = ReadManifest(path);
                snapshots.Add(new SaveSnapshotInfo(
                    path,
                    manifest?.CreatedAt ?? file.LastWriteTime,
                    file.Length,
                    manifest?.FileCount ?? 0,
                    string.IsNullOrWhiteSpace(manifest?.Reason) ? "历史备份" : manifest.Reason));
            }
            catch { }
        }
        return snapshots.OrderByDescending(item => item.CreatedAt).ToList();
    }

    public string GetBackupDirectory(int appId)
    {
        var configured = _settingsService.Load().SaveBackupPath;
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DreamPixel studio",
                "MJJsteamtools",
                "SaveBackups")
            : Environment.ExpandEnvironmentVariables(configured.Trim());
        return Path.Combine(root, appId.ToString());
    }

    public async Task<SaveOperationResult> CreateSnapshotAsync(
        SaveGameRecord game,
        string reason = "手动备份",
        CancellationToken cancellationToken = default)
    {
        if (!game.HasSaveData)
            return new SaveOperationResult(false, "当前游戏没有可备份的存档文件");

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateSnapshotCoreAsync(game, reason, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new SaveOperationResult(false, "存档备份已取消");
        }
        catch (Exception ex)
        {
            return new SaveOperationResult(false, $"存档备份失败：{ex.GetBaseException().Message}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<SaveOperationResult> RestoreSnapshotAsync(
        SaveGameRecord game,
        SaveSnapshotInfo snapshot,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(snapshot.FilePath))
            return new SaveOperationResult(false, "所选备份文件不存在");

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var rollback = await CreateSnapshotCoreAsync(game, "恢复前自动回滚备份", cancellationToken);
            if (!rollback.Success) return rollback;

            var manifest = ReadManifest(snapshot.FilePath);
            if (manifest is null || manifest.AppId != game.AppId)
                return new SaveOperationResult(false, "备份清单无效或不属于当前游戏");

            var allowedRoots = game.Locations
                .Select(item => Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (manifest.Locations.Any(item =>
                    string.IsNullOrWhiteSpace(item.OriginalPath)
                    || !allowedRoots.Contains(Path.GetFullPath(item.OriginalPath).TrimEnd(Path.DirectorySeparatorChar))))
                return new SaveOperationResult(false, "备份中的存档路径与当前识别结果不一致，已中止恢复");

            using var archive = ZipFile.OpenRead(snapshot.FilePath);
            var restored = 0;
            foreach (var location in manifest.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(location.OriginalPath)) continue;
                var root = Path.GetFullPath(location.OriginalPath);
                Directory.CreateDirectory(root);
                var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var entryPrefix = $"location-{location.Index}/";
                foreach (var entry in archive.Entries.Where(item => item.FullName.StartsWith(entryPrefix, StringComparison.Ordinal)))
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var relative = entry.FullName[entryPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                    var target = Path.GetFullPath(Path.Combine(root, relative));
                    if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("备份包含越界路径，已中止恢复");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = entry.Open();
                    await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                    await input.CopyToAsync(output, cancellationToken);
                    File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
                    restored++;
                }
            }

            return new SaveOperationResult(
                true,
                $"已恢复 {restored} 个存档文件；恢复前状态已自动备份。建议启动游戏前确认 Steam Cloud 冲突提示");
        }
        catch (OperationCanceledException)
        {
            return new SaveOperationResult(false, "存档恢复已取消");
        }
        catch (Exception ex)
        {
            return new SaveOperationResult(false, $"存档恢复失败：{ex.GetBaseException().Message}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private SaveGameRecord ScanGameCore(GameInfo game, CancellationToken cancellationToken)
    {
        var record = new SaveGameRecord
        {
            AppId = game.AppId,
            GameName = string.IsNullOrWhiteSpace(game.GameName) ? $"AppID: {game.AppId}" : game.GameName,
            CoverImagePath = game.CoverImagePath
        };
        var candidates = FindCandidates(game, cancellationToken);
        foreach (var candidate in RemoveNestedDuplicates(candidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stats = GetLocationStats(candidate.Path, cancellationToken);
            if (stats.FileCount == 0) continue;
            record.Locations.Add(new SaveLocationInfo(
                candidate.Path,
                candidate.Source,
                stats.FileCount,
                stats.TotalBytes,
                stats.LastModified));
        }

        record.FileCount = record.Locations.Sum(item => item.FileCount);
        record.TotalBytes = record.Locations.Sum(item => item.TotalBytes);
        record.LastModified = record.Locations.Count == 0
            ? null
            : record.Locations.Max(item => item.LastModified);
        foreach (var slot in BuildSlotPreviews(record.Locations, cancellationToken))
            record.Slots.Add(slot);
        foreach (var snapshot in GetSnapshots(game.AppId)) record.Snapshots.Add(snapshot);
        record.StatusText = record.HasSaveData
            ? record.Locations.Any(item => item.Source.Contains("Steam", StringComparison.OrdinalIgnoreCase))
                ? "可以备份 · 含 Steam 数据"
                : "可以备份"
            : "未找到存档";
        record.NotifySummaryChanged();
        return record;
    }

    private static IReadOnlyList<SaveSlotPreview> BuildSlotPreviews(
        IReadOnlyCollection<SaveLocationInfo> locations,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string File, string Source, int Score, DateTime Modified)>();
        foreach (var location in locations)
        {
            foreach (var file in EnumerateFilesSafe(location.Path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var extension = Path.GetExtension(file);
                    var name = Path.GetFileNameWithoutExtension(file);
                    var previewable = PreviewExtensions.Contains(extension);
                    var looksLikeSlot = name.Contains("save", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains("slot", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains("profile", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains("career", StringComparison.OrdinalIgnoreCase);
                    if (!previewable && !looksLikeSlot) continue;
                    var score = (looksLikeSlot ? 4 : 0)
                                + (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                                   || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                                   || extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ? 3 : 0);
                    candidates.Add((file, location.Source, score, File.GetLastWriteTime(file)));
                }
                catch { }
            }
        }

        return candidates
            .GroupBy(item => Path.GetFullPath(item.File), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Modified)
            .Take(8)
            .Select(item => CreateSlotPreview(item.File, item.Source))
            .Where(item => item is not null)
            .Cast<SaveSlotPreview>()
            .ToList();
    }

    private static SaveSlotPreview? CreateSlotPreview(string path, string source)
    {
        try
        {
            var info = new FileInfo(path);
            var extension = info.Extension.ToLowerInvariant();
            var fields = info.Length <= 5 * 1024 * 1024
                ? TryExtractFields(path, extension)
                : [];
            return new SaveSlotPreview
            {
                FileName = info.Name,
                FilePath = info.FullName,
                Source = source,
                Format = extension switch
                {
                    ".json" => "JSON",
                    ".xml" => "XML",
                    ".ini" or ".cfg" => "配置文本",
                    ".sav" or ".save" or ".slot" => "游戏存档",
                    ".bin" or ".dat" => "二进制",
                    _ => string.IsNullOrWhiteSpace(extension) ? "未知格式" : extension.TrimStart('.').ToUpperInvariant()
                },
                LastModified = info.LastWriteTime,
                SizeBytes = info.Length,
                Fields = fields
            };
        }
        catch { return null; }
    }

    private static IReadOnlyList<SaveDataField> TryExtractFields(string path, string extension)
    {
        try
        {
            return extension switch
            {
                ".json" => ExtractJsonFields(File.ReadAllText(path)),
                ".xml" => ExtractXmlFields(path),
                ".ini" or ".cfg" or ".txt" => ExtractKeyValueFields(File.ReadLines(path)),
                _ => []
            };
        }
        catch { return []; }
    }

    private static IReadOnlyList<SaveDataField> ExtractJsonFields(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 32
        });
        var fields = new List<SaveDataField>();
        WalkJson(document.RootElement, fields, 0);
        return fields.Take(8).ToList();
    }

    private static void WalkJson(JsonElement element, List<SaveDataField> fields, int depth)
    {
        if (depth > 5 || fields.Count >= 8) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (TryMapField(property.Name, property.Value.ToString(), out var field)
                    && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    fields.Add(field);
                else
                    WalkJson(property.Value, fields, depth + 1);
                if (fields.Count >= 8) break;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray().Take(4)) WalkJson(item, fields, depth + 1);
        }
    }

    private static IReadOnlyList<SaveDataField> ExtractXmlFields(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var fields = new List<SaveDataField>();
        foreach (var element in document.Descendants())
        {
            if (!element.HasElements && TryMapField(element.Name.LocalName, element.Value, out var field))
                fields.Add(field);
            foreach (var attribute in element.Attributes())
                if (TryMapField(attribute.Name.LocalName, attribute.Value, out field)) fields.Add(field);
            if (fields.Count >= 8) break;
        }
        return fields.Take(8).ToList();
    }

    private static IReadOnlyList<SaveDataField> ExtractKeyValueFields(IEnumerable<string> lines)
    {
        var fields = new List<SaveDataField>();
        foreach (var line in lines.Take(2000))
        {
            var match = Regex.Match(line, @"^\s*([^#;=:\s]+)\s*[=:]\s*(.+?)\s*$");
            if (match.Success && TryMapField(match.Groups[1].Value, match.Groups[2].Value, out var field))
                fields.Add(field);
            if (fields.Count >= 8) break;
        }
        return fields;
    }

    private static bool TryMapField(string rawKey, string rawValue, out SaveDataField field)
    {
        field = null!;
        var key = Regex.Replace(rawKey, @"[^\p{L}\p{N}]", string.Empty).ToLowerInvariant();
        if (!PreviewFieldLabels.TryGetValue(key, out var label)) return false;
        var value = rawValue.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120) return false;
        if (key.Contains("playtime", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(value, out var seconds)
            && (key.Contains("sec", StringComparison.OrdinalIgnoreCase) || seconds > 10000))
            value = $"{TimeSpan.FromSeconds(seconds).TotalHours:0.0} 小时";
        field = new SaveDataField(label, value);
        return true;
    }

    private List<(string Path, string Source)> FindCandidates(GameInfo game, CancellationToken cancellationToken)
    {
        var result = new List<(string Path, string Source)>();
        var steamPath = _steamPathService.GetCustomPath() ?? _steamPathService.DetectSteamPath();
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            var userdata = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userdata))
            {
                foreach (var account in SafeDirectories(userdata))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddIfDirectory(result, Path.Combine(account, game.AppId.ToString(), "remote"), "Steam 用户数据");
                }
            }
        }

        var manifestPath = _steamPathService.FindAppManifest(game.AppId);
        var installDirectoryName = TryReadInstallDir(manifestPath);
        if (!string.IsNullOrWhiteSpace(manifestPath) && !string.IsNullOrWhiteSpace(installDirectoryName))
        {
            var steamApps = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrWhiteSpace(steamApps))
            {
                var installRoot = Path.Combine(steamApps, "common", installDirectoryName);
                foreach (var path in FindSaveNamedDirectories(installRoot, 3, cancellationToken))
                    AddIfDirectory(result, path, "游戏安装目录");
            }
        }

        var names = new[] { game.GameName, installDirectoryName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            (Path.Combine(documents, "My Games"), "文档 / My Games"),
            (documents, "文档"),
            (Path.Combine(userProfile, "Saved Games"), "Saved Games"),
            (local, "系统本地数据"),
            (roaming, "系统漫游数据")
        };
        foreach (var (root, source) in roots)
        {
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddIfDirectory(result, Path.Combine(root, SanitizePathSegment(name)), source);
            }
            foreach (var match in FindNameMatches(root, names, cancellationToken))
                AddIfDirectory(result, match, source);
        }
        return result;
    }

    private async Task<SaveOperationResult> CreateSnapshotCoreAsync(
        SaveGameRecord game,
        string reason,
        CancellationToken cancellationToken)
    {
        var directory = GetBackupDirectory(game.AppId);
        Directory.CreateDirectory(directory);
        var created = DateTime.Now;
        var finalPath = Path.Combine(directory, $"{game.AppId}_{created:yyyyMMdd_HHmmss_fff}.zip");
        var tempPath = finalPath + ".tmp";
        var excludedBackupPrefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                                   + Path.DirectorySeparatorChar;
        var manifest = new SnapshotManifest
        {
            AppId = game.AppId,
            GameName = game.GameName,
            CreatedAt = created,
            Reason = reason,
            Locations = game.Locations.Select((item, index) => new SnapshotLocation
            {
                Index = index,
                OriginalPath = item.Path,
                Source = item.Source
            }).ToList()
        };
        var skipped = 0;
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var location in manifest.Locations)
                {
                    foreach (var file in EnumerateFilesSafe(location.OriginalPath, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Path.GetFullPath(file).StartsWith(excludedBackupPrefix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        try
                        {
                            var relative = Path.GetRelativePath(location.OriginalPath, file).Replace('\\', '/');
                            var entry = archive.CreateEntry($"location-{location.Index}/{relative}", CompressionLevel.Optimal);
                            entry.LastWriteTime = File.GetLastWriteTime(file);
                            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                            await using var output = entry.Open();
                            await input.CopyToAsync(output, cancellationToken);
                            manifest.FileCount++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { skipped++; }
                    }
                }
                var manifestEntry = archive.CreateEntry("mjj-save-manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);
            }
            File.Move(tempPath, finalPath);
            ApplyRetention(game.AppId);
            var fileInfo = new FileInfo(finalPath);
            var snapshot = new SaveSnapshotInfo(finalPath, created, fileInfo.Length, manifest.FileCount, reason);
            return new SaveOperationResult(
                true,
                skipped == 0
                    ? $"已备份 {manifest.FileCount} 个存档文件"
                    : $"已备份 {manifest.FileCount} 个文件，另有 {skipped} 个占用中的文件未读取",
                snapshot);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private void ApplyRetention(int appId)
    {
        var retention = Math.Clamp(_settingsService.Load().SaveBackupRetention, 1, 200);
        var old = GetSnapshots(appId).Skip(retention).ToList();
        foreach (var snapshot in old)
        {
            try { File.Delete(snapshot.FilePath); } catch { }
        }
    }

    private static SnapshotManifest? ReadManifest(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("mjj-save-manifest.json");
            if (entry is null) return null;
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<SnapshotManifest>(stream);
        }
        catch { return null; }
    }

    private static (int FileCount, long TotalBytes, DateTime LastModified) GetLocationStats(
        string path,
        CancellationToken cancellationToken)
    {
        var count = 0;
        long bytes = 0;
        var last = Directory.GetLastWriteTime(path);
        foreach (var file in EnumerateFilesSafe(path, cancellationToken))
        {
            if (count >= MaxFilesPerLocation) break;
            try
            {
                var info = new FileInfo(file);
                count++;
                bytes += info.Length;
                if (info.LastWriteTime > last) last = info.LastWriteTime;
            }
            catch { }
        }
        return (count, bytes, last);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var yielded = 0;
        while (pending.Count > 0 && yielded < MaxFilesPerLocation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { files = []; }
            foreach (var file in files)
            {
                if (yielded++ >= MaxFilesPerLocation) yield break;
                yield return file;
            }
            foreach (var directory in SafeDirectories(current))
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(directory);
                }
                catch { }
            }
        }
    }

    private static IEnumerable<string> FindSaveNamedDirectories(
        string root,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var inspected = 0;
        while (pending.Count > 0 && inspected < 1500)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = pending.Dequeue();
            inspected++;
            if (depth > 0 && SaveDirectoryNames.Contains(Path.GetFileName(current), StringComparer.OrdinalIgnoreCase))
            {
                yield return current;
                continue;
            }
            if (depth >= maxDepth) continue;
            foreach (var child in SafeDirectories(current)) pending.Enqueue((child, depth + 1));
        }
    }

    private static IEnumerable<string> FindNameMatches(
        string root,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) yield break;
        var normalizedNames = names.Select(NormalizeName).Where(value => value.Length >= 4).ToList();
        var inspected = 0;
        foreach (var directory in SafeDirectories(root))
        {
            if (inspected++ >= 500) yield break;
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = NormalizeName(Path.GetFileName(directory));
            if (candidate.Length >= 4 && normalizedNames.Any(name =>
                    candidate.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || (name.Length >= 6 && candidate.Contains(name, StringComparison.OrdinalIgnoreCase))
                    || (candidate.Length >= 8
                        && candidate.Length >= name.Length * 0.7
                        && name.Contains(candidate, StringComparison.OrdinalIgnoreCase))))
                yield return directory;
        }
    }

    private static List<(string Path, string Source)> RemoveNestedDuplicates(List<(string Path, string Source)> candidates)
    {
        var unique = candidates
            .Where(item => Directory.Exists(item.Path))
            .GroupBy(item => Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .Select(group => (Path: group.Key, group.First().Source))
            .OrderBy(item => item.Path.Length)
            .ToList();
        var result = new List<(string Path, string Source)>();
        foreach (var item in unique)
        {
            var prefix = item.Path + Path.DirectorySeparatorChar;
            if (result.Any(existing => item.Path.StartsWith(existing.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(item);
        }
        return result;
    }

    private static void AddIfDirectory(List<(string Path, string Source)> result, string path, string source)
    {
        try
        {
            if (Directory.Exists(path)) result.Add((Path.GetFullPath(path), source));
        }
        catch { }
    }

    private static string[] SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    private static string? TryReadInstallDir(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return null;
        try
        {
            var match = Regex.Match(File.ReadAllText(manifestPath), @"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static string SanitizePathSegment(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, ' ');
        return value.Trim().TrimEnd('.');
    }

    private static string NormalizeName(string value) =>
        Regex.Replace(value, @"[^\p{L}\p{N}]", string.Empty).ToLowerInvariant();

    private sealed class SnapshotManifest
    {
        public int Version { get; set; } = 1;
        public int AppId { get; set; }
        public string GameName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public List<SnapshotLocation> Locations { get; set; } = new();
    }

    private sealed class SnapshotLocation
    {
        public int Index { get; set; }
        public string OriginalPath { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }
}
