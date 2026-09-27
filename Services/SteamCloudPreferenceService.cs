using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace SteamLuaManager.Services;

public interface ISteamCloudPreferenceService
{
    SteamCloudPreferenceState? GetCurrentAccountState(int appId);

    Task<SteamCloudPreferenceResult> SetCurrentAccountStateAsync(
        IEnumerable<int> appIds,
        bool enabled,
        CancellationToken cancellationToken = default);
}

public sealed record SteamCloudPreferenceState(
    bool IsEnabled,
    bool HasAppOverride,
    bool IsGlobalEnabled,
    bool? AppOverrideEnabled);

public sealed record SteamCloudPreferenceResult(
    bool Success,
    string Message,
    int ChangedCount = 0,
    string? BackupPath = null);

/// <summary>
/// Manages Steam's per-account, per-app Cloud preference without changing the
/// global Cloud switch. Steam keeps sharedconfig.vdf in memory, so writes are
/// only performed after a clean client shutdown.
/// </summary>
public sealed class SteamCloudPreferenceService : ISteamCloudPreferenceService
{
    private const string RootKey = "UserRoamingConfigStore";
    private static readonly string[] SteamObjectPath = [RootKey, "Software", "Valve", "Steam"];
    private readonly ISteamPathService _steamPathService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SteamCloudPreferenceService(ISteamPathService steamPathService)
    {
        _steamPathService = steamPathService;
    }

    public SteamCloudPreferenceState? GetCurrentAccountState(int appId)
    {
        if (appId <= 0)
            return null;

        try
        {
            var steamPath = GetSteamPath();
            var sharedConfigPath = ResolveCurrentSharedConfigPath(steamPath);
            if (!File.Exists(sharedConfigPath))
                return new SteamCloudPreferenceState(true, false, true, null);

            var text = ReadUtf8File(sharedConfigPath).Text;
            return ReadCloudState(text, appId);
        }
        catch
        {
            return null;
        }
    }

    public async Task<SteamCloudPreferenceResult> SetCurrentAccountStateAsync(
        IEnumerable<int> appIds,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _writeLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new SteamCloudPreferenceResult(false, "操作已取消，未修改 Steam 云设置。");
        }

