using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public sealed class FamilyLibraryService : IFamilyLibraryService
{
    private const ulong SteamIdBase = 76561197960265728UL;
    private const string ApiRoot = "https://api.steampowered.com";
    private readonly ISteamPathService _steamPathService;
    private readonly ISteamApiService _steamApiService;
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly object _sessionLock = new();
    private string _accessToken = string.Empty;

    public bool HasSession
    {
        get { lock (_sessionLock) return !string.IsNullOrWhiteSpace(_accessToken); }
    }

    public FamilyLibraryService(
        ISteamPathService steamPathService,
        ISteamApiService steamApiService,
        IHttpClientProvider httpClientProvider)
    {
        _steamPathService = steamPathService;
        _steamApiService = steamApiService;
        _httpClientProvider = httpClientProvider;
    }

    public void SetAccessToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return;
        lock (_sessionLock) _accessToken = accessToken.Trim();
    }

    public void ClearSession()
    {
        lock (_sessionLock) _accessToken = string.Empty;
    }

    public async Task<FamilyLibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var token = GetAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new FamilyLibrarySnapshot
            {
                RequiresConnection = true,
                DataScopeText = "连接 Steam 后读取真实家庭成员和共享库"
            };
        }

        var steamPath = _steamPathService.GetCustomPath();
        if (string.IsNullOrWhiteSpace(steamPath)) steamPath = _steamPathService.DetectSteamPath();
        var localAccounts = ParseLocalAccounts(steamPath ?? string.Empty);
        var activeAccount = localAccounts.FirstOrDefault(account => account.IsMostRecent) ?? localAccounts.FirstOrDefault();
        if (activeAccount == null)
        {
            return new FamilyLibrarySnapshot
            {
                RequiresConnection = false,
                DataScopeText = "未识别到当前 Steam 账号，请先启动并登录 Steam"
            };
        }

        try
        {
            var familyInfo = await GetFamilyInfoAsync(token, activeAccount.SteamId, cancellationToken);
            if (familyInfo == null || string.IsNullOrWhiteSpace(familyInfo.FamilyGroupId))
            {
                return new FamilyLibrarySnapshot
                {
                    DataScopeText = "当前 Steam 账号尚未加入 Steam 家庭"
                };
            }

            var profileMap = await GetMemberProfilesAsync(
                token,
                familyInfo.Members.Select(member => member.SteamId).ToList(),
                localAccounts,
                cancellationToken);
            var playtimeMap = await GetPlaytimeSummaryAsync(token, familyInfo.FamilyGroupId, cancellationToken);

            var members = familyInfo.Members.Select(member =>
            {
                profileMap.TryGetValue(member.SteamId, out var profile);
                profile ??= new MemberProfile(
                    member.SteamId,
                    $"SteamID64 {member.SteamId}",
                    FindLocalAvatar(localAccounts, member.SteamId));
                playtimeMap.TryGetValue(member.SteamId, out var secondsPlayed);
                var joinedText = member.TimeJoined > 0
                    ? $"加入于 {DateTimeOffset.FromUnixTimeSeconds(member.TimeJoined).LocalDateTime:yyyy年M月d日}"
                    : "Steam 家庭成员";
                return new FamilyMemberInfo
                {
                    SteamId = member.SteamId,
                    AccountId = SteamIdToAccountId(member.SteamId),
                    PersonaName = profile.PersonaName,
                    AvatarPath = profile.Avatar,
                    IsMostRecent = member.SteamId == activeAccount.SteamId,
                    RoleText = member.Role == 2 ? "儿童成员" : "成人成员",
                    DetailText = $"SteamID64: {member.SteamId}",
                    SecondaryText = member.SteamId == activeAccount.SteamId ? $"{joinedText} · 当前账号" : joinedText,
                    TertiaryText = secondsPlayed > 0 ? $"家庭游玩记录 {secondsPlayed / 3600d:0.#} 小时" : "暂无家庭游玩时长"
                };
            }).OrderByDescending(member => member.IsMostRecent).ThenBy(member => member.RoleText).ToList();

            var sharedApps = await GetSharedAppsAsync(token, familyInfo.FamilyGroupId, activeAccount.SteamId, cancellationToken);
            var installed = ReadInstalledGames();
            var metadataGames = sharedApps.Select(app => new GameInfo { AppId = app.AppId, GameName = app.Name }).ToList();
            _steamApiService.PopulateFromCache(metadataGames);
            var metadataById = metadataGames.ToDictionary(game => game.AppId);

            var memberNameById = members.ToDictionary(member => member.SteamId, member => member.PersonaName, StringComparer.Ordinal);
            var games = sharedApps.Select(app =>
            {
                installed.TryGetValue(app.AppId, out var installedGame);
                var ownerNames = app.OwnerSteamIds
                    .Select(id => memberNameById.GetValueOrDefault(id, $"SteamID64 {id}"))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var cached = metadataById[app.AppId];
                var cover = !string.IsNullOrWhiteSpace(cached.CoverImagePath)
                    ? cached.CoverImagePath
                    : BuildCapsuleUrl(app.AppId, app.CapsuleFilename);
                return new FamilyGameInfo
                {
                    AppId = app.AppId,
                    Name = string.IsNullOrWhiteSpace(app.Name) ? $"AppID: {app.AppId}" : app.Name,
                    CoverImage = cover,
                    InstallPath = installedGame?.InstallPath ?? string.Empty,
                    IsInstalled = installedGame != null,
                    OwnerDisplay = ownerNames.Count == 0 ? "Steam 家庭成员" : string.Join("、", ownerNames),
                    OwnerEvidence = "Steam 家庭共享接口返回的实际拥有者",
                    ShareStatus = "付费 · 支持家庭共享",
                    ShareStatusKind = "Available",
                    TotalPlaytimeMinutes = Math.Max(0, app.PlaytimeMinutes),
                    LastPlayedUnix = app.LastPlayedUnix,
                    Availability = "等待 Steam 返回实时占用状态"
                };
            }).OrderByDescending(game => game.IsInstalled)
              .ThenByDescending(game => game.LastPlayedUnix)
              .ThenBy(game => game.Name)
              .ToList();

            ApplyLocalRuntimeStatus(games);
            return new FamilyLibrarySnapshot
            {
                FamilyName = familyInfo.Name,
                Members = members,
                Games = games,
                DataScopeText = $"Steam 家庭“{familyInfo.Name}” · {members.Count} 位成员 · {games.Count} 款付费可共享游戏"
            };
        }
        catch (UnauthorizedAccessException)
        {
            ClearSession();
            return new FamilyLibrarySnapshot
            {
                RequiresConnection = true,
                DataScopeText = "Steam 登录已过期，请重新连接家庭库"
            };
        }
    }

    public IReadOnlySet<int> GetLocallyRunningAppIds(IEnumerable<FamilyGameInfo> games)
    {
        var installed = games
            .Where(game => game.IsInstalled && !string.IsNullOrWhiteSpace(game.InstallPath))
            .Select(game => (game.AppId, Path: NormalizePath(game.InstallPath)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .OrderByDescending(item => item.Path.Length)
            .ToList();
        var running = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var processPath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(processPath)) continue;
                var normalized = NormalizePath(processPath);
                var match = installed.FirstOrDefault(item =>
                    normalized.StartsWith(item.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (match.AppId > 0) running.Add(match.AppId);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return running;
    }

    private void ApplyLocalRuntimeStatus(List<FamilyGameInfo> games)
    {
        var running = GetLocallyRunningAppIds(games);
        foreach (var game in games)
        {
            game.IsRunningLocally = running.Contains(game.AppId);
            game.Availability = game.IsRunningLocally
                ? "本机正在运行"
                : "本机未占用 · 远程占用由 Steam 确认";
        }
    }

    private async Task<FamilyGroupInfo?> GetFamilyInfoAsync(string token, string steamId, CancellationToken cancellationToken)
    {
        var url = $"{ApiRoot}/IFamilyGroupsService/GetFamilyGroupForUser/v1/?access_token={Uri.EscapeDataString(token)}" +
                  $"&steamid={Uri.EscapeDataString(steamId)}&include_family_group_response=true";
        using var document = await GetJsonAsync(url, cancellationToken);
        if (!document.RootElement.TryGetProperty("response", out var response)) return null;
        if (response.TryGetProperty("is_not_member_of_any_group", out var notMember) && notMember.ValueKind == JsonValueKind.True)
            return null;
        var familyGroupId = GetString(response, "family_groupid");
        if (string.IsNullOrWhiteSpace(familyGroupId)) return null;

        JsonElement familyGroup;
        if (!response.TryGetProperty("family_group", out familyGroup))
        {
            var groupUrl = $"{ApiRoot}/IFamilyGroupsService/GetFamilyGroup/v1/?access_token={Uri.EscapeDataString(token)}" +
                           $"&family_groupid={Uri.EscapeDataString(familyGroupId)}";
            using var groupDocument = await GetJsonAsync(groupUrl, cancellationToken);
            if (!groupDocument.RootElement.TryGetProperty("response", out familyGroup)) return null;
            return ParseFamilyGroup(familyGroupId, familyGroup);
        }
        return ParseFamilyGroup(familyGroupId, familyGroup);
    }

    private static FamilyGroupInfo ParseFamilyGroup(string familyGroupId, JsonElement familyGroup)
    {
        var name = GetString(familyGroup, "name");
        var members = new List<FamilyMemberRecord>();
        if (familyGroup.TryGetProperty("members", out var membersNode) && membersNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in membersNode.EnumerateArray())
            {
                var steamId = GetString(member, "steamid");
                if (string.IsNullOrWhiteSpace(steamId)) continue;
                members.Add(new FamilyMemberRecord(
                    steamId,
                    GetInt32(member, "role"),
                    GetInt64(member, "time_joined")));
            }
        }
        return new FamilyGroupInfo(familyGroupId, string.IsNullOrWhiteSpace(name) ? "未命名家庭" : name, members);
    }

    private async Task<List<SharedAppRecord>> GetSharedAppsAsync(
        string token,
        string familyGroupId,
        string steamId,
        CancellationToken cancellationToken)
    {
        var url = $"{ApiRoot}/IFamilyGroupsService/GetSharedLibraryApps/v1/?access_token={Uri.EscapeDataString(token)}" +
                  $"&family_groupid={Uri.EscapeDataString(familyGroupId)}&steamid={Uri.EscapeDataString(steamId)}" +
                  "&include_own=true&include_excluded=false&include_free=false&include_non_games=false&language=schinese&max_apps=50000";
        using var document = await GetJsonAsync(url, cancellationToken);
        if (!document.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("apps", out var appsNode) || appsNode.ValueKind != JsonValueKind.Array)
            return [];

        var apps = new List<SharedAppRecord>();
        foreach (var app in appsNode.EnumerateArray())
        {
            var appId = GetInt32(app, "appid");
            var excludeReason = GetInt32(app, "exclude_reason");
            var appType = GetInt32(app, "app_type");
            if (appId <= 0 || excludeReason != 0 || appType != 1) continue;
            var owners = new List<string>();
            if (app.TryGetProperty("owner_steamids", out var ownerNode) && ownerNode.ValueKind == JsonValueKind.Array)
            {
                owners.AddRange(ownerNode.EnumerateArray()
                    .Select(owner => owner.ValueKind == JsonValueKind.String ? owner.GetString() ?? string.Empty : owner.ToString())
                    .Where(owner => !string.IsNullOrWhiteSpace(owner)));
            }
            apps.Add(new SharedAppRecord(
                appId,
                GetString(app, "name"),
                GetString(app, "capsule_filename"),
                owners,
                GetInt64(app, "rt_last_played"),
                GetInt32(app, "rt_playtime")));
        }
        return apps;
    }

    private async Task<Dictionary<string, MemberProfile>> GetMemberProfilesAsync(
        string token,
        IReadOnlyList<string> steamIds,
        IReadOnlyList<LocalAccountRecord> localAccounts,
        CancellationToken cancellationToken)
    {
        var profiles = localAccounts.ToDictionary(
            account => account.SteamId,
            account => new MemberProfile(account.SteamId, account.PersonaName, account.Avatar),
            StringComparer.Ordinal);
        if (steamIds.Count == 0) return profiles;

        try
        {
            var parameters = string.Join("&", steamIds.Select((steamId, index) =>
                $"steamids%5B{index}%5D={Uri.EscapeDataString(steamId)}"));
            var url = $"{ApiRoot}/IPlayerService/GetPlayerLinkDetails/v1/?access_token={Uri.EscapeDataString(token)}&{parameters}";
            using var document = await GetJsonAsync(url, cancellationToken);
            if (!document.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("accounts", out var accountsNode) || accountsNode.ValueKind != JsonValueKind.Array)
                return profiles;
            foreach (var account in accountsNode.EnumerateArray())
            {
                var steamId = GetString(account, "steamid");
                if (string.IsNullOrWhiteSpace(steamId)) continue;
                var publicData = account.TryGetProperty("public_data", out var publicNode) ? publicNode : account;
                var personaName = GetString(publicData, "persona_name");
                if (string.IsNullOrWhiteSpace(personaName)) personaName = GetString(publicData, "personaname");
                var avatarHash = GetString(publicData, "sha_digest_avatar");
                var avatar = string.IsNullOrWhiteSpace(avatarHash)
                    ? profiles.GetValueOrDefault(steamId)?.Avatar ?? string.Empty
                    : $"https://avatars.fastly.steamstatic.com/{avatarHash}_medium.jpg";
                if (!string.IsNullOrWhiteSpace(personaName) || !string.IsNullOrWhiteSpace(avatar))
                {
                    profiles[steamId] = new MemberProfile(
                        steamId,
                        string.IsNullOrWhiteSpace(personaName) ? $"SteamID64 {steamId}" : personaName,
                        avatar);
                }
            }
        }
        catch (UnauthorizedAccessException) { throw; }
        catch
        {
            // Local account names remain usable when the profile lookup is unavailable.
        }

        foreach (var steamId in steamIds.Where(id => !profiles.ContainsKey(id)))
        {
            var publicProfile = await GetCommunityProfileAsync(steamId, cancellationToken);
            profiles[steamId] = publicProfile ?? new MemberProfile(steamId, $"SteamID64 {steamId}", string.Empty);
        }
        return profiles;
    }

    private async Task<MemberProfile?> GetCommunityProfileAsync(string steamId, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId)}/?xml=1";
            return await _httpClientProvider.SendWithProxyRetryAsync(
                "steam-community-profiles",
                TimeSpan.FromSeconds(12),
                async client =>
                {
                    using var response = await client.GetAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode) return null;
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
                    var personaName = document.Root?.Element("steamID")?.Value?.Trim() ?? string.Empty;
                    var avatar = document.Root?.Element("avatarMedium")?.Value?.Trim() ?? string.Empty;
                    return string.IsNullOrWhiteSpace(personaName) && string.IsNullOrWhiteSpace(avatar)
                        ? null
                        : new MemberProfile(
                            steamId,
                            string.IsNullOrWhiteSpace(personaName) ? $"SteamID64 {steamId}" : personaName,
                            avatar);
                },
                client =>
                {
                    if (!client.DefaultRequestHeaders.UserAgent.Any())
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("MJJsteamtools/2.4.0");
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<Dictionary<string, long>> GetPlaytimeSummaryAsync(
        string token,
        string familyGroupId,
        CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            var url = $"{ApiRoot}/IFamilyGroupsService/GetPlaytimeSummary/v1/?access_token={Uri.EscapeDataString(token)}" +
                      $"&family_groupid={Uri.EscapeDataString(familyGroupId)}";
            using var document = await GetJsonAsync(url, cancellationToken);
            if (!document.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("entries", out var entriesNode) || entriesNode.ValueKind != JsonValueKind.Array)
                return totals;
            foreach (var entry in entriesNode.EnumerateArray())
            {
                var steamId = GetString(entry, "steamid");
                if (string.IsNullOrWhiteSpace(steamId)) continue;
                totals[steamId] = totals.GetValueOrDefault(steamId) + Math.Max(0, GetInt64(entry, "seconds_played"));
            }
        }
        catch (UnauthorizedAccessException) { throw; }
        catch
        {
        }
        return totals;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        return await _httpClientProvider.SendWithProxyRetryAsync(
            "steam-family-api",
            TimeSpan.FromSeconds(20),
            async client =>
            {
                using var response = await client.GetAsync(url, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new UnauthorizedAccessException("Steam 家庭会话已过期");
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            },
            client =>
            {
                if (!client.DefaultRequestHeaders.UserAgent.Any())
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("MJJsteamtools/2.4.0");
                if (!client.DefaultRequestHeaders.Accept.Any())
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            });
    }

    private Dictionary<int, InstalledGameRecord> ReadInstalledGames()
    {
        var result = new Dictionary<int, InstalledGameRecord>();
        foreach (var libraryPath in _steamPathService.GetAllLibraryPaths())
        {
            var steamAppsPath = Path.Combine(libraryPath, "steamapps");
            if (!Directory.Exists(steamAppsPath)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf"))
            {
                try
                {
                    var content = File.ReadAllText(manifestPath);
                    if (!int.TryParse(GetVdfValue(content, "appid"), out var appId) || appId <= 0) continue;
                    var installDir = GetVdfValue(content, "installdir");
                    result[appId] = new InstalledGameRecord(
                        appId,
                        string.IsNullOrWhiteSpace(installDir) ? string.Empty : Path.Combine(steamAppsPath, "common", installDir));
                }
                catch
                {
                }
            }
        }
        return result;
    }

    private static List<LocalAccountRecord> ParseLocalAccounts(string steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath)) return [];
        var path = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(path)) return [];
        try
        {
            var content = File.ReadAllText(path);
            var usersBlock = EnumerateBlocks(content).FirstOrDefault(block => block.Key.Equals("users", StringComparison.OrdinalIgnoreCase));
            var body = usersBlock?.Body ?? content;
            var result = new List<LocalAccountRecord>();
            foreach (var block in EnumerateBlocks(body))
            {
                if (!ulong.TryParse(block.Key, out var steamId) || steamId < SteamIdBase) continue;
                var personaName = GetVdfValue(block.Body, "PersonaName");
                var accountName = GetVdfValue(block.Body, "AccountName");
                var avatar = FindAvatarPath(steamPath, block.Key);
                result.Add(new LocalAccountRecord(
                    block.Key,
                    string.IsNullOrWhiteSpace(personaName) ? accountName : personaName,
                    avatar,
                    GetVdfValue(block.Body, "MostRecent") == "1"));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    private string GetAccessToken()
    {
        lock (_sessionLock) return _accessToken;
    }

    private static string BuildCapsuleUrl(int appId, string capsuleFilename)
    {
        if (Uri.TryCreate(capsuleFilename, UriKind.Absolute, out var absolute)) return absolute.ToString();
        var filename = string.IsNullOrWhiteSpace(capsuleFilename) ? "header_schinese.jpg" : capsuleFilename.TrimStart('/');
        return $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/{filename}";
    }

    private static string SteamIdToAccountId(string steamId)
    {
        return ulong.TryParse(steamId, out var value) && value >= SteamIdBase
            ? (value - SteamIdBase).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string FindLocalAvatar(IEnumerable<LocalAccountRecord> accounts, string steamId) =>
        accounts.FirstOrDefault(account => account.SteamId == steamId)?.Avatar ?? string.Empty;

    private static string FindAvatarPath(string steamPath, string steamId)
    {
        var avatarDir = Path.Combine(steamPath, "config", "avatarcache");
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            var path = Path.Combine(avatarDir, steamId + extension);
            if (File.Exists(path)) return path;
        }
        return string.Empty;
    }

    private static IEnumerable<VdfBlock> EnumerateBlocks(string content)
    {
        var position = 0;
        while (position < content.Length)
        {
            var keyMatch = Regex.Match(content[position..], "\\\"([^\\\"]+)\\\"\\s*\\{");
            if (!keyMatch.Success) yield break;
            var key = keyMatch.Groups[1].Value;
            var openBrace = position + keyMatch.Index + keyMatch.Length - 1;
            var closeBrace = FindMatchingBrace(content, openBrace);
            if (closeBrace < 0) yield break;
            yield return new VdfBlock(key, content[(openBrace + 1)..closeBrace]);
            position = closeBrace + 1;
        }
    }

    private static int FindMatchingBrace(string content, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < content.Length; i++)
        {
            if (content[i] == '{') depth++;
            else if (content[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static string GetVdfValue(string content, string key)
    {
        var match = Regex.Match(content, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]*)\\\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node)) return string.Empty;
        return node.ValueKind == JsonValueKind.String ? node.GetString() ?? string.Empty : node.ToString();
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node)) return 0;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value)) return value;
        return int.TryParse(node.ToString(), out value) ? value : 0;
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node)) return 0;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value)) return value;
        return long.TryParse(node.ToString(), out value) ? value : 0;
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    private sealed record FamilyGroupInfo(string FamilyGroupId, string Name, List<FamilyMemberRecord> Members);
    private sealed record FamilyMemberRecord(string SteamId, int Role, long TimeJoined);
    private sealed record MemberProfile(string SteamId, string PersonaName, string Avatar);
    private sealed record SharedAppRecord(int AppId, string Name, string CapsuleFilename, List<string> OwnerSteamIds, long LastPlayedUnix, int PlaytimeMinutes);
    private sealed record InstalledGameRecord(int AppId, string InstallPath);
    private sealed record LocalAccountRecord(string SteamId, string PersonaName, string Avatar, bool IsMostRecent);
    private sealed record VdfBlock(string Key, string Body);
}
