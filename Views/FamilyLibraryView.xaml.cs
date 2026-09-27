using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public partial class FamilyLibraryView : UserControl, INotifyPropertyChanged
{
    private const string FamilyManagementUrl = "https://store.steampowered.com/account/familymanagement";
    private readonly IFamilyLibraryService _familyLibraryService;
    private readonly List<FamilyGameInfo> _allGames = [];
    private readonly DispatcherTimer _runtimeTimer;
    private CancellationTokenSource? _loadCts;
    private bool _hasLoaded;
    private bool _isBusy;
    private bool _isEmpty = true;
    private bool _requiresConnection = true;
    private string _dataScopeText = "等待连接 Steam 家庭";
    private string _gameCountText = "0 款";

    public ObservableCollection<FamilyMemberInfo> Members { get; } = [];
    public ObservableCollection<FamilyGameInfo> VisibleGames { get; } = [];
    public Func<FamilyGameInfo, Task>? ImportRequested { get; set; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetField(ref _isEmpty, value);
    }

    public bool RequiresConnection
    {
        get => _requiresConnection;
        private set => SetField(ref _requiresConnection, value);
    }

    public string DataScopeText
    {
        get => _dataScopeText;
        private set => SetField(ref _dataScopeText, value);
    }

    public string GameCountText
    {
        get => _gameCountText;
        private set => SetField(ref _gameCountText, value);
    }

    public FamilyLibraryView(IFamilyLibraryService familyLibraryService)
    {
        InitializeComponent();
        _familyLibraryService = familyLibraryService;
        DataContext = this;
        _runtimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _runtimeTimer.Tick += RuntimeTimer_Tick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void FamilyLibraryView_Loaded(object sender, RoutedEventArgs e)
    {
        _runtimeTimer.Start();
        if (!_hasLoaded)
            await RefreshAsync();
    }

    private void FamilyLibraryView_Unloaded(object sender, RoutedEventArgs e)
    {
        _runtimeTimer.Stop();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        IsBusy = true;

        try
        {
            var snapshot = await _familyLibraryService.LoadAsync(token);
            Members.Clear();
            foreach (var member in snapshot.Members) Members.Add(member);
            _allGames.Clear();
            _allGames.AddRange(snapshot.Games);
            DataScopeText = snapshot.DataScopeText;
            RequiresConnection = snapshot.RequiresConnection;
            _hasLoaded = true;
            ApplyFilter();
            IsBusy = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DataScopeText = $"读取失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RuntimeTimer_Tick(object? sender, EventArgs e)
    {
        if (_allGames.Count == 0) return;
        var running = _familyLibraryService.GetLocallyRunningAppIds(_allGames);
        foreach (var game in _allGames)
        {
            game.IsRunningLocally = running.Contains(game.AppId);
            game.Availability = game.IsRunningLocally
                ? "本机正在运行"
                : "本机未占用 · 远程状态由 Steam 确认";
        }
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        var filter = (FilterBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        var filtered = _allGames.Where(game =>
        {
            var matchesQuery = string.IsNullOrWhiteSpace(query) ||
                               game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               game.AppId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               game.OwnerDisplay.Contains(query, StringComparison.OrdinalIgnoreCase);
            var matchesFilter = filter switch
            {
                "Installed" => game.IsInstalled,
                "Shareable" => game.ShareStatusKind == "Available",
                "Running" => game.IsRunningLocally,
                _ => true
            };
            return matchesQuery && matchesFilter;
        }).ToList();

        VisibleGames.Clear();
        foreach (var game in filtered) VisibleGames.Add(game);
        IsEmpty = VisibleGames.Count == 0 && !IsBusy && !RequiresConnection;
        GameCountText = $"{VisibleGames.Count} / {_allGames.Count} 款";
    }

    private async void ConnectSteamFamily_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SteamFamilyConnectWindow
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.AccessToken))
        {
            _familyLibraryService.SetAccessToken(dialog.AccessToken);
            await RefreshAsync();
        }
    }

    private void OpenFamilyManagement_Click(object sender, RoutedEventArgs e)
    {
        OpenExternal(FamilyManagementUrl);
    }

    private async void ImportGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FamilyGameInfo game } && ImportRequested != null)
            await ImportRequested(game);
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FamilyGameInfo game })
            OpenExternal($"steam://nav/games/details/{game.AppId}");
    }

    private static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
