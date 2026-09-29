using System.Collections.ObjectModel;
using System.Collections.Concurrent;
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
using HtmlAgilityPack;
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
    private readonly ConcurrentDictionary<int, GameReviewSummary> _reviewCache = new();
    private readonly DispatcherTimer _modeRefreshTimer;
    private readonly Task _dailyRecommendationsTask;
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
    private bool _isLoadingRecommendations;

    [ObservableProperty]
    private bool _hasMoreRecommendations = true;

    [ObservableProperty]
    private bool _canAutoLoadRecommendations = true;

    [ObservableProperty]
    private string _recommendationLoadStatus = "继续向下探索更多游戏";

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private string _selectedTag = "全部标签";

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
    public ObservableCollection<FoundGame> FilteredSearchResults { get; } = new();
    public ObservableCollection<string> AvailableTags { get; } = new();
    public ObservableCollection<SearchSuggestion> SearchSuggestions { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<FoundGame> HotRecommendations { get; } = new();
    public ObservableCollection<FoundGame> MoreRecommendations { get; } = new();
    public ObservableCollection<FoundGame> FilteredHotRecommendations { get; } = new();
    public ObservableCollection<FoundGame> FilteredMoreRecommendations { get; } = new();
    public ObservableCollection<CarouselPageIndicator> HotRecommendationPages { get; } = new();
    // The ZIP source does not publish a catalog index. App-info cache entries
    // are not manifests, so they must not be presented as a library total.
    public string ManifestCatalogCountText => "清单总数 · 待清单源同步";
    public int FilteredSearchResultCount => FilteredSearchResults.Count;
    private int _hotRecommendationStartIndex;
    private bool _isCarouselSwitching;
    private int _recommendationSearchOffset;
    // Steam search rounds offsets down to its 25-result page boundary.
    private const int RecommendationPageSize = 25;
    private readonly Queue<int> _pendingRecommendationAppIds = new();
    private readonly Queue<FoundGame> _pendingRecommendationGames = new();
    private bool _hasMoreRecommendationSource = true;
    private bool _suspendRecommendationFiltering;

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
        HotRecommendations.CollectionChanged += (_, _) => { if (!_suspendRecommendationFiltering) ApplyTagFilter(); };
        MoreRecommendations.CollectionChanged += (_, _) => { if (!_suspendRecommendationFiltering) ApplyTagFilter(); };
        SearchResults.CollectionChanged += (_, _) => ApplyTagFilter();
        AvailableTags.Add("全部标签");

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
        _dailyRecommendationsTask = LoadDailyHotRecommendationsAsync();
    }

    partial void OnSelectedTagChanged(string value) => ApplyTagFilter();

    private void ApplyTagFilter()
    {
        var tags = SearchResults.Concat(HotRecommendations).Concat(MoreRecommendations)
            .SelectMany(game => game.Tags)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var desiredTags = new[] { "全部标签" }.Concat(tags).ToList();
        if (!desiredTags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase))
        {
            SelectedTag = "全部标签";
            return;
        }
        // Incremental edits preserve the selected ComboBox item while metadata
        // streams in; clearing the collection would bounce the selection.
        for (var index = AvailableTags.Count - 1; index >= 0; index--)
        {
            if (!desiredTags.Contains(AvailableTags[index]))
                AvailableTags.RemoveAt(index);
        }
        for (var index = 0; index < desiredTags.Count; index++)
        {
            if (index < AvailableTags.Count && AvailableTags[index] == desiredTags[index])
                continue;
            var oldIndex = AvailableTags.IndexOf(desiredTags[index]);
            if (oldIndex >= 0)
                AvailableTags.Move(oldIndex, index);
            else
                AvailableTags.Insert(index, desiredTags[index]);
        }

        var filtered = string.Equals(SelectedTag, "全部标签", StringComparison.OrdinalIgnoreCase)
            ? SearchResults
            : SearchResults.Where(game => game.Tags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase));
        FilteredSearchResults.Clear();
        foreach (var game in filtered)
            FilteredSearchResults.Add(game);
        var filteredHot = string.Equals(SelectedTag, "全部标签", StringComparison.OrdinalIgnoreCase)
            ? HotRecommendations
            : HotRecommendations.Where(game => game.Tags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase));
        FilteredHotRecommendations.Clear();
        foreach (var game in filteredHot)
            FilteredHotRecommendations.Add(game);
        var filteredMore = string.Equals(SelectedTag, "全部标签", StringComparison.OrdinalIgnoreCase)
            ? MoreRecommendations
            : MoreRecommendations.Where(game => game.Tags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase));
        FilteredMoreRecommendations.Clear();
        foreach (var game in filteredMore)
            FilteredMoreRecommendations.Add(game);
        NotifyHotRecommendationSlots();
        OnPropertyChanged(nameof(FilteredSearchResultCount));
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
        public bool IsGame { get; init; }
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> GameplayTags { get; init; } = Array.Empty<string>();
        public IReadOnlyList<GameFeature> Features { get; init; } = Array.Empty<GameFeature>();
        public GameReviewSummary Review { get; init; } = GameReviewSummary.Unavailable;

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

    public sealed record GameFeature(string Key, string Label, string IconPath, string CutoutPath);

    public sealed record GameReviewSummary(string Description, int Positive, int Negative)
    {
        public static GameReviewSummary Unavailable { get; } = new("暂无评测", 0, 0);
        public GameHotReview? HotReview { get; init; }
        public int Total => Positive + Negative;
        public bool HasReviews => Total > 0;
        public double Stars => HasReviews ? Math.Round(5d * Positive / Total, 1) : 0;
        public string StarsLabel => HasReviews ? $"{Stars:0.0}/5" : "";
        public IReadOnlyList<bool> StarSlots => Enumerable.Range(0, 5)
            .Select(index => index < (int)Math.Round(Stars, MidpointRounding.AwayFromZero)).ToArray();
        public bool HasHotReview => HotReview is not null;
        public string CountLabel => Total >= 10000
            ? $"{Total / 10000d:0.#}万条"
            : Total > 0 ? $"{Total:N0}条" : "";
        public string Tooltip => HasReviews
            ? $"Steam 玩家评测：{Positive:N0} 条好评、{Negative:N0} 条差评。星级按好评率折算，非 Steam 官方星级。"
            : "Steam 暂无可用评测";
    }

    public sealed record GameHotReview(string Text, int HelpfulVotes)
    {
        public string Excerpt => Text.Length > 94 ? Text[..94].TrimEnd() + "…" : Text;
        public string HelpfulLabel => HelpfulVotes > 0 ? $"{HelpfulVotes:N0} 人觉得有帮助" : "";
    }

    private sealed record StoreSearchItem(int AppId, string Name, string CoverUrl);
    private sealed record AliasSearchItem(int AppId, string ChineseName, string EnglishName);
    private sealed record AppDetails(string? Name, string? ReleaseDate, string? Price, string? HeaderImage,
        bool IsFree, IReadOnlyList<string> Tags, IReadOnlyList<string> GameplayTags,
        IReadOnlyList<GameFeature> Features, string? Type, bool IsSoftware);
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
        new(item.AppId, item.Name, item.EnglishName, GetFallbackCoverUrl(item.AppId), "正在获取发售时间", "正在获取价格")
        { IsGame = true };

    private static bool IsRecommendationGame(FoundGame game) =>
        game.IsGame && !game.IsFree && !game.Name.StartsWith("App ", StringComparison.Ordinal);

    private async Task LoadDailyHotRecommendationsAsync()
    {
        try
        {
            var hotAppIds = await GetDailyHotAppIdsAsync();
            var discoveryAppIds = await GetDiscoveryAppIdsAsync();
            var seedAppIds = HotRecommendationSeeds
                .Concat(MoreRecommendationSeeds)
                .Select(item => item.AppId);
            var mixedAppIds = new List<int>();
            for (var index = 0; index < Math.Max(discoveryAppIds.Count, hotAppIds.Count); index++)
            {
                if (index < discoveryAppIds.Count)
                    mixedAppIds.Add(discoveryAppIds[index]);
                if (index < hotAppIds.Count)
                    mixedAppIds.Add(hotAppIds[index]);
            }
            var pool = mixedAppIds
                .Concat(seedAppIds)
                .Distinct()
                .ToList();

            // Rotate the discovery pool by day so a stable Steam chart does not
            // produce the same first six cards forever.
            if (pool.Count > 1)
            {
                var offset = DateTime.Today.DayOfYear % pool.Count;
                pool = pool.Skip(offset).Concat(pool.Take(offset)).ToList();
            }

            if (pool.Count > 0)
            {
                var candidates = pool
                    .Take(24)
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
                    .Where(IsRecommendationGame)
                    .Take(14)
                    .ToList();

                if (validGames.Count >= 5)
                {
                    HotRecommendations.Clear();
                    foreach (var game in validGames.Take(6))
                        HotRecommendations.Add(game);
                    var more = validGames.Skip(6).Take(8).ToList();
                    if (more.Count < 4)
                    {
                        var fallback = MoreRecommendationSeeds
                            .Select(CreateRecommendation)
                            .Where(game => !validGames.Any(item => item.AppId == game.AppId))
                            .ToList();
                        more.AddRange(await EnrichRecommendationsAsync(fallback));
                    }
                    ResetRecommendations(MoreRecommendations, more.Where(IsRecommendationGame).Take(8));
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

    private async Task<List<int>> GetDiscoveryAppIdsAsync()
    {
        try
        {
            const string url = "https://store.steampowered.com/api/featuredcategories/?cc=cn&l=schinese";
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                "script-steam-featured-discovery",
                TimeSpan.FromSeconds(10),
                client => client.GetStringAsync(url),
                ConfigureSteamStoreHeaders);
            using var document = JsonDocument.Parse(json);
            var sections = new[] { "new_releases", "specials", "coming_soon", "top_sellers", "popular_new_releases" };
            var sectionIds = new List<List<int>>();
            foreach (var section in sections)
            {
                if (!document.RootElement.TryGetProperty(section, out var node) ||
                    !node.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                    continue;
                var ids = new List<int>();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.TryGetInt32(out var appId) && appId > 0)
                        ids.Add(appId);
                }
                sectionIds.Add(ids);
            }
            // Round-robin prevents the 30-item new-release section from crowding
            // the discovery pool before specials and best sellers are considered.
            var interleaved = new List<int>();
            var maximumLength = sectionIds.Count == 0 ? 0 : sectionIds.Max(ids => ids.Count);
            for (var index = 0; index < maximumLength; index++)
            {
                foreach (var ids in sectionIds)
                {
                    if (index < ids.Count)
                        interleaved.Add(ids[index]);
                }
            }
            return interleaved.Distinct().Take(40).ToList();
        }
        catch
        {
            return new List<int>();
        }
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

            ResetRecommendations(HotRecommendations, (await hotTask).Where(IsRecommendationGame));
            ResetRecommendations(MoreRecommendations, (await moreTask).Where(IsRecommendationGame));
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
            ResetRecommendations(MoreRecommendations, enriched.Where(IsRecommendationGame));
        }
        catch
        {
            // 更多推荐同样保留静态兜底内容。
        }
    }

    private async Task<List<FoundGame>> EnrichRecommendationsAsync(List<FoundGame> games, bool includeAliases = true)
    {
        using var limiter = new SemaphoreSlim(6);
        var metadataTasks = games.Select(async game =>
        {
            await limiter.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var detailsTask = GetAppDetailsAsync(game.AppId, "schinese", cts.Token);
                var reviewTask = GetReviewSummaryAsync(game.AppId, cts.Token);
                await Task.WhenAll(detailsTask, reviewTask);
                return (Game: game, Details: await detailsTask, Review: await reviewTask);
            }
            finally
            {
                limiter.Release();
            }
        });

        var metadata = await Task.WhenAll(metadataTasks);
        var aliasCandidates = metadata
            .Where(item => !ContainsChinese(item.Game.Name) &&
                           !ContainsChinese(item.Details?.Name ?? string.Empty))
            .Select(item => item.Game.AppId)
            .ToList();
        var chineseAliases = includeAliases
            ? await TryGetChineseAliasesByAppIdsAsync(aliasCandidates)
            : new Dictionary<int, string>();

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
                IsFree = details?.IsFree ?? game.IsFree,
                IsGame = details is null ? game.IsGame :
                    string.Equals(details.Type, "game", StringComparison.OrdinalIgnoreCase) && !details.IsSoftware,
                Tags = details?.Tags ?? game.Tags,
                GameplayTags = details?.GameplayTags ?? game.GameplayTags,
                Features = details?.Features ?? game.Features,
                Review = item.Review
            };
        }).ToList();
    }

    private async Task<GameReviewSummary> GetReviewSummaryAsync(int appId, CancellationToken ct)
    {
        if (_reviewCache.TryGetValue(appId, out var cached))
            return cached;

        try
        {
            var client = _httpClientProvider.GetClient(
                "script-steam-review-summary", TimeSpan.FromSeconds(6), ConfigureSteamStoreHeaders);
            var hotReviewTask = GetHotReviewAsync(client, appId, ct);
            var url = $"https://store.steampowered.com/appreviews/{appId}" +
                      "?json=1&filter=summary&language=all&purchase_type=all&num_per_page=0&l=schinese";
            var json = await client.GetStringAsync(url, ct);
            var summary = ParseReviewSummary(json) with { HotReview = await hotReviewTask };
            if (summary.HasReviews)
                _reviewCache.TryAdd(appId, summary);
            return summary;
        }
        catch
        {
            return GameReviewSummary.Unavailable;
        }
    }

    private static async Task<GameHotReview?> GetHotReviewAsync(HttpClient client, int appId, CancellationToken ct)
    {
        // Steam 的 all 筛选按有用程度排序；优先选简体中文，未找到再退到所有语言。
        foreach (var language in new[] { "schinese", "all" })
        {
            try
            {
                var url = $"https://store.steampowered.com/appreviews/{appId}" +
                          $"?json=1&filter=all&language={language}&purchase_type=all&num_per_page=1&l=schinese";
                var hotReview = ParseHotReview(await client.GetStringAsync(url, ct));
                if (hotReview is not null)
                    return hotReview;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                // 热评不可用时保留已获取的总体评分，不阻塞推荐卡片。
            }
        }
        return null;
    }

    private static GameHotReview? ParseHotReview(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.GetInt32() != 1 ||
            !root.TryGetProperty("reviews", out var reviews) || reviews.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var review in reviews.EnumerateArray())
        {
            if (!review.TryGetProperty("review", out var textElement) ||
                textElement.ValueKind != JsonValueKind.String)
                continue;
            var text = string.Join(" ", (textElement.GetString() ?? "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length < 8)
                continue;
            var votes = review.TryGetProperty("votes_up", out var votesElement) &&
                        votesElement.TryGetInt32(out var helpfulVotes) ? Math.Max(0, helpfulVotes) : 0;
            return new GameHotReview(text, votes);
        }
        return null;
    }

    private static GameReviewSummary ParseReviewSummary(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.GetInt32() != 1 ||
            !root.TryGetProperty("query_summary", out var summary))
            return GameReviewSummary.Unavailable;

        var positive = summary.TryGetProperty("total_positive", out var positiveElement) &&
                       positiveElement.TryGetInt32(out var positiveCount) ? positiveCount : 0;
        var negative = summary.TryGetProperty("total_negative", out var negativeElement) &&
                       negativeElement.TryGetInt32(out var negativeCount) ? negativeCount : 0;
        if (positive + negative <= 0)
            return GameReviewSummary.Unavailable;

        var description = summary.TryGetProperty("review_score_desc", out var descriptionElement)
            ? descriptionElement.GetString() : null;
        return new GameReviewSummary(
            string.IsNullOrWhiteSpace(description) ? "玩家评价" : description,
            positive, negative);
    }

    [RelayCommand]
    private async Task LoadMoreRecommendationsAsync(bool automatic)
    {
        if (HasStartedSearch || IsLoadingRecommendations || !HasMoreRecommendations ||
            (automatic && !CanAutoLoadRecommendations))
            return;

        // A failed automatic request must not be started again by every wheel
        // event. A deliberate button click is the only retry path.
        if (!automatic)
            CanAutoLoadRecommendations = true;
        IsLoadingRecommendations = true;
        RecommendationLoadStatus = "正在发现更多游戏…";
        try
        {
            await _dailyRecommendationsTask;
            if (_pendingRecommendationGames.Count == 0)
            {
                // Enrich only a small batch per scroll. Requesting details for
                // every item on a 25-result page made the footer wait too long.
                for (var batch = 0; batch < 3 && _pendingRecommendationGames.Count == 0; batch++)
                {
                    if (_pendingRecommendationAppIds.Count == 0)
                    {
                        if (!_hasMoreRecommendationSource)
                            break;
                        var (appIds, totalCount) = await GetRecommendationPageAsync(_recommendationSearchOffset);
                        _recommendationSearchOffset += RecommendationPageSize;
                        _hasMoreRecommendationSource = appIds.Count > 0 && _recommendationSearchOffset < totalCount;
                        var knownIds = HotRecommendations.Select(game => game.AppId)
                            .Concat(MoreRecommendations.Select(game => game.AppId))
                            .Concat(_pendingRecommendationGames.Select(game => game.AppId))
                            .Concat(_pendingRecommendationAppIds).ToHashSet();
                        foreach (var appId in appIds.Where(knownIds.Add))
                            _pendingRecommendationAppIds.Enqueue(appId);
                    }

                    var candidateIds = new List<int>();
                    while (candidateIds.Count < 8 && _pendingRecommendationAppIds.TryDequeue(out var appId))
                        candidateIds.Add(appId);
                    var candidates = candidateIds
                        .Select(appId => new FoundGame(appId, $"App {appId}", string.Empty,
                            GetFallbackCoverUrl(appId), "正在获取发售时间", "正在获取价格"))
                        .ToList();
                    var games = (await EnrichRecommendationsAsync(candidates, includeAliases: false))
                        .Where(IsRecommendationGame);
                    foreach (var game in games)
                        _pendingRecommendationGames.Enqueue(game);
                }
            }

            // Mount at most one row group at a time so each scroll step stays responsive.
            _suspendRecommendationFiltering = true;
            var added = 0;
            try
            {
                while (added < 12 && _pendingRecommendationGames.TryDequeue(out var game))
                {
                    MoreRecommendations.Add(game);
                    added++;
                }
            }
            finally
            {
                _suspendRecommendationFiltering = false;
            }
            if (added > 0)
                ApplyTagFilter();
            HasMoreRecommendations = _pendingRecommendationGames.Count > 0 ||
                                     _pendingRecommendationAppIds.Count > 0 || _hasMoreRecommendationSource;
            RecommendationLoadStatus = HasMoreRecommendations
                ? "继续向下探索更多游戏"
                : "已浏览完当前可用游戏";
        }
        catch (Exception exception)
        {
            CanAutoLoadRecommendations = false;
            RecommendationLoadStatus = exception switch
            {
                TaskCanceledException or TimeoutException => "连接超时，点击重试",
                HttpRequestException => "网络连接中断，点击重试",
                _ => "推荐加载失败，点击重试"
            };
            StatusMessage = $"推荐加载失败：{exception.Message}";
        }
        finally
        {
            IsLoadingRecommendations = false;
        }
    }

    private async Task<(List<int> AppIds, int TotalCount)> GetRecommendationPageAsync(int offset)
    {
        var url = $"https://store.steampowered.com/search/results/?query=&start={offset}&count={RecommendationPageSize}" +
                  "&sort_by=Reviews_DESC&category1=998&cc=cn&l=schinese&infinite=1";
        var json = await _httpClientProvider.SendWithProxyRetryAsync(
            "script-steam-game-feed",
            TimeSpan.FromSeconds(12),
            client => client.GetStringAsync(url),
            ConfigureSteamStoreHeaders);
        return ParseRecommendationPage(json);
    }

    private static (List<int> AppIds, int TotalCount) ParseRecommendationPage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.GetInt32() != 1 ||
            !root.TryGetProperty("results_html", out var htmlElement) ||
            !root.TryGetProperty("total_count", out var totalElement))
            throw new FormatException("Steam 推荐列表响应不完整");

        var html = new HtmlDocument();
        html.LoadHtml(htmlElement.GetString() ?? string.Empty);
        var appIds = (html.DocumentNode.SelectNodes("//*[@data-ds-appid]") ?? Enumerable.Empty<HtmlNode>())
            .Select(node => node.GetAttributeValue("data-ds-appid", string.Empty))
            .Select(value => int.TryParse(value, out var appId) ? appId : 0)
            .Where(appId => appId > 0)
            .Distinct()
            .ToList();
        return (appIds, totalElement.GetInt32());
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
        if (_isCarouselSwitching || FilteredHotRecommendations.Count < 2) return;
        _isCarouselSwitching = true;
        try
        {
            _hotRecommendationStartIndex = (_hotRecommendationStartIndex + 1) % FilteredHotRecommendations.Count;
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
        if (_isCarouselSwitching || FilteredHotRecommendations.Count < 2) return;
        _isCarouselSwitching = true;
        try
        {
            _hotRecommendationStartIndex =
                (_hotRecommendationStartIndex - 1 + FilteredHotRecommendations.Count) % FilteredHotRecommendations.Count;
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
        if (FilteredHotRecommendations.Count <= offset) return null;
        var normalizedStart = _hotRecommendationStartIndex % FilteredHotRecommendations.Count;
        return FilteredHotRecommendations[(normalizedStart + offset) % FilteredHotRecommendations.Count];
    }

    private void NotifyHotRecommendationSlots()
    {
        if (FilteredHotRecommendations.Count == 0)
            _hotRecommendationStartIndex = 0;
        else if (_hotRecommendationStartIndex >= FilteredHotRecommendations.Count)
            _hotRecommendationStartIndex %= FilteredHotRecommendations.Count;

        OnPropertyChanged(nameof(HotRecommendationSlot1));
        OnPropertyChanged(nameof(HotRecommendationSlot2));
        OnPropertyChanged(nameof(HotRecommendationSlot3));
        OnPropertyChanged(nameof(HotRecommendationSlot4));
        OnPropertyChanged(nameof(HotRecommendationSlot5));

        HotRecommendationPages.Clear();
        for (var index = 0; index < FilteredHotRecommendations.Count; index++)
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
                        chineseDetails?.Price ?? englishDetails?.Price ?? "暂无价格")
                    {
                        Tags = chineseDetails?.Tags ?? englishDetails?.Tags ?? Array.Empty<string>(),
                        GameplayTags = chineseDetails?.GameplayTags ?? englishDetails?.GameplayTags ?? Array.Empty<string>(),
                        Features = chineseDetails?.Features ?? englishDetails?.Features ?? Array.Empty<GameFeature>()
                    });
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

            var tags = ParseAppTags(data);
            var type = data.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            // Some software, such as Wallpaper Engine, reports type="game";
            // Steam's software-specific genre IDs are the reliable second gate.
            var isSoftware = HasSoftwareGenre(data);
            return new AppDetails(name, releaseDate, price, headerImage, isFree,
                tags.FilterTags, tags.GameplayTags, tags.Features, type, isSoftware);
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

    private sealed record ParsedGameTags(IReadOnlyList<string> FilterTags,
        IReadOnlyList<string> GameplayTags, IReadOnlyList<GameFeature> Features);

    private static ParsedGameTags ParseAppTags(JsonElement data)
    {
        var gameplay = new List<string>();
        if (data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
        {
            foreach (var genre in genres.EnumerateArray())
            {
                var id = ReadSteamTagId(genre);
                if (id is 23 or 37 or 70 || // Indie, Free to Play, Early Access are not gameplay genres.
                    !genre.TryGetProperty("description", out var description))
                    continue;
                var label = description.GetString();
                if (!string.IsNullOrWhiteSpace(label) &&
                    !gameplay.Contains(label, StringComparer.OrdinalIgnoreCase))
                    gameplay.Add(label);
                if (gameplay.Count == 4) break; // Keep the card scannable.
            }
        }

        var features = new Dictionary<string, GameFeature>(StringComparer.Ordinal);
        if (data.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            foreach (var category in categories.EnumerateArray())
            {
                var key = FeatureKeyForCategory(ReadSteamTagId(category));
                if (key is not null && !features.ContainsKey(key))
                    features.Add(key, CreateFeature(key));
            }
        }
        // One pictogram per capability, up to seven. Accessibility sub-options
        // and controller variants collapse instead of flooding the card.
        var visibleFeatures = features.Values
            .OrderBy(feature => FeaturePriority(feature.Key))
            .Take(7)
            .ToList();
        var filterTags = gameplay.Concat(visibleFeatures.Select(feature => feature.Label))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ParsedGameTags(filterTags, gameplay, visibleFeatures);
    }

    private static int ReadSteamTagId(JsonElement item) =>
        item.TryGetProperty("id", out var id) && int.TryParse(id.ToString(), out var value) ? value : -1;

    private static bool HasSoftwareGenre(JsonElement data) =>
        data.TryGetProperty("genres", out var genres) &&
        genres.ValueKind == JsonValueKind.Array &&
        genres.EnumerateArray().Any(genre => ReadSteamTagId(genre) is >= 51 and <= 60);

    private static string? FeatureKeyForCategory(int id) => id switch
    {
        18 or 28 or 55 or 56 or 57 or 58 => "controller",
        2 => "solo",
        1 or 27 => "multiplayer",
        9 or 38 or 48 => "coop",
        22 => "achievement",
        23 => "cloud",
        30 => "workshop",
        41 or 42 or 43 or 44 => "remote",
        62 => "family",
        29 => "cards",
        31 or 32 => "vr",
        64 or 65 or 66 or 67 or 68 or 69 or 70 or 74 or 78 or 79 => "accessibility",
        _ => null
    };

    private static int FeaturePriority(string key) => key switch
    {
        "controller" => 0, "solo" => 1, "multiplayer" => 2, "coop" => 3,
        "achievement" => 4, "cloud" => 5, "workshop" => 6,
        "remote" => 7, "family" => 8, "cards" => 9, "vr" => 10,
        _ => 11
    };

    // All capability pictograms are solid 24x24 silhouettes. The cutout is
    // rendered in the chip colour so controls remain legible at 16 px.
    private static GameFeature CreateFeature(string key) => key switch
    {
        "controller" => new(key, "支持手柄",
            "M7,7 H17 C20,7 22,9.5 22,13.5 C22,17.5 20,20 17,20 C15,20 14,18 12,18 C10,18 9,20 7,20 C4,20 2,17.5 2,13.5 C2,9.5 4,7 7,7 Z",
            "M7,10 H9 V12 H11 V14 H9 V16 H7 V14 H5 V12 H7 Z M16,10 A1.2,1.2 0 1 1 16,12.4 A1.2,1.2 0 1 1 16,10 Z M18,13 A1.2,1.2 0 1 1 18,15.4 A1.2,1.2 0 1 1 18,13 Z"),
        "solo" => new(key, "单人游玩",
            "M12,3 A4,4 0 1 1 12,11 A4,4 0 1 1 12,3 Z M4,21 C4,16 7,13 12,13 C17,13 20,16 20,21 Z", ""),
        "multiplayer" => new(key, "多人游玩",
            "M8,4 A3,3 0 1 1 8,10 A3,3 0 1 1 8,4 Z M16,4 A3,3 0 1 1 16,10 A3,3 0 1 1 16,4 Z M1,20 C1,15 3.5,12 8,12 C10,12 11,12.7 12,14 C13,12.7 14,12 16,12 C20.5,12 23,15 23,20 Z", ""),
        "coop" => new(key, "合作游玩",
            "M7,4 A3,3 0 1 1 7,10 A3,3 0 1 1 7,4 Z M17,4 A3,3 0 1 1 17,10 A3,3 0 1 1 17,4 Z M1,20 C1,15.5 3,13 7,13 H10 L12,15 L14,13 H17 C21,13 23,15.5 23,20 Z", ""),
        "achievement" => new(key, "Steam 成就",
            "M12,2 L14.9,8.1 L21.6,9 L16.8,13.7 L18,20.4 L12,17.2 L6,20.4 L7.2,13.7 L2.4,9 L9.1,8.1 Z", ""),
        "cloud" => new(key, "Steam 云存档",
            "M6,19 C3.2,19 2,17.1 2,14.8 C2,12.5 3.7,10.8 6,10.6 C6.8,6.9 9.1,5 12.3,5 C16,5 18.5,7.6 18.7,11 C21,11.3 22,12.9 22,15 C22,17.4 20.2,19 17.5,19 Z", ""),
        "workshop" => new(key, "创意工坊",
            "M20.5,3 C18,2 15.9,2.8 14.5,4.4 C13.1,6 12.9,8.1 13.7,10 L4,19.7 C3.2,20.5 2,20.5 1.3,19.8 C0.6,19.1 0.6,17.9 1.4,17.1 L11,7.5 C10.9,5.1 12.2,3.2 14.2,2.2 C16.1,1.2 18.4,1.4 20,2.5 L16.8,5.7 L18.3,7.2 Z", ""),
        "remote" => new(key, "远程同乐",
            "M2,4 H22 V17 H2 Z M9,19 H15 V21 H9 Z M6,21 H18 V22 H6 Z",
            "M10,8 L16,10.5 L10,13 Z"),
        "family" => new(key, "家庭共享",
            "M12,2 L22,10 H20 V21 H4 V10 H2 Z M12,11 A2.5,2.5 0 1 1 12,16 A2.5,2.5 0 1 1 12,11 Z", ""),
        "cards" => new(key, "Steam 集换式卡牌",
            "M4,3 H18 V17 H4 Z M7,6 H21 V20 H7 Z", "M9,9 H16 V11 H9 Z"),
        "vr" => new(key, "VR 支持",
            "M2,8 H22 V16 C22,18 20,19 18,19 H15 L12,16 L9,19 H6 C4,19 2,18 2,16 Z",
            "M5,11 H10 V14 H5 Z M14,11 H19 V14 H14 Z"),
        _ => new(key, "无障碍选项",
            "M12,2 A2,2 0 1 1 12,6 A2,2 0 1 1 12,2 Z M3,7 H21 V9 L15,10 V21 H12.5 V15 H11.5 V21 H9 V10 L3,9 Z", "")
    };

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
                details?.Price ?? "暂无价格")
            {
                Tags = details?.Tags ?? Array.Empty<string>(),
                GameplayTags = details?.GameplayTags ?? Array.Empty<string>(),
                Features = details?.Features ?? Array.Empty<GameFeature>()
            });
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
