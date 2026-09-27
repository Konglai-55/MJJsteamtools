using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public sealed class SteamStorageLocation
{
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
    public string SizeText => SteamStorageService.FormatBytes(SizeBytes);
}

public sealed class SteamStorageCategory
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public required string SafetyText { get; init; }
    public required IReadOnlyList<SteamStorageLocation> Locations { get; init; }
    public string? CleanupKey { get; init; }
    public string CleanupLabel { get; init; } = "清理";
    public long SizeBytes => Locations.Sum(location => location.SizeBytes);
    public int FileCount => Locations.Sum(location => location.FileCount);
    public string SizeText => SteamStorageService.FormatBytes(SizeBytes);
    public string DetailText => Key == "games"
        ? $"{Locations.Count} 个位置 · {FileCount:N0} 款游戏"
        : $"{Locations.Count} 个位置 · {FileCount:N0} 个文件";
    public bool HasLocations => Locations.Count > 0;
    public bool CanCleanup => !string.IsNullOrWhiteSpace(CleanupKey) && SizeBytes > 0;
    public bool HasDetailPage => Key is "games" or "workshop" or "shader-cache";
    public string PrimaryActionText => HasDetailPage ? "查看详情" : "查看位置";
}

public sealed class SteamStorageDetailItem
{
    public required string CategoryKey { get; init; }
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
    public string SizeText => SteamStorageService.FormatBytes(SizeBytes);
    public string AppIdText => $"AppID: {AppId}";
    public string FileCountText => CategoryKey == "games"
        ? "Steam 已安装游戏"
        : $"{FileCount:N0} 个文件";
    public string ActionText => CategoryKey == "games" ? "Steam 卸载" : "删除";
    public bool IsGame => CategoryKey == "games";
}

public readonly record struct SteamStorageDetailProgress(string Item, int Completed, int Total);

