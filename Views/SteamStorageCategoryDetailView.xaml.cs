using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public partial class SteamStorageCategoryDetailView : UserControl
{
    private readonly SteamStorageService _storageService;
    private readonly SteamStorageCategory _category;
    private readonly ObservableCollection<SteamStorageDetailItem> _items = [];
    private CancellationTokenSource? _loadCancellation;
    private bool _hasLoaded;

    public Action? BackRequested { get; set; }
    public Func<SteamStorageDetailItem, Task<bool>>? DeleteRequested { get; set; }
    public Action? DataChanged { get; set; }

    public SteamStorageCategoryDetailView(
        SteamStorageService storageService,
        SteamStorageCategory category)
    {
        _storageService = storageService;
        _category = category;
        InitializeComponent();
        DetailList.ItemsSource = _items;
        ConfigureHeader();
    }

    private void ConfigureHeader()
    {
        PageTitleText.Text = _category.Key switch
        {
            "games" => "游戏本体明细",
            "workshop" => "创意工坊明细",
            "shader-cache" => "着色器缓存明细",
            _ => _category.Title
        };
        PageSubtitleText.Text = _category.Key switch
        {
            "games" => "按已安装游戏展示容量；卸载操作由 Steam 接管。",
            "workshop" => "按游戏汇总已下载的创意工坊内容，删除后 Steam 可能按订阅重新下载。",
            "shader-cache" => "按游戏展示可重建缓存；删除后首次运行可能需要重新编译。",
            _ => _category.Description
        };
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hasLoaded)
            return;
        _hasLoaded = true;
        await LoadAsync();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e) => CancelActiveLoad();

    public void CancelActiveLoad()
    {
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        cancellation?.Cancel();
    }

    private async Task LoadAsync()
    {
        CancelActiveLoad();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        SetLoadingState(true, "正在读取明细…");

        var progress = new Progress<SteamStorageDetailProgress>(value =>
        {
            if (!ReferenceEquals(_loadCancellation, cancellation))
                return;
            StatusText.Text = string.IsNullOrWhiteSpace(value.Item)
                ? "正在统计目录…"
                : $"正在统计：{value.Item}";
            LoadProgressBar.Value = value.Total <= 0 ? 0 : value.Completed * 100d / value.Total;
        });

        try
        {
            var items = await _storageService.GetCategoryDetailsAsync(_category.Key, progress, token);
            if (!ReferenceEquals(_loadCancellation, cancellation))
                return;
            _loadCancellation = null;
            _items.Clear();
            foreach (var item in items)
                _items.Add(item);
            UpdateSummary();
            StatusText.Text = $"统计完成 · {_items.Count:N0} 个条目";
            StatusIcon.Glyph = "\uE73E";
            StatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x79, 0xC4, 0x7B));
            SetLoadingState(false, StatusText.Text);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
                SetLoadingState(false, "已取消读取明细。");
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_loadCancellation, cancellation))
                return;
            _loadCancellation = null;
            StatusIcon.Glyph = "\uE783";
            StatusIcon.Foreground = Brushes.IndianRed;
            SetLoadingState(false, $"读取失败：{ex.Message}");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void SetLoadingState(bool isLoading, string message)
    {
        StatusText.Text = message;
        LoadProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        DetailList.IsEnabled = !isLoading;
        if (isLoading)
        {
            StatusIcon.Glyph = "\uE895";
            LoadProgressBar.Value = 0;
        }
        EmptyState.Visibility = !isLoading && _items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateSummary()
    {
        ItemCountText.Text = $"{_items.Count:N0} 个";
        TotalSizeText.Text = SteamStorageService.FormatBytes(_items.Sum(item => item.SizeBytes));
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        CancelActiveLoad();
        BackRequested?.Invoke();
    }

    private void OpenDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SteamStorageDetailItem item } || !Directory.Exists(item.Path))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{item.Path}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SteamStorageDetailItem item } button || DeleteRequested is null)
            return;
        button.IsEnabled = false;
        try
        {
            if (!await DeleteRequested(item))
                return;
            _items.Remove(item);
            UpdateSummary();
            StatusText.Text = $"已删除 {item.Name} 的 {item.SizeText} 数据。";
            DataChanged?.Invoke();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
