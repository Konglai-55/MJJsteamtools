using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public sealed class SaveAutoBackupService : ISaveAutoBackupService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan ExitWriteDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InstallPathRefreshInterval = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex InstallDirRegex = new(
        "\\\"installdir\\\"\\s+\\\"(?<value>[^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ISteamPathService _steamPathService;
    private readonly ISaveVaultService _saveVaultService;
    private readonly object _syncRoot = new();
    private readonly string _storePath;
    private readonly Dictionary<int, string> _installPaths = new();
    private readonly HashSet<int> _runningAppIds = [];
    private readonly HashSet<int> _pendingAppIds = [];
    private AutoBackupStore _store;
    private Func<IReadOnlyCollection<GameInfo>>? _gameProvider;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private DateTimeOffset _lastInstallPathRefresh = DateTimeOffset.MinValue;

    public SaveAutoBackupService(
        ISteamPathService steamPathService,
        ISaveVaultService saveVaultService)
    {
        _steamPathService = steamPathService;
        _saveVaultService = saveVaultService;
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DreamPixel studio",
            "MJJsteamtools");
        Directory.CreateDirectory(folder);
        _storePath = Path.Combine(folder, "auto-save-backup.json");
        _store = LoadStore();
    }

    public event Action<SaveAutoBackupEvent>? BackupCompleted;

    public bool IsEnabled(int appId)
    {
        lock (_syncRoot)
            return appId > 0 && !_store.DisabledAppIds.Contains(appId);
    }

    public DateTimeOffset? GetLastBackupTime(int appId)
    {
        lock (_syncRoot)
            return _store.LastBackupTimes.TryGetValue(appId.ToString(), out var value)
                ? value.ToLocalTime()
                : null;
    }

    public void SetEnabled(int appId, bool enabled)
    {
        if (appId <= 0) return;
        lock (_syncRoot)
        {
            var changed = enabled
                ? _store.DisabledAppIds.Remove(appId)
                : _store.DisabledAppIds.Add(appId);
            if (changed) SaveStoreCore();
        }
    }

    public void Start(Func<IReadOnlyCollection<GameInfo>> gameProvider)
    {
        ArgumentNullException.ThrowIfNull(gameProvider);
        lock (_syncRoot)
        {
            _gameProvider = gameProvider;
            if (_monitorTask is { IsCompleted: false }) return;
            _monitorCts?.Dispose();
            _monitorCts = new CancellationTokenSource();
            _runningAppIds.Clear();
            _pendingAppIds.Clear();
            _lastInstallPathRefresh = DateTimeOffset.MinValue;
            var cancellationToken = _monitorCts.Token;
            _monitorTask = Task.Run(() => MonitorLoopAsync(cancellationToken), cancellationToken);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellationSource;
        Task? monitorTask;
        lock (_syncRoot)
        {
            cancellationSource = _monitorCts;
            monitorTask = _monitorTask;
            _monitorCts = null;
            _monitorTask = null;
            _gameProvider = null;
            _runningAppIds.Clear();
            _pendingAppIds.Clear();
        }

        if (cancellationSource is null) return;
        cancellationSource.Cancel();
        if (monitorTask is null)
        {
            cancellationSource.Dispose();
            return;
        }

        _ = monitorTask.ContinueWith(
            _ => cancellationSource.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose() => Stop();

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await MonitorOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A protected process or a temporarily unavailable Steam library must not stop monitoring.
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task MonitorOnceAsync(CancellationToken cancellationToken)
    {
        Func<IReadOnlyCollection<GameInfo>>? provider;
        lock (_syncRoot) provider = _gameProvider;
        if (provider is null) return;

        IReadOnlyCollection<GameInfo> providedGames;
        try
        {
            providedGames = provider();
        }
        catch
        {
            return;
        }

        var games = providedGames
            .Where(game => game.AppId > 0)
            .DistinctBy(game => game.AppId)
            .ToDictionary(game => game.AppId);
        if (games.Count == 0) return;

        RefreshInstallPathsIfNeeded(games.Values, cancellationToken);
        var runningNow = DetectRunningGames();
        List<int> exitedAppIds;
        lock (_syncRoot)
        {
            exitedAppIds = _runningAppIds
                .Where(appId => !runningNow.Contains(appId))
                .ToList();
            _runningAppIds.Clear();
            _runningAppIds.UnionWith(runningNow);
        }

        foreach (var appId in exitedAppIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!games.TryGetValue(appId, out var game) || !TryMarkPending(appId)) continue;
            try
            {
                await BackupAfterExitAsync(game, cancellationToken);
            }
            finally
            {
                lock (_syncRoot) _pendingAppIds.Remove(appId);
            }
        }
    }

    private async Task BackupAfterExitAsync(GameInfo game, CancellationToken cancellationToken)
    {
        if (!IsEnabled(game.AppId)) return;
        await Task.Delay(ExitWriteDelay, cancellationToken);
        if (!IsEnabled(game.AppId) || IsGameRunning(game.AppId)) return;

        var stableSave = await WaitForStableSaveAsync(game, cancellationToken);
        if (stableSave is null) return;
        var (record, fingerprint) = stableSave.Value;
        lock (_syncRoot)
        {
            if (_store.LastFingerprints.TryGetValue(game.AppId.ToString(), out var previous)
                && string.Equals(previous, fingerprint, StringComparison.Ordinal))
                return;
        }

        var result = await _saveVaultService.CreateSnapshotAsync(
            record,
            "游戏退出后自动备份",
            cancellationToken);
        var occurredAt = DateTimeOffset.Now;
        if (result.Success)
        {
            lock (_syncRoot)
            {
                var key = game.AppId.ToString();
                _store.LastFingerprints[key] = fingerprint;
                _store.LastBackupTimes[key] = occurredAt.ToUniversalTime();
                SaveStoreCore();
            }
        }

        RaiseBackupCompleted(new SaveAutoBackupEvent(
            game.AppId,
            result.Success,
            result.Success ? $"自动备份完成 · {result.Message}" : $"自动备份失败 · {result.Message}",
            occurredAt));
    }

    private async Task<(SaveGameRecord Record, string Fingerprint)?> WaitForStableSaveAsync(
        GameInfo game,
        CancellationToken cancellationToken)
    {
        string? previousFingerprint = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEnabled(game.AppId) || IsGameRunning(game.AppId)) return null;
            var record = await _saveVaultService.ScanGameAsync(game, cancellationToken);
            if (!record.HasSaveData) return null;
            var fingerprint = await Task.Run(
                () => ComputeFingerprint(record, cancellationToken),
                cancellationToken);
            if (string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
                return (record, fingerprint);
            previousFingerprint = fingerprint;
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
        }

        RaiseBackupCompleted(new SaveAutoBackupEvent(
            game.AppId,
            false,
            "存档仍在写入，本次自动备份已暂缓；稍后可手动备份",
            DateTimeOffset.Now));
        return null;
    }

    private bool TryMarkPending(int appId)
    {
        lock (_syncRoot) return _pendingAppIds.Add(appId);
    }

    private void RefreshInstallPathsIfNeeded(
        IEnumerable<GameInfo> games,
        CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (DateTimeOffset.UtcNow - _lastInstallPathRefresh < InstallPathRefreshInterval)
                return;
        }

        var refreshed = new Dictionary<int, string>();
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = _steamPathService.FindAppManifest(game.AppId);
            var installPath = ResolveInstallPath(manifestPath);
            if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
                refreshed[game.AppId] = NormalizeDirectory(installPath);
        }

        lock (_syncRoot)
        {
            _installPaths.Clear();
            foreach (var pair in refreshed) _installPaths[pair.Key] = pair.Value;
            _lastInstallPathRefresh = DateTimeOffset.UtcNow;
        }
    }

    private HashSet<int> DetectRunningGames()
    {
        KeyValuePair<int, string>[] installs;
        lock (_syncRoot) installs = _installPaths.ToArray();
        var result = new HashSet<int>();
        if (installs.Length == 0) return result;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? executablePath;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(executablePath)) continue;
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(executablePath);
                }
                catch
                {
                    continue;
                }

                foreach (var install in installs)
                {
                    if (fullPath.StartsWith(install.Value, StringComparison.OrdinalIgnoreCase))
                        result.Add(install.Key);
                }
            }
        }
        return result;
    }

    private bool IsGameRunning(int appId)
    {
        string? installPath;
        lock (_syncRoot) _installPaths.TryGetValue(appId, out installPath);
        if (string.IsNullOrWhiteSpace(installPath)) return false;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var executablePath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executablePath)
                        && Path.GetFullPath(executablePath).StartsWith(installPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Protected system processes are unrelated to Steam game folders.
                }
            }
        }
        return false;
    }

    private static string ComputeFingerprint(
        SaveGameRecord record,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var location in record.Locations.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendHashValue(hash, NormalizeDirectory(location.Path));
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(
                    location.Path,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                    });
            }
            catch
            {
                continue;
            }

            foreach (var filePath in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var file = new FileInfo(filePath);
                    AppendHashValue(hash, Path.GetRelativePath(location.Path, file.FullName));
                    AppendHashValue(hash, file.Length.ToString());
                    AppendHashValue(hash, file.LastWriteTimeUtc.Ticks.ToString());
                }
                catch
                {
                    // A file may disappear while the game finishes writing; the next exit will retry it.
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHashValue(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }

    private static string? ResolveInstallPath(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return null;
        try
        {
            var match = InstallDirRegex.Match(File.ReadAllText(manifestPath));
            if (!match.Success) return null;
            var installDir = match.Groups["value"].Value.Replace("\\\\", "\\").Trim();
            var steamAppsPath = Path.GetDirectoryName(manifestPath);
            return string.IsNullOrWhiteSpace(steamAppsPath)
                ? null
                : Path.Combine(steamAppsPath, "common", installDir);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    private AutoBackupStore LoadStore()
    {
        try
        {
            if (!File.Exists(_storePath)) return new AutoBackupStore();
            var store = JsonSerializer.Deserialize<AutoBackupStore>(File.ReadAllText(_storePath))
                        ?? new AutoBackupStore();
            store.DisabledAppIds ??= [];
            store.LastFingerprints ??= [];
            store.LastBackupTimes ??= [];
            return store;
        }
        catch
        {
            return new AutoBackupStore();
        }
    }

    private void SaveStoreCore()
    {
        try
        {
            var json = JsonSerializer.Serialize(_store, JsonOptions);
            var tempPath = _storePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storePath, true);
        }
        catch
        {
            // Keep the in-memory preference active even if the settings file is temporarily locked.
        }
    }

    private void RaiseBackupCompleted(SaveAutoBackupEvent value)
    {
        var handlers = BackupCompleted;
        if (handlers is null) return;
        foreach (Action<SaveAutoBackupEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // UI listeners must not terminate background protection.
            }
        }
    }

    private sealed class AutoBackupStore
    {
        public int Version { get; set; } = 1;
        public HashSet<int> DisabledAppIds { get; set; } = [];
        public Dictionary<string, string> LastFingerprints { get; set; } = [];
        public Dictionary<string, DateTimeOffset> LastBackupTimes { get; set; } = [];
    }
}
