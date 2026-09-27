using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public partial class SteamStorageManagerView : UserControl
{
    private readonly SteamStorageService _storageService;
    private readonly List<SteamDriveContentItem> _allItems = [];
    private CancellationTokenSource? _scanCancellation;
    private SteamDriveContentSnapshot? _currentSnapshot;
    private bool _hasStarted;
    private bool _suppressDriveSelection;

    public Action? BackRequested { get; set; }
    public Func<SteamStorageCategory, Task<bool>>? CleanupRequested { get; set; }
    public Func<SteamStorageDetailItem, Task<bool>>? DetailDeleteRequested { get; set; }

    public SteamStorageManagerView(SteamStorageService storageService)
    {
        _storageService = storageService;
        InitializeComponent();
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hasStarted)
            return;
        _hasStarted = true;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var previousRoot = (DriveSelector.SelectedItem as SteamStorageDrive)?.RootPath;
        CancelCurrentScan(updateStatus: false);
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        SetScanningState(true, "正在读取 Steam 库磁盘…");
        try
        {
            var drives = await _storageService.GetStorageDrivesAsync(cancellation.Token);
            if (!ReferenceEquals(_scanCancellation, cancellation))
                return;
            if (drives.Count == 0)
                throw new DirectoryNotFoundException("没有检测到 Steam 库磁盘。");

            _suppressDriveSelection = true;
            DriveSelector.ItemsSource = drives;
            DriveSelector.SelectedItem = drives.FirstOrDefault(drive =>
                string.Equals(drive.RootPath, previousRoot, StringComparison.OrdinalIgnoreCase)) ?? drives[0];
            _suppressDriveSelection = false;
            await LoadDriveCoreAsync((SteamStorageDrive)DriveSelector.SelectedItem, cancellation);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
                SetScanningState(false, "扫描已取消。");
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_scanCancellation, cancellation))
                return;
            _scanCancellation = null;
            ScanStatusIcon.Glyph = "\uE783";
            ScanStatusIcon.Foreground = Brushes.IndianRed;
            SetScanningState(false, $"扫描失败：{ex.Message}");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task LoadSelectedDriveAsync()
    {
        if (DriveSelector.SelectedItem is not SteamStorageDrive drive)
            return;
        CancelCurrentScan(updateStatus: false);
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        SetScanningState(true, $"正在统计 {drive.DisplayName}…");
        try
        {
            await LoadDriveCoreAsync(drive, cancellation);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
                SetScanningState(false, "扫描已取消。");
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_scanCancellation, cancellation))
                return;
            _scanCancellation = null;
            ScanStatusIcon.Glyph = "\uE783";
            ScanStatusIcon.Foreground = Brushes.IndianRed;
            SetScanningState(false, $"扫描失败：{ex.Message}");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task LoadDriveCoreAsync(SteamStorageDrive drive, CancellationTokenSource cancellation)
    {
        var progress = new Progress<SteamDriveScanProgress>(value =>
        {
            if (!ReferenceEquals(_scanCancellation, cancellation))
                return;
            ScanStatusText.Text = string.IsNullOrWhiteSpace(value.Item)
                ? "正在统计磁盘内容…"
                : $"正在统计：{value.Item}";
            ScanProgressBar.Value = value.Total <= 0 ? 0 : value.Completed * 100d / value.Total;
        });
        var snapshot = await _storageService.ScanDriveAsync(drive.RootPath, progress, cancellation.Token);
        if (!ReferenceEquals(_scanCancellation, cancellation))
            return;

        _scanCancellation = null;
        _currentSnapshot = snapshot;
        _allItems.Clear();
        _allItems.AddRange(snapshot.Items);
        RenderDriveSummary(snapshot);
        ApplyFilterAndSort();
        var elapsedText = snapshot.Elapsed.TotalSeconds < 1
            ? $"{snapshot.Elapsed.TotalMilliseconds:0} ms"
            : $"{snapshot.Elapsed.TotalSeconds:0.0} 秒";
        ScanStatusIcon.Glyph = "\uE73E";
        ScanStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x79, 0xC4, 0x7B));
        SetScanningState(false, $"统计完成 · {snapshot.Items.Count:N0} 个项目 · 用时 {elapsedText}");
    }

    private void RenderDriveSummary(SteamDriveContentSnapshot snapshot)
    {
        DriveCapacityText.Text = snapshot.Drive.CapacityText;
        DriveLibraryCountText.Text = $"{snapshot.Drive.LibraryCount} 个 Steam 库";
        SelectedLibraryText.Text = string.Join("  ·  ", snapshot.LibraryPaths);

        GameSegmentColumn.Width = ToStar(snapshot.GameBytes);
        WorkshopSegmentColumn.Width = ToStar(snapshot.WorkshopBytes);
        ShaderSegmentColumn.Width = ToStar(snapshot.ShaderBytes);
        OtherSteamSegmentColumn.Width = ToStar(snapshot.OtherSteamBytes);
        OtherUsedSegmentColumn.Width = ToStar(snapshot.OtherUsedBytes);
        FreeSegmentColumn.Width = ToStar(snapshot.Drive.FreeBytes);

        GameLegendText.Text = $"游戏 {SteamStorageService.FormatBytes(snapshot.GameBytes)}";
        WorkshopLegendText.Text = $"创意工坊 {SteamStorageService.FormatBytes(snapshot.WorkshopBytes)}";
        ShaderLegendText.Text = $"着色器 {SteamStorageService.FormatBytes(snapshot.ShaderBytes)}";
        OtherSteamLegendText.Text = $"其他 Steam {SteamStorageService.FormatBytes(snapshot.OtherSteamBytes)}";
        OtherUsedLegendText.Text = $"其他已用 {SteamStorageService.FormatBytes(snapshot.OtherUsedBytes)}";
        FreeLegendText.Text = $"可用 {SteamStorageService.FormatBytes(snapshot.Drive.FreeBytes)}";
    }

    private static GridLength ToStar(long bytes) =>
        new(Math.Max(0d, bytes), GridUnitType.Star);

    private void ApplyFilterAndSort()
    {
        if (ContentItemsList is null || ItemCountText is null)
            return;
        var filter = (FilterComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var sort = (SortComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "size";
        IEnumerable<SteamDriveContentItem> query = _allItems;
        query = filter switch
        {
            "games" => query.Where(item => item.HasGame),
            "workshop" => query.Where(item => item.HasWorkshop),
            "shader" => query.Where(item => item.HasShader),
            "other" => query.Where(item => item.HasOther),
            _ => query
        };
        query = sort == "name"
            ? query.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            : query.OrderByDescending(item => item.TotalBytes).ThenBy(item => item.Name);
        var visibleItems = query.ToList();
        ContentItemsList.ItemsSource = visibleItems;
        ItemCountText.Text = $"项目  {visibleItems.Count:N0}";
        EmptyState.Visibility = visibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DriveSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDriveSelection || !IsLoaded || DriveSelector.SelectedItem is not SteamStorageDrive)
            return;
        await LoadSelectedDriveAsync();
    }

    private void FilterOrSort_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyFilterAndSort();

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCurrentScan(updateStatus: false);
        BackRequested?.Invoke();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null)
        {
            CancelCurrentScan(updateStatus: true);
            return;
        }
        await RefreshAsync();
    }

    private void OpenDriveMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSnapshot is null)
            return;
        var menu = new ContextMenu
        {
            PlacementTarget = OpenDriveMenuButton,
            Placement = PlacementMode.Bottom
        };
        foreach (var path in _currentSnapshot.LibraryPaths)
        {
            var item = new MenuItem { Header = $"打开 {path}", ToolTip = path };
            item.Click += (_, _) => OpenDirectory(path);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var refreshItem = new MenuItem { Header = "重新扫描当前磁盘" };
        refreshItem.Click += async (_, _) => await LoadSelectedDriveAsync();
        menu.Items.Add(refreshItem);
        OpenDriveMenuButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ManageItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SteamDriveContentItem item } button)
            return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        AddOpenMenuItem(menu, "打开主目录", item.PrimaryPath);
        if (item.HasGame && !string.Equals(item.GamePath, item.PrimaryPath, StringComparison.OrdinalIgnoreCase))
            AddOpenMenuItem(menu, "打开游戏本体目录", item.GamePath);
        if (item.HasWorkshop)
            AddOpenMenuItem(menu, "打开创意工坊目录", item.WorkshopPath);
        if (item.HasShader)
            AddOpenMenuItem(menu, "打开着色器目录", item.ShaderPath);

        if (item.HasGame || item.HasWorkshop || item.HasShader)
            menu.Items.Add(new Separator());
        if (item.HasGame)
            AddActionMenuItem(menu, "通过 Steam 卸载游戏", () => RequestItemActionAsync(item, "games"));
        if (item.HasWorkshop)
            AddActionMenuItem(menu, "删除这款游戏的工坊内容", () => RequestItemActionAsync(item, "workshop"));
        if (item.HasShader)
            AddActionMenuItem(menu, "清理这款游戏的着色器缓存", () => RequestItemActionAsync(item, "shader-cache"));

        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static void AddOpenMenuItem(ContextMenu menu, string label, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        var menuItem = new MenuItem { Header = label, ToolTip = path };
        menuItem.Click += (_, _) => OpenDirectory(path);
        menu.Items.Add(menuItem);
    }

    private static void AddActionMenuItem(ContextMenu menu, string label, Func<Task> action)
    {
        var menuItem = new MenuItem { Header = label };
        menuItem.Click += async (_, _) => await action();
        menu.Items.Add(menuItem);
    }

    private async Task RequestItemActionAsync(SteamDriveContentItem item, string categoryKey)
    {
        if (DetailDeleteRequested is null)
            return;
        var path = categoryKey switch
        {
            "games" => item.GamePath,
            "workshop" => item.WorkshopPath,
            "shader-cache" => item.ShaderPath,
            _ => null
        };
        var size = categoryKey switch
        {
            "games" => item.GameBytes,
            "workshop" => item.WorkshopBytes,
            "shader-cache" => item.ShaderBytes,
            _ => 0
        };
        if (string.IsNullOrWhiteSpace(path))
            return;
        var detailItem = new SteamStorageDetailItem
        {
            CategoryKey = categoryKey,
            AppId = item.AppId,
            Name = item.Name,
            Path = path,
            SizeBytes = size,
            FileCount = item.FileCount
        };
        if (await DetailDeleteRequested(detailItem))
            await LoadSelectedDriveAsync();
    }

    private void SetScanningState(bool isScanning, string message)
    {
        ScanButton.Content = isScanning ? "取消扫描" : "重新扫描";
        ScanProgressBar.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
        ScanStatusText.Text = message;
        if (isScanning)
        {
            ScanStatusIcon.Glyph = "\uE895";
            ScanStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x79, 0xC4, 0x7B));
            ScanProgressBar.Value = 0;
        }
    }

    private void CancelCurrentScan(bool updateStatus)
    {
        var cancellation = _scanCancellation;
        if (cancellation is null)
            return;
        _scanCancellation = null;
        cancellation.Cancel();
        ScanProgressBar.Visibility = Visibility.Collapsed;
        ScanButton.Content = "重新扫描";
        if (updateStatus)
            ScanStatusText.Text = "扫描已取消，可随时重新扫描。";
    }

    public void CancelActiveScan(bool updateStatus = false) => CancelCurrentScan(updateStatus);

    public void SetActionStatus(string message)
    {
        ScanStatusText.Text = message;
        ScanStatusIcon.Glyph = "\uE946";
        ScanStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x69, 0xA9, 0xD0));
    }

    private static void OpenDirectory(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