public sealed class SteamStorageDrive
{
    public required string Name { get; init; }
    public required string Format { get; init; }
    public string RootPath { get; init; } = string.Empty;
    public string VolumeLabel { get; init; } = string.Empty;
    public int LibraryCount { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long SteamBytes { get; init; }
    public string FreeText => SteamStorageService.FormatBytes(FreeBytes);
    public string TotalText => SteamStorageService.FormatBytes(TotalBytes);
    public string SteamText => SteamStorageService.FormatBytes(SteamBytes);
    public string HeaderText => $"{Name}  {Format}";
    public string DetailText => $"已统计 {SteamText}";
    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"本地磁盘 ({Name})"
        : $"{VolumeLabel} ({Name})";
    public string CapacityText => $"{TotalText} 中共有 {FreeText} 可用";
}

public sealed class SteamDriveContentItem
{
    public int AppId { get; init; }
    public required string Name { get; init; }
    public string CoverImagePath { get; init; } = string.Empty;
    public required string LibraryPath { get; init; }
    public string? GamePath { get; init; }
    public string? WorkshopPath { get; init; }
    public string? ShaderPath { get; init; }
    public string? OtherPath { get; init; }
    public long GameBytes { get; init; }
    public long WorkshopBytes { get; init; }
    public long ShaderBytes { get; init; }
    public long OtherBytes { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes => GameBytes + WorkshopBytes + ShaderBytes + OtherBytes;
    public string TotalSizeText => SteamStorageService.FormatBytes(TotalBytes);
    public string IdentityText => AppId > 0 ? $"AppID: {AppId}" : "Steam 数据";
    public bool HasCover => !string.IsNullOrWhiteSpace(CoverImagePath) && File.Exists(CoverImagePath);
    public bool HasGame => GameBytes > 0 && !string.IsNullOrWhiteSpace(GamePath);
    public bool HasWorkshop => WorkshopBytes > 0 && !string.IsNullOrWhiteSpace(WorkshopPath);
    public bool HasShader => ShaderBytes > 0 && !string.IsNullOrWhiteSpace(ShaderPath);
    public bool HasOther => OtherBytes > 0 && !string.IsNullOrWhiteSpace(OtherPath);
    public string PrimaryPath => GamePath ?? WorkshopPath ?? ShaderPath ?? OtherPath ?? LibraryPath;
    public string ContentSummaryText
    {
        get
        {
            var parts = new List<string>(4);
            if (GameBytes > 0) parts.Add($"游戏本体 {SteamStorageService.FormatBytes(GameBytes)}");
            if (WorkshopBytes > 0) parts.Add($"创意工坊 {SteamStorageService.FormatBytes(WorkshopBytes)}");
            if (ShaderBytes > 0) parts.Add($"着色器 {SteamStorageService.FormatBytes(ShaderBytes)}");
            if (OtherBytes > 0) parts.Add($"其他数据 {SteamStorageService.FormatBytes(OtherBytes)}");
            return string.Join("  ·  ", parts);
        }
    }
}

public sealed class SteamDriveContentSnapshot
{
    public required SteamStorageDrive Drive { get; init; }
    public required IReadOnlyList<string> LibraryPaths { get; init; }
    public required IReadOnlyList<SteamDriveContentItem> Items { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public long GameBytes => Items.Sum(item => item.GameBytes);
    public long WorkshopBytes => Items.Sum(item => item.WorkshopBytes);
    public long ShaderBytes => Items.Sum(item => item.ShaderBytes);
    public long OtherSteamBytes => Items.Sum(item => item.OtherBytes);
    public long SteamBytes => Items.Sum(item => item.TotalBytes);
    public long OtherUsedBytes => Math.Max(0, Drive.TotalBytes - Drive.FreeBytes - SteamBytes);
}

public readonly record struct SteamDriveScanProgress(string Item, int Completed, int Total);

public sealed class SteamStorageSnapshot
{
    public required IReadOnlyList<string> LibraryPaths { get; init; }
    public required IReadOnlyList<SteamStorageCategory> Categories { get; init; }
    public required IReadOnlyList<SteamStorageDrive> Drives { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public long TotalBytes => Categories.Sum(category => category.SizeBytes);
    public long CacheBytes => Categories
        .Where(category => category.Key is "shader-cache" or "client-cache")
        .Sum(category => category.SizeBytes);
}

public readonly record struct SteamStorageScanProgress(string Category, int Completed, int Total);

public sealed class SteamStorageService
{
    private static readonly Regex SizeOnDiskRegex = new(
        "\\\"SizeOnDisk\\\"\\s+\\\"(\\d+)\\\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AppIdRegex = new(
        "\\\"appid\\\"\\s+\\\"(\\d+)\\\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameRegex = new(
        "\\\"name\\\"\\s+\\\"([^\\\"]*)\\\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstallDirRegex = new(
        "\\\"installdir\\\"\\s+\\\"([^\\\"]*)\\\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly ISteamPathService _steamPathService;
    private readonly ISteamApiService _steamApiService;

    public SteamStorageService(ISteamPathService steamPathService, ISteamApiService steamApiService)
    {
        _steamPathService = steamPathService;
        _steamApiService = steamApiService;
    }

    public Task<IReadOnlyList<SteamStorageDrive>> GetStorageDrivesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<SteamStorageDrive>>(() =>
        {
            var drives = GetLibraryPaths()
                .GroupBy(path => NormalizeDriveRoot(Path.GetPathRoot(path) ?? path), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group =>
                {
                    var libraries = group.ToList();
                    var indexedGameBytes = ReadAppManifests(libraries, cancellationToken)
                        .Sum(manifest => manifest.SizeBytes);
                    return CreateDriveInfo(group.Key, libraries.Count, indexedGameBytes);
                })
                .OrderByDescending(drive => drive.SteamBytes)
                .ThenBy(drive => drive.Name)
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();
            return drives;
        }, cancellationToken);
    }

    public Task<SteamDriveContentSnapshot> ScanDriveAsync(
        string driveRoot,
        IProgress<SteamDriveScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => ScanDrive(driveRoot, progress, cancellationToken),
            cancellationToken);
    }

