using System.Net;
using System.Net.Http;
using System.Linq;
using Microsoft.Win32;

namespace SteamLuaManager.Services;

public interface IHttpClientProvider
{
    HttpClient GetClient(string name, TimeSpan timeout, Action<HttpClient>? configure = null);
    Task<T> SendWithProxyRetryAsync<T>(string name, TimeSpan timeout, Func<HttpClient, Task<T>> sendAsync, Action<HttpClient>? configure = null);
    Task SendWithProxyRetryAsync(string name, TimeSpan timeout, Func<HttpClient, Task> sendAsync, Action<HttpClient>? configure = null);
    void Reset(string? name = null);
}

public sealed class HttpClientProvider : IHttpClientProvider, IDisposable
{
    private sealed record ClientEntry(HttpClient Client, string ProxySignature);
    private sealed record ProxySnapshot(string Signature, IWebProxy? Proxy, bool UseProxy);

    private readonly object _lock = new();
    private readonly Dictionary<string, ClientEntry> _clients = new();
    private readonly ISettingsService _settingsService;

    public HttpClientProvider(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public HttpClient GetClient(string name, TimeSpan timeout, Action<HttpClient>? configure = null)
    {
        var proxy = GetProxySnapshot();
        lock (_lock)
        {
            if (_clients.TryGetValue(name, out var entry) && entry.ProxySignature == proxy.Signature)
                return entry.Client;

            if (_clients.Remove(name, out entry))
                entry.Client.Dispose();

            var client = CreateClient(timeout, proxy);
            configure?.Invoke(client);
            _clients[name] = new ClientEntry(client, proxy.Signature);
            return client;
        }
    }

    public async Task<T> SendWithProxyRetryAsync<T>(string name, TimeSpan timeout, Func<HttpClient, Task<T>> sendAsync, Action<HttpClient>? configure = null)
    {
        try
        {
            return await sendAsync(GetClient(name, timeout, configure));
        }
        catch (Exception ex) when (ShouldRefreshClient(ex))
        {
            Reset(name);
            try
            {
                return await sendAsync(GetClient(name, timeout, configure));
            }
            catch (Exception retryException) when (ShouldRefreshClient(retryException) && ShouldTryDirectFallback())
            {
                using var directClient = CreateClient(timeout, new ProxySnapshot("direct-fallback", null, false));
                configure?.Invoke(directClient);
                return await sendAsync(directClient);
            }
        }
    }

    public async Task SendWithProxyRetryAsync(string name, TimeSpan timeout, Func<HttpClient, Task> sendAsync, Action<HttpClient>? configure = null)
    {
        try
        {
            await sendAsync(GetClient(name, timeout, configure));
        }
        catch (Exception ex) when (ShouldRefreshClient(ex))
        {
            Reset(name);
            try
            {
                await sendAsync(GetClient(name, timeout, configure));
            }
            catch (Exception retryException) when (ShouldRefreshClient(retryException) && ShouldTryDirectFallback())
            {
                using var directClient = CreateClient(timeout, new ProxySnapshot("direct-fallback", null, false));
                configure?.Invoke(directClient);
                await sendAsync(directClient);
            }
        }
    }

    public void Reset(string? name = null)
    {
        lock (_lock)
        {
            if (name != null)
            {
                if (_clients.Remove(name, out var entry))
                    entry.Client.Dispose();
                return;
            }

            foreach (var entry in _clients.Values)
                entry.Client.Dispose();
            _clients.Clear();
        }
    }

    public void Dispose()
    {
        Reset();
    }

    private static HttpClient CreateClient(TimeSpan timeout, ProxySnapshot proxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = proxy.UseProxy,
            Proxy = proxy.Proxy,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        if (handler.Proxy != null)
            handler.Proxy.Credentials = CredentialCache.DefaultCredentials;

        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    private ProxySnapshot GetProxySnapshot()
    {
        var settings = _settingsService.Load();
        return settings.ProxyMode switch
        {
            "Direct" => new ProxySnapshot("direct", null, false),
            "Manual" => CreateManualProxySnapshot(settings.ManualProxy),
            "System" => GetWindowsProxySnapshot(),
            _ => GetAutomaticProxySnapshot()
        };
    }

    private bool ShouldTryDirectFallback()
    {
        var settings = _settingsService.Load();
        return settings.ProxyMode == "Auto" && GetAutomaticProxySnapshot().UseProxy;
    }

    private static ProxySnapshot CreateManualProxySnapshot(string proxyAddress)
    {
        if (TryCreateExplicitProxy(proxyAddress, out var proxy, out var signature))
            return new ProxySnapshot($"manual|{signature}", proxy, true);
        throw new InvalidOperationException(
            "手动代理地址无效，请填写 http://127.0.0.1:7890 或 socks5://127.0.0.1:7890");
    }

    private static ProxySnapshot GetAutomaticProxySnapshot()
    {
        foreach (var variableName in new[] { "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy", "HTTP_PROXY", "http_proxy" })
        {
            var environmentProxy = Environment.GetEnvironmentVariable(variableName);
            if (TryCreateExplicitProxy(environmentProxy ?? string.Empty, out var proxy, out var signature))
                return new ProxySnapshot($"environment|{variableName}|{signature}", proxy, true);
        }

        return GetWindowsProxySnapshot();
    }

    private static ProxySnapshot GetWindowsProxySnapshot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var proxyEnable = key?.GetValue("ProxyEnable") is int enabled && enabled == 1;
            var proxyServer = key?.GetValue("ProxyServer") as string ?? string.Empty;
            var autoConfigUrl = key?.GetValue("AutoConfigURL") as string ?? string.Empty;

            if (proxyEnable && TryCreateExplicitProxy(proxyServer, out var explicitProxy, out var explicitSignature))
                return new ProxySnapshot($"explicit|{explicitSignature}|{autoConfigUrl}", explicitProxy, true);

            if (!string.IsNullOrWhiteSpace(autoConfigUrl))
            {
                var systemProxy = WebRequest.GetSystemWebProxy();
                systemProxy.Credentials = CredentialCache.DefaultCredentials;
                var http = systemProxy.GetProxy(new Uri("http://store.steampowered.com/"))?.ToString() ?? string.Empty;
                var https = systemProxy.GetProxy(new Uri("https://store.steampowered.com/"))?.ToString() ?? string.Empty;
                return new ProxySnapshot($"auto|{autoConfigUrl}|{http}|{https}", systemProxy, true);
            }

            var fallbackProxy = WebRequest.GetSystemWebProxy();
            fallbackProxy.Credentials = CredentialCache.DefaultCredentials;
            var probeUri = new Uri("https://store.steampowered.com/");
            var resolvedProxy = fallbackProxy.GetProxy(probeUri);
            if (resolvedProxy != null && resolvedProxy != probeUri)
                return new ProxySnapshot($"system|{resolvedProxy}", fallbackProxy, true);

            return new ProxySnapshot("direct", null, false);
        }
        catch
        {
            return new ProxySnapshot("fallback-system", WebRequest.GetSystemWebProxy(), true);
        }
    }

    private static bool TryCreateExplicitProxy(string proxyServer, out IWebProxy? proxy, out string signature)
    {
        proxy = null;
        signature = string.Empty;
        if (string.IsNullOrWhiteSpace(proxyServer)) return false;

        var endpoint = proxyServer;
        if (proxyServer.Contains(';'))
        {
            var entries = proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            endpoint = entries
                .Select(entry => entry.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .OrderBy(parts => parts[0].Equals("https", StringComparison.OrdinalIgnoreCase) ? 0 :
                                  parts[0].Equals("http", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .Select(parts => parts[1])
                .FirstOrDefault() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (!endpoint.Contains("://", StringComparison.Ordinal))
            endpoint = "http://" + endpoint;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var proxyUri)) return false;

        proxy = new WebProxy(proxyUri)
        {
            Credentials = CredentialCache.DefaultCredentials
        };
        signature = proxyUri.ToString();
        return true;
    }

    private static bool ShouldRefreshClient(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException or TimeoutException ||
               ex.InnerException is HttpRequestException or WebException or TimeoutException;
    }
}
