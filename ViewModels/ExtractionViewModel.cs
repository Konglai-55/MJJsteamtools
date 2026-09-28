using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class ExtractionViewModel : ObservableObject
{
    private readonly ISteamDepotService _depotService;
    private readonly ISteamPathService _steamPathService;
    private readonly ISteamApiService _steamApiService;
    private CancellationTokenSource? _cts;
    private readonly List<ExtractionGameItem> _allAccountGames = [];

    [ObservableProperty]
    private string _appId = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _pinManifest;

    [ObservableProperty]
    private bool _extractAchievements;

    [ObservableProperty]
    private bool _isLibraryScanning;

    [ObservableProperty]
    private string _libraryScanStatus = "尚未扫描 Steam 账号";

    [ObservableProperty]
    private int _accountCount;

    [ObservableProperty]
    private int _gameCount;

    [ObservableProperty]
    private int _resultCount;

    public ObservableCollection<ExtractionGameItem> AccountGames { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    public ExtractionViewModel(
        ISteamDepotService depotService,
        ISteamPathService steamPathService,
        ISteamApiService steamApiService)
    {
        _depotService = depotService;
        _steamPathService = steamPathService;
        _steamApiService = steamApiService;
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
    }

    [RelayCommand]
    private async Task ScanLibraryAsync()
    {
        if (IsLibraryScanning) return;
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            LibraryScanStatus = "未检测到 Steam 安装路径";
            return;
        }

        IsLibraryScanning = true;
        LibraryScanStatus = "正在读取账号与本地游戏清单…";
        try
        {
            var result = await Task.Run(() => ScanSteamAccounts(steamPath));
            _allAccountGames.Clear();
            _allAccountGames.AddRange(result.Games);
            AccountCount = result.AccountCount;
            GameCount = result.UniqueGameCount;
            ApplyLibraryFilter(string.Empty);
            LibraryScanStatus = result.Games.Count == 0
                ? "未找到本地账号游戏记录"
                : $"已扫描 {result.AccountCount} 个账号，共 {result.UniqueGameCount} 款游戏；正在补充名称和封面…";

            // Local manifests and caches do not contain every account-owned title.
            // Refresh missing metadata asynchronously so the first list appears
            // immediately, then replaces App-only labels with names and covers.
            try
            {
                await _steamApiService.RefreshGameInfoAsync(result.Metadata);
                var metadata = result.Metadata.ToDictionary(game => game.AppId);
                var enriched = result.Games.Select(game =>
                    metadata.TryGetValue(game.AppId, out var info)
                        ? game with
                        {
                            GameName = string.IsNullOrWhiteSpace(info.GameName) ? game.GameName : info.GameName,
                            CoverImagePath = string.IsNullOrWhiteSpace(info.CoverImagePath)
                                ? game.CoverImagePath
                                : info.CoverImagePath
                        }
                        : game).ToList();
                _allAccountGames.Clear();
                _allAccountGames.AddRange(enriched);
                ApplyLibraryFilter(string.Empty);
                LibraryScanStatus = $"已扫描 {result.AccountCount} 个账号，共 {result.UniqueGameCount} 款游戏；名称和封面已更新";
            }
            catch (Exception metadataError)
            {
                LibraryScanStatus = $"已扫描 {result.AccountCount} 个账号，共 {result.UniqueGameCount} 款游戏；部分名称/封面暂未补充";
                PostLog($"名称和封面补充失败：{metadataError.Message}");
            }
        }
        catch (Exception ex)
        {
            AccountGames.Clear();
            _allAccountGames.Clear();
            AccountCount = GameCount = ResultCount = 0;
            LibraryScanStatus = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsLibraryScanning = false;
        }
    }

    public void ApplyLibraryFilter(string? query)
    {
        query = query?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allAccountGames
            : _allAccountGames.Where(item =>
                item.GameName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.AppIdText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.AccountName.Contains(query, StringComparison.OrdinalIgnoreCase));

        AccountGames.Clear();
        foreach (var item in filtered)
            AccountGames.Add(item);
        ResultCount = AccountGames.Count;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartExtractionAsync()
    {
        var id = AppId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            StatusMessage = "请输入 AppID";
            return;
        }

        if (!int.TryParse(id, out var appId))
        {
            StatusMessage = "AppID 必须为数字";
            return;
        }

        if (IsRunning)
        {
            _cts?.Cancel();
            return;
        }

        IsRunning = true;
        StatusMessage = "正在查询游戏仓库信息...";
        LogLines.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            PostLog($"开始查询 AppID:{id}");

            // 1. Query depot info from api.steamcmd.net
            var queryResult = await Task.Run(() => _depotService.QueryAppAsync(appId, ct), ct);
            if (queryResult == null || queryResult.GameDepots.Count == 0)
            {
                StatusMessage = "查询失败，未找到该游戏的仓库信息";
                PostLog("❌ 查询失败，未找到该游戏的仓库信息");
                return;
            }

            PostLog($"✔ 已获取仓库信息，游戏名称：{queryResult.AppName}");
            PostLog($"找到 {queryResult.GameDepots.Count} 个仓库（已排除共享仓库），{queryResult.DlcAppIds.Count} 个 DLC");
            StatusMessage = "正在读取 Steam 配置...";

            // 2. Read config.vdf and parse depot keys
            var steamPath = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                StatusMessage = "未检测到 Steam 安装路径";
                PostLog("❌ 未检测到 Steam 安装路径");
                return;
            }

            var vdfPath = Path.Combine(steamPath, "config", "config.vdf");
            if (!File.Exists(vdfPath))
            {
                StatusMessage = "未找到 config.vdf";
                PostLog($"❌ 未找到 config.vdf：{vdfPath}");
                return;
            }

            var vdfContent = await File.ReadAllTextAsync(vdfPath, ct);
            var depotKeys = VdfHelper.ParseDepotKeys(vdfContent);
            PostLog($"✔ 已读取 Steam 配置，找到 {depotKeys.Count} 个仓库密钥");

            // 3. Generate lua content
            var sb = new StringBuilder();
            sb.AppendLine("-- lua by Fluent-Steam-Lua (https://github.com/huanyuejue/Fluent-Steam-Lua)");
            sb.AppendLine();

            var matchedCount = 0;

            if (depotKeys.TryGetValue(id, out var mainKey))
            {
                sb.AppendLine($"addappid({id}, 1, \"{mainKey}\")");
                matchedCount++;
                PostLog($"✔ 主仓库 {id} 密钥匹配成功");
            }
            else
            {
                sb.AppendLine($"addappid({id})");
                PostLog($"⚠ 主仓库 {id} 未找到密钥，跳过加密");
            }

            foreach (var depot in queryResult.GameDepots)
            {
                var depotIdStr = depot.DepotId.ToString();
                if (depotKeys.TryGetValue(depotIdStr, out var key))
                {
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{key}\")");
                    if (PinManifest && !string.IsNullOrEmpty(depot.ManifestId))
                        sb.AppendLine($"setManifestid({depot.DepotId},\"{depot.ManifestId}\",0)");
                    depot.Key = key;
                    depot.IsMatched = true;
                    matchedCount++;
                    PostLog($"✔ 仓库 {depot.DepotId} 密钥匹配成功");
                }
                else
                {
                    PostLog($"⚠ 仓库 {depot.DepotId} 未找到密钥，跳过");
                }
            }

            // 5. Process DLCs — query each DLC's depots for key matching
            var mainDepotIds = new HashSet<int>(queryResult.GameDepots.Select(d => d.DepotId));
            foreach (var dlcAppId in queryResult.DlcAppIds)
            {
                var dlcIdStr = dlcAppId.ToString();
                var isMainDepot = mainDepotIds.Contains(dlcAppId);

                // If DLC app ID is also a main game depot, skip addappid (already handled above)
                if (!isMainDepot)
                {
                    if (depotKeys.TryGetValue(dlcIdStr, out var dlcKey))
                    {
                        sb.AppendLine($"addappid({dlcAppId}, 1, \"{dlcKey}\")");
                        matchedCount++;
                        PostLog($"✔ DLC {dlcAppId} 密钥匹配成功");
                    }
                    else
                    {
                        sb.AppendLine($"addappid({dlcAppId})");
                    }
                }

                // Query DLC's own sub-depots for additional key matching
                var dlcResult = await Task.Run(() => _depotService.QueryAppAsync(dlcAppId, ct), ct);
                if (dlcResult != null)
                {
                    int subMatched = 0;
                    foreach (var depot in dlcResult.GameDepots)
                    {
                        if (depotKeys.TryGetValue(depot.DepotId.ToString(), out var depotKey))
                        {
                            sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depotKey}\")");
                            if (PinManifest && !string.IsNullOrEmpty(depot.ManifestId))
                                sb.AppendLine($"setManifestid({depot.DepotId},\"{depot.ManifestId}\",0)");
                            matchedCount++;
                            subMatched++;
                        }
                    }
                    if (dlcResult.GameDepots.Count > 0)
                        PostLog(isMainDepot
                            ? $"✔ DLC {dlcAppId}（跳过，已是主仓库）{subMatched}/{dlcResult.GameDepots.Count} 子仓库匹配密钥"
                            : $"✔ DLC {dlcAppId} {subMatched}/{dlcResult.GameDepots.Count} 子仓库匹配密钥");
                    else
                        PostLog(isMainDepot
                            ? $"ℹ DLC {dlcAppId}（跳过，已是主仓库），无额外子仓库"
                            : $"ℹ DLC {dlcAppId} 无子仓库");
                }
                else
                {
                    PostLog(isMainDepot
                        ? $"ℹ DLC {dlcAppId}（跳过，已是主仓库），无额外子仓库信息"
                        : $"ℹ DLC {dlcAppId} 无子仓库信息");
                }
            }

            if (matchedCount == 0)
            {
                StatusMessage = "未找到任何可用密钥";
                PostLog("❌ 本地 config.vdf 中未找到该游戏及其仓库的任何密钥");
                PostLog("提示：请确保 Steam 已登录正版账号并启动过该游戏");
                return;
            }

            // 4. Save to Cache\dump\{appid}\
            StatusMessage = "正在保存 Lua 清单...";
            var dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "dump", id);
            if (!Directory.Exists(dumpDir))
                Directory.CreateDirectory(dumpDir);

            var luaPath = Path.Combine(dumpDir, $"{id}.lua");
            await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);

            StatusMessage = $"提取完成，匹配到 {matchedCount} 个密钥";
            PostLog($"✔ 提取成功！文件已保存到：{luaPath}");

            if (ExtractAchievements)
            {
                var statsDir = Path.Combine(steamPath, "appcache", "stats");
                if (Directory.Exists(statsDir))
                {
                    var achFiles = Directory.GetFiles(statsDir, $"*{id}*")
                        .Where(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (achFiles.Count > 0)
                    {
                        var largest = achFiles.OrderByDescending(f => new FileInfo(f).Length).First();
                        var dest = Path.Combine(dumpDir, Path.GetFileName(largest));
                        File.Copy(largest, dest, true);
                        PostLog($"✔ 已提取成就文件：{Path.GetFileName(largest)}");
                    }
                    else
                    {
                        PostLog("ℹ 未找到该游戏的成就缓存文件");
                    }
                }
                else
                {
                    PostLog($"ℹ 成就缓存目录不存在：{statsDir}");
                }
            }

            ShowOpenDirectoryPrompt(dumpDir);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消";
            PostLog("⏹ 操作已取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"异常: {ex.Message}";
            PostLog($"❌ 异常: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ShowOpenDirectoryPrompt(string directory)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = "提取完成",
                Content = new TextBlock
                {
                    Text = $"提取完成！文件已保存到:\n{directory}\n\n是否打开该目录？",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                PrimaryButtonText = "打开目录",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                Process.Start("explorer.exe", directory);
        }));
    }

    private void PostLog(string message)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => LogLines.Add(message)));
    }

    private AccountScanResult ScanSteamAccounts(string steamPath)
    {
        var manifests = new Dictionary<int, ManifestGame>();
        foreach (var library in EnumerateLibraries(steamPath))
        {
            var appsPath = Path.Combine(library, "steamapps");
            if (!Directory.Exists(appsPath)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(appsPath, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifestPath);
                    var appIdMatch = Regex.Match(text, "\\\"appid\\\"\\s+\\\"?(\\d+)\\\"?", RegexOptions.IgnoreCase);
                    if (!appIdMatch.Success || !int.TryParse(appIdMatch.Groups[1].Value, out var appId)) continue;
                    var name = ReadVdfValue(text, "name");
                    var installDir = ReadVdfValue(text, "installdir");
                    manifests[appId] = new ManifestGame(
                        appId,
                        string.IsNullOrWhiteSpace(name) ? $"App {appId}" : name,
                        manifestPath,
                        string.IsNullOrWhiteSpace(installDir) ? "" : installDir);
                }
                catch { }
            }
        }

        var loginNames = ReadLoginNames(Path.Combine(steamPath, "config", "loginusers.vdf"));
        var accountDirs = Directory.Exists(Path.Combine(steamPath, "userdata"))
            ? Directory.EnumerateDirectories(Path.Combine(steamPath, "userdata"))
                .Where(path => int.TryParse(Path.GetFileName(path), out _))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        if (accountDirs.Count == 0)
            accountDirs.Add(string.Empty);

        var cachedGames = manifests.Values
            .Select(game => new GameInfo { AppId = game.AppId, GameName = game.Name })
            .ToList();
        _steamApiService.PopulateFromCache(cachedGames);
        var cachedMetadata = cachedGames.ToDictionary(game => game.AppId);

        var rows = new List<ExtractionGameItem>();
        for (var accountIndex = 0; accountIndex < accountDirs.Count; accountIndex++)
        {
            var accountDir = accountDirs[accountIndex];
            var accountId = Path.GetFileName(accountDir);
            var accountName = string.IsNullOrWhiteSpace(accountId)
                ? "当前 Steam 配置"
                : loginNames.TryGetValue(accountId, out var friendlyName)
                    ? friendlyName
                    : $"Steam 账号 {accountIndex + 1}";
            var localConfig = string.IsNullOrWhiteSpace(accountDir)
                ? string.Empty
                : Path.Combine(accountDir, "config", "localconfig.vdf");
            var accountApps = ReadLocalConfigApps(localConfig);
            if (accountApps.Count == 0)
                accountApps = manifests.Keys.ToHashSet();

            foreach (var appId in accountApps.OrderBy(id => id))
            {
                manifests.TryGetValue(appId, out var manifest);
                var luaPath = FindLuaPath(steamPath, appId);
                cachedMetadata.TryGetValue(appId, out var cachedGame);
                var coverPath = FindCoverPath(steamPath, appId, cachedGame?.CoverImagePath);
                rows.Add(new ExtractionGameItem(
                    appId,
                    cachedGame?.GameName ?? manifest?.Name ?? $"App {appId}",
                    accountName,
                    accountId,
                    manifest?.ManifestPath ?? string.Empty,
                    luaPath,
                    coverPath,
                    manifest is not null));
            }
        }

        // If no userdata records exist, still expose installed games as one local account.
        if (rows.Count == 0)
        {
            rows.AddRange(manifests.Values.OrderBy(game => game.Name).Select(game =>
                new ExtractionGameItem(game.AppId,
                    cachedMetadata.TryGetValue(game.AppId, out var cached) ? cached.GameName : game.Name,
                    "当前 Steam 配置", "", game.ManifestPath,
                    FindLuaPath(steamPath, game.AppId),
                    FindCoverPath(steamPath, game.AppId, cached?.CoverImagePath), true)));
        }

        var metadataById = rows
            .Where(row => row.AppId > 0)
            .GroupBy(row => row.AppId)
            .ToDictionary(group => group.Key, group => new GameInfo
            {
                AppId = group.Key,
                GameName = group.Select(row => row.GameName)
                    .FirstOrDefault(name => !name.StartsWith("App ", StringComparison.OrdinalIgnoreCase))
                    ?? group.First().GameName
            });
        _steamApiService.PopulateFromCache(metadataById.Values.ToList());
        rows = rows.Select(row => metadataById.TryGetValue(row.AppId, out var info)
            ? row with
            {
                GameName = string.IsNullOrWhiteSpace(info.GameName) ? row.GameName : info.GameName,
                CoverImagePath = FindCoverPath(steamPath, row.AppId, info.CoverImagePath)
            }
            : row).ToList();

        return new AccountScanResult(
            rows,
            metadataById.Values.ToList(),
            rows.Select(row => row.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            rows.Select(row => row.AppId).Where(appId => appId > 0).Distinct().Count());
    }

    private static IEnumerable<string> EnumerateLibraries(string steamPath)
    {
        var libraries = new List<string> { steamPath };
        var libraryFolders = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFolders))
        {
            try
            {
                var text = File.ReadAllText(libraryFolders);
                foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                {
                    var path = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase)) libraries.Add(path);
                }
            }
            catch { }
        }
        return libraries;
    }

    private static HashSet<int> ReadLocalConfigApps(string path)
    {
        var ids = new HashSet<int>();
        if (!File.Exists(path)) return ids;
        try
        {
            var text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, "\\\"(\\d{3,})\\\"\\s*\\{", RegexOptions.IgnoreCase))
            {
                if (int.TryParse(match.Groups[1].Value, out var appId) && appId > 1000)
                    ids.Add(appId);
            }
        }
        catch { }
        return ids;
    }

    private static Dictionary<string, string> ReadLoginNames(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        try
        {
            var text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text,
                "\\\"(?<id>\\d{10,})\\\"\\s*\\{(?:(?!\\\"\\d{10,}\\\"\\s*\\{).)*?\\\"PersonaName\\\"\\s+\\\"(?<name>[^\\\"]*)\\\"",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var name = match.Groups["name"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var steamId64 = match.Groups["id"].Value;
                    result[steamId64] = name;
                    // loginusers.vdf stores SteamID64 while userdata folders use
                    // the 32-bit account id. Keep both keys for reliable mapping.
                    if (ulong.TryParse(steamId64, out var parsedId) && parsedId >= 76561197960265728UL)
                        result[(parsedId - 76561197960265728UL).ToString()] = name;
                }
            }
        }
        catch { }
        return result;
    }

    private static string ReadVdfValue(string text, string key)
    {
        var match = Regex.Match(text, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]*)\\\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace("\\\\", "\\") : string.Empty;
    }

    private static string FindLuaPath(string steamPath, int appId)
    {
        var candidates = new[]
        {
            Path.Combine(steamPath, "config", "lua", $"{appId}.lua"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "dump", appId.ToString(), $"{appId}.lua")
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static string FindCoverPath(string steamPath, int appId, string? cachedPath)
    {
        var candidates = new[]
        {
            cachedPath ?? string.Empty,
            Path.Combine(steamPath, "appcache", "librarycache", appId.ToString(), "header.jpg"),
            Path.Combine(steamPath, "appcache", "librarycache", appId.ToString(), "library_hero.jpg"),
            Path.Combine(steamPath, "appcache", "librarycache", appId.ToString(), "library_600x900.jpg")
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private sealed record ManifestGame(int AppId, string Name, string ManifestPath, string InstallDirectory);
    private sealed record AccountScanResult(
        List<ExtractionGameItem> Games,
        List<GameInfo> Metadata,
        int AccountCount,
        int UniqueGameCount);
}

public sealed record ExtractionGameItem(
    int AppId,
    string GameName,
    string AccountName,
    string AccountId,
    string ManifestPath,
    string LuaPath,
    string CoverImagePath,
    bool IsInstalled)
{
    public string AppIdText => AppId.ToString();
    public string AccountCaption => AccountName;
    public string StateText => !string.IsNullOrWhiteSpace(LuaPath) ? "已有 Lua" : IsInstalled ? "已安装" : "账号记录";
    public string StateGlyph => !string.IsNullOrWhiteSpace(LuaPath) ? "●" : IsInstalled ? "◆" : "○";
}
