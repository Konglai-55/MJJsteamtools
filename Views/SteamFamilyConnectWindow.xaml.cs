using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace SteamLuaManager.Views;

public partial class SteamFamilyConnectWindow : Window
{
    private bool _isCheckingToken;
    private bool _isClosing;
    private bool _isNavigatingToTokenEndpoint;
    private int _tokenReadAttempts;
    private const string TokenEndpoint = "https://store.steampowered.com/pointssummary/ajaxgetasyncconfig";

    public string AccessToken { get; private set; } = string.Empty;

    public SteamFamilyConnectWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MJJsteamtools",
                "SteamFamilyWebView");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Browser.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            Browser.Source = new Uri("https://store.steampowered.com/login/?redir=account%2Ffamilymanagement&redir_ssl=1");
        }
        catch (Exception ex)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            StatusText.Text = $"登录窗口启动失败：{ex.GetBaseException().Message}";
        }
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }

        if (!IsAllowedSteamHost(uri.Host))
        {
            e.Cancel = true;
            StatusText.Text = "已阻止离开 Steam 官方域名";
            return;
        }
        StatusText.Text = "等待 Steam 登录…";
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess ||
            !Uri.TryCreate(Browser.CoreWebView2?.Source, UriKind.Absolute, out var currentUri) ||
            !currentUri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase))
            return;

        if (currentUri.AbsolutePath.Contains("/pointssummary/ajaxgetasyncconfig", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(350);
            await ReadAccessTokenPageAsync();
            return;
        }

        if (!_isNavigatingToTokenEndpoint &&
            !currentUri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
        {
            _isNavigatingToTokenEndpoint = true;
            StatusText.Text = "已登录，正在连接家庭接口…";
            Browser.CoreWebView2.Navigate(TokenEndpoint);
        }
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && IsAllowedSteamHost(uri.Host))
            Browser.CoreWebView2.Navigate(uri.ToString());
    }

    private async Task ReadAccessTokenPageAsync()
    {
        if (_isCheckingToken || _isClosing || Browser.CoreWebView2 == null) return;
        _isCheckingToken = true;
        try
        {
            var scriptResult = await Browser.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            var responseText = JsonSerializer.Deserialize<string>(scriptResult) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(responseText))
            {
                await RetryTokenReadAsync("正在等待 Steam 返回家庭库凭据…");
                return;
            }
            using var document = JsonDocument.Parse(responseText);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("webapi_token", out var tokenNode))
            {
                await RetryTokenReadAsync("正在确认 Steam 登录状态…");
                return;
            }
            var token = tokenNode.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text = "访问令牌为空，请重新登录";
                return;
            }

            AccessToken = token;
            StatusText.Text = "连接成功，正在读取家庭库…";
            _isClosing = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"连接失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            _isCheckingToken = false;
        }
    }

    private async Task RetryTokenReadAsync(string status)
    {
        if (_tokenReadAttempts++ >= 5)
        {
            StatusText.Text = "未能取得家庭库访问权限，请刷新登录后重试";
            return;
        }

        StatusText.Text = status;
        _isCheckingToken = false;
        await Task.Delay(600);
        await ReadAccessTokenPageAsync();
    }

    private static bool IsAllowedSteamHost(string host) =>
        host.Equals("steampowered.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".steampowered.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase);

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        if (Browser.CoreWebView2 != null)
        {
            Browser.CoreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            Browser.CoreWebView2.NewWindowRequested -= CoreWebView2_NewWindowRequested;
        }
        Browser.Dispose();
    }
}
