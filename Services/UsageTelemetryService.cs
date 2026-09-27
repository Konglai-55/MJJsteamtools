using System.IO;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SteamLuaManager.Services;

/// <summary>
/// Sends an anonymous, best-effort heartbeat when the user has explicitly opted in.
/// Telemetry failures are intentionally isolated from the rest of the application.
/// </summary>
public sealed class UsageTelemetryService : IUsageTelemetryService
{
    private const string ProductionHeartbeatEndpoint =
        "https://mjjsteamtools-telemetry.konglai55.workers.dev/api/heartbeat";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly ISettingsService _settingsService;
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly object _lifecycleLock = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("D");
    private readonly Lazy<string> _installationHash = new(
        CreateAnonymousInstallationHash,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _disposed;

    public UsageTelemetryService(
        ISettingsService settingsService,
        IHttpClientProvider httpClientProvider)
    {
        _settingsService = settingsService;
        _httpClientProvider = httpClientProvider;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public bool IsConfigured => ResolveEndpoint() is not null;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || !_settingsService.Load().AnonymousUsageStatisticsEnabled)
                return;

            var endpoint = ResolveEndpoint();
            if (endpoint is null || _loopTask is { IsCompleted: false })
                return;

            _loopCancellation?.Dispose();
            _loopCancellation = new CancellationTokenSource();
            _loopTask = RunHeartbeatLoopAsync(endpoint, _loopCancellation.Token);
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _loopCancellation?.Cancel();
            _loopCancellation?.Dispose();
            _loopCancellation = null;
            _loopTask = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        Stop();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (settings.AnonymousUsageStatisticsEnabled)
            Start();
        else
            Stop();
    }

    private async Task RunHeartbeatLoopAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        await TrySendHeartbeatAsync(endpoint, cancellationToken);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await TrySendHeartbeatAsync(endpoint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task TrySendHeartbeatAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var payload = new UsageHeartbeat(
                _installationHash.Value,
                GetApplicationVersion(),
                _sessionId);

            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "anonymous-usage-statistics",
                RequestTimeout,
                client => client.PostAsJsonAsync(endpoint, payload, cancellationToken));

            // The heartbeat is deliberately best-effort. A non-success status is not
            // surfaced to users and will be retried on the next regular interval.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Anonymous statistics must never affect startup, navigation or shutdown.
        }
    }

    private static string CreateAnonymousInstallationHash()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MJJsteamtools");
        var idPath = Path.Combine(dataDirectory, "anonymous-install-id");

        Directory.CreateDirectory(dataDirectory);
        string installationId;
        try
        {
            installationId = File.Exists(idPath)
                ? File.ReadAllText(idPath).Trim()
                : string.Empty;
        }
        catch
        {
            installationId = string.Empty;
        }

        if (!Guid.TryParse(installationId, out var parsedId))
        {
            parsedId = Guid.NewGuid();
            installationId = parsedId.ToString("D");
            try
            {
                File.WriteAllText(idPath, installationId, new UTF8Encoding(false));
            }
            catch
            {
                // A stable process-local ID still allows this session to be counted.
            }
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(installationId)))
            .ToLowerInvariant();
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null)
            return "unknown";

        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private static Uri? ResolveEndpoint()
    {
        var configured = Environment.GetEnvironmentVariable("MJJST_TELEMETRY_ENDPOINT");
        if (string.IsNullOrWhiteSpace(configured))
        {
            var endpointFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "telemetry-endpoint.txt");
            try
            {
                if (File.Exists(endpointFile))
                {
                    configured = File.ReadLines(endpointFile)
                        .Select(line => line.Trim())
                        .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'));
                }
            }
            catch
            {
                configured = null;
            }
        }

        if (string.IsNullOrWhiteSpace(configured))
            configured = ProductionHeartbeatEndpoint;

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint))
            return null;

        if (endpoint.Scheme == Uri.UriSchemeHttps)
            return endpoint;

        return endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback
            ? endpoint
            : null;
    }

    private sealed record UsageHeartbeat(
        string InstallationId,
        string AppVersion,
        string SessionId);
}
