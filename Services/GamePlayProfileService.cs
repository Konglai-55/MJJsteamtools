using System.IO;
using System.Text.Json;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public sealed class GamePlayProfileService : IGamePlayProfileService
{
    private const string VanillaProfileId = "builtin-vanilla";
    private const string FullDlcProfileId = "builtin-full-dlc";
    private readonly object _syncRoot = new();
    private readonly string _storePath;
    private ProfileStore _store;

    public GamePlayProfileService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MJJsteamtools");
        Directory.CreateDirectory(folder);
        _storePath = Path.Combine(folder, "play-profiles.json");
        _store = LoadStore();
    }

    public IReadOnlyList<GamePlayProfile> GetProfiles(int appId)
    {
        lock (_syncRoot)
        {
            var activeId = GetActiveProfileIdCore(appId);
            var profiles = new List<GamePlayProfile>
            {
                new()
                {
                    Id = VanillaProfileId,
                    AppId = appId,
                    Name = "原版纯净",
                    Kind = GamePlayProfileKind.Vanilla,
                    BackupSaveBeforeApply = true
                },
                new()
                {
                    Id = FullDlcProfileId,
                    AppId = appId,
                    Name = "完整 DLC",
                    Kind = GamePlayProfileKind.FullDlc,
                    BackupSaveBeforeApply = true
                }
            };
            profiles.AddRange(_store.Profiles
                .Where(profile => profile.AppId == appId)
                .OrderByDescending(profile => profile.UpdatedAtUtc)
                .Select(Clone));
            foreach (var profile in profiles) profile.IsActive = profile.Id == activeId;
            return profiles;
        }
    }

    public string? GetActiveProfileId(int appId)
    {
        lock (_syncRoot) return GetActiveProfileIdCore(appId);
    }

    public void SetActiveProfile(int appId, string profileId)
    {
        lock (_syncRoot)
        {
            _store.ActiveProfileIds[appId.ToString()] = profileId;
            SaveStore();
        }
    }

    public GamePlayProfile SaveCustomProfile(GamePlayProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AppId <= 0) throw new ArgumentException("AppID 无效。", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("方案名称不能为空。", nameof(profile));

        lock (_syncRoot)
        {
            profile.Kind = GamePlayProfileKind.Custom;
            profile.Name = profile.Name.Trim();
            profile.LaunchArguments = profile.LaunchArguments.Trim();
            profile.EnabledDlcAppIds = profile.EnabledDlcAppIds.Distinct().Order().ToList();
            profile.UpdatedAtUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");

            var index = _store.Profiles.FindIndex(item => item.AppId == profile.AppId && item.Id == profile.Id);
            if (index >= 0) _store.Profiles[index] = Clone(profile);
            else _store.Profiles.Add(Clone(profile));
            SaveStore();
            return Clone(profile);
        }
    }

    public void DeleteProfile(int appId, string profileId)
    {
        if (profileId is VanillaProfileId or FullDlcProfileId) return;
        lock (_syncRoot)
        {
            _store.Profiles.RemoveAll(profile => profile.AppId == appId && profile.Id == profileId);
            if (_store.ActiveProfileIds.TryGetValue(appId.ToString(), out var activeId) && activeId == profileId)
                _store.ActiveProfileIds.Remove(appId.ToString());
            SaveStore();
        }
    }

    private string? GetActiveProfileIdCore(int appId) =>
        _store.ActiveProfileIds.TryGetValue(appId.ToString(), out var id) ? id : null;

    private ProfileStore LoadStore()
    {
        try
        {
            if (!File.Exists(_storePath)) return new ProfileStore();
            var json = File.ReadAllText(_storePath);
            return JsonSerializer.Deserialize<ProfileStore>(json) ?? new ProfileStore();
        }
        catch
        {
            return new ProfileStore();
        }
    }

    private void SaveStore()
    {
        var json = JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = _storePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _storePath, true);
    }

    private static GamePlayProfile Clone(GamePlayProfile profile) => new()
    {
        Id = profile.Id,
        AppId = profile.AppId,
        Name = profile.Name,
        Kind = profile.Kind,
        EnabledDlcAppIds = [.. profile.EnabledDlcAppIds],
        LaunchArguments = profile.LaunchArguments,
        BackupSaveBeforeApply = profile.BackupSaveBeforeApply,
        UpdatedAtUtc = profile.UpdatedAtUtc,
        IsActive = profile.IsActive,
        DlcSummary = profile.DlcSummary
    };

    private sealed class ProfileStore
    {
        public int Version { get; set; } = 1;
        public List<GamePlayProfile> Profiles { get; set; } = [];
        public Dictionary<string, string> ActiveProfileIds { get; set; } = [];
    }
}
