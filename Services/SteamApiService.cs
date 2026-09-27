using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class SteamApiService : ISteamApiService
{
	private static readonly IReadOnlyDictionary<int, string> BuiltInChineseAliases =
		new Dictionary<int, string>
		{
			[2379780] = "小丑牌",
			[2868840] = "杀戮尖塔2",
			[394360] = "钢铁雄心IV",
			[413150] = "星露谷物语",
			[812140] = "刺客信条：奥德赛",
			[1086940] = "博德之门3",
			[3722330] = "午夜轮班",
			[3751950] = "刺客信条：黑旗 记忆重置",
			[534380] = "消逝的光芒2：重装上阵版",
			[632360] = "雨中冒险2",
			[2358720] = "黑神话：悟空",
			[2246340] = "怪物猎人：荒野",
			[1091500] = "赛博朋克2077",
			[1245620] = "艾尔登法环",
			[252490] = "腐蚀",
			[367520] = "空洞骑士",
			[1145360] = "哈迪斯",
			[105600] = "泰拉瑞亚"
		};

	private readonly IHttpClientProvider _httpClientProvider;
	private readonly string _cacheDir;
	private readonly string _coversDir;
	private readonly string _cacheFilePath;
	private readonly string _legacyCacheFilePath;
	private readonly string _intermediateCacheFilePath;
	private readonly string _storageCacheFilePath;
	private ConcurrentDictionary<int, string> _nameCache = new();
	private ConcurrentDictionary<int, string> _legacyNameCache = new();
	private ConcurrentDictionary<int, long> _storageCache = new();
	private readonly SemaphoreSlim _metaGate = new(8, 8);
	private readonly SemaphoreSlim _coverGate = new(6, 6);
	private readonly SemaphoreSlim _communityGate = new(2, 2);
	private readonly object _saveCacheLock = new();
	private readonly object _cdnSwitchLock = new();
	private int _completedSinceSave;
	private DateTime _autoSwitchCooldownUntil = DateTime.MinValue;
	private readonly ISettingsService _settingsService;
	private int _selectedCdnIndex;
	private int _selectedCdnFailCount;
	public event Action<int>? CdnAutoSwitched;

	public int SelectedCdnIndex => _selectedCdnIndex;

	private static void ConfigureBasicHeaders(HttpClient client)
	{
		if (!client.DefaultRequestHeaders.UserAgent.Any())
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
		if (!client.DefaultRequestHeaders.Accept.Any())
			client.DefaultRequestHeaders.Add("Accept", "application/json");
	}

	private static void ConfigureCoverHeaders(HttpClient client)
	{
		if (!client.DefaultRequestHeaders.UserAgent.Any())
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
	}

	public SteamApiService(ISettingsService settingsService, IHttpClientProvider httpClientProvider)
	{
		_settingsService = settingsService;
		_httpClientProvider = httpClientProvider;
		_selectedCdnIndex = _settingsService.Load().SelectedCdnIndex;

		_cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
		_coversDir = Path.Combine(_cacheDir, "covers");
		_cacheFilePath = Path.Combine(_cacheDir, "gameinfo_schinese_alias_v2.json");
		_legacyCacheFilePath = Path.Combine(_cacheDir, "gameinfo.json");
		_intermediateCacheFilePath = Path.Combine(_cacheDir, "gameinfo_schinese_v1.json");
		_storageCacheFilePath = Path.Combine(_cacheDir, "game_storage_v1.json");

		Directory.CreateDirectory(_coversDir);
		LoadCache();
		SaveCache();
	}

	public void UpdateCdnPreference(int selectedIndex)
	{
		_selectedCdnIndex = selectedIndex;
		_selectedCdnFailCount = 0;
	}

	public async Task<List<(string Name, long LatencyMs, bool IsSuccess)>> TestCdnSpeedAsync(
		IProgress<(string Name, long LatencyMs, bool IsSuccess)>? progress = null)
	{
		const int testAppId = 730;

		var taskList = CdnEndpoint.Defaults.Select(async cdn =>
		{
			var url = string.Format(cdn.UrlTemplate, testAppId);
			var sw = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				var response = await _httpClientProvider.SendWithProxyRetryAsync(
					"steam-api-test",
					TimeSpan.FromSeconds(10),
					client => client.GetAsync(url),
					ConfigureBasicHeaders);
				sw.Stop();
				return (cdn.Name, sw.ElapsedMilliseconds, response.IsSuccessStatusCode);
			}
			catch
			{
				sw.Stop();
				return (cdn.Name, sw.ElapsedMilliseconds, false);
			}
		}).Select(async task =>
		{
			var result = await task;
			progress?.Report(result);
			return result;
		}).ToList();

		var completedTasks = new List<Task<(string Name, long LatencyMs, bool IsSuccess)>>(taskList);
		var results = new List<(string Name, long LatencyMs, bool IsSuccess)>();

		while (completedTasks.Count > 0)
		{
			var done = await Task.WhenAny(completedTasks);
			completedTasks.Remove(done);
			results.Add(await done);
		}

		return results;
	}

	private void LoadCache()
	{
		try
		{
			if (File.Exists(_cacheFilePath))
			{
				var json = File.ReadAllText(_cacheFilePath);
				_nameCache = JsonSerializer.Deserialize<ConcurrentDictionary<int, string>>(json) ?? new();
				foreach (var entry in _nameCache.ToArray())
				{
					if (ContainsChinese(entry.Value))
						_nameCache[entry.Key] = NormalizeChineseAlias(entry.Value);
				}
			}
			if (File.Exists(_legacyCacheFilePath))
			{
				var legacyJson = File.ReadAllText(_legacyCacheFilePath);
				_legacyNameCache = JsonSerializer.Deserialize<ConcurrentDictionary<int, string>>(legacyJson) ?? new();
			}
			if (File.Exists(_intermediateCacheFilePath))
			{
				var intermediateJson = File.ReadAllText(_intermediateCacheFilePath);
				var intermediateCache = JsonSerializer.Deserialize<ConcurrentDictionary<int, string>>(intermediateJson) ?? new();
				foreach (var entry in intermediateCache)
					_legacyNameCache[entry.Key] = entry.Value;
			}
			if (File.Exists(_storageCacheFilePath))
			{
				var storageJson = File.ReadAllText(_storageCacheFilePath);
				_storageCache = JsonSerializer.Deserialize<ConcurrentDictionary<int, long>>(storageJson) ?? new();
			}
		}
		catch
		{
			_nameCache = new();
			_legacyNameCache = new();
			_storageCache = new();
		}

		foreach (var entry in BuiltInChineseAliases)
		{
			if (!_nameCache.TryGetValue(entry.Key, out var existingName) ||
				!ContainsChinese(existingName))
				_nameCache[entry.Key] = entry.Value;
		}
	}

	private void SaveCache()
	{
		lock (_saveCacheLock)
		{
			try
			{
				WriteCacheAtomically(_cacheFilePath, JsonSerializer.Serialize(_nameCache));
				WriteCacheAtomically(_storageCacheFilePath, JsonSerializer.Serialize(_storageCache));
			}
			catch { }
		}
	}

	private static void WriteCacheAtomically(string path, string json)
	{
		var temporary = path + ".tmp_" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, json);
			File.Move(temporary, path, overwrite: true);
		}
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}

	private static bool IsJunkGameName(string? name) =>
		string.IsNullOrWhiteSpace(name) ||
		name.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
		name.Equals("Steam Community", StringComparison.OrdinalIgnoreCase) ||
		name.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
		name.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
		name.Contains("Attention Required", StringComparison.OrdinalIgnoreCase);

	private string GetLocalizedWideCoverPath(int appId) => Path.Combine(_coversDir, $"{appId}_wide_16x9_v3.jpg");
	private string GetWideCoverPath(int appId) => Path.Combine(_coversDir, $"{appId}_wide.jpg");
	private string GetLegacyCoverPath(int appId) => Path.Combine(_coversDir, $"{appId}.jpg");

	public void PopulateFromCache(List<GameInfo> games)
	{
		Directory.CreateDirectory(_coversDir);
		foreach (var game in games)
		{
			if (_nameCache.TryGetValue(game.AppId, out var name) && !name.StartsWith("AppID:") && !IsJunkGameName(name))
				game.GameName = name;
			else if (_legacyNameCache.TryGetValue(game.AppId, out var legacyName) && !legacyName.StartsWith("AppID:") && !IsJunkGameName(legacyName))
				game.GameName = legacyName;

			if (game.InstallSizeBytes <= 0 && _storageCache.TryGetValue(game.AppId, out var storageBytes))
				game.RequiredStorageBytes = storageBytes;

			var localizedWideCoverPath = GetLocalizedWideCoverPath(game.AppId);
			var wideCoverPath = GetWideCoverPath(game.AppId);
			var legacyCoverPath = GetLegacyCoverPath(game.AppId);
			if (IsValidCoverFile(localizedWideCoverPath))
				game.CoverImagePath = localizedWideCoverPath;
			else if (IsValidCoverFile(wideCoverPath))
				game.CoverImagePath = wideCoverPath;
			else
			{
				if (File.Exists(localizedWideCoverPath))
					DeleteInvalidCover(localizedWideCoverPath);
				if (File.Exists(wideCoverPath))
					DeleteInvalidCover(wideCoverPath);
				if (IsValidCoverFile(legacyCoverPath))
					game.CoverImagePath = legacyCoverPath;
			}
		}
	}

	public async Task RefreshGameInfoAsync(List<GameInfo> games, CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(_coversDir);
		_selectedCdnFailCount = 0;

		var snapshot = games.ToArray();
		var needInfo = await Task.Run(() => snapshot.Where(g =>
			IsJunkGameName(g.GameName) ||
			g.GameName == $"AppID: {g.AppId}" ||
			!_nameCache.TryGetValue(g.AppId, out var cachedName) ||
			!ContainsChinese(cachedName) ||
			!IsValidCoverFile(GetLocalizedWideCoverPath(g.AppId)) ||
			(g.InstallSizeBytes <= 0 && g.RequiredStorageBytes <= 0 && !_storageCache.ContainsKey(g.AppId)))
			.ToList(), cancellationToken).ConfigureAwait(false);

		if (needInfo.Count == 0) return;

		try
		{
			await Parallel.ForEachAsync(needInfo,
				new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
				async (game, ct) => await RefreshOneGameAsync(game, ct)).ConfigureAwait(false);
		}
		finally { SaveCache(); }
	}

	public async Task RefreshSingleGameAsync(GameInfo game, CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(_coversDir);
		var coverPath = GetLocalizedWideCoverPath(game.AppId);
		if (File.Exists(coverPath))
			File.Delete(coverPath);

		var oldName = game.GameName;
		game.CoverImagePath = string.Empty;

		await RefreshOneGameAsync(game, cancellationToken, forceNameRefresh: true);

		if (IsJunkGameName(game.GameName) || game.GameName == $"AppID: {game.AppId}")
		{
			if (!IsJunkGameName(oldName) && !oldName.StartsWith("AppID:"))
			{
				game.GameName = oldName;
				_nameCache[game.AppId] = oldName;
			}
		}

		SaveCache();
	}

	private async Task RefreshOneGameAsync(
		GameInfo game,
		CancellationToken cancellationToken,
		bool forceNameRefresh = false)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			game.IsLoading = true;

			var needName = forceNameRefresh ||
				IsJunkGameName(game.GameName) ||
				game.GameName == $"AppID: {game.AppId}" ||
				!_nameCache.TryGetValue(game.AppId, out var cachedName) ||
				!ContainsChinese(cachedName);
			var coverPath = GetLocalizedWideCoverPath(game.AppId);
			var needCover = !IsValidCoverFile(coverPath);
			var needStorage = game.InstallSizeBytes <= 0 && game.RequiredStorageBytes <= 0;
			if (needCover && File.Exists(coverPath))
				DeleteInvalidCover(coverPath, game);
			string? headerUrl = null;

			// 1. 优先通过 Store API 获取 header_image URL（同时获取名称）
			if (needCover || needName || needStorage)
			{
				await _metaGate.WaitAsync(cancellationToken);
				try
				{
				var storeResult = await TryStoreApi(game.AppId, "schinese", cancellationToken);
				if (needName && storeResult.Name != null)
				{
					var preferredName = await ResolvePreferredChineseNameAsync(
						game.AppId,
						storeResult.Name,
						cancellationToken);
					game.GameName = preferredName;
					_nameCache[game.AppId] = preferredName;
				}
				if (needCover)
					headerUrl = storeResult.HeaderUrl;
				if (needStorage && storeResult.RequiredStorageBytes > 0)
				{
					game.RequiredStorageBytes = storeResult.RequiredStorageBytes;
					_storageCache[game.AppId] = storeResult.RequiredStorageBytes;
				}

				if ((needName && storeResult.Name == null) ||
					(needStorage && game.RequiredStorageBytes <= 0))
				{
					storeResult = await TryStoreApi(game.AppId, "english", cancellationToken);
					if (storeResult.Name != null)
					{
						var preferredName = await ResolvePreferredChineseNameAsync(
							game.AppId,
							storeResult.Name,
							cancellationToken);
						game.GameName = preferredName;
						_nameCache[game.AppId] = preferredName;
					}
					if (needCover && headerUrl == null)
						headerUrl = storeResult.HeaderUrl;
					if (needStorage && storeResult.RequiredStorageBytes > 0)
					{
						game.RequiredStorageBytes = storeResult.RequiredStorageBytes;
						_storageCache[game.AppId] = storeResult.RequiredStorageBytes;
					}
				}

				if (needCover && headerUrl == null)
				{
					storeResult = await TryStoreApi(game.AppId, "english", cancellationToken);
					headerUrl = storeResult.HeaderUrl;
				}

				// 名称后备来源（SteamSpy / SteamCommunity）
				if (needName && (IsJunkGameName(game.GameName) || game.GameName.StartsWith("AppID:")))
				{
					var spyName = await TrySteamSpy(game.AppId, cancellationToken);
					if (spyName != null)
					{
						game.GameName = spyName;
						_nameCache[game.AppId] = spyName;
					}
					else
					{
						var communityName = await TrySteamCommunity(game.AppId, cancellationToken);
						if (communityName != null)
						{
							game.GameName = communityName;
							_nameCache[game.AppId] = communityName;
						}
					}
				}
				}
				finally { _metaGate.Release(); }
			}

			// 2. 游戏库卡片接近 16:9：优先 616x353，失败后再降级到 Header/CDN/旧缓存。
			if (needCover)
			{
				await _coverGate.WaitAsync(cancellationToken);
				try
				{
				var assetRoot = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{game.AppId}";
				var preferredWideUrls = new[]
				{
					$"{assetRoot}/capsule_616x353_schinese.jpg",
					$"{assetRoot}/capsule_616x353.jpg"
				};
				string? cover = null;
				foreach (var preferredWideUrl in preferredWideUrls)
				{
					cover = await DownloadCoverFromUrl(preferredWideUrl, game.AppId, cancellationToken);
					if (!string.IsNullOrEmpty(cover))
						break;
				}
				var endpoints = CdnEndpoint.Defaults;

				// 首选宽封面不可用时，使用用户指定的图片 CDN。
				if (string.IsNullOrEmpty(cover) &&
					_selectedCdnIndex > 0 && _selectedCdnIndex < endpoints.Count)
				{
					var selected = endpoints[_selectedCdnIndex];
					if (selected.IsImageEndpoint)
					{
						var cdnUrl = string.Format(selected.UrlTemplate, game.AppId);
						var (path, nodeFailed) = await DownloadCoverAttemptAsync(cdnUrl, game.AppId, cancellationToken);
						cover = path;
						if (nodeFailed)
						{
							CountCdnFailure();
						}
						else if (!string.IsNullOrEmpty(cover))
						{
							_selectedCdnFailCount = 0;
						}
					}
				}

				// Store API header_image 作为通用横图后备。
				if (string.IsNullOrEmpty(cover) && headerUrl != null)
					cover = await DownloadCoverFromUrl(headerUrl, game.AppId, cancellationToken);

				// 其余尺寸与 CDN 继续轮询。
				if (string.IsNullOrEmpty(cover))
				{
					cover = await FetchCoverAsync(game.AppId, cancellationToken);
					if (!string.IsNullOrEmpty(cover))
						game.CoverImagePath = cover;
				}
				if (string.IsNullOrEmpty(cover))
				{
					var wideCoverPath = GetWideCoverPath(game.AppId);
					var legacyCoverPath = GetLegacyCoverPath(game.AppId);
					if (IsValidCoverFile(wideCoverPath))
						cover = wideCoverPath;
					else if (IsValidCoverFile(legacyCoverPath))
						cover = legacyCoverPath;
				}

				if (!string.IsNullOrEmpty(cover))
					game.CoverImagePath = cover;
				}
				finally { _coverGate.Release(); }
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch { }
		finally
		{
			game.IsLoading = false;
			if (Interlocked.Increment(ref _completedSinceSave) % 50 == 0)
				SaveCache();
		}
	}

	private void CountCdnFailure()
	{
		lock (_cdnSwitchLock)
		{
			if (DateTime.UtcNow < _autoSwitchCooldownUntil) return;
			if (++_selectedCdnFailCount >= 3) AutoSwitchCdn();
		}
	}

	private void AutoSwitchCdn()
	{
		var endpoints = CdnEndpoint.Defaults;
		for (int i = _selectedCdnIndex + 1; i < endpoints.Count; i++)
		{
			if (endpoints[i].IsImageEndpoint)
			{
				_selectedCdnIndex = i;
				_selectedCdnFailCount = 0;
				var settings = _settingsService.Load();
				settings.SelectedCdnIndex = i;
				_settingsService.Save(settings);
				CdnAutoSwitched?.Invoke(i);
				return;
			}
		}
		// 没有更多可用节点，重置到 Store API
		_selectedCdnIndex = 0;
		_selectedCdnFailCount = 0;
		_autoSwitchCooldownUntil = DateTime.UtcNow.AddMinutes(10);
		var s = _settingsService.Load();
		s.SelectedCdnIndex = 0;
		_settingsService.Save(s);
		CdnAutoSwitched?.Invoke(0);
	}

	private static bool IsValidCoverFile(string path)
	{
		try
		{
			if (!File.Exists(path)) return false;
			var info = new FileInfo(path);
			if (info.Length <= 1000) return false;

			Span<byte> header = stackalloc byte[12];
			using var stream = File.OpenRead(path);
			var read = stream.Read(header);
			return IsValidImageHeader(header[..read]);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsValidImageBytes(byte[] bytes)
	{
		return bytes.Length > 1000 && IsValidImageHeader(bytes.AsSpan(0, Math.Min(bytes.Length, 12)));
	}

	private static bool IsValidImageHeader(ReadOnlySpan<byte> header)
	{
		if (header.Length < 4) return false;
		var isJpeg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
		var isPng = header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
		            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
		var isWebp = header.Length >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
		             header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50;
		return isJpeg || isPng || isWebp;
	}

	private static void DeleteInvalidCover(string path, GameInfo? game = null)
	{
		try { File.Delete(path); }
		catch { }
		if (game != null)
			game.CoverImagePath = string.Empty;
	}

	private async Task<(string? Name, string? HeaderUrl, long RequiredStorageBytes)> TryStoreApi(int appId, string lang, CancellationToken cancellationToken)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(TimeSpan.FromSeconds(5));
		try
		{
			var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={lang}";
			await using var stream = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-json",
				TimeSpan.FromSeconds(8),
				client => client.GetStreamAsync(url, cts.Token),
				ConfigureBasicHeaders);
			using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
			var root = doc.RootElement;

			if (TryGetAppNode(root, appId, out var app) &&
				app.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
				app.TryGetProperty("data", out var data))
			{
				var name = data.TryGetProperty("name", out var n) ? n.GetString() : null;
				var header = data.TryGetProperty("header_image", out var h) ? h.GetString() : null;
				return (IsJunkGameName(name) ? null : name, header, ExtractRequiredStorageBytes(data));
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
		catch { }
		return (null, null, 0);
	}

	private static bool TryGetAppNode(JsonElement root, int appId, out JsonElement node)
	{
		node = default;
		if (root.ValueKind != JsonValueKind.Object) return false;
		if (root.TryGetProperty(appId.ToString(), out var exact))
		{
			if (exact.ValueKind != JsonValueKind.Object) return false;
			node = exact;
			return true;
		}
		foreach (var property in root.EnumerateObject())
		{
			var candidate = property.Value;
			if (candidate.ValueKind != JsonValueKind.Object ||
				!candidate.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
				!candidate.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
				!data.TryGetProperty("steam_appid", out var id) || !id.TryGetInt32(out var actualId) || actualId != appId) continue;
			node = candidate;
			return true;
		}
		return false;
	}

	private static long ExtractRequiredStorageBytes(JsonElement data)
	{
		if (!data.TryGetProperty("pc_requirements", out var requirements)) return 0;
		var blocks = new List<string>();
		if (requirements.ValueKind == JsonValueKind.Object)
		{
			foreach (var propertyName in new[] { "minimum", "recommended" })
			{
				if (requirements.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String)
					blocks.Add(node.GetString() ?? string.Empty);
			}
		}
		else if (requirements.ValueKind == JsonValueKind.String)
		{
			blocks.Add(requirements.GetString() ?? string.Empty);
		}

		long largest = 0;
		foreach (var block in blocks)
		{
			var withLines = System.Text.RegularExpressions.Regex.Replace(
				block,
				"<br\\s*/?>|</li>|</p>",
				"\n",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			var plain = System.Net.WebUtility.HtmlDecode(
				System.Text.RegularExpressions.Regex.Replace(withLines, "<[^>]+>", string.Empty));
			foreach (var line in plain.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (!line.Contains("storage", StringComparison.OrdinalIgnoreCase) &&
					!line.Contains("hard drive", StringComparison.OrdinalIgnoreCase) &&
					!line.Contains("available space", StringComparison.OrdinalIgnoreCase) &&
					!line.Contains("存储", StringComparison.OrdinalIgnoreCase) &&
					!line.Contains("硬盘", StringComparison.OrdinalIgnoreCase) &&
					!line.Contains("可用空间", StringComparison.OrdinalIgnoreCase))
					continue;

				foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
					line,
					@"(\d+(?:[\.,]\d+)?)\s*(TB|TiB|GB|GiB|MB|MiB)",
					System.Text.RegularExpressions.RegexOptions.IgnoreCase))
				{
					if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'),
						System.Globalization.NumberStyles.Float,
						System.Globalization.CultureInfo.InvariantCulture,
						out var amount)) continue;
					var unit = match.Groups[2].Value.ToUpperInvariant();
					var multiplier = unit.StartsWith("T", StringComparison.Ordinal)
						? 1024d * 1024 * 1024 * 1024
						: unit.StartsWith("G", StringComparison.Ordinal)
							? 1024d * 1024 * 1024
							: 1024d * 1024;
					largest = Math.Max(largest, (long)(amount * multiplier));
				}
			}
		}
		return largest;
	}

	private async Task<string> ResolvePreferredChineseNameAsync(
		int appId,
		string steamName,
		CancellationToken cancellationToken)
	{
		if (ContainsChinese(steamName))
			return NormalizeChineseAlias(steamName);

		if (BuiltInChineseAliases.TryGetValue(appId, out var builtInName))
			return builtInName;

		if (_nameCache.TryGetValue(appId, out var cachedName) && ContainsChinese(cachedName))
			return NormalizeChineseAlias(cachedName);

		var wikidataName = await TryWikidataChineseNameAsync(appId, cancellationToken);
		return !string.IsNullOrWhiteSpace(wikidataName) ? wikidataName : steamName;
	}

	private async Task<string?> TryWikidataChineseNameAsync(int appId, CancellationToken cancellationToken)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(TimeSpan.FromSeconds(5));
		try
		{
			var query = $"SELECT ?item ?itemLabel WHERE {{ ?item wdt:P1733 \"{appId}\". " +
				"SERVICE wikibase:label { bd:serviceParam wikibase:language \"zh-cn,zh,en\". } } LIMIT 1";
			var url = $"https://query.wikidata.org/sparql?format=json&query={Uri.EscapeDataString(query)}";
			var json = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-wikidata-name",
				TimeSpan.FromSeconds(7),
				client => client.GetStringAsync(url, cts.Token),
				client =>
				{
					if (!client.DefaultRequestHeaders.UserAgent.Any())
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("MJJsteamtools/2.4.0");
					if (!client.DefaultRequestHeaders.Accept.Any())
						client.DefaultRequestHeaders.Add("Accept", "application/sparql-results+json");
				});

			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("results", out var results) ||
				!results.TryGetProperty("bindings", out var bindings) ||
				bindings.ValueKind != JsonValueKind.Array)
				return null;

			foreach (var binding in bindings.EnumerateArray())
			{
				if (!binding.TryGetProperty("itemLabel", out var itemLabel) ||
					!itemLabel.TryGetProperty("value", out var value))
					continue;
				var label = value.GetString();
				if (!string.IsNullOrWhiteSpace(label) && ContainsChinese(label))
					return NormalizeChineseAlias(label);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch { }
		return null;
	}

	private static bool ContainsChinese(string text) =>
		text.Any(character => character is >= '\u3400' and <= '\u9FFF');

	private static string NormalizeChineseAlias(string text)
	{
		try
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
		catch
		{
			return text
				.Replace(" (电子游戏)", string.Empty, StringComparison.Ordinal)
				.Replace("（电子游戏）", string.Empty, StringComparison.Ordinal)
				.Trim();
		}
	}

	private async Task<string?> TrySteamSpy(int appId, CancellationToken cancellationToken)
	{
		try
		{
			var url = $"https://steamspy.com/api.php?request=appdetails&appid={appId}";
			await using var stream = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-json",
				TimeSpan.FromSeconds(8),
				client => client.GetStreamAsync(url, cancellationToken),
				ConfigureBasicHeaders);
			using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
			if (doc.RootElement.TryGetProperty("name", out var name))
			{
				var value = name.GetString();
				return IsJunkGameName(value) ? null : value;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch { }
		return null;
	}

	private async Task<string?> TrySteamCommunity(int appId, CancellationToken cancellationToken)
	{
		await _communityGate.WaitAsync(cancellationToken);
		try
		{
			var url = $"https://steamcommunity.com/app/{appId}?l=english";
			var html = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-json",
				TimeSpan.FromSeconds(8),
				client => client.GetStringAsync(url, cancellationToken),
				ConfigureBasicHeaders);

			var tag = "<title>";
			var start = html.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
			if (start < 0) return null;
			start += tag.Length;

			var end = html.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
			if (end < 0) return null;

			var title = System.Net.WebUtility.HtmlDecode(html[start..end]).Trim();
			var sep = title.IndexOf(" :: ", StringComparison.OrdinalIgnoreCase);
			var name = sep >= 0 ? title[(sep + 4)..].Trim() : title;
			if (sep < 0)
			{
				sep = title.LastIndexOf(" - ", StringComparison.OrdinalIgnoreCase);
				if (sep > 0) name = title[..sep].Trim();
			}
			return IsJunkGameName(name) ? null : name;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch { }
		finally { _communityGate.Release(); }
		return null;
	}

	private async Task<string?> DownloadCoverFromUrl(string url, int appId, CancellationToken cancellationToken)
		=> (await DownloadCoverAttemptAsync(url, appId, cancellationToken)).Path;

	private async Task<(string? Path, bool NodeFailed)> DownloadCoverAttemptAsync(string url, int appId, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_coversDir);
		var localPath = GetLocalizedWideCoverPath(appId);
		if (IsValidCoverFile(localPath))
			return (localPath, false);
		if (File.Exists(localPath))
			DeleteInvalidCover(localPath);

		try
		{
			using var response = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-cover",
				TimeSpan.FromSeconds(15),
				client => client.GetAsync(url, cancellationToken),
				ConfigureCoverHeaders);
			if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
				return (null, false);
			if (!response.IsSuccessStatusCode) return (null, true);

			var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
			if (!IsValidImageBytes(bytes)) return (null, false);

			await File.WriteAllBytesAsync(localPath, NormalizeWideCoverBytes(bytes), cancellationToken);
			return (localPath, false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch { return (null, true); }
	}

	private async Task<string?> FetchCoverAsync(int appId, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_coversDir);
		var localPath = GetLocalizedWideCoverPath(appId);
		if (IsValidCoverFile(localPath))
			return localPath;
		if (File.Exists(localPath))
			DeleteInvalidCover(localPath);

		var endpoints = CdnEndpoint.Defaults.ToList();
		var ordered = new List<string>
		{
			$"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_616x353_schinese.jpg",
			$"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_616x353.jpg",
			$"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/capsule_616x353.jpg",
			$"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header_schinese.jpg",
			$"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
			$"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg"
		};
		if (_selectedCdnIndex >= 0 && _selectedCdnIndex < endpoints.Count)
		{
			var selected = endpoints[_selectedCdnIndex];
			if (selected.IsImageEndpoint)
				ordered.Add(string.Format(selected.UrlTemplate, appId));
		}
		for (int i = 0; i < endpoints.Count; i++)
		{
			if (i != _selectedCdnIndex && endpoints[i].IsImageEndpoint)
				ordered.Add(string.Format(endpoints[i].UrlTemplate, appId));
		}

		for (int attempt = 0; attempt < 2; attempt++)
		{
			foreach (var url in ordered.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				try
				{
					using var response = await _httpClientProvider.SendWithProxyRetryAsync(
						"steam-api-cover",
						TimeSpan.FromSeconds(15),
						client => client.GetAsync(url, cancellationToken),
						ConfigureCoverHeaders);
					if (!response.IsSuccessStatusCode) continue;

					var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
					if (!IsValidImageBytes(bytes)) continue;

					await File.WriteAllBytesAsync(localPath, NormalizeWideCoverBytes(bytes), cancellationToken);
					return localPath;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
				catch { }
			}
			if (attempt == 0)
				await Task.Delay(2000, cancellationToken);
		}
		return null;
	}

	private static byte[] NormalizeWideCoverBytes(byte[] bytes)
	{
		try
		{
			using var input = new MemoryStream(bytes, writable: false);
			var decoder = BitmapDecoder.Create(
				input,
				BitmapCreateOptions.PreservePixelFormat,
				BitmapCacheOption.OnLoad);
			var frame = decoder.Frames[0];
			if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
				return bytes;

			const double targetRatio = 16d / 9d;
			var sourceRatio = (double)frame.PixelWidth / frame.PixelHeight;
			var cropWidth = frame.PixelWidth;
			var cropHeight = frame.PixelHeight;
			if (sourceRatio > targetRatio)
				cropWidth = Math.Max(1, (int)Math.Round(frame.PixelHeight * targetRatio));
			else if (sourceRatio < targetRatio)
				cropHeight = Math.Max(1, (int)Math.Round(frame.PixelWidth / targetRatio));

			if (cropWidth == frame.PixelWidth && cropHeight == frame.PixelHeight)
				return bytes;

			var cropRect = new Int32Rect(
				(frame.PixelWidth - cropWidth) / 2,
				(frame.PixelHeight - cropHeight) / 2,
				cropWidth,
				cropHeight);
			var cropped = new CroppedBitmap(frame, cropRect);
			var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
			encoder.Frames.Add(BitmapFrame.Create(cropped));
			using var output = new MemoryStream();
			encoder.Save(output);
			return output.ToArray();
		}
		catch
		{
			return bytes;
		}
	}
}