    private SteamDriveContentSnapshot ScanDrive(
        string driveRoot,
        IProgress<SteamDriveScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var normalizedRoot = NormalizeDriveRoot(driveRoot);
        var allLibraries = GetLibraryPaths();
        var libraries = allLibraries
            .Where(path => string.Equals(
                NormalizeDriveRoot(Path.GetPathRoot(path) ?? path),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (libraries.Count == 0)
            throw new DirectoryNotFoundException($"磁盘 {normalizedRoot} 中没有 Steam 库目录。");

        var manifests = ReadAppManifests(libraries, cancellationToken);
        var workshopDirectories = EnumerateAppDirectories(
            libraries.Select(path => Path.Combine(path, "steamapps", "workshop", "content")),
            cancellationToken);
        var shaderDirectories = EnumerateAppDirectories(
            libraries.Select(path => Path.Combine(path, "steamapps", "shadercache")),
            cancellationToken);
        var otherLocations = GetOtherSteamLocations(libraries, normalizedRoot);
        var totalSteps = manifests.Count + workshopDirectories.Count + shaderDirectories.Count + otherLocations.Count;
        var completed = 0;
        var builders = new Dictionary<int, DriveContentBuilder>();

        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SteamDriveScanProgress(manifest.Name, completed++, Math.Max(1, totalSteps)));
            var builder = GetOrCreateBuilder(builders, manifest.AppId, manifest.LibraryPath, manifest.Name);
            builder.GameBytes += manifest.SizeBytes;
            builder.GamePath ??= Path.Combine(manifest.LibraryPath, "steamapps", "common", manifest.InstallDir);
            builder.FileCount++;
        }

        foreach (var directory in workshopDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SteamDriveScanProgress($"AppID {directory.AppId} · 创意工坊", completed++, Math.Max(1, totalSteps)));
            var location = ScanLocation(directory.Path, cancellationToken);
            var builder = GetOrCreateBuilder(builders, directory.AppId, directory.LibraryPath, $"AppID {directory.AppId}");
            builder.WorkshopBytes += location.SizeBytes;
            builder.WorkshopPath ??= location.Path;
            builder.FileCount += location.FileCount;
        }

        foreach (var directory in shaderDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SteamDriveScanProgress($"AppID {directory.AppId} · 着色器", completed++, Math.Max(1, totalSteps)));
            var location = ScanLocation(directory.Path, cancellationToken);
            var builder = GetOrCreateBuilder(builders, directory.AppId, directory.LibraryPath, $"AppID {directory.AppId}");
            builder.ShaderBytes += location.SizeBytes;
            builder.ShaderPath ??= location.Path;
            builder.FileCount += location.FileCount;
        }

        var gameInfo = builders.Values
            .Select(builder => new GameInfo { AppId = builder.AppId, GameName = builder.Name })
            .ToList();
        _steamApiService.PopulateFromCache(gameInfo);
        var cachedInfo = gameInfo.ToDictionary(game => game.AppId);
        var items = builders.Values.Select(builder =>
        {
            cachedInfo.TryGetValue(builder.AppId, out var cached);
            var cachedName = cached?.GameName;
            var resolvedName = !IsPlaceholderName(cachedName, builder.AppId)
                ? cachedName!
                : !IsPlaceholderName(builder.Name, builder.AppId)
                    ? builder.Name
                    : $"AppID {builder.AppId}";
            return new SteamDriveContentItem
            {
                AppId = builder.AppId,
                Name = resolvedName,
                CoverImagePath = cached?.CoverImagePath ?? string.Empty,
                LibraryPath = builder.LibraryPath,
                GamePath = builder.GamePath,
                WorkshopPath = builder.WorkshopPath,
                ShaderPath = builder.ShaderPath,
                GameBytes = builder.GameBytes,
                WorkshopBytes = builder.WorkshopBytes,
                ShaderBytes = builder.ShaderBytes,
                FileCount = builder.FileCount
            };
        }).ToList();

        foreach (var other in otherLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SteamDriveScanProgress(other.Name, completed++, Math.Max(1, totalSteps)));
            var location = ScanLocation(other.Path, cancellationToken);
            if (location.SizeBytes <= 0)
                continue;
            items.Add(new SteamDriveContentItem
            {
                AppId = 0,
                Name = other.Name,
                LibraryPath = other.LibraryPath,
                OtherPath = location.Path,
                OtherBytes = location.SizeBytes,
                FileCount = location.FileCount
            });
        }

        items = items
            .Where(item => item.TotalBytes > 0)
            .OrderByDescending(item => item.TotalBytes)
            .ThenBy(item => item.Name)
            .ToList();
        var steamBytes = items.Sum(item => item.TotalBytes);
        stopwatch.Stop();
        progress?.Report(new SteamDriveScanProgress("完成", totalSteps, Math.Max(1, totalSteps)));
        return new SteamDriveContentSnapshot
        {
            Drive = CreateDriveInfo(normalizedRoot, libraries.Count, steamBytes),
            LibraryPaths = libraries,
            Items = items,
            Elapsed = stopwatch.Elapsed
        };
    }

