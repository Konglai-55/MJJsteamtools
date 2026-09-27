using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class ScriptDownloadViewModel : ObservableObject
{
    private readonly ISteamPathService _steamPathService;
    private readonly ISteamDepotService _depotService;
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamCloudPreferenceService _steamCloudPreferenceService;
    private readonly DispatcherTimer _modeRefreshTimer;
    private string _currentDownloadMode = "DepotKey";

    [ObservableProperty]
    private string _gameId = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasStartedSearch;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _isImportResultOpen;

    [ObservableProperty]
    private string _importResultTitle = string.Empty;

    [ObservableProperty]
    private string _importResultMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _importResultSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private bool _isImportResultSuccess;

    [ObservableProperty]
    private int _importResultGameCount;

    [ObservableProperty]
    private int _importResultImportedDlcCount;

    [ObservableProperty]
    private string _importResultTotalDlcCount = "未知";

    [ObservableProperty]
    private string _importResultDetail = string.Empty;

    public bool IsDepotKeyMode => _currentDownloadMode == "DepotKey";
    public bool IsLocalCacheMode => _currentDownloadMode == "DepotKey" || _currentDownloadMode == "DepotKey2";
    public string CurrentDataSourceLabel => _currentDownloadMode switch
    {
        "DepotKey" => "本地缓存仓库V1",
        "DepotKey2" => "本地缓存仓库V2",
        _ => "远程清单仓库"
    };

    public ObservableCollection<FoundGame> SearchResults { get; } = new();
    public ObservableCollection<SearchSuggestion> SearchSuggestions { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<FoundGame> HotRecommendations { get; } = new();
    public ObservableCollection<FoundGame> MoreRecommendations { get; } = new();
    public ObservableCollection<CarouselPageIndicator> HotRecommendationPages { get; } = new();
    private int _hotRecommendationStartIndex;
    private bool _isCarouselSwitching;

    public FoundGame? HotRecommendationSlot1 => GetHotRecommendationSlot(0);
    public FoundGame? HotRecommendationSlot2 => GetHotRecommendationSlot(1);
    public FoundGame? HotRecommendationSlot3 => GetHotRecommendationSlot(2);
    public FoundGame? HotRecommendationSlot4 => GetHotRecommendationSlot(3);
    public FoundGame? HotRecommendationSlot5 => GetHotRecommendationSlot(4);

    public ScriptDownloadViewModel(
        ISteamPathService steamPathService,
        ISteamDepotService depotService,
        ISettingsService settingsService,
        IHttpClientProvider httpClientProvider,
        ISteamCloudPreferenceService steamCloudPreferenceService)
    {
        _steamPathService = steamPathService;
        _depotService = depotService;
        _settingsService = settingsService;
        _httpClientProvider = httpClientProvider;
        _steamCloudPreferenceService = steamCloudPreferenceService;
        _currentDownloadMode = _settingsService.Load().DownloadMode;
        HotRecommendations.CollectionChanged += (_, _) => NotifyHotRecommendationSlots();

        _modeRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _modeRefreshTimer.Tick += (s, e) =>
        {
            var mode = _settingsService.Load().DownloadMode;
            if (_currentDownloadMode != mode)
            {
                _currentDownloadMode = mode;
                OnPropertyChanged(nameof(IsDepotKeyMode));
                OnPropertyChanged(nameof(IsLocalCacheMode));
                OnPropertyChanged(nameof(CurrentDataSourceLabel));
            }
        };
        _modeRefreshTimer.Start();

        SeedRecommendations();
        _ = LoadDailyHotRecommendationsAsync();
    }

    public sealed record FoundGame(
        int AppId,
        string Name,
        string EnglishName,
        string CoverUrl,
        string ReleaseDate,
        string Price)
    {
        public bool IsFree { get; init; }

        public string SecondaryName => string.Equals(Name, EnglishName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : EnglishName;

        public bool HasSecondaryName => !string.IsNullOrWhiteSpace(SecondaryName);

        public string PortraitCoverUrl =>
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{AppId}/library_600x900_schinese.jpg";

        public string WideCoverUrl =>
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{AppId}/capsule_616x353_schinese.jpg";
    }

    public sealed record SearchSuggestion(int AppId, string Name, string EnglishName)
    {
        public string SecondaryName => string.Equals(Name, EnglishName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : EnglishName;

        public bool HasSecondaryName => !string.IsNullOrWhiteSpace(SecondaryName);
    }

    public sealed record CarouselPageIndicator(int Index, bool IsActive);

    private sealed record StoreSearchItem(int AppId, string Name, string CoverUrl);
    private sealed record AliasSearchItem(int AppId, string ChineseName, string EnglishName);
    private sealed record AppDetails(string? Name, string? ReleaseDate, string? Price, string? HeaderImage, bool IsFree);
    private sealed record ExtractedLuaResult(int FileCount, HashSet<int> AppIds);
    private sealed record DailyHotGamesCache(string Date, List<int> AppIds);

    private static readonly (int AppId, string Name, string EnglishName)[] HotRecommendationSeeds =
    {
        (2358720, "黑神话：悟空", "Black Myth: Wukong"),
        (2246340, "怪物猎人：荒野", "Monster Hunter Wilds"),
        (1091500, "赛博朋克 2077", "Cyberpunk 2077"),
        (1245620, "艾尔登法环", "ELDEN RING"),
        (252490, "腐蚀", "Rust"),
        (367520, "空洞骑士", "Hollow Knight")
    };

    private static readonly (int AppId, string Name, string EnglishName)[] MoreRecommendationSeeds =
    {
        (413150, "星露谷物语", "Stardew Valley"),
        (1086940, "博德之门 3", "Baldur's Gate 3"),
        (1145360, "哈迪斯", "Hades"),
        (105600, "泰拉瑞亚", "Terraria"),
        (367520, "空洞骑士", "Hollow Knight"),
        (252490, "腐蚀", "Rust")
    };

    private void SeedRecommendations()
    {
        foreach (var item in HotRecommendationSeeds)
            HotRecommendations.Add(CreateRecommendation(item));

        foreach (var item in MoreRecommendationSeeds)
            MoreRecommendations.Add(CreateRecommendation(item));
    }

    private static FoundGame CreateRecommendation((int AppId, string Name, string EnglishName) item) =>
        new(item.AppId, item.Name, item.EnglishName, GetFallbackCoverUrl(item.AppId), "正在获取发售时间", "正在获取价格");

    private async Task LoadDailyHotRecommendationsAsync()
    {
        try
        {
            var hotAppIds = await GetDailyHotAppIdsAsync();
            if (hotAppIds.Count > 0)
            {
                var candidates = hotAppIds
                    .Take(20)
                    .Select(appId => new FoundGame(
                        appId,
                        $"App {appId}",
                        string.Empty,
                        GetFallbackCoverUrl(appId),
                        "正在获取发售时间",
                        "正在获取价格"))
                    .ToList();
                var enriched = await EnrichRecommendationsAsync(candidates);
                var validGames = enriched
                    .Where(game => !game.IsFree && !game.Name.StartsWith("App ", StringComparison.Ordinal))
                    .Take(6)
                    .ToList();

                if (validGames.Count >= 5)
                {
                    HotRecommendations.Clear();
                    foreach (var game in validGames)
                        HotRecommendations.Add(game);
                    await RefreshMoreRecommendationMetadataAsync();
                    return;
                }
            }
        }
        catch
        {
            // Steam 榜单不可用时继续使用预设热门游戏。
        }

        await RefreshRecommendationMetadataAsync();
    }

    private async Task<List<int>> GetDailyHotAppIdsAsync()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MJJsteamtools",
            "cache");
        var cachePath = Path.Combine(cacheDirectory, "daily-hot-games.json");

        try
        {
            if (File.Exists(cachePath))
            {
                var cachedJson = await File.ReadAllTextAsync(cachePath);
                var cached = JsonSerializer.Deserialize<DailyHotGamesCache>(cachedJson);
                if (cached is { AppIds.Count: > 0 } && cached.Date == today)
                    return cached.AppIds;
            }
        }
        catch
        {
            // 缓存损坏时重新从 Steam 获取。
        }

        const string chartsUrl = "https://api.steampowered.com/ISteamChartsService/GetMostPlayedGames/v1/";
        var json = await _httpClientProvider.SendWithProxyRetryAsync(
            "script-steam-daily-charts",
            TimeSpan.FromSeconds(10),
            client => client.GetStringAsync(chartsUrl),
            ConfigureSteamStoreHeaders);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("ranks", out var ranks) ||
            ranks.ValueKind != JsonValueKind.Array)
            return new List<int>();

        var appIds = ranks
            .EnumerateArray()
            .Select(rank => rank.TryGetProperty("appid", out var appIdElement) && appIdElement.TryGetInt32(out var appId)
                ? appId
                : 0)
            .Where(appId => appId > 0)
            .Distinct()
            .Take(20)
            .ToList();

        if (appIds.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                var cacheJson = JsonSerializer.Serialize(new DailyHotGamesCache(today, appIds));
                await File.WriteAllTextAsync(cachePath, cacheJson);
            }
            catch
            {
                // 缓存写入失败不影响当次推荐结果。
            }
        }

        return appIds;
    }

    private async Task RefreshRecommendationMetadataAsync()
    {
        try
        {
            var hotTask = EnrichRecommendationsAsync(HotRecommendations.ToList());
            var moreTask = EnrichRecommendationsAsync(MoreRecommendations.ToList());
            await Task.WhenAll(hotTask, moreTask);

            ResetRecommendations(HotRecommendations, (await hotTask).Where(game => !game.IsFree));
            ResetRecommendations(MoreRecommendations, (await moreTask).Where(game => !game.IsFree));
        }
        catch
        {
            // 推荐区有静态兜底数据；网络失败不影响搜索和入库。
        }
    }

    private async Task RefreshMoreRecommendationMetadataAsync()
    {
        try
        {
            var enriched = await EnrichRecommendationsAsync(MoreRecommendations.ToList());
            ResetRecommendations(MoreRecommendations, enriched.Where(game => !game.IsFree));
        }
        catch
        {
            // 更多推荐同样保留静态兜底内容。
        }
    }

    private async Task<List<FoundGame>> EnrichRecommendationsAsync(List<FoundGame> games)
    {
        var metadataTasks = games.Select(async game =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var details = await GetAppDetailsAsync(game.AppId, "schinese", cts.Token);
            return (Game: game, Details: details);
        });

        var metadata = await Task.WhenAll(metadataTasks);
        var aliasCandidates = metadata
            .Where(item => !ContainsChinese(item.Game.Name) &&
                           !ContainsChinese(item.Details?.Name ?? string.Empty))
            .Select(item => item.Game.AppId)
            .ToList();
        var chineseAliases = await TryGetChineseAliasesByAppIdsAsync(aliasCandidates);

        return metadata.Select(item =>
        {
            var game = item.Game;
            var details = item.Details;
            var steamName = details?.Name ?? game.Name;
            var preferredName = ContainsChinese(steamName)
                ? steamName
                : ContainsChinese(game.Name)
                    ? game.Name
                    : chineseAliases.GetValueOrDefault(game.AppId) ?? steamName;
            var englishName = !ContainsChinese(steamName)
                ? steamName
                : game.EnglishName;

            return game with
            {
                Name = preferredName,
                EnglishName = englishName,
                CoverUrl = details?.HeaderImage ?? game.CoverUrl,
                ReleaseDate = details?.ReleaseDate ?? "发售时间未知",
                Price = details?.Price ?? "暂无价格",
                IsFree = details?.IsFree ?? game.IsFree
            };
        }).ToList();
    }

    private async Task<Dictionary<int, string>> TryGetChineseAliasesByAppIdsAsync(IEnumerable<int> appIds)
    {
        var ids = appIds.Distinct().Take(25).ToList();
        if (ids.Count == 0)
            return new Dictionary<int, string>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var values = string.Join(' ', ids.Select(id => $"\"{id}\""));
            var query = "SELECT ?steamAppId ?itemLabel WHERE { " +
                $"VALUES ?steamAppId {{ {values} }} " +
                "?item wdt:P1733 ?steamAppId. " +
                "SERVICE wikibase:label { bd:serviceParam wikibase:language \"zh-cn,zh,en\". } }";
            var url = $"https://query.wikidata.org/sparql?format=json&query={Uri.EscapeDataString(query)}";
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                "script-wikidata-recommendation-names",
                TimeSpan.FromSeconds(8),
                client => client.GetStringAsync(url, cts.Token),
                ConfigureWikidataHeaders);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                !results.TryGetProperty("bindings", out var bindings) ||
                bindings.ValueKind != JsonValueKind.Array)
                return new Dictionary<int, string>();

            var aliases = new Dictionary<int, string>();
            foreach (var binding in bindings.EnumerateArray())
            {
                if (!binding.TryGetProperty("steamAppId", out var appIdNode) ||
                    !appIdNode.TryGetProperty("value", out var appIdValue) ||
                    !int.TryParse(appIdValue.GetString(), out var appId) ||
                    !binding.TryGetProperty("itemLabel", out var labelNode) ||
                    !labelNode.TryGetProperty("value", out var labelValue))
                    continue;

                var label = labelValue.GetString();
                if (!string.IsNullOrWhiteSpace(label) && ContainsChinese(label))
                    aliases[appId] = NormalizeChineseAlias(label);
            }
            return aliases;
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    private static string NormalizeChineseAlias(string text)
    {
        var simplified = Microsoft.VisualBasic.Strings.StrConv(
            text,
            Microsoft.VisualBasic.VbStrConv.SimplifiedChinese,
            0x0804) ?? text;
        return simplified
            .Replace(" (电子游戏)", string.Empty, StringComparison.Ordinal)
            .Replace("（电子游戏）", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static void ResetRecommendations(ObservableCollection<FoundGame> target, IEnumerable<FoundGame> source)
    {
        var items = source.ToList();
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    [RelayCommand]
    private async Task ShowNextRecommendationsAsync()
    {
        if (_isCarouselSwitching || HotRecommendations.Count < 2) return;
        _isCarouselSwitching = true;
        try
        {
            _hotRecommendationStartIndex = (_hotRecommendationStartIndex + 1) % HotRecommendations.Count;
            NotifyHotRecommendationSlots();
            await Task.Delay(180);
        }
        finally
        {
            _isCarouselSwitching = false;
        }
    }

    [RelayCommand]
    private async Task ShowPreviousRecommendationsAsync()
    {
        if (_isCarouselSwitching || HotRecommendations.Count < 2) return;
        _isCarouselSwitching = true;
        try
        {
            _hotRecommendationStartIndex =
                (_hotRecommendationStartIndex - 1 + HotRecommendations.Count) % HotRecommendations.Count;
            NotifyHotRecommendationSlots();
            await Task.Delay(180);
        }
        finally
        {
            _isCarouselSwitching = false;
        }
    }

    private FoundGame? GetHotRecommendationSlot(int offset)
    {
        if (HotRecommendations.Count == 0) return null;
        var normalizedStart = _hotRecommendationStartIndex % HotRecommendations.Count;
        return HotRecommendations[(normalizedStart + offset) % HotRecommendations.Count];
    }

    private void NotifyHotRecommendationSlots()
    {
        if (HotRecommendations.Count == 0)
            _hotRecommendationStartIndex = 0;
        else if (_hotRecommendationStartIndex >= HotRecommendations.Count)
            _hotRecommendationStartIndex %= HotRecommendations.Count;

        OnPropertyChanged(nameof(HotRecommendationSlot1));
        OnPropertyChanged(nameof(HotRecommendationSlot2));
        OnPropertyChanged(nameof(HotRecommendationSlot3));
        OnPropertyChanged(nameof(HotRecommendationSlot4));
        OnPropertyChanged(nameof(HotRecommendationSlot5));

        HotRecommendationPages.Clear();
        for (var index = 0; index < HotRecommendations.Count; index++)
        {
            HotRecommendationPages.Add(new CarouselPageIndicator(
                index,
                index == _hotRecommendationStartIndex));
        }
    }

    private static void ConfigureSteamStoreHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    private static void ConfigureWikidataHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "MJJsteamtools/2.0.0 (Steam game name lookup)");
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching) return;

        var query = GameId?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            StatusMessage = "请输入游戏ID或名称";
            return;
        }

        HasStartedSearch = true;
        IsSearching = true;
        SearchResults.Clear();
        SearchSuggestions.Clear();
        LogLines.Clear();
        AddLog($"🔍 搜索：{query}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            if (int.TryParse(query, out int appId))
            {
                var chineseDetailsTask = GetAppDetailsAsync(appId, "schinese", cts.Token);
                var englishDetailsTask = GetAppDetailsAsync(appId, "english", cts.Token);
                await Task.WhenAll(chineseDetailsTask, englishDetailsTask);

                var chineseDetails = await chineseDetailsTask;
                var englishDetails = await englishDetailsTask;
                var displayName = chineseDetails?.Name ?? englishDetails?.Name;
                if (displayName != null)
                {
                    SearchResults.Add(new FoundGame(
                        appId,
                        displayName,
                        englishDetails?.Name ?? string.Empty,
                        chineseDetails?.HeaderImage ?? englishDetails?.HeaderImage ?? GetFallbackCoverUrl(appId),
                        chineseDetails?.ReleaseDate ?? englishDetails?.ReleaseDate ?? "发售日未知",
                        chineseDetails?.Price ?? englishDetails?.Price ?? "暂无价格"));
                    AddLog($"✅ 找到：{displayName} (AppID: {appId})");
                    StatusMessage = $"找到：{displayName}";
                }
                else
                {
                    AddLog($"❌ 未找到 AppId 对应的游戏：{appId}");
                    StatusMessage = "未找到匹配的游戏";
                }
            }
            else
            {
                await SearchByNameAsync(query, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("❌ 搜索超时（15秒），请检查网络连接");
            AddLog("💡 建议：尝试开启VPN或代理后重试");
            StatusMessage = "搜索超时，请检查网络";
        }
        catch (Exception ex)
        {
            AddLog($"❌ 搜索异常：{ex.Message}");
            StatusMessage = $"搜索异常：{ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    public async Task<int> UpdateSearchSuggestionsAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2)
        {
            SearchSuggestions.Clear();
            return 0;
        }

        var chineseTask = TryLoadSuggestionLocaleAsync(query, "schinese", ct);
        var englishTask = TryLoadSuggestionLocaleAsync(query, "english", ct);
        await Task.WhenAll(chineseTask, englishTask);

        var chineseResults = await chineseTask;
        var englishResults = await englishTask;
        var chineseById = chineseResults
            .GroupBy(item => item.AppId)
            .ToDictionary(group => group.Key, group => group.First());
        var englishById = englishResults
            .GroupBy(item => item.AppId)
            .ToDictionary(group => group.Key, group => group.First());
        var orderedAppIds = chineseResults
            .Select(item => item.AppId)
            .Concat(englishResults.Select(item => item.AppId))
            .Distinct()
            .Take(8)
            .ToList();

        ct.ThrowIfCancellationRequested();
        SearchSuggestions.Clear();
        foreach (var appId in orderedAppIds)
        {
            chineseById.TryGetValue(appId, out var chinese);
            englishById.TryGetValue(appId, out var english);
            var displayName = chinese?.Name ?? english?.Name ?? $"App {appId}";
            SearchSuggestions.Add(new SearchSuggestion(appId, displayName, english?.Name ?? string.Empty));
        }

        return SearchSuggestions.Count;
    }

    private async Task<List<StoreSearchItem>> TryLoadSuggestionLocaleAsync(
        string query,
        string language,
        CancellationToken ct)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&cc=cn&l={language}";
            var client = _httpClientProvider.GetClient(
                $"script-steam-suggestions-{language}",
                TimeSpan.FromSeconds(6),
                ConfigureSteamStoreHeaders);
            var json = await client.GetStringAsync(url, ct);
            return ParseStoreSearchResults(json, query, 8);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new List<StoreSearchItem>();
        }
    }

    private async Task<AppDetails?> GetAppDetailsAsync(int appId, string language, CancellationToken ct = default)
    {
        var requestName = $"script-steam-appdetails-{appId}-{language}-{Guid.NewGuid():N}";
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=cn&l={language}";
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                requestName,
                TimeSpan.FromSeconds(6),
                client => client.GetStringAsync(url, ct),
                ConfigureSteamStoreHeaders);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appId.ToString(), out var root) ||
                !root.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("data", out var data))
                return null;

            var name = data.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var releaseDate = data.TryGetProperty("release_date", out var releaseDateElement) &&
                              releaseDateElement.TryGetProperty("date", out var dateElement)
                ? LocalizeReleaseDate(dateElement.GetString())
                : null;

            var isFree = data.TryGetProperty("is_free", out var isFreeElement) &&
                         isFreeElement.ValueKind == JsonValueKind.True;
            string? price = null;
            if (isFree)
            {
                price = "免费";
            }
            else if (data.TryGetProperty("price_overview", out var priceElement) &&
                     priceElement.TryGetProperty("final_formatted", out var formattedElement))
            {
                price = formattedElement.GetString();
            }

            var headerImage = data.TryGetProperty("header_image", out var headerImageElement)
                ? headerImageElement.GetString()
                : null;

            return new AppDetails(name, releaseDate, price, headerImage, isFree);
        }
        catch
        {
            // Metadata is optional: a failed detail request must not hide a valid search result.
            return null;
        }
        finally
        {
            _httpClientProvider.Reset(requestName);
        }
    }

    private async Task SearchByNameAsync(string name, CancellationToken ct)
    {
        var chineseTask = TrySearchStoreLocaleAsync(name, "schinese", "中文", ct);
        var englishTask = TrySearchStoreLocaleAsync(name, "english", "英文", ct);
        var aliasTask = ContainsChinese(name)
            ? TrySearchChineseAliasesAsync(name, ct)
            : Task.FromResult(new List<AliasSearchItem>());
        await Task.WhenAll(chineseTask, englishTask, aliasTask);

        var chineseResults = await chineseTask;
        var englishResults = await englishTask;
        var aliasResults = await aliasTask;
        var chineseById = chineseResults
            .GroupBy(item => item.AppId)
            .ToDictionary(group => group.Key, group => group.First());
        var englishById = englishResults
            .GroupBy(item => item.AppId)
            .ToDictionary(group => group.Key, group => group.First());
        var aliasesById = aliasResults
            .GroupBy(item => item.AppId)
            .ToDictionary(group => group.Key, group => group.First());

        var orderedAppIds = aliasResults
            .Select(item => item.AppId)
            .Concat(chineseResults.Select(item => item.AppId))
            .Concat(englishResults.Select(item => item.AppId))
            .Distinct()
            .Take(10)
            .ToList();

        if (orderedAppIds.Count == 0)
        {
            AddLog("❌ 未找到匹配的游戏");
            StatusMessage = "未找到匹配的游戏";
            return;
        }

        var detailTasks = orderedAppIds.ToDictionary(
            appId => appId,
            appId => GetAppDetailsAsync(appId, "english", ct));
        await Task.WhenAll(detailTasks.Values);

        foreach (var appId in orderedAppIds)
        {
            aliasesById.TryGetValue(appId, out var alias);
            chineseById.TryGetValue(appId, out var chinese);
            englishById.TryGetValue(appId, out var english);

            var details = await detailTasks[appId];
            var displayName = alias?.ChineseName ?? chinese?.Name ?? english?.Name ?? details?.Name ?? $"App {appId}";
            var englishName = alias?.EnglishName ?? details?.Name ?? english?.Name ?? string.Empty;
            var coverUrl = details?.HeaderImage ?? chinese?.CoverUrl ?? english?.CoverUrl ?? GetFallbackCoverUrl(appId);

            SearchResults.Add(new FoundGame(
                appId,
                displayName,
                englishName,
                coverUrl,
                details?.ReleaseDate ?? "发售日未知",
                details?.Price ?? "暂无价格"));
        }

        var aliasNote = aliasResults.Count > 0 ? $"，其中中文别名匹配 {aliasResults.Count} 个" : string.Empty;
        AddLog($"✅ 中英文联合搜索找到 {orderedAppIds.Count} 个结果{aliasNote}");
        StatusMessage = $"找到 {orderedAppIds.Count} 个匹配结果";
    }

    private async Task<List<AliasSearchItem>> TrySearchChineseAliasesAsync(string query, CancellationToken ct)
    {
        using var aliasCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        aliasCts.CancelAfter(TimeSpan.FromSeconds(4));

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var searchUrl = "https://www.wikidata.org/w/api.php" +
                $"?action=wbsearchentities&search={encodedQuery}&language=zh&uselang=zh" +
                "&type=item&limit=10&format=json&origin=*";
            var searchJson = await _httpClientProvider.SendWithProxyRetryAsync(
                "script-wikidata-alias-search",
                TimeSpan.FromSeconds(5),
                client => client.GetStringAsync(searchUrl, aliasCts.Token),
                ConfigureWikidataHeaders);

            using var searchDoc = JsonDocument.Parse(searchJson);
            if (!searchDoc.RootElement.TryGetProperty("search", out var searchItems) ||
                searchItems.ValueKind != JsonValueKind.Array)
                return new List<AliasSearchItem>();

            var entityIds = searchItems
                .EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id) && id![0] == 'Q')
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToList();
            if (entityIds.Count == 0)
                return new List<AliasSearchItem>();

            var encodedIds = Uri.EscapeDataString(string.Join('|', entityIds));
            var entitiesUrl = "https://www.wikidata.org/w/api.php" +
                $"?action=wbgetentities&ids={encodedIds}&props=labels%7Cclaims" +
                "&languages=zh-cn%7Czh%7Cen&languagefallback=1&format=json&origin=*";
            var entitiesJson = await _httpClientProvider.SendWithProxyRetryAsync(
                "script-wikidata-alias-entities",
                TimeSpan.FromSeconds(5),
                client => client.GetStringAsync(entitiesUrl, aliasCts.Token),
                ConfigureWikidataHeaders);

            using var entitiesDoc = JsonDocument.Parse(entitiesJson);
            if (!entitiesDoc.RootElement.TryGetProperty("entities", out var entities) ||
                entities.ValueKind != JsonValueKind.Object)
                return new List<AliasSearchItem>();

            var results = new List<AliasSearchItem>();
            foreach (var entityId in entityIds)
            {
                if (!entities.TryGetProperty(entityId, out var entity))
                    continue;
                if (!TryGetSteamAppId(entity, out var appId))
                    continue;

                var chineseName = GetEntityLabel(entity, "zh-cn")
                    ?? GetEntityLabel(entity, "zh")
                    ?? GetEntityLabel(entity, "en")
                    ?? $"App {appId}";
                var englishName = GetEntityLabel(entity, "en") ?? string.Empty;
                results.Add(new AliasSearchItem(appId, chineseName, englishName));
            }

            if (results.Count > 0)
                AddLog($"💡 中文别名索引匹配到 {results.Count} 个 Steam 游戏");
            return results;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AddLog("⚠️ 中文别名索引响应较慢，已使用 Steam 搜索结果");
            return new List<AliasSearchItem>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddLog($"⚠️ 中文别名索引不可用：{ex.Message}");
            return new List<AliasSearchItem>();
        }
    }

    private static bool TryGetSteamAppId(JsonElement entity, out int appId)
    {
        appId = 0;
        if (!entity.TryGetProperty("claims", out var claims) ||
            !claims.TryGetProperty("P1733", out var steamClaims) ||
            steamClaims.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var claim in steamClaims.EnumerateArray())
        {
            if (!claim.TryGetProperty("mainsnak", out var mainSnak) ||
                !mainSnak.TryGetProperty("datavalue", out var dataValue) ||
                !dataValue.TryGetProperty("value", out var value))
                continue;

            var appIdText = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString();
            if (int.TryParse(appIdText, out appId) && appId > 0)
                return true;
        }
        return false;
    }

    private static string? GetEntityLabel(JsonElement entity, string language)
    {
        if (!entity.TryGetProperty("labels", out var labels) ||
            !labels.TryGetProperty(language, out var label) ||
            !label.TryGetProperty("value", out var value))
            return null;
        return value.GetString();
    }

    private static bool ContainsChinese(string text) =>
        text.Any(character => character is >= '\u3400' and <= '\u9FFF');

    private async Task<List<StoreSearchItem>> TrySearchStoreLocaleAsync(
        string query,
        string language,
        string sourceLabel,
        CancellationToken ct)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&cc=us&l={language}";
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                $"script-steam-store-{language}",
                TimeSpan.FromSeconds(10),
                client => client.GetStringAsync(url, ct),
                ConfigureSteamStoreHeaders);

            return ParseStoreSearchResults(json, query, 10);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddLog($"⚠️ {sourceLabel}搜索接口失败：{ex.Message}");
            return new List<StoreSearchItem>();
        }
    }

    private static List<StoreSearchItem> ParseStoreSearchResults(string json, string fallbackName, int limit)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return new List<StoreSearchItem>();

        var results = new List<StoreSearchItem>();
        foreach (var item in items.EnumerateArray().Take(limit))
        {
            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var appId))
                continue;

            var gameName = item.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? fallbackName
                : fallbackName;
            results.Add(new StoreSearchItem(appId, gameName, GetCoverUrl(item, appId)));
        }

        return results;
    }

    private static string GetCoverUrl(JsonElement item, int appId)
    {
        if (item.TryGetProperty("large_image", out var largeImage) &&
            !string.IsNullOrWhiteSpace(largeImage.GetString()))
            return largeImage.GetString()!;

        if (item.TryGetProperty("small_image", out var smallImage) &&
            !string.IsNullOrWhiteSpace(smallImage.GetString()))
            return smallImage.GetString()!;

        if (item.TryGetProperty("tiny_image", out var tinyImage) &&
            !string.IsNullOrWhiteSpace(tinyImage.GetString()))
            return tinyImage.GetString()!;

        return GetFallbackCoverUrl(appId);
    }

    private static string GetFallbackCoverUrl(int appId) =>
        $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";

    private static string? LocalizeReleaseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var dateText = value.Trim();
        if (dateText.Contains('年') && dateText.Contains('月'))
            return dateText.Replace(" ", string.Empty);

        var englishCulture = CultureInfo.GetCultureInfo("en-US");
        var fullDateFormats = new[]
        {
            "d MMM, yyyy", "dd MMM, yyyy", "d MMMM, yyyy", "dd MMMM, yyyy",
            "MMM d, yyyy", "MMMM d, yyyy"
        };
        if (DateTime.TryParseExact(
                dateText,
                fullDateFormats,
                englishCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var fullDate))
        {
            return $"{fullDate.Year}年{fullDate.Month}月{fullDate.Day}日";
        }

        var monthFormats = new[] { "MMM yyyy", "MMMM yyyy" };
        if (DateTime.TryParseExact(
                dateText,
                monthFormats,
                englishCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var monthDate))
        {
            return $"{monthDate.Year}年{monthDate.Month}月";
        }

        if (dateText.Length > 3 &&
            dateText[0] is 'Q' or 'q' &&
            dateText[1] is >= '1' and <= '4' &&
            int.TryParse(dateText[2..].Trim(), out var quarterYear))
        {
            return $"{quarterYear}年第{dateText[1]}季度";
        }

        var normalized = dateText.ToLowerInvariant();
        if (normalized is "coming soon" or "coming soon..." or "coming soon…")
            return "即将推出";
        if (normalized is "to be announced" or "tba")
            return "待定";

        foreach (var (english, chinese) in new[]
                 {
                     ("spring", "春季"), ("summer", "夏季"),
                     ("autumn", "秋季"), ("fall", "秋季"), ("winter", "冬季")
                 })
        {
            if (normalized.StartsWith(english + " ", StringComparison.Ordinal) &&
                int.TryParse(dateText[(english.Length + 1)..], out var seasonYear))
            {
                return $"{seasonYear}年{chinese}";
            }
        }

        return dateText;
    }

    [RelayCommand]
    private async Task DownloadGameAsync(FoundGame game)
    {
        if (game == null || IsDownloading) return;

        var secondaryName = game.HasSecondaryName
            ? $"\n英文名：{game.SecondaryName}"
            : string.Empty;
        var disableCloudCheckBox = new CheckBox
        {
            Content = "同时关闭此游戏的 Steam 云同步",
            IsChecked = _settingsService.Load().DisableCloudForImportedGames,
            Margin = new Thickness(0, 14, 0, 0),
            FontWeight = FontWeights.SemiBold
        };
        var dialogContent = new StackPanel { MaxWidth = 460 };
        dialogContent.Children.Add(new TextBlock
        {
            Text = $"游戏：{game.Name}{secondaryName}\nAppID：{game.AppId}\n入库接口：{CurrentDataSourceLabel}\n\n确认后将开始生成或下载 Lua 入库清单。",
            TextWrapping = TextWrapping.Wrap
        });
        dialogContent.Children.Add(disableCloudCheckBox);
        dialogContent.Children.Add(new TextBlock
        {
            Text = "推荐用于虚拟入库：可阻止无云许可引起的同步报错。入库成功后会正常退出并重启 Steam；只影响当前账号的这款游戏。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.72,
            Margin = new Thickness(22, 5, 0, 0)
        });
        var dialog = new ContentDialog
        {
            Title = "确认游戏入库",
            Content = dialogContent,
            PrimaryButtonText = "开始入库",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            StatusMessage = "已取消入库";
            AddLog($"ℹ️ 已取消入库：{game.Name} (AppID: {game.AppId})");
            return;
        }

        await ExecuteDownloadAsync(game.AppId.ToString(), disableCloudCheckBox.IsChecked == true);
    }

    private async Task ExecuteDownloadAsync(string gameId, bool disableCloudAfterImport)
    {
        HasStartedSearch = true;
        IsDownloading = true;
        IsImportResultOpen = false;
        LogLines.Clear();
        AddLog($"🚀 开始处理 ID：{gameId}");
        AddLog($"📌 当前入库接口：{CurrentDataSourceLabel}");
        AddLog("=".PadRight(50, '='));

        try
        {
            if (!int.TryParse(gameId, out int appId))
            {
                AddLog("❌ 无效的游戏 ID");
                StatusMessage = "无效的游戏 ID";
                ShowImportResult(false, 0, 0, null, StatusMessage);
                return;
            }

            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder))
            {
                AddLog("❌ 未配置 Steam 路径，请先在基本设置中设置路径");
                StatusMessage = "未配置 Steam 路径";
                ShowImportResult(false, 0, 0, null, StatusMessage);
                return;
            }

            if (!Directory.Exists(luaFolder))
            {
                Directory.CreateDirectory(luaFolder);
                AddLog($"📂 已创建目录：{luaFolder}");
            }
            else
            {
                AddLog($"📂 目标目录：{luaFolder}");
            }

            if (IsLocalCacheMode)
            {
                await ExecuteDepotKeyDownloadAsync(appId, disableCloudAfterImport);
            }
            else
            {
                await ExecuteRemoteDownloadAsync(appId, luaFolder, disableCloudAfterImport);
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ 任务异常：{ex.Message}");
            StatusMessage = $"异常：{ex.Message}";
            ShowImportResult(false, 0, 0, null, StatusMessage);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void ShowImportResult(
        bool success,
        int gameCount,
        int importedDlcCount,
        int? totalDlcCount,
        string detail)
    {
        ImportResultTitle = success ? "入库成功" : "入库失败";
        ImportResultSeverity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        var totalDlcText = totalDlcCount?.ToString() ?? "未知";
        IsImportResultSuccess = success;
        ImportResultGameCount = gameCount;
        ImportResultImportedDlcCount = importedDlcCount;
        ImportResultTotalDlcCount = totalDlcText;
        ImportResultDetail = detail;
        ImportResultMessage =
            $"游戏：{gameCount} 个  ·  DLC：已入库 {importedDlcCount} 个 / 共 {totalDlcText} 个  ·  {detail}";
        IsImportResultOpen = true;
    }

    [RelayCommand]
    private void CloseImportResult()
    {
        IsImportResultOpen = false;
    }

    private async Task ExecuteDepotKeyDownloadAsync(int appId, bool disableCloudAfterImport)
    {
        _depotService.UseDataSource(_currentDownloadMode);

        AddLog("🔍 查询游戏仓库信息...");
        DepotQueryResult? queryResult = null;
        try
        {
            queryResult = await _depotService.QueryAppAsync(appId);
        }
        catch (Exception ex)
        {
            AddLog($"❌ 查询异常：{ex.InnerException?.Message ?? ex.Message}");
            StatusMessage = "查询失败";
            ShowImportResult(false, 0, 0, null, StatusMessage);
            return;
        }

        if (queryResult == null)
        {
            AddLog("❌ 查询失败：API 返回数据中不包含该 AppID 的仓库信息");
            StatusMessage = "查询失败";
            ShowImportResult(false, 0, 0, null, StatusMessage);
            return;
        }
        AddLog($"✅ 查询完成：{queryResult.AppName}");
        AddLog($"   主游戏仓库: {queryResult.GameDepots.Count} 个");
        AddLog($"   总DLC数量: {queryResult.DlcAppIds.Count} 个");

        AddLog("📥 下载密钥文件...");
        var keyReady = await _depotService.EnsureKeyFilesAsync();
        if (!keyReady)
        {
            AddLog("❌ 下载密钥文件失败");
            StatusMessage = "下载密钥文件失败";
            ShowImportResult(false, 0, 0, queryResult.DlcAppIds.Count, StatusMessage);
            return;
        }
        AddLog("✅ 密钥文件已就绪");

        AddLog("⚙️ 正在生成 Lua 配置文件...");
        string? luaPath;
        try
        {
            if (queryResult.DlcAppIds.Count > 0)
            {
                luaPath = await _depotService.GenerateLuaWithDlcAsync(appId);
                AddLog($"   包含 DLC 共 {queryResult.DlcAppIds.Count} 个");
            }
            else
            {
                luaPath = await _depotService.GenerateLuaAsync(appId);
            }
        }
        catch (InvalidOperationException ex)
        {
            var fallbackSource = _currentDownloadMode == "DepotKey" ? "DepotKey2" : "DepotKey";
            var fallbackLabel = fallbackSource == "DepotKey2" ? "V2" : "V1";
            AddLog($"⚠️ 当前仓库缺少所需密钥，自动尝试本地缓存仓库 {fallbackLabel}...");
            _depotService.UseDataSource(fallbackSource);
            try
            {
                if (!await _depotService.EnsureKeyFilesAsync())
                    throw new InvalidOperationException("备用密钥仓库不可用");

                luaPath = queryResult.DlcAppIds.Count > 0
                    ? await _depotService.GenerateLuaWithDlcAsync(appId)
                    : await _depotService.GenerateLuaAsync(appId);
                AddLog($"✅ 已自动切换到本地缓存仓库 {fallbackLabel}");
            }
            catch (Exception fallbackException)
            {
                AddLog($"❌ V1/V2 均无法生成：{fallbackException.Message}");
                AddLog($"   首选源错误：{ex.Message}");
                StatusMessage = "入库失败：两个缓存仓库均缺少所需密钥";
                ShowImportResult(false, 0, 0, queryResult.DlcAppIds.Count, StatusMessage);
                return;
            }
        }

        if (string.IsNullOrEmpty(luaPath))
        {
            AddLog("❌ 生成 Lua 文件失败");
            StatusMessage = "生成失败";
            ShowImportResult(false, 0, 0, queryResult.DlcAppIds.Count, StatusMessage);
            return;
        }

        AddLog($"✅ Lua 配置文件已保存：{luaPath}");
        AddLog($"🎉 入库成功！Lua 文件：{Path.GetFileName(luaPath)}");
        StatusMessage = $"入库成功：{queryResult.AppName}";
        var cloudResult = await ApplyCloudPreferenceAfterImportAsync(appId, disableCloudAfterImport);
        var resultDetail = string.IsNullOrWhiteSpace(cloudResult)
            ? queryResult.AppName
            : $"{queryResult.AppName} · {cloudResult}";
        ShowImportResult(
            true,
            1,
            queryResult.DlcAppIds.Count,
            queryResult.DlcAppIds.Count,
            resultDetail);
    }

    private async Task ExecuteRemoteDownloadAsync(int appId, string luaFolder, bool disableCloudAfterImport)
    {
        DepotQueryResult? queryResult = null;
        try
        {
            queryResult = await _depotService.QueryAppAsync(appId);
        }
        catch (Exception ex)
        {
            AddLog($"⚠️ 无法获取 DLC 总数，将继续入库：{ex.GetBaseException().Message}");
        }
        var totalDlcCount = queryResult?.DlcAppIds.Count;

        var zipPath = Path.Combine(Path.GetTempPath(), $"{appId}.zip");
        var directUrl = $"https://steamgames554.s3.us-east-1.amazonaws.com/{appId}.zip";
        AddLog("🌐 正在尝试直连清单存储...");
        var success = await TryDirectDownloadAsync(directUrl, zipPath);

        string? shortCode = null;
        if (success)
        {
            AddLog("✅ S3 直连下载完成");
        }
        else
        {
            AddLog("🔗 正在获取中转线路...");
            shortCode = await GetShortCodeAsync(appId.ToString());
            if (string.IsNullOrEmpty(shortCode))
            {
                AddLog("❌ 获取中转线路失败");
                StatusMessage = "直连和中转线路均不可用";
                ShowImportResult(false, 0, 0, totalDlcCount, StatusMessage);
                return;
            }
            AddLog($"🔗 获取中转短码：{shortCode}");
        }

        AddLog("📥 开始下载文件...");
        if (!success)
        {
            AddLog("🔁 直连失败，正在切换中转线路...");
            try
            {
                success = await DownloadFileAsync(shortCode!, zipPath);
            }
            catch (Exception ex)
            {
                AddLog($"❌ 中转线路下载失败：{ex.GetBaseException().Message}");
                StatusMessage = "直连和中转线路均下载失败";
                ShowImportResult(false, 0, 0, totalDlcCount, StatusMessage);
                return;
            }
        }
        if (!success)
        {
            AddLog("❌ 下载失败");
            StatusMessage = "下载失败";
            ShowImportResult(false, 0, 0, totalDlcCount, StatusMessage);
            return;
        }
        AddLog("✅ 文件下载完成");

        AddLog("📦 正在解压...");
        var extracted = ExtractLuaFiles(zipPath, luaFolder);
        var luaCount = extracted.FileCount;
        AddLog("✅ 清理临时压缩包");

        if (luaCount > 0)
        {
            AddLog($"🎉 入库完成！共导入 {luaCount} 个 Lua 脚本");
            StatusMessage = $"成功入库 {luaCount} 个 Lua 脚本";
            var importedDlcCount = queryResult is not null
                ? queryResult.DlcAppIds.Count(extracted.AppIds.Contains)
                : Math.Max(0, luaCount - 1);
            var gameCount = extracted.AppIds.Contains(appId)
                ? 1
                : Math.Max(1, luaCount - importedDlcCount);
            var cloudResult = await ApplyCloudPreferenceAfterImportAsync(appId, disableCloudAfterImport);
            var gameName = queryResult?.AppName ?? $"AppID {appId}";
            var resultDetail = string.IsNullOrWhiteSpace(cloudResult)
                ? gameName
                : $"{gameName} · {cloudResult}";
            ShowImportResult(
                true,
                gameCount,
                importedDlcCount,
                totalDlcCount,
                resultDetail);
        }
        else
        {
            AddLog("⚠️ 未找到任何 Lua 文件");
            StatusMessage = "未找到 Lua 文件";
            ShowImportResult(false, 0, 0, totalDlcCount, StatusMessage);
        }
    }

    private async Task<string> ApplyCloudPreferenceAfterImportAsync(int appId, bool disableCloudAfterImport)
    {
        if (!disableCloudAfterImport)
            return string.Empty;

        AddLog("☁️ 正在为当前账号关闭此游戏的 Steam 云同步...");
        var result = await _steamCloudPreferenceService.SetCurrentAccountStateAsync([appId], enabled: false);
        if (result.Success)
        {
            AddLog("✅ Steam 云同步已关闭，不会再尝试连接这款游戏的云存档");
            return "Steam 云同步已关闭";
        }

        AddLog($"⚠️ 入库已完成，但未能关闭 Steam 云同步：{result.Message}");
        return "云同步设置未更改";
    }

    private async Task<bool> TryDirectDownloadAsync(string url, string savePath)
    {
        try
        {
            return await RetryAsync(
                () => DownloadWithResumeAsync("script-remote-direct", url, savePath),
                "S3 直连下载",
                2);
        }
        catch (Exception ex)
        {
            AddLog($"⚠️ S3 直连不可用：{ex.GetBaseException().Message}");
            return false;
        }
    }

    private async Task<bool> DownloadWithResumeAsync(
        string clientName,
        string url,
        string savePath,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        var existingLength = File.Exists(savePath) ? new FileInfo(savePath).Length : 0;
        using var response = await _httpClientProvider.SendWithProxyRetryAsync(
            clientName,
            TimeSpan.FromMinutes(3),
            async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existingLength > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
                configureRequest?.Invoke(request);
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            });

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(savePath);
            throw new HttpRequestException("服务器拒绝断点位置，已重置临时文件");
        }

        response.EnsureSuccessStatusCode();
        var canAppend = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(
            savePath,
            canAppend ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await contentStream.CopyToAsync(fileStream);
        await fileStream.FlushAsync();
        return true;
    }

    private async Task<string?> GetShortCodeAsync(string gameId)
    {
        var targetUrl = $"https://steamgames554.s3.us-east-1.amazonaws.com/{gameId}.zip";
        var payload = new Dictionary<string, string> { { "url", targetUrl } };

        return await RetryAsync(async () =>
        {
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "script-remote-download",
                TimeSpan.FromSeconds(30),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://short.walftech.com/api_create_link.php")
                    {
                        Content = new FormUrlEncodedContent(payload)
                    };
                    request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                    request.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36 Edg/148.0.0.0");
                    request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"148\", \"Microsoft Edge\";v=\"148\", \"Not/A)Brand\";v=\"99\"");
                    request.Headers.TryAddWithoutValidation("accept", "*/*");
                    request.Headers.TryAddWithoutValidation("origin", "https://remlua.com");
                    request.Headers.TryAddWithoutValidation("referer", "https://remlua.com/");
                    return await client.SendAsync(request);
                });
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("short_code").GetString();
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ 短码 API 响应解析失败：{ex.GetType().Name}: {ex.Message}");
                AddLog($"   响应内容前 500 字符：{json[..Math.Min(json.Length, 500)]}");
                return null;
            }
        }, "获取短码");
    }

    private async Task<bool> DownloadFileAsync(string shortCode, string savePath)
    {
        var proxyUrl = $"https://short.walftech.com/proxy.php?short={shortCode}";
        return await RetryAsync(
            () => DownloadWithResumeAsync(
                "script-remote-download",
                proxyUrl,
                savePath,
                request =>
                {
                    request.Headers.TryAddWithoutValidation("referer", $"https://short.walftech.com/?id={shortCode}");
                    request.Headers.TryAddWithoutValidation("accept-language", "zh-CN,zh;q=0.9");
                    request.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                }),
            "中转线路下载");
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, string stepName, int maxRetries = 3)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt >= maxRetries)
                    break;

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                AddLog($"⚠️ {stepName}失败（第{attempt}次）：{ex.GetType().Name}: {ex.Message}，{delay.TotalSeconds}s后重试...");
                await Task.Delay(delay);
            }
        }
        AddLog($"❌ {stepName}失败，已重试{maxRetries}次");
        throw new HttpRequestException($"{stepName}失败，请检查网络后重试", lastException);
    }

    private ExtractedLuaResult ExtractLuaFiles(string zipPath, string targetDir)
    {
        int count = 0;
        var appIds = new HashSet<int>();
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    var destPath = Path.Combine(targetDir, Path.GetFileName(entry.FullName));
                    entry.ExtractToFile(destPath, overwrite: true);
                    count++;
                    if (int.TryParse(Path.GetFileNameWithoutExtension(entry.Name), out var appId))
                        appIds.Add(appId);
                    AddLog($"📄 导入：{Path.GetFileName(entry.FullName)}");
                }
            }
        }
        File.Delete(zipPath);
        return new ExtractedLuaResult(count, appIds);
    }

    private void AddLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() => LogLines.Add(message));
    }

    [RelayCommand]
    private async Task RefreshKeyCacheAsync()
    {
        if (IsDownloading) return;
        try
        {
            HasStartedSearch = true;
            IsDownloading = true;
            _depotService.UseDataSource(_currentDownloadMode);
            AddLog($"🔄 正在更新密钥缓存（{CurrentDataSourceLabel}）...");
            var updateResult = await _depotService.UpdateKeyFilesAsync();

            if (updateResult.Success)
            {
                var depotDelta = updateResult.DepotKeysNewCount - updateResult.DepotKeysOldCount;
                var tokenDelta = updateResult.TokenKeysNewCount - updateResult.TokenKeysOldCount;

                AddLog($"✅ {CurrentDataSourceLabel}状态：");
                AddLog($"   depotkeys.json 已更新：{updateResult.DepotKeysOldCount} → {updateResult.DepotKeysNewCount} 条 ({(depotDelta >= 0 ? "+" : "")}{depotDelta})");
                AddLog($"   appaccesstokens.json 已更新：{updateResult.TokenKeysOldCount} → {updateResult.TokenKeysNewCount} 条 ({(tokenDelta >= 0 ? "+" : "")}{tokenDelta})");
                StatusMessage = $"{CurrentDataSourceLabel}密钥缓存已更新";
            }
            else
            {
                AddLog("❌ 更新失败，请检查网络");
                StatusMessage = "更新失败";
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ 更新失败：{ex.Message}");
            StatusMessage = "更新失败";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
        SearchResults.Clear();
        IsImportResultOpen = false;
        HasStartedSearch = false;
        StatusMessage = "日志已清除";
    }
}
