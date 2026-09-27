using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public sealed class SteamAchievementService : ISteamAchievementService
{
    private readonly ISteamPathService _steamPathService;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public SteamAchievementService(ISteamPathService steamPathService)
    {
        _steamPathService = steamPathService;
    }

    public async Task<AchievementLoadResult> LoadAsync(
        int appId,
        string? installPath,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => LoadCore(appId, installPath, cancellationToken), cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<AchievementOperationResult> SetAchievementAsync(
        int appId,
        string? installPath,
        string apiName,
        bool unlocked,
        CancellationToken cancellationToken = default) =>
        SetAllAchievementsAsync(appId, installPath, new[] { apiName }, unlocked, cancellationToken);

    public async Task<AchievementOperationResult> SetAllAchievementsAsync(
        int appId,
        string? installPath,
        IReadOnlyCollection<string> apiNames,
        bool unlocked,
        CancellationToken cancellationToken = default)
    {
        if (apiNames.Count == 0)
            return new AchievementOperationResult(false, "没有可操作的成就");

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await RunHelperProcessAsync(appId, installPath, apiNames, unlocked, cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<AchievementOperationResult> SetAllAchievementsDirectAsync(
        int appId,
        string? installPath,
        IReadOnlyCollection<string> apiNames,
        bool unlocked,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => SetAllCore(appId, installPath, apiNames, unlocked, cancellationToken), cancellationToken);

    private AchievementLoadResult LoadCore(int appId, string? installPath, CancellationToken cancellationToken)
    {
        var achievements = LoadFromLocalCache(appId);
        cancellationToken.ThrowIfCancellationRequested();

        var apiPath = FindSteamApiPath(installPath);
        if (achievements.Count == 0)
            return new AchievementLoadResult(
                achievements,
                false,
                false,
                "未找到本地成就定义；请先通过 Steam 启动一次该游戏");

        var unlockedCount = achievements.Count(item => item.IsUnlocked);
        if (apiPath is null)
            return new AchievementLoadResult(
                achievements,
                false,
                HasAnyKnownState(achievements),
                $"已读取本地缓存 · {unlockedCount} / {achievements.Count} 已解锁 · 未找到写入接口");

        return new AchievementLoadResult(
            achievements,
            true,
            HasAnyKnownState(achievements),
            $"已读取本地缓存 · {unlockedCount} / {achievements.Count} 已解锁 · 修改时连接 Steam");
    }

    private static async Task<AchievementOperationResult> RunHelperProcessAsync(
        int appId,
        string? installPath,
        IReadOnlyCollection<string> apiNames,
        bool unlocked,
        CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(Path.GetTempPath(), $"mjj-achievement-request-{Guid.NewGuid():N}.json");
        var resultPath = Path.Combine(Path.GetTempPath(), $"mjj-achievement-result-{Guid.NewGuid():N}.json");
        Process? helper = null;
        try
        {
            var request = new AchievementHelperRequest(appId, installPath, apiNames.ToArray(), unlocked);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), cancellationToken);

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                return new AchievementOperationResult(false, "无法定位成就操作辅助程序");

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            startInfo.ArgumentList.Add("--achievement-helper");
            startInfo.ArgumentList.Add(requestPath);
            startInfo.ArgumentList.Add(resultPath);
            helper = Process.Start(startInfo);
            if (helper is null)
                return new AchievementOperationResult(false, "无法启动成就操作辅助程序");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await helper.WaitForExitAsync(timeout.Token);

            if (!File.Exists(resultPath))
                return new AchievementOperationResult(false, "辅助程序未返回结果，请确认 Steam 正在运行");
            var json = await File.ReadAllTextAsync(resultPath, cancellationToken);
            return JsonSerializer.Deserialize<AchievementOperationResult>(json)
                   ?? new AchievementOperationResult(false, "无法解析成就操作结果");
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (helper is { HasExited: false }) helper.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            return new AchievementOperationResult(false, "成就操作已取消或等待 Steam 响应超时");
        }
        catch (Exception ex)
        {
            return new AchievementOperationResult(false, $"启动成就辅助程序失败：{FriendlyError(ex)}");
        }
        finally
        {
            helper?.Dispose();
            try { File.Delete(requestPath); } catch { }
            try { File.Delete(resultPath); } catch { }
        }
    }

    private AchievementOperationResult SetAllCore(
        int appId,
        string? installPath,
        IReadOnlyCollection<string> apiNames,
        bool unlocked,
        CancellationToken cancellationToken)
    {
        if (IsGameRunning(installPath))
            return new AchievementOperationResult(false, "请先退出游戏，再修改成就状态");

        var apiPath = FindSteamApiPath(installPath);
        if (apiPath is null)
            return new AchievementOperationResult(false, "未找到该游戏的 steam_api64.dll，无法连接 Steam 成就接口");

        try
        {
            using var session = SteamNativeSession.TryCreate(apiPath, appId);
            if (session is null)
                return new AchievementOperationResult(false, "Steam 接口初始化失败；请确认 Steam 已登录且本工具与 Steam 权限一致");

            var first = apiNames.First();
            if (!session.WaitForStats(first, cancellationToken))
                return new AchievementOperationResult(false, "Steam 未返回该游戏的账号成就状态，已取消操作");

            BackupStateFile(appId);
            var changed = 0;
            foreach (var apiName in apiNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.SetAchievement(apiName, unlocked))
                    changed++;
            }

            if (changed == 0)
                return new AchievementOperationResult(false, "Steam 拒绝了此次修改，账号可能不拥有该游戏或成就由服务器控制");
            if (!session.StoreStats(cancellationToken))
                return new AchievementOperationResult(false, "成就已写入本地状态，但 Steam 云端提交失败");

            var verified = apiNames.Count(apiName =>
                session.TryGetAchievement(apiName, out var current, out _) && current == unlocked);
            if (verified != apiNames.Count)
                return new AchievementOperationResult(false, $"Steam 仅确认了 {verified} / {apiNames.Count} 项，请刷新后检查");

            return new AchievementOperationResult(
                true,
                unlocked ? $"已解锁 {verified} 项成就并同步到 Steam" : $"已重锁 {verified} 项成就并同步到 Steam");
        }
        catch (Exception ex)
        {
            return new AchievementOperationResult(false, $"成就操作失败：{FriendlyError(ex)}");
        }
    }

    private List<SteamAchievement> LoadFromLocalCache(int appId)
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath)) return new List<SteamAchievement>();
        var statsPath = Path.Combine(steamPath, "appcache", "stats");
        var schemaPath = Path.Combine(statsPath, $"UserGameStatsSchema_{appId}.bin");
        if (!File.Exists(schemaPath)) return new List<SteamAchievement>();

        try
        {
            var root = BinaryKeyValues.Read(schemaPath);
            var stats = root.Descendants().FirstOrDefault(node => node.Name == "stats");
            if (stats is null) return new List<SteamAchievement>();

            var result = new List<SteamAchievement>();
            foreach (var stat in stats.Children)
            {
                var bits = stat.Child("bits");
                if (bits is null || !string.Equals(stat.String("type"), "ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var bit in bits.Children)
                {
                    var apiName = bit.String("name");
                    var display = bit.Child("display");
                    if (string.IsNullOrWhiteSpace(apiName) || display is null || !int.TryParse(bit.Name, out var bitIndex))
                        continue;

                    var icon = display.String("icon");
                    var grayIcon = display.String("icon_gray");
                    result.Add(new SteamAchievement
                    {
                        ApiName = apiName,
                        Name = Localized(display.Child("name"), apiName),
                        Description = Localized(display.Child("desc"), display.String("desc")),
                        IsHidden = display.Int("hidden") != 0,
                        IconUrl = BuildIconUrl(appId, icon),
                        LockedIconUrl = BuildIconUrl(appId, grayIcon),
                        StatId = stat.Name,
                        BitIndex = bitIndex
                    });
                }
            }

            ApplyCachedState(appId, statsPath, result);
            return result
                .GroupBy(item => item.ApiName, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }
        catch
        {
            return new List<SteamAchievement>();
        }
    }

    private static void ApplyCachedState(int appId, string statsPath, IReadOnlyCollection<SteamAchievement> achievements)
    {
        var statePath = FindStateFile(appId, statsPath);
        if (statePath is null) return;
        try
        {
            var root = BinaryKeyValues.Read(statePath);
            var cache = root.Descendants().FirstOrDefault(node => node.Name == "cache");
            if (cache is null) return;
            foreach (var achievement in achievements)
            {
                var stat = cache.Child(achievement.StatId);
                if (stat is null || achievement.BitIndex is < 0 or > 31) continue;
                var mask = unchecked((uint)stat.Int("data"));
                achievement.IsUnlocked = (mask & (1u << achievement.BitIndex)) != 0;
                var times = stat.Child("AchievementTimes");
                achievement.UnlockTimeUnix = unchecked((uint)(times?.Int(achievement.BitIndex.ToString()) ?? 0));
            }
        }
        catch
        {
        }
    }

    private static string? FindStateFile(int appId, string statsPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var activeUser = key?.GetValue("ActiveUser") is int value ? unchecked((uint)value) : 0;
            if (activeUser != 0)
            {
                var activePath = Path.Combine(statsPath, $"UserGameStats_{activeUser}_{appId}.bin");
                if (File.Exists(activePath)) return activePath;
            }
        }
        catch
        {
        }

        return Directory.EnumerateFiles(statsPath, $"UserGameStats_*_{appId}.bin")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private void BackupStateFile(int appId)
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath)) return;
        var statsPath = Path.Combine(steamPath, "appcache", "stats");
        var statePath = FindStateFile(appId, statsPath);
        if (statePath is null) return;

        var backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "AchievementBackups", appId.ToString());
        Directory.CreateDirectory(backupDir);
        var backupName = $"{Path.GetFileNameWithoutExtension(statePath)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.bin";
        File.Copy(statePath, Path.Combine(backupDir, backupName), overwrite: false);
    }

    private static string? FindSteamApiPath(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)) return null;
        try
        {
            return Directory.EnumerateFiles(installPath, "steam_api64.dll", SearchOption.AllDirectories)
                .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGameRunning(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return false;
        var prefix = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    private static AchievementLoadResult CacheOnly(List<SteamAchievement> achievements, string message) =>
        new(achievements, false, HasAnyKnownState(achievements), message);

    private static bool HasAnyKnownState(IEnumerable<SteamAchievement> achievements) =>
        achievements.Any(item => item.IsUnlocked || item.UnlockTimeUnix > 0);

    private static string BuildIconUrl(int appId, string hash) =>
        string.IsNullOrWhiteSpace(hash)
            ? string.Empty
            : $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{hash}";

    private static string Localized(BinaryKeyValues.Node? node, string fallback)
    {
        if (node is null) return fallback ?? string.Empty;
        foreach (var language in new[] { "schinese", "simplified_chinese", "tchinese", "english" })
        {
            var value = node.String(language);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return !string.IsNullOrWhiteSpace(node.Text) ? node.Text : fallback ?? string.Empty;
    }

    private static string FriendlyError(Exception exception) =>
        exception.GetBaseException().Message.Replace("\r", " ").Replace("\n", " ");

    private sealed class SteamNativeSession : IDisposable
    {
        private readonly nint _library;
        private readonly nint _stats;
        private readonly string? _oldAppId;
        private readonly string? _oldGameId;
        private readonly ShutdownDelegate _shutdown;
        private readonly RunCallbacksDelegate _runCallbacks;
        private readonly RequestCurrentStatsDelegate _requestCurrentStats;
        private readonly GetNumAchievementsDelegate _getNumAchievements;
        private readonly GetAchievementNameDelegate _getAchievementName;
        private readonly GetDisplayAttributeDelegate _getDisplayAttribute;
        private readonly GetAchievementDelegate _getAchievement;
        private readonly GetAchievementAndUnlockTimeDelegate _getAchievementAndUnlockTime;
        private readonly ChangeAchievementDelegate _setAchievement;
        private readonly ChangeAchievementDelegate _clearAchievement;
        private readonly StoreStatsDelegate _storeStats;
        private bool _disposed;

        private SteamNativeSession(nint library, int appId)
        {
            _library = library;
            _oldAppId = Environment.GetEnvironmentVariable("SteamAppId");
            _oldGameId = Environment.GetEnvironmentVariable("SteamGameId");
            Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", appId.ToString());

            var init = TryExport<BoolNoArgsDelegate>("SteamAPI_Init")
                       ?? Export<BoolNoArgsDelegate>("SteamAPI_InitSafe");
            _shutdown = Export<ShutdownDelegate>("SteamAPI_Shutdown");
            _runCallbacks = Export<RunCallbacksDelegate>("SteamAPI_RunCallbacks");
            if (!init()) throw new InvalidOperationException("SteamAPI_Init 返回失败");

            var getUser = Export<GetHSteamUserDelegate>("SteamAPI_GetHSteamUser");
            var findInterface = Export<FindUserInterfaceDelegate>("SteamInternal_FindOrCreateUserInterface");
            _stats = findInterface(getUser(), "STEAMUSERSTATS_INTERFACE_VERSION012");
            if (_stats == 0) throw new InvalidOperationException("无法获取 ISteamUserStats 接口");

            _requestCurrentStats = Export<RequestCurrentStatsDelegate>("SteamAPI_ISteamUserStats_RequestCurrentStats");
            _getNumAchievements = Export<GetNumAchievementsDelegate>("SteamAPI_ISteamUserStats_GetNumAchievements");
            _getAchievementName = Export<GetAchievementNameDelegate>("SteamAPI_ISteamUserStats_GetAchievementName");
            _getDisplayAttribute = Export<GetDisplayAttributeDelegate>("SteamAPI_ISteamUserStats_GetAchievementDisplayAttribute");
            _getAchievement = Export<GetAchievementDelegate>("SteamAPI_ISteamUserStats_GetAchievement");
            _getAchievementAndUnlockTime = Export<GetAchievementAndUnlockTimeDelegate>("SteamAPI_ISteamUserStats_GetAchievementAndUnlockTime");
            _setAchievement = Export<ChangeAchievementDelegate>("SteamAPI_ISteamUserStats_SetAchievement");
            _clearAchievement = Export<ChangeAchievementDelegate>("SteamAPI_ISteamUserStats_ClearAchievement");
            _storeStats = Export<StoreStatsDelegate>("SteamAPI_ISteamUserStats_StoreStats");
        }

        public static SteamNativeSession? TryCreate(string libraryPath, int appId)
        {
            nint library = 0;
            var oldAppId = Environment.GetEnvironmentVariable("SteamAppId");
            var oldGameId = Environment.GetEnvironmentVariable("SteamGameId");
            try
            {
                library = NativeLibrary.Load(libraryPath);
                return new SteamNativeSession(library, appId);
            }
            catch
            {
                if (library != 0)
                {
                    try
                    {
                        var address = NativeLibrary.GetExport(library, "SteamAPI_Shutdown");
                        Marshal.GetDelegateForFunctionPointer<ShutdownDelegate>(address)();
                    }
                    catch
                    {
                    }
                    NativeLibrary.Free(library);
                }
                Environment.SetEnvironmentVariable("SteamAppId", oldAppId);
                Environment.SetEnvironmentVariable("SteamGameId", oldGameId);
                return null;
            }
        }

        public void WarmUpStats(CancellationToken cancellationToken)
        {
            if (!_requestCurrentStats(_stats)) return;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _runCallbacks();
                Thread.Sleep(50);
            }
        }

        public bool WaitForStats(string probeApiName, CancellationToken cancellationToken)
        {
            if (!_requestCurrentStats(_stats)) return false;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _runCallbacks();
                if (_getAchievement(_stats, probeApiName, out _)) return true;
                Thread.Sleep(50);
            }
            return false;
        }

        public List<string> GetAchievementNames()
        {
            var count = _getNumAchievements(_stats);
            if (count > 10_000) return new List<string>();
            var result = new List<string>((int)count);
            for (uint index = 0; index < count; index++)
            {
                var name = Utf8(_getAchievementName(_stats, index));
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
            return result;
        }

        public SteamAchievement CreateAchievement(string apiName, int appId)
        {
            var name = Display(apiName, "name");
            var description = Display(apiName, "desc");
            return new SteamAchievement
            {
                ApiName = apiName,
                Name = string.IsNullOrWhiteSpace(name) ? apiName : name,
                Description = description,
                IsHidden = Display(apiName, "hidden") == "1"
            };
        }

        public bool TryGetAchievement(string apiName, out bool unlocked, out uint unlockTime)
        {
            unlocked = false;
            unlockTime = 0;
            if (_getAchievementAndUnlockTime(_stats, apiName, out var value, out unlockTime))
            {
                unlocked = value != 0;
                return true;
            }
            if (_getAchievement(_stats, apiName, out value))
            {
                unlocked = value != 0;
                return true;
            }
            return false;
        }

        public bool SetAchievement(string apiName, bool unlocked) =>
            unlocked ? _setAchievement(_stats, apiName) : _clearAchievement(_stats, apiName);

        public bool StoreStats(CancellationToken cancellationToken)
        {
            if (!_storeStats(_stats)) return false;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _runCallbacks();
                Thread.Sleep(50);
            }
            return true;
        }

        private string Display(string apiName, string key) => Utf8(_getDisplayAttribute(_stats, apiName, key));

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

        private T? TryExport<T>(string name) where T : Delegate =>
            NativeLibrary.TryGetExport(_library, name, out var address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : null;

        private static string Utf8(nint pointer) =>
            pointer == 0 ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _shutdown(); } catch { }
            Environment.SetEnvironmentVariable("SteamAppId", _oldAppId);
            Environment.SetEnvironmentVariable("SteamGameId", _oldGameId);
            NativeLibrary.Free(_library);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool BoolNoArgsDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ShutdownDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RunCallbacksDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetHSteamUserDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate nint FindUserInterfaceDelegate(int user, string version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool RequestCurrentStatsDelegate(nint self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetNumAchievementsDelegate(nint self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetAchievementNameDelegate(nint self, uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate nint GetDisplayAttributeDelegate(nint self, string name, string key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool GetAchievementDelegate(nint self, string name, out byte achieved);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool GetAchievementAndUnlockTimeDelegate(nint self, string name, out byte achieved, out uint unlockTime);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool ChangeAchievementDelegate(nint self, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool StoreStatsDelegate(nint self);
    }

    private static class BinaryKeyValues
    {
        public sealed class Node
        {
            public required string Name { get; init; }
            public string Text { get; init; } = string.Empty;
            public int Number { get; init; }
            public List<Node> Children { get; } = new();

            public Node? Child(string name) =>
                Children.FirstOrDefault(child => child.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            public string String(string name) => Child(name)?.Text ?? string.Empty;
            public int Int(string name) => Child(name)?.Number ?? 0;
            public IEnumerable<Node> Descendants()
            {
                foreach (var child in Children)
                {
                    yield return child;
                    foreach (var descendant in child.Descendants()) yield return descendant;
                }
            }
        }

        public static Node Read(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var root = new Node { Name = "root" };
            ReadObject(reader, root);
            return root;
        }

        private static void ReadObject(BinaryReader reader, Node parent)
        {
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                var type = reader.ReadByte();
                if (type == 8) return;
                var name = ReadZeroString(reader);
                var node = new Node { Name = name };
                switch (type)
                {
                    case 0:
                        ReadObject(reader, node);
                        break;
                    case 1:
                        node = new Node { Name = name, Text = ReadZeroString(reader) };
                        break;
                    case 2:
                        node = new Node { Name = name, Number = reader.ReadInt32() };
                        break;
                    case 3:
                        reader.ReadSingle();
                        break;
                    case 4:
                        reader.ReadUInt64();
                        break;
                    case 5:
                        var length = reader.ReadUInt16();
                        reader.ReadBytes(length * 2);
                        break;
                    case 6:
                        reader.ReadUInt32();
                        break;
                    case 7:
                        reader.ReadUInt64();
                        break;
                    default:
                        throw new InvalidDataException($"不支持的 Binary VDF 类型：{type}");
                }
                parent.Children.Add(node);
            }
        }

        private static string ReadZeroString(BinaryReader reader)
        {
            using var buffer = new MemoryStream();
            byte value;
            while ((value = reader.ReadByte()) != 0) buffer.WriteByte(value);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