    public Task<SteamStorageSnapshot> ScanAsync(
        IProgress<SteamStorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Scan(progress, cancellationToken), cancellationToken);
    }

    private SteamStorageSnapshot Scan(
        IProgress<SteamStorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var steamPath = _steamPathService.GetCustomPath();
        if (string.IsNullOrWhiteSpace(steamPath))
            steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            throw new DirectoryNotFoundException("未检测到有效的 Steam 安装目录。");

        var libraryPaths = _steamPathService.GetAllLibraryPaths()
            .Where(Directory.Exists)
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!libraryPaths.Contains(NormalizePath(steamPath), StringComparer.OrdinalIgnoreCase))
            libraryPaths.Insert(0, NormalizePath(steamPath));

        var definitions = new[]
        {
            new CategoryDefinition(
                "games", "游戏本体", "Steam 库中已经安装的游戏文件。", "\uE7FC", "核心数据 · 不提供删除",
                libraryPaths.Select(path => Path.Combine(path, "steamapps", "common"))),
            new CategoryDefinition(
                "workshop", "创意工坊", "已订阅模组与社区内容，直接删除后可能会重新下载。", "\uE71B", "订阅内容 · 仅查看",
                libraryPaths.Select(path => Path.Combine(path, "steamapps", "workshop", "content"))),
            new CategoryDefinition(
                "shader-cache", "着色器缓存", "可重新生成；清理后首次进入游戏可能出现短暂编译或卡顿。", "\uE950", "可重建缓存",
                libraryPaths.Select(path => Path.Combine(path, "steamapps", "shadercache")),
                "shader-cache", "清理缓存"),
            new CategoryDefinition(
                "downloads", "下载中与残留", "包含正在下载、更新或上次中断遗留的临时文件。", "\uE896", "可能包含进行中的任务 · 仅查看",
                libraryPaths.Select(path => Path.Combine(path, "steamapps", "downloading"))),
            new CategoryDefinition(
                "client-cache", "Steam 客户端缓存", "商店资源、下载清单和客户端生成的可重建缓存。", "\uE774", "由 Steam 自己清理",
                GetClientCachePaths(steamPath), "download-cache", "Steam 清理"),
            new CategoryDefinition(
                "screenshots", "游戏截图", "Steam 截图管理器保存的原图，清理前建议先备份。", "\uEB9F", "个人文件 · 仅查看",
                GetScreenshotPaths(steamPath))
        };

        var scanPlans = definitions
            .Select(definition => new ScanPlan(
                definition,
                definition.Paths
                    .Where(Directory.Exists)
                    .Select(NormalizePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();
        var totalLocations = scanPlans.Sum(plan => plan.Paths.Count);
        var completedLocations = 0;
        var categories = new List<SteamStorageCategory>(definitions.Length);
        foreach (var plan in scanPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = plan.Definition;
            var locations = new List<SteamStorageLocation>(plan.Paths.Count);
            foreach (var path in plan.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var drive = (Path.GetPathRoot(path) ?? path).TrimEnd(Path.DirectorySeparatorChar);
                progress?.Report(new SteamStorageScanProgress(
                    $"{definition.Title} · {drive}", completedLocations, Math.Max(totalLocations, 1)));
                locations.Add(definition.Key == "games"
                    ? ScanIndexedGameLocation(path, cancellationToken)
                    : ScanLocation(path, cancellationToken));
                completedLocations++;
            }
            categories.Add(new SteamStorageCategory
            {
                Key = definition.Key,
                Title = definition.Title,
                Description = definition.Description,
                Glyph = definition.Glyph,
                SafetyText = definition.SafetyText,
                Locations = locations,
                CleanupKey = definition.CleanupKey,
                CleanupLabel = definition.CleanupLabel
            });
        }
        var drives = BuildDriveSummaries(categories, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();
        return new SteamStorageSnapshot
        {
            LibraryPaths = libraryPaths,
            Categories = categories,
            Drives = drives,
            Elapsed = stopwatch.Elapsed
        };
    }

    public Task<IReadOnlyList<SteamStorageDetailItem>> GetCategoryDetailsAsync(
        string categoryKey,
        IProgress<SteamStorageDetailProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (categoryKey is not ("games" or "workshop" or "shader-cache"))
            throw new ArgumentOutOfRangeException(nameof(categoryKey), "该分类没有二级明细页面。");

        return Task.Run<IReadOnlyList<SteamStorageDetailItem>>(
            () => GetCategoryDetails(categoryKey, progress, cancellationToken),
            cancellationToken);
    }

    public Task<long> DeleteDetailItemAsync(
        SteamStorageDetailItem item,
        CancellationToken cancellationToken = default)
    {
        if (item.CategoryKey == "games")
            throw new InvalidOperationException("游戏本体必须交给 Steam 卸载，不能直接删除目录。");

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = NormalizePath(item.Path);
            ValidateDeletableDetailPath(item, normalizedPath);
            if (!Directory.Exists(normalizedPath))
                return 0L;
            if ((File.GetAttributes(normalizedPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("拒绝删除重解析目录。");

            var bytes = ScanLocation(normalizedPath, cancellationToken).SizeBytes;
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(normalizedPath, recursive: true);
            return bytes;
        }, cancellationToken);
    }

    private IReadOnlyList<SteamStorageDetailItem> GetCategoryDetails(
        string categoryKey,
        IProgress<SteamStorageDetailProgress>? progress,
        CancellationToken cancellationToken)
    {
        var libraryPaths = GetLibraryPaths();
        var manifests = ReadAppManifests(libraryPaths, cancellationToken);
        var names = manifests
            .GroupBy(item => item.AppId)
            .ToDictionary(group => group.Key, group => group.First().Name);

        var cachedGames = names
            .Select(pair => new GameInfo { AppId = pair.Key, GameName = pair.Value })
            .ToList();
        _steamApiService.PopulateFromCache(cachedGames);
        foreach (var game in cachedGames)
        {
            if (!string.IsNullOrWhiteSpace(game.GameName))
                names[game.AppId] = game.GameName;
        }

        if (categoryKey == "games")
        {
            var results = new List<SteamStorageDetailItem>(manifests.Count);
            for (var index = 0; index < manifests.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifest = manifests[index];
                progress?.Report(new SteamStorageDetailProgress(
                    names.GetValueOrDefault(manifest.AppId, manifest.Name), index, manifests.Count));
                results.Add(new SteamStorageDetailItem
                {
                    CategoryKey = categoryKey,
                    AppId = manifest.AppId,
                    Name = names.GetValueOrDefault(manifest.AppId, manifest.Name),
                    Path = Path.Combine(manifest.LibraryPath, "steamapps", "common", manifest.InstallDir),
                    SizeBytes = manifest.SizeBytes,
                    FileCount = 1
                });
            }
            progress?.Report(new SteamStorageDetailProgress("完成", manifests.Count, manifests.Count));
            return results.OrderByDescending(item => item.SizeBytes).ThenBy(item => item.Name).ToList();
        }

        var directoryName = categoryKey == "workshop"
            ? Path.Combine("steamapps", "workshop", "content")
            : Path.Combine("steamapps", "shadercache");
        var appDirectories = new List<(int AppId, string Path)>();
        foreach (var libraryPath in libraryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.Combine(libraryPath, directoryName);
            if (!Directory.Exists(root))
                continue;
            try
            {
                foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    if (int.TryParse(Path.GetFileName(path), out var appId) && appId > 0)
                        appDirectories.Add((appId, path));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var unknownGames = appDirectories
            .Select(item => item.AppId)
            .Distinct()
            .Where(appId => !names.ContainsKey(appId))
            .Select(appId => new GameInfo { AppId = appId, GameName = $"AppID {appId}" })
            .ToList();
        _steamApiService.PopulateFromCache(unknownGames);
        foreach (var game in unknownGames)
            names[game.AppId] = string.IsNullOrWhiteSpace(game.GameName) ? $"AppID {game.AppId}" : game.GameName;

        var details = new List<SteamStorageDetailItem>(appDirectories.Count);
        for (var index = 0; index < appDirectories.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var appDirectory = appDirectories[index];
            var name = names.GetValueOrDefault(appDirectory.AppId, $"AppID {appDirectory.AppId}");
            progress?.Report(new SteamStorageDetailProgress(name, index, appDirectories.Count));
            var location = ScanLocation(appDirectory.Path, cancellationToken);
            details.Add(new SteamStorageDetailItem
            {
                CategoryKey = categoryKey,
                AppId = appDirectory.AppId,
                Name = name,
                Path = location.Path,
                SizeBytes = location.SizeBytes,
                FileCount = location.FileCount
            });
        }
        progress?.Report(new SteamStorageDetailProgress("完成", appDirectories.Count, appDirectories.Count));
        return details.OrderByDescending(item => item.SizeBytes).ThenBy(item => item.Name).ToList();
    }

    private List<string> GetLibraryPaths()
    {
        var steamPath = _steamPathService.GetCustomPath();
        if (string.IsNullOrWhiteSpace(steamPath))
            steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            throw new DirectoryNotFoundException("未检测到有效的 Steam 安装目录。");

        var paths = _steamPathService.GetAllLibraryPaths()
            .Where(Directory.Exists)
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var normalizedSteamPath = NormalizePath(steamPath);
        if (!paths.Contains(normalizedSteamPath, StringComparer.OrdinalIgnoreCase))
            paths.Insert(0, normalizedSteamPath);
        return paths;
    }

    private static List<AppManifestEntry> ReadAppManifests(
        IReadOnlyList<string> libraryPaths,
        CancellationToken cancellationToken)
    {
        var manifests = new List<AppManifestEntry>();
        foreach (var libraryPath in libraryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamAppsPath = Path.Combine(libraryPath, "steamapps");
            if (!Directory.Exists(steamAppsPath))
                continue;
            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var manifestPath in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var content = File.ReadAllText(manifestPath);
                    if (!TryReadInt(AppIdRegex, content, out var appId) || appId <= 0)
                        continue;
                    var name = ReadText(NameRegex, content);
                    var installDir = ReadText(InstallDirRegex, content);
                    if (string.IsNullOrWhiteSpace(installDir))
                        continue;
                    TryReadLong(SizeOnDiskRegex, content, out var sizeBytes);
                    manifests.Add(new AppManifestEntry(
                        appId,
                        string.IsNullOrWhiteSpace(name) ? $"AppID {appId}" : name,
                        installDir,
                        Math.Max(0, sizeBytes),
                        libraryPath));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return manifests;
    }

    private static void ValidateDeletableDetailPath(SteamStorageDetailItem item, string normalizedPath)
    {
        if (item.CategoryKey is not ("workshop" or "shader-cache"))
            throw new InvalidOperationException("该条目不允许直接删除。");
        if (!string.Equals(Path.GetFileName(normalizedPath), item.AppId.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("条目目录与 AppID 不匹配，已拒绝删除。");

        var parent = Directory.GetParent(normalizedPath)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("无法验证条目目录。");
        var expectedSuffix = item.CategoryKey == "workshop"
            ? Path.Combine("steamapps", "workshop", "content")
            : Path.Combine("steamapps", "shadercache");
        if (!NormalizePath(parent).EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("条目不在标准 Steam 缓存目录中，已拒绝删除。");
    }

    private static string ReadText(Regex regex, string content) =>
        regex.Match(content) is { Success: true } match ? match.Groups[1].Value.Trim() : string.Empty;

    private static bool TryReadInt(Regex regex, string content, out int value) =>
        int.TryParse(ReadText(regex, content), out value);

    private static bool TryReadLong(Regex regex, string content, out long value) =>
        long.TryParse(ReadText(regex, content), out value);

    public Task<long> ClearShaderCachesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            long clearedBytes = 0;
            foreach (var libraryPath in _steamPathService.GetAllLibraryPaths()
                         .Where(Directory.Exists)
                         .Select(NormalizePath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var shaderRoot = Path.Combine(libraryPath, "steamapps", "shadercache");
                if (!Directory.Exists(shaderRoot))
                    continue;

                var normalizedRoot = NormalizePath(shaderRoot);
                var expectedSuffix = $"{Path.DirectorySeparatorChar}steamapps{Path.DirectorySeparatorChar}shadercache";
                if (!normalizedRoot.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"拒绝清理非标准着色器目录：{normalizedRoot}");
                if ((File.GetAttributes(normalizedRoot) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"拒绝清理重解析目录：{normalizedRoot}");

                clearedBytes += ScanLocation(normalizedRoot, cancellationToken).SizeBytes;
                Directory.Delete(normalizedRoot, recursive: true);
                Directory.CreateDirectory(normalizedRoot);
            }
            return clearedBytes;
        }, cancellationToken);
    }

    private static SteamStorageLocation ScanLocation(string path, CancellationToken cancellationToken)
    {
        long size = 0;
        var files = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var filePath in Directory.EnumerateFiles(path, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    size += new FileInfo(filePath).Length;
                    files++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new SteamStorageLocation
        {
            Path = NormalizePath(path),
            SizeBytes = size,
            FileCount = files
        };
    }

    private static SteamStorageLocation ScanIndexedGameLocation(
        string commonPath,
        CancellationToken cancellationToken)
    {
        long size = 0;
        var games = 0;
        var steamAppsPath = Directory.GetParent(commonPath)?.FullName;
        if (!string.IsNullOrWhiteSpace(steamAppsPath) && Directory.Exists(steamAppsPath))
        {
            try
            {
                foreach (var manifestPath in Directory.EnumerateFiles(
                             steamAppsPath,
                             "appmanifest_*.acf",
                             SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var content = File.ReadAllText(manifestPath);
                        var match = SizeOnDiskRegex.Match(content);
                        if (match.Success && long.TryParse(match.Groups[1].Value, out var indexedBytes))
                            size += indexedBytes;
                        games++;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new SteamStorageLocation
        {
            Path = NormalizePath(commonPath),
            SizeBytes = size,
            FileCount = games
        };
    }

    private static IReadOnlyList<SteamStorageDrive> BuildDriveSummaries(
        IReadOnlyList<SteamStorageCategory> categories,
        CancellationToken cancellationToken)
    {
        var locationGroups = categories
            .SelectMany(category => category.Locations)
            .GroupBy(location => Path.GetPathRoot(location.Path) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        var drives = new List<SteamStorageDrive>();
        foreach (var group in locationGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(group.Key))
                continue;
            drives.Add(new SteamStorageDrive
            {
                Name = group.Key.TrimEnd(Path.DirectorySeparatorChar),
                Format = "Steam 库",
                TotalBytes = 0,
                FreeBytes = 0,
                SteamBytes = group.Sum(location => location.SizeBytes)
            });
        }
        return drives;
    }

    private static IEnumerable<string> GetClientCachePaths(string steamPath)
    {
        yield return Path.Combine(steamPath, "appcache");
        yield return Path.Combine(steamPath, "depotcache");
        yield return Path.Combine(steamPath, "GPUCache");
        yield return Path.Combine(steamPath, "DawnCache");
    }

    private static IEnumerable<string> GetScreenshotPaths(string steamPath)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata))
            yield break;

        IEnumerable<string> accountDirectories;
        try
        {
            accountDirectories = Directory.EnumerateDirectories(userdata).ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var accountDirectory in accountDirectories)
        {
            var remoteRoot = Path.Combine(accountDirectory, "760", "remote");
            if (!Directory.Exists(remoteRoot))
                continue;
            string[] appDirectories;
            try
            {
                appDirectories = Directory.GetDirectories(remoteRoot);
            }
            catch
            {
                continue;
            }
            foreach (var appDirectory in appDirectories)
            {
                var screenshots = Path.Combine(appDirectory, "screenshots");
                if (Directory.Exists(screenshots))
                    yield return screenshots;
            }
        }
    }

    private static DriveContentBuilder GetOrCreateBuilder(
        IDictionary<int, DriveContentBuilder> builders,
        int appId,
        string libraryPath,
        string name)
    {
        if (builders.TryGetValue(appId, out var existing))
        {
            if (IsPlaceholderName(existing.Name, appId) && !IsPlaceholderName(name, appId))
                existing.Name = name;
            return existing;
        }
        var builder = new DriveContentBuilder
        {
            AppId = appId,
            Name = name,
            LibraryPath = libraryPath
        };
        builders[appId] = builder;
        return builder;
    }

    private static List<AppDirectoryEntry> EnumerateAppDirectories(
        IEnumerable<string> roots,
        CancellationToken cancellationToken)
    {
        var results = new List<AppDirectoryEntry>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
                continue;
            var libraryPath = FindLibraryPath(root);
            try
            {
                foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (int.TryParse(Path.GetFileName(path), out var appId) && appId > 0)
                        results.Add(new AppDirectoryEntry(appId, NormalizePath(path), libraryPath));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return results;
    }

    private List<OtherSteamLocation> GetOtherSteamLocations(
        IReadOnlyList<string> libraries,
        string driveRoot)
    {
        var results = new List<OtherSteamLocation>();
        foreach (var library in libraries)
        {
            var downloading = Path.Combine(library, "steamapps", "downloading");
            if (Directory.Exists(downloading))
                results.Add(new OtherSteamLocation("下载中与残留", downloading, library));
        }

        var steamPath = _steamPathService.GetCustomPath();
        if (string.IsNullOrWhiteSpace(steamPath))
            steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath) ||
            !string.Equals(NormalizeDriveRoot(Path.GetPathRoot(steamPath) ?? steamPath), driveRoot, StringComparison.OrdinalIgnoreCase))
            return results;

        foreach (var path in GetClientCachePaths(steamPath).Where(Directory.Exists))
            results.Add(new OtherSteamLocation("Steam 客户端缓存", path, steamPath));
        foreach (var path in GetScreenshotPaths(steamPath).Where(Directory.Exists))
            results.Add(new OtherSteamLocation("Steam 截图", path, steamPath));
        return results;
    }

    private static SteamStorageDrive CreateDriveInfo(string root, int libraryCount, long steamBytes)
    {
        var normalizedRoot = NormalizeDriveRoot(root);
        var name = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            var drive = new DriveInfo(normalizedRoot);
            return new SteamStorageDrive
            {
                Name = name,
                Format = drive.IsReady ? drive.DriveFormat : "磁盘",
                RootPath = normalizedRoot,
                VolumeLabel = drive.IsReady ? drive.VolumeLabel : string.Empty,
                LibraryCount = libraryCount,
                TotalBytes = drive.IsReady ? drive.TotalSize : 0,
                FreeBytes = drive.IsReady ? drive.AvailableFreeSpace : 0,
                SteamBytes = steamBytes
            };
        }
        catch
        {
            return new SteamStorageDrive
            {
                Name = name,
                Format = "磁盘",
                RootPath = normalizedRoot,
                LibraryCount = libraryCount,
                SteamBytes = steamBytes
            };
        }
    }

    private static string FindLibraryPath(string contentPath)
    {
        var marker = $"{Path.DirectorySeparatorChar}steamapps{Path.DirectorySeparatorChar}";
        var index = contentPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index > 0 ? NormalizePath(contentPath[..index]) : NormalizePath(contentPath);
    }

    private static bool IsPlaceholderName(string? name, int appId)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;
        var compact = Regex.Replace(name, "[^a-zA-Z0-9]", string.Empty);
        return compact.Equals($"appid{appId}", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path) ?? path;
        return Path.GetFullPath(root).TrimEnd(Path.AltDirectorySeparatorChar) +
               (root.EndsWith(Path.DirectorySeparatorChar) ? string.Empty : Path.DirectorySeparatorChar);
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024d):0.0} MB";
        if (bytes < 1024L * 1024 * 1024 * 1024) return $"{bytes / (1024d * 1024d * 1024d):0.00} GB";
        return $"{bytes / (1024d * 1024d * 1024d * 1024d):0.00} TB";
    }

    private sealed record CategoryDefinition(
        string Key,
        string Title,
        string Description,
        string Glyph,
        string SafetyText,
        IEnumerable<string> Paths,
        string? CleanupKey = null,
        string CleanupLabel = "清理");

    private sealed record ScanPlan(CategoryDefinition Definition, IReadOnlyList<string> Paths);

    private sealed record AppManifestEntry(
        int AppId,
        string Name,
        string InstallDir,
        long SizeBytes,
        string LibraryPath);

    private sealed record AppDirectoryEntry(int AppId, string Path, string LibraryPath);

    private sealed record OtherSteamLocation(string Name, string Path, string LibraryPath);

    private sealed class DriveContentBuilder
    {
        public int AppId { get; init; }
        public required string Name { get; set; }
        public required string LibraryPath { get; init; }
        public string? GamePath { get; set; }
        public string? WorkshopPath { get; set; }
        public string? ShaderPath { get; set; }
        public long GameBytes { get; set; }
        public long WorkshopBytes { get; set; }
        public long ShaderBytes { get; set; }
        public int FileCount { get; set; }
    }
}
