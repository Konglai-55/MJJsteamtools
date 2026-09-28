using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public partial class ManifestCacheWindow : Window
{
    private readonly ManifestCacheService _cache = new();
    private readonly ISteamPathService _steamPaths;
    private CancellationTokenSource? _operation;

    public ManifestCacheWindow(ISteamPathService steamPaths)
    {
        _steamPaths = steamPaths;
        InitializeComponent();
        RefreshEntries();
        StatusText.Text = "尚未扫描。导入支持 .manifest 和 DepotDownloader 导出的清单 .txt。";
        Closing += (_, _) => _operation?.Cancel();
    }

    private void RefreshEntries() => ManifestList.ItemsSource = _cache.ReadEntries();

    private async Task RunAsync(Func<CancellationToken, Task<string>> action)
    {
        if (_operation is not null) { StatusText.Text = "正在执行任务，请等待或取消。"; return; }
        using var cts = new CancellationTokenSource();
        _operation = cts;
        FetchButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = "正在处理…";
        try { StatusText.Text = await action(cts.Token); }
        catch (OperationCanceledException) { StatusText.Text = "任务已取消；已完成的缓存保留。"; }
        catch (Exception ex) { StatusText.Text = $"操作失败：{ex.Message}"; }
        finally
        {
            _operation = null;
            FetchButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            RefreshEntries();
        }
    }

    private async Task<string> ImportFilesAsync(IEnumerable<string> files, string source, CancellationToken ct)
    {
        var added = 0; var duplicates = 0; var failed = 0;
        string? lastError = null;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try { if (await _cache.ImportAsync(file, source, ct)) added++; else duplicates++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { failed++; lastError = $"{Path.GetFileName(file)}：{ex.Message}"; }
        }
        return $"新增 {added} 份 · 重复 {duplicates} 份 · 失败 {failed} 份" +
            (lastError is null ? string.Empty : $"。最后一个错误：{lastError}");
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "清单文件|*.manifest;manifest_*.txt", Title = "选择本地清单" };
        if (dialog.ShowDialog(this) == true)
            await RunAsync(ct => ImportFilesAsync(dialog.FileNames, "本地导入", ct));
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async ct =>
        {
            var folders = _steamPaths.GetAllLibraryPaths().SelectMany(p => new[]
                { Path.Combine(p, "depotcache"), Path.Combine(p, "steamapps", "depotcache") }).Distinct().ToArray();
            var files = await Task.Run(() =>
            {
                var found = new List<string>();
                foreach (var folder in folders)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(folder)) found.AddRange(Directory.EnumerateFiles(folder, "*.manifest"));
                }
                return found;
            }, ct);
            if (files.Count == 0) return "未找到 Steam 本地清单。可使用本地导入或账号获取。";
            return await ImportFilesAsync(files, "Steam 本地缓存", ct);
        });
    }

    private void SelectDownloader_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "DepotDownloader|DepotDownloader.exe", Title = "选择官方 DepotDownloader.exe" };
        if (dialog.ShowDialog(this) == true) DownloaderPath.Text = dialog.FileName;
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AppIdInput.Text.Trim(), out var id) || id <= 0)
        { StatusText.Text = "请输入有效的数字 AppID。"; AppIdInput.Focus(); return; }
        var executable = DownloaderPath.Text; var username = UsernameInput.Text;
        await RunAsync(async ct =>
        {
            StatusText.Text = "请在下载器窗口完成 Steam 登录。这里只获取清单，不下载游戏文件。";
            var count = await _cache.FetchAsync(executable, id, username, ct);
            return $"已获取并缓存 {count} 份清单。";
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Official_Click(object sender, RoutedEventArgs e) => Open("https://github.com/SteamRE/DepotDownloader/releases");
    private void OpenCache_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_cache.CacheDirectory);
        Open(_cache.CacheDirectory);
    }
    private void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"打开失败：{ex.Message}"; }
    }
}