        try
        {
            return await SetCurrentAccountStateCoreAsync(appIds, enabled, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<SteamCloudPreferenceResult> SetCurrentAccountStateCoreAsync(
        IEnumerable<int> appIds,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var targets = appIds
            .Where(appId => appId > 0)
            .Distinct()
            .OrderBy(appId => appId)
            .ToArray();
        if (targets.Length == 0)
            return new SteamCloudPreferenceResult(false, "没有可处理的游戏 AppID。");

        string? steamPath = null;
        string? sharedConfigPath = null;
        string? backupPath = null;
        var steamWasRunning = false;
        var originalExisted = false;

        try
        {
            steamPath = GetSteamPath();
            sharedConfigPath = ResolveCurrentSharedConfigPath(steamPath);

            // Avoid closing Steam when every target already has the requested
            // explicit per-game override. We still re-read after shutdown when
            // a write is required because Steam keeps this file in memory.
            if (File.Exists(sharedConfigPath))
            {
                var preflightText = ReadUtf8File(sharedConfigPath).Text;
                var alreadyConfigured = targets.All(appId =>
                {
                    var state = ReadCloudState(preflightText, appId);
                    return state.AppOverrideEnabled == enabled;
                });
                if (alreadyConfigured)
                {
                    var unchangedMessage = enabled
                        ? "此游戏的 Steam 云同步已经开启。"
                        : "此游戏的 Steam 云同步已经关闭，不会再尝试连接云存档。";
                    return new SteamCloudPreferenceResult(true, unchangedMessage);
                }
            }

            steamWasRunning = IsSteamRunning();

            if (steamWasRunning && !await RequestSteamShutdownAsync(steamPath, cancellationToken))
            {
                return new SteamCloudPreferenceResult(
                    false,
                    "Steam 没有完全退出。请先关闭正在运行的游戏，再重试。不会修改任何配置。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(sharedConfigPath)!);
            originalExisted = File.Exists(sharedConfigPath);

            var source = originalExisted
                ? ReadUtf8File(sharedConfigPath)
                : new Utf8File(CreateEmptyDocument(Environment.NewLine), false);
            var updatedText = source.Text;
            var changedCount = 0;

            foreach (var appId in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadCloudState(updatedText, appId);
                if (current.AppOverrideEnabled == enabled)
                    continue;

                updatedText = UpdateCloudSettingText(updatedText, appId, enabled);
                changedCount++;
            }

            if (changedCount == 0)
            {
                var unchangedMessage = enabled
                    ? "此游戏的 Steam 云同步已经开启。"
                    : "此游戏的 Steam 云同步已经关闭，不会再尝试连接云存档。";
                return new SteamCloudPreferenceResult(
                    true,
                    steamWasRunning ? $"{unchangedMessage} Steam 将自动重新启动。" : unchangedMessage);
            }

            if (originalExisted)
            {
                backupPath = sharedConfigPath + $".mjjst-cloud-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak";
                File.Copy(sharedConfigPath, backupPath, overwrite: false);
            }

            WriteUtf8Atomically(sharedConfigPath, updatedText, source.HasBom);

            var verificationText = ReadUtf8File(sharedConfigPath).Text;
            var verificationFailed = targets.Any(appId =>
            {
                var verified = ReadCloudState(verificationText, appId);
                return verified.AppOverrideEnabled != enabled;
            });
            if (verificationFailed)
                throw new InvalidDataException("写入后的 Steam 云设置校验失败。");

            var action = enabled ? "开启" : "关闭";
            var suffix = steamWasRunning ? "Steam 将自动重新启动。" : "下次启动 Steam 后生效。";
            return new SteamCloudPreferenceResult(
                true,
                $"已为当前 Steam 账号{action} {changedCount} 款游戏的云同步。{suffix}",
                changedCount,
                backupPath);
        }
        catch (OperationCanceledException)
        {
            return new SteamCloudPreferenceResult(false, "操作已取消，未继续修改 Steam 云设置。", BackupPath: backupPath);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(sharedConfigPath) &&
                !string.IsNullOrWhiteSpace(backupPath) &&
                File.Exists(backupPath))
            {
                try { File.Copy(backupPath, sharedConfigPath, overwrite: true); }
                catch { }
            }
            else if (!originalExisted && !string.IsNullOrWhiteSpace(sharedConfigPath))
            {
                try { if (File.Exists(sharedConfigPath)) File.Delete(sharedConfigPath); }
                catch { }
            }

            return new SteamCloudPreferenceResult(
                false,
                $"{(enabled ? "开启" : "关闭")} Steam 云同步失败：{ex.GetBaseException().Message}",
                BackupPath: backupPath);
        }
        finally
        {
            if (steamWasRunning && !string.IsNullOrWhiteSpace(steamPath) && !IsSteamRunning())
            {
                try
                {
                    StartSteam(steamPath);
                }
                catch
                {
                    // The result explains that Steam was stopped. The user can
                    // still start it normally if automatic launch is blocked.
                }
            }
        }
    }

    internal static string UpdateCloudSettingText(string source, int appId, bool enabled)
    {
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = string.IsNullOrWhiteSpace(source) ? CreateEmptyDocument(newline) : source;
        var objectPath = SteamObjectPath.Concat(["Apps", appId.ToString()]).ToArray();

        for (var depth = 0; depth < objectPath.Length; depth++)
        {
            var document = VdfDocument.Parse(text);
            if (TryFindUniqueObject(document, objectPath[..(depth + 1)], out _))
                continue;

            if (depth == 0)
            {
                throw new InvalidDataException(
                    "Steam sharedconfig.vdf 缺少预期的根节点，已停止写入以保护原配置。");
            }

            if (!TryFindUniqueObject(document, objectPath[..depth], out var parent))
                throw new InvalidDataException("Steam sharedconfig.vdf 的层级结构不完整。");
            text = InsertObject(text, parent, objectPath[depth], newline);
        }

        var finalDocument = VdfDocument.Parse(text);
        if (!TryFindUniqueObject(finalDocument, objectPath, out var appObject))
            throw new InvalidDataException("无法创建游戏的 Steam 云设置节点。");

        var value = enabled ? "1" : "0";
        var cloudEntries = appObject.Entries
            .Where(entry => entry.Key.Equals("CloudEnabled", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (cloudEntries.Count > 1 || cloudEntries.FirstOrDefault()?.ValueToken is null && cloudEntries.Count == 1)
        {
            throw new InvalidDataException(
                "此游戏的 CloudEnabled 节点重复或格式异常，已停止写入以保护 Steam 配置。");
        }
        var existing = cloudEntries.FirstOrDefault();
        if (existing?.ValueToken is { } valueToken)
            return text[..valueToken.Start] + $"\"{value}\"" + text[valueToken.End..];

        return InsertProperty(text, appObject, "CloudEnabled", value, newline);
    }

    private static SteamCloudPreferenceState ReadCloudState(string text, int appId)
    {
        var document = VdfDocument.Parse(text);
        var globalEnabled = true;
        if (TryFindUniqueObject(document, SteamObjectPath, out var steamObject))
        {
            var global = GetUniqueProperty(steamObject, "CloudEnabled");
            if (global?.ValueToken is { } globalValue)
                globalEnabled = globalValue.Value != "0";
        }

        bool? appOverrideEnabled = null;
        var appPath = SteamObjectPath.Concat(["Apps", appId.ToString()]).ToArray();
        if (TryFindUniqueObject(document, appPath, out var appObject))
        {
            var setting = GetUniqueProperty(appObject, "CloudEnabled");
            if (setting?.ValueToken is { } appValue)
                appOverrideEnabled = appValue.Value != "0";
        }

        var effectiveEnabled = globalEnabled && (appOverrideEnabled ?? true);
        return new SteamCloudPreferenceState(
            effectiveEnabled,
            appOverrideEnabled.HasValue,
            globalEnabled,
            appOverrideEnabled);
    }

    private string GetSteamPath()
    {
        var path = _steamPathService.GetCustomPath();
        if (string.IsNullOrWhiteSpace(path))
            path = _steamPathService.DetectSteamPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.Combine(path, "steam.exe")))
            throw new DirectoryNotFoundException("未检测到有效的 Steam 安装路径，请先在设置中配置。");
        return path;
    }

    private static string ResolveCurrentSharedConfigPath(string steamPath)
    {
        var userdataRoot = Path.Combine(steamPath, "userdata");
        var activeUser = ReadActiveAccountId();
        if (activeUser != 0)
        {
            var accountRoot = Path.Combine(userdataRoot, activeUser.ToString());
            if (!Directory.Exists(accountRoot))
            {
                throw new InvalidOperationException(
                    $"Steam 当前账号 {activeUser} 没有对应的 userdata 目录，已停止操作以免修改错账号。");
            }
            return Path.Combine(accountRoot, "7", "remote", "sharedconfig.vdf");
        }

        var accounts = ReadLoginAccounts(steamPath);
        LoginAccount? selected = null;
        var autoLoginUser = ReadAutoLoginUser();
        if (!string.IsNullOrWhiteSpace(autoLoginUser))
        {
            selected = accounts.FirstOrDefault(account =>
                account.AccountName.Equals(autoLoginUser, StringComparison.OrdinalIgnoreCase));
        }

        selected ??= SelectOnly(accounts.Where(account => account.AutoLogin));
        selected ??= SelectOnly(accounts.Where(account => account.MostRecent));
        selected ??= accounts.Count == 1 ? accounts[0] : null;
        if (selected is null)
        {
            throw new InvalidOperationException(
                "无法唯一确定当前 Steam 账号。请先登录 Steam，再重试；不会猜测或修改其他账号配置。");
        }

        var selectedRoot = Path.Combine(userdataRoot, selected.AccountId.ToString());
        if (!Directory.Exists(selectedRoot))
        {
            throw new InvalidOperationException(
                $"Steam 账号 {selected.AccountName} 没有对应的 userdata 目录，已停止操作以免修改错账号。");
        }
        return Path.Combine(selectedRoot, "7", "remote", "sharedconfig.vdf");
    }

    private static uint ReadActiveAccountId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            return key?.GetValue("ActiveUser") switch
            {
                int value => unchecked((uint)value),
                long value => unchecked((uint)value),
                uint value => value,
                string value when uint.TryParse(value, out var parsed) => parsed,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadAutoLoginUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("AutoLoginUser") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<LoginAccount> ReadLoginAccounts(string steamPath)
    {
        var loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(loginUsersPath))
            return [];

        var document = VdfDocument.Parse(ReadUtf8File(loginUsersPath).Text);
        if (!TryFindUniqueObject(document, ["users"], out var users))
            return [];

        var result = new List<LoginAccount>();
        foreach (var entry in users.Entries)
        {
            if (entry.ObjectValue is not { } accountObject ||
                !ulong.TryParse(entry.Key, out var steamId64))
                continue;

            var accountName = GetPropertyValue(accountObject, "AccountName");
            if (string.IsNullOrWhiteSpace(accountName))
                continue;

            result.Add(new LoginAccount(
                unchecked((uint)(steamId64 & uint.MaxValue)),
                accountName,
                GetPropertyValue(accountObject, "MostRecent") == "1",
                GetPropertyValue(accountObject, "AutoLogin") == "1"));
        }
        return result;
    }

    private static string GetPropertyValue(VdfObject value, string key) =>
        GetUniqueProperty(value, key)?.ValueToken?.Value ?? string.Empty;

    private static LoginAccount? SelectOnly(IEnumerable<LoginAccount> accounts)
    {
        using var iterator = accounts.Take(2).GetEnumerator();
        if (!iterator.MoveNext())
            return null;
        var selected = iterator.Current;
        return iterator.MoveNext() ? null : selected;
    }

    private static async Task<bool> RequestSteamShutdownAsync(
        string steamPath,
        CancellationToken cancellationToken)
    {
        if (!IsSteamRunning())
            return true;

        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(steamPath, "steam.exe"),
            Arguments = "-shutdown",
            WorkingDirectory = steamPath,
            UseShellExecute = true
        });

        var timeout = Stopwatch.StartNew();
        while (IsSteamRunning() && timeout.Elapsed < TimeSpan.FromSeconds(25))
            await Task.Delay(250, cancellationToken);
        return !IsSteamRunning();
    }

    private static bool IsSteamRunning()
    {
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                        return true;
                }
                catch
                {
                }
            }
        }
        return false;
    }

    private static void StartSteam(string steamPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(steamPath, "steam.exe"),
            WorkingDirectory = steamPath,
            UseShellExecute = true
        });
    }

    private static Utf8File ReadUtf8File(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = hasBom ? 3 : 0;
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return new Utf8File(strictUtf8.GetString(bytes, offset, bytes.Length - offset), hasBom);
    }

    private static void WriteUtf8Atomically(string path, string text, bool withBom)
    {
        var tempPath = path + $".mjjst-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, text, new UTF8Encoding(withBom));
            if (File.Exists(path))
            {
                try { File.Replace(tempPath, path, null, ignoreMetadataErrors: true); }
                catch (IOException) { File.Move(tempPath, path, overwrite: true); }
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { }
        }
    }

    private static string CreateEmptyDocument(string newline) =>
        $"\"{RootKey}\"{newline}{{{newline}}}{newline}";

    private static string InsertObject(string text, VdfObject parent, string key, string newline)
    {
        var (insertAt, parentIndent) = FindClosingLine(text, parent.CloseBrace.Start);
        var indent = parentIndent + "\t";
        var block =
            $"{indent}\"{key}\"{newline}" +
            $"{indent}{{{newline}" +
            $"{indent}}}{newline}";
        return text.Insert(insertAt, block);
    }

    private static string InsertProperty(
        string text,
        VdfObject parent,
        string key,
        string value,
        string newline)
    {
        var (insertAt, parentIndent) = FindClosingLine(text, parent.CloseBrace.Start);
        var line = $"{parentIndent}\t\"{key}\"\t\t\"{value}\"{newline}";
        return text.Insert(insertAt, line);
    }

    private static (int InsertAt, string Indent) FindClosingLine(string text, int braceIndex)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, braceIndex - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var indentLength = 0;
        while (lineStart + indentLength < braceIndex &&
               text[lineStart + indentLength] is ' ' or '\t')
            indentLength++;

        if (lineStart + indentLength == braceIndex)
            return (lineStart, text.Substring(lineStart, indentLength));
        return (braceIndex, string.Empty);
    }

    private static bool TryFindUniqueObject(
        VdfDocument document,
        IReadOnlyList<string> path,
        out VdfObject value)
    {
        value = null!;
        if (path.Count == 0)
            return false;

        IReadOnlyCollection<VdfEntry> entries = document.Entries;
        VdfObject? current = null;
        foreach (var segment in path)
        {
            var matches = entries
                .Where(entry => entry.Key.Equals(segment, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                return false;
            if (matches.Count != 1 || matches[0].ObjectValue is not { } child)
            {
                throw new InvalidDataException(
                    $"Steam VDF 节点 {segment} 重复或不是对象，已停止写入以保护原配置。");
            }

            current = child;
            entries = child.Entries;
        }

        value = current!;
        return true;
    }

    private static VdfEntry? GetUniqueProperty(VdfObject value, string key)
    {
        var matches = value.Entries
            .Where(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            return null;
        if (matches.Count != 1 || matches[0].ValueToken is null)
        {
            throw new InvalidDataException(
                $"Steam VDF 属性 {key} 重复或格式异常，已停止读取以保护原配置。");
        }
        return matches[0];
    }

    private sealed record Utf8File(string Text, bool HasBom);
    private sealed record LoginAccount(uint AccountId, string AccountName, bool MostRecent, bool AutoLogin);

    private enum VdfTokenKind
    {
        String,
        OpenBrace,
        CloseBrace
    }

    private sealed record VdfToken(VdfTokenKind Kind, string Value, int Start, int End);

    private sealed class VdfEntry
    {
        public required string Key { get; init; }
        public VdfToken? ValueToken { get; init; }
        public VdfObject? ObjectValue { get; init; }
    }

    private sealed class VdfObject
    {
        public required VdfToken OpenBrace { get; init; }
        public required VdfToken CloseBrace { get; init; }
        public List<VdfEntry> Entries { get; } = [];
    }

    private sealed class VdfDocument
    {
        public List<VdfEntry> Entries { get; } = [];

        public static VdfDocument Parse(string text)
        {
            var tokens = Tokenize(text);
            var document = new VdfDocument();
            var index = 0;
            ParseEntries(tokens, ref index, document.Entries, expectClosingBrace: false);
            return document;
        }

        private static void ParseEntries(
            IReadOnlyList<VdfToken> tokens,
            ref int index,
            ICollection<VdfEntry> entries,
            bool expectClosingBrace)
        {
            while (index < tokens.Count)
            {
                if (tokens[index].Kind == VdfTokenKind.CloseBrace)
                {
                    if (!expectClosingBrace)
                        throw new InvalidDataException("Steam VDF 包含多余的右括号。");
                    return;
                }

                if (tokens[index].Kind != VdfTokenKind.String)
                    throw new InvalidDataException("Steam VDF 键格式无效。");
                var key = tokens[index++].Value;
                if (index >= tokens.Count)
                    throw new InvalidDataException("Steam VDF 键缺少值。");

                if (tokens[index].Kind == VdfTokenKind.String)
                {
                    entries.Add(new VdfEntry { Key = key, ValueToken = tokens[index++] });
                    continue;
                }

                if (tokens[index].Kind != VdfTokenKind.OpenBrace)
                    throw new InvalidDataException("Steam VDF 对象格式无效。");
                var openBrace = tokens[index++];
                var childEntries = new List<VdfEntry>();
                ParseEntries(tokens, ref index, childEntries, expectClosingBrace: true);
                if (index >= tokens.Count || tokens[index].Kind != VdfTokenKind.CloseBrace)
                    throw new InvalidDataException("Steam VDF 对象缺少右括号。");
                var closeBrace = tokens[index++];
                var child = new VdfObject { OpenBrace = openBrace, CloseBrace = closeBrace };
                child.Entries.AddRange(childEntries);
                entries.Add(new VdfEntry { Key = key, ObjectValue = child });
            }

            if (expectClosingBrace)
                throw new InvalidDataException("Steam VDF 对象未闭合。");
        }

        private static List<VdfToken> Tokenize(string text)
        {
            var tokens = new List<VdfToken>();
            for (var index = 0; index < text.Length;)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    index++;
                    continue;
                }

                if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
                {
                    index += 2;
                    while (index < text.Length && text[index] is not '\r' and not '\n')
                        index++;
                    continue;
                }

                if (text[index] == '{')
                {
                    tokens.Add(new VdfToken(VdfTokenKind.OpenBrace, "{", index, index + 1));
                    index++;
                    continue;
                }
                if (text[index] == '}')
                {
                    tokens.Add(new VdfToken(VdfTokenKind.CloseBrace, "}", index, index + 1));
                    index++;
                    continue;
                }
                if (text[index] != '"')
                    throw new InvalidDataException($"Steam VDF 在位置 {index} 包含无法识别的内容。");

                var start = index++;
                var value = new StringBuilder();
                var closed = false;
                while (index < text.Length)
                {
                    var character = text[index++];
                    if (character == '"')
                    {
                        closed = true;
                        break;
                    }
                    if (character == '\\' && index < text.Length)
                    {
                        var escaped = text[index++];
                        value.Append(escaped switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            _ => escaped
                        });
                        continue;
                    }
                    value.Append(character);
                }
                if (!closed)
                    throw new InvalidDataException("Steam VDF 字符串未闭合。");
                tokens.Add(new VdfToken(VdfTokenKind.String, value.ToString(), start, index));
            }
            return tokens;
        }
    }
}
