using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class HomeView : UserControl
{
    private const double GameCardGap = 14;
    private const double MinimumGameCardWidth = 248;

    public static readonly DependencyProperty GameCardWidthProperty = DependencyProperty.Register(
        nameof(GameCardWidth), typeof(double), typeof(HomeView), new PropertyMetadata(286d));

    public static readonly DependencyProperty GameCardCoverHeightProperty = DependencyProperty.Register(
        nameof(GameCardCoverHeight), typeof(double), typeof(HomeView), new PropertyMetadata(160d));

    public double GameCardWidth
    {
        get => (double)GetValue(GameCardWidthProperty);
        private set => SetValue(GameCardWidthProperty, value);
    }

    public double GameCardCoverHeight
    {
        get => (double)GetValue(GameCardCoverHeightProperty);
        private set => SetValue(GameCardCoverHeightProperty, value);
    }

    private static readonly SolidColorBrush MenuHoverBrush = new(Color.FromRgb(0xDC, 0xE3, 0xE8));
    private static readonly SolidColorBrush MenuNormalTextBrush = new(Color.FromRgb(0xF0, 0xF2, 0xF4));
    private static readonly SolidColorBrush MenuSelectedTextBrush = new(Color.FromRgb(0x25, 0x31, 0x3B));
    private GameInfo? _activeMenuGame;
    private MainViewModel? _activeMenuViewModel;
    private Border? _cardSubmenuTrigger;
    private Button? _activeMenuButton;
    private readonly HashSet<int> _pendingToggleAppIds = new();
    private readonly GameDetailView _gameDetailView;
    public event Action<int>? OpenSaveVaultRequested;

    public HomeView(
        ISteamPathService steamPathService,
        ISteamDepotService steamDepotService,
        ISteamApiService steamApiService,
        ISteamAchievementService steamAchievementService,
        IDlcManagementService dlcManagementService,
        ISaveVaultService saveVaultService,
        ISaveAutoBackupService saveAutoBackupService,
        IGamePlayProfileService gamePlayProfileService,
        ISteamCloudPreferenceService steamCloudPreferenceService)
    {
        InitializeComponent();
        _gameDetailView = new GameDetailView(
            steamPathService,
            steamDepotService,
            steamApiService,
            steamAchievementService,
            dlcManagementService,
            saveVaultService,
            saveAutoBackupService,
            gamePlayProfileService,
            steamCloudPreferenceService);
        _gameDetailView.CloseRequested += GameDetailView_CloseRequested;
        _gameDetailView.OpenSaveVaultRequested += appId => OpenSaveVaultRequested?.Invoke(appId);
        GameDetailHost.Content = _gameDetailView;
        PreviewMouseLeftButtonDown += HomeView_PreviewMouseLeftButtonDown;
        Unloaded += HomeView_Unloaded;
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SearchText = e.QueryText ?? string.Empty;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            vm.SearchText = sender.Text ?? string.Empty;
        }
    }

    private void LibraryFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainViewModel vm || button.ContextMenu == null)
            return;

        AllGamesFilterItem.IsChecked = vm.SelectedDisableFilter == "全部游戏";
        EnabledGamesFilterItem.IsChecked = vm.SelectedDisableFilter == "已启用入库";
        DisabledGamesFilterItem.IsChecked = vm.SelectedDisableFilter == "已禁用入库";
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void LibraryFilterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string filter } && DataContext is MainViewModel vm)
        {
            vm.SelectedDisableFilter = filter;
            AllGamesFilterItem.IsChecked = filter == "全部游戏";
            EnabledGamesFilterItem.IsChecked = filter == "已启用入库";
            DisabledGamesFilterItem.IsChecked = filter == "已禁用入库";
        }
    }

    private void ViewModeContainer_Loaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, CardScrollViewer))
            UpdateGameCardLayout();
        if (sender is FrameworkElement fe)
        {
            fe.IsVisibleChanged -= ViewModeContainer_IsVisibleChanged;
            fe.IsVisibleChanged += ViewModeContainer_IsVisibleChanged;
        }
        QueueAppendGames(sender);
    }

    private void ViewModeContainer_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not FrameworkElement fe) return;
        fe.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        QueueAppendGames(sender);
    }

    private readonly HashSet<ScrollViewer> _queuedAppends = new();
    private readonly Dictionary<ScrollViewer, (object Source, double Extent, int Count)> _appendGates = new();

    private void GamesScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReferenceEquals(sender, CardScrollViewer))
            UpdateGameCardLayout();
        QueueAppendGames(sender);
    }

    private void GamesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(sender, CardScrollViewer) && e.ViewportWidthChange != 0)
            UpdateGameCardLayout();
        QueueAppendGames(sender);
    }

    private void UpdateGameCardLayout()
    {
        if (!CardScrollViewer.IsVisible)
            return;

        var viewportWidth = CardScrollViewer.ViewportWidth;
        if (viewportWidth <= 0 || double.IsNaN(viewportWidth) || double.IsInfinity(viewportWidth))
            viewportWidth = CardScrollViewer.ActualWidth;
        if (viewportWidth <= 0)
            return;

        // WPF layout rounding at 125% DPI can add several device-independent
        // pixels across item containers. Leave a small safety budget so the
        // final card never wraps into a new row at the minimum window width.
        var layoutWidth = Math.Max(1, Math.Floor(viewportWidth) - 8);
        var columns = Math.Max(1, (int)Math.Floor(layoutWidth / (MinimumGameCardWidth + GameCardGap)));
        var cardWidth = Math.Floor(layoutWidth / columns - GameCardGap);
        if (cardWidth < MinimumGameCardWidth && columns > 1)
        {
            columns--;
            cardWidth = Math.Floor(layoutWidth / columns - GameCardGap);
        }

        // Both the card and its 16:9 cover respond to the pane's current width.
        // A 4-column compact layout becomes 3 wider cards when the pane opens.
        CardItemsControl.Width = layoutWidth;
        GameCardWidth = Math.Max(1, cardWidth);
        GameCardCoverHeight = Math.Round(GameCardWidth * 9 / 16);
    }

    private void QueueAppendGames(object sender)
    {
        if (sender is not ScrollViewer sv || !sv.IsVisible || !_queuedAppends.Add(sv)) return;
        // Let WPF finish the current layout before reading the extent or adding another batch.
        Dispatcher.BeginInvoke(() =>
        {
            _queuedAppends.Remove(sv);
            if (!IsLoaded || !sv.IsVisible || sv.ViewportHeight <= 0 || DataContext is not MainViewModel vm) return;
            if (sv.ScrollableHeight > 0 && sv.ScrollableHeight - sv.VerticalOffset > 600) return;
            _appendGates.TryGetValue(sv, out var gate);
            // Collection identity resets the gate even when a new filter has the same count.
            if (ReferenceEquals(gate.Source, vm.Games) && gate.Count == vm.Games.Count && sv.ExtentHeight <= gate.Extent) return;
            vm.LoadMoreGames();
            _appendGates[sv] = (vm.Games, sv.ExtentHeight, vm.Games.Count);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OpenLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameInfo game })
            OpenUrl($"steam://nav/games/details/{game.AppId}");
    }

    private async void CardImportToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: GameInfo game } toggle ||
            DataContext is not MainViewModel vm ||
            toggle.IsChecked == !game.IsDisabled)
        {
            return;
        }

        if (!_pendingToggleAppIds.Add(game.AppId))
        {
            toggle.IsChecked = !game.IsDisabled;
            return;
        }

        try
        {
            await vm.ToggleGameDisableCommand.ExecuteAsync(game);
        }
        catch (Exception ex)
        {
            toggle.IsChecked = !game.IsDisabled;
            vm.StatusMessage = $"切换入库状态失败：{ex.Message}";
        }
        finally
        {
            _pendingToggleAppIds.Remove(game.AppId);
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is GameInfo game && DataContext is MainViewModel vm)
        {
            _activeMenuGame = game;
            _activeMenuViewModel = vm;
            _activeMenuButton = btn;
            _ = HideCardMenuAsync();
            GameDetailHost.Visibility = Visibility.Visible;
            GameDetailHost.Opacity = 0;
            GameDetailHost.RenderTransform = new TranslateTransform(26, 0);
            _gameDetailView.ShowGame(game, vm);

            GameDetailHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            ((TranslateTransform)GameDetailHost.RenderTransform).BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    private async void GameDetailView_CloseRequested(object? sender, EventArgs e)
    {
        if (GameDetailHost.Visibility != Visibility.Visible) return;

        var opacity = new DoubleAnimation(GameDetailHost.Opacity, 0, TimeSpan.FromMilliseconds(120));
        GameDetailHost.BeginAnimation(OpacityProperty, opacity);
        if (GameDetailHost.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            GameDetailHost.RenderTransform = transform;
        }
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(120)));
        await Task.Delay(120);
        _gameDetailView.CloseGame();
        GameDetailHost.Visibility = Visibility.Collapsed;
        GameDetailHost.BeginAnimation(OpacityProperty, null);
        GameDetailHost.Opacity = 1;
    }

    private void HomeView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (GameDetailHost.Visibility != Visibility.Visible) return;
        _gameDetailView.CloseGame();
        GameDetailHost.Visibility = Visibility.Collapsed;
    }

    private void HomeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CardMenuPanel.Visibility != Visibility.Visible) return;

        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source == CardMenuPanel || source == CardSubmenuPanel || source == _activeMenuButton)
                return;
            source = VisualTreeHelper.GetParent(source);
        }
        _ = HideCardMenuAsync();
    }

    private void PositionCardMenu(Button btn)
    {
        CardMenuPanel.UpdateLayout();
        CardMenuPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var panelWidth = GetMeasuredWidth(CardMenuPanel, 190);
        var panelHeight = GetMeasuredHeight(CardMenuPanel, 180);
        var canvasWidth = CardMenuCanvas.ActualWidth;
        var canvasHeight = CardMenuCanvas.ActualHeight;
        var anchor = btn.TranslatePoint(new Point(0, 0), CardMenuCanvas);

        // Steam 式菜单优先贴着齿轮按钮向上展开，并与按钮右侧对齐。
        var left = anchor.X + btn.ActualWidth - panelWidth;
        left = Math.Clamp(left, 8, Math.Max(8, canvasWidth - panelWidth - 8));

        var top = anchor.Y - panelHeight - 2;
        if (top < 8)
            top = anchor.Y + btn.ActualHeight + 2;
        top = Math.Clamp(top, 8, Math.Max(8, canvasHeight - panelHeight - 8));

        Canvas.SetLeft(CardMenuPanel, left);
        Canvas.SetTop(CardMenuPanel, top);
    }

    private void PositionCardSubmenu(Border trigger)
    {
        CardSubmenuPanel.UpdateLayout();
        CardSubmenuPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var point = trigger.TranslatePoint(new Point(trigger.ActualWidth + 1, 0), CardMenuCanvas);
        var submenuWidth = GetMeasuredWidth(CardSubmenuPanel, 226);
        var submenuHeight = GetMeasuredHeight(CardSubmenuPanel, 120);
        var canvasWidth = CardMenuCanvas.ActualWidth;
        var canvasHeight = CardMenuCanvas.ActualHeight;
        var menuLeft = Canvas.GetLeft(CardMenuPanel);
        var menuWidth = GetMeasuredWidth(CardMenuPanel, 190);

        var rightLeft = menuLeft + menuWidth + 1;
        var leftLeft = menuLeft - submenuWidth - 1;

        double left;
        if (rightLeft + submenuWidth <= canvasWidth - 8)
            left = rightLeft;
        else if (leftLeft >= 8)
            left = leftLeft;
        else
            left = Math.Clamp(point.X, 8, Math.Max(8, canvasWidth - submenuWidth - 8));

        var top = point.Y;
        top = Math.Clamp(top, 8, Math.Max(8, canvasHeight - submenuHeight - 8));

        Canvas.SetLeft(CardSubmenuPanel, left);
        Canvas.SetTop(CardSubmenuPanel, top);
    }

    private async void CardMenuItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: CardMenuItem item } || item.IsSeparator || item.HasSubmenu) return;
        if (_activeMenuGame == null || _activeMenuViewModel == null) return;

        await HideCardMenuAsync();
        switch (item.Action)
        {
            case "refresh":
                await _activeMenuViewModel.RefreshSingleGameCommand.ExecuteAsync(_activeMenuGame);
                break;
        }
    }

    private void CardMenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        SetMenuItemHighlighted(border, true);

        if (_cardSubmenuTrigger != null && _cardSubmenuTrigger != border)
        {
            SetMenuItemHighlighted(_cardSubmenuTrigger, false);
            _cardSubmenuTrigger = null;
            _ = HidePanelAsync(CardSubmenuPanel);
        }

        if (border.Tag is not CardMenuItem item || !item.HasSubmenu || _activeMenuGame == null) return;

        _cardSubmenuTrigger = border;
        CardSubmenuList.ItemsSource = item.Action switch
        {
            "manage" => BuildManageSubmenu(_activeMenuGame),
            "pin" => BuildPinSubmenu(_activeMenuGame),
            _ => BuildInfoSubmenu()
        };
        CardSubmenuPanel.Visibility = Visibility.Hidden;
        PositionCardSubmenu(border);
        if (CardSubmenuPanel.Visibility != Visibility.Visible)
            ShowPanel(CardSubmenuPanel);
    }

    private void CardMenuItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border &&
            (border != _cardSubmenuTrigger || CardSubmenuPanel.Visibility != Visibility.Visible))
            SetMenuItemHighlighted(border, false);
        if (_cardSubmenuTrigger != null)
            _ = DelayedHideCardSubmenuAsync();
    }

    private void CardSubmenuPanel_MouseEnter(object sender, MouseEventArgs e) { }
    private void CardSubmenuPanel_MouseLeave(object sender, MouseEventArgs e) => _ = DelayedHideCardSubmenuAsync();
    private void CardMenuPanel_MouseLeave(object sender, MouseEventArgs e) => _ = DelayedHideCardMenuAsync();

    private void CardSubmenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            SetMenuItemHighlighted(border, true);
    }

    private void CardSubmenuItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            SetMenuItemHighlighted(border, false);
    }

    private async void CardSubmenuItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: CardMenuItem item } || _activeMenuGame == null || _activeMenuViewModel == null) return;
        await HideCardMenuAsync();

        switch (item.Action)
        {
            case "batchmanage":
                _activeMenuViewModel.IsSelectionMode = true;
                _activeMenuGame.IsSelected = true;
                _activeMenuViewModel.NotifySelectionChanged();
                break;
            case "disable":
            case "enable":
                await _activeMenuViewModel.ToggleGameDisableCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "edit":
                _activeMenuViewModel.EditGameCommand.Execute(_activeMenuGame);
                break;
            case "delete":
                await _activeMenuViewModel.DeleteGameCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "unpin":
                await _activeMenuViewModel.UnpinGameCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "pin-latest":
                await _activeMenuViewModel.PinToLatestCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "pin-current":
                await _activeMenuViewModel.PinToCurrentCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "steamdb":
                OpenUrl($"https://steamdb.info/app/{_activeMenuGame.AppId}/");
                break;
            case "store":
                OpenUrl($"https://store.steampowered.com/app/{_activeMenuGame.AppId}/");
                break;
            case "dlc-query":
                await _activeMenuViewModel.QueryDlcCommand.ExecuteAsync(_activeMenuGame);
                break;
        }
    }

    private async Task DelayedHideCardSubmenuAsync()
    {
        await Task.Delay(200);
        if (CardSubmenuPanel.IsMouseOver || (_cardSubmenuTrigger?.IsMouseOver ?? false)) return;
        await HidePanelAsync(CardSubmenuPanel);
        if (_cardSubmenuTrigger != null)
            SetMenuItemHighlighted(_cardSubmenuTrigger, false);
        _cardSubmenuTrigger = null;
    }

    private async Task DelayedHideCardMenuAsync()
    {
        await Task.Delay(200);
        if (CardMenuPanel.IsMouseOver || CardSubmenuPanel.IsMouseOver) return;
        await HideCardMenuAsync();
    }

    private async Task HideCardMenuAsync()
    {
        await HidePanelAsync(CardSubmenuPanel);
        await HidePanelAsync(CardMenuPanel);
        if (_cardSubmenuTrigger != null)
            SetMenuItemHighlighted(_cardSubmenuTrigger, false);
        _activeMenuButton = null;
        _cardSubmenuTrigger = null;
    }

    private static void ShowPanel(Border panel)
    {
        panel.Visibility = Visibility.Visible;
        panel.Opacity = 0;
        var opacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(85))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        panel.BeginAnimation(OpacityProperty, opacity);
    }

    private static double GetMeasuredWidth(FrameworkElement element, double fallback)
    {
        if (element.DesiredSize.Width > 0) return element.DesiredSize.Width;
        if (element.ActualWidth > 0) return element.ActualWidth;
        return fallback;
    }

    private static double GetMeasuredHeight(FrameworkElement element, double fallback)
    {
        if (element.DesiredSize.Height > 0) return element.DesiredSize.Height;
        if (element.ActualHeight > 0) return element.ActualHeight;
        return fallback;
    }

    private static async Task HidePanelAsync(Border panel)
    {
        if (panel.Visibility != Visibility.Visible) return;

        var opacity = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(65))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        panel.BeginAnimation(OpacityProperty, opacity);
        await Task.Delay(65);
        panel.Visibility = Visibility.Collapsed;
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = 1;
    }

    private static CardMenuItem[] BuildManageSubmenu(GameInfo game) =>
    [
        new CardMenuItem(game.IsDisabled ? "enable" : "disable", game.IsDisabled ? "启用入库" : "禁用入库"),
        CardMenuItem.Separator(),
        new CardMenuItem("edit", "编辑 Lua"),
        new CardMenuItem("delete", "删除 Lua"),
        CardMenuItem.Separator(),
        new CardMenuItem("batchmanage", "批量管理")
    ];

    private CardMenuItem[] BuildPinSubmenu(GameInfo game) => game.IsManifestPinned
        ? [new CardMenuItem("unpin", "取消版本固定")]
        : [new CardMenuItem("pin-latest", "固定到游戏最新版本"), CardMenuItem.Separator(), new CardMenuItem("pin-current", "固定到当前已安装版本")];

    private static CardMenuItem[] BuildInfoSubmenu() =>
        [new CardMenuItem("steamdb", "SteamDB页面"), CardMenuItem.Separator(), new CardMenuItem("store", "Steam商店页面"), CardMenuItem.Separator(), new CardMenuItem("dlc-query", "清单DLC入库查询")];

    private void UpdateCardMenuBackground()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x43, 0x4D, 0x5D));
        CardMenuPanel.Background = brush;
        CardSubmenuPanel.Background = brush;
    }

    private static void SetMenuItemHighlighted(Border border, bool highlighted)
    {
        border.Background = highlighted ? MenuHoverBrush : Brushes.Transparent;
        SetDescendantForeground(border, highlighted ? MenuSelectedTextBrush : MenuNormalTextBrush);
    }

    private static void SetDescendantForeground(DependencyObject parent, Brush brush)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock textBlock)
                textBlock.Foreground = brush;
            else if (child is Control control)
                control.Foreground = brush;
            SetDescendantForeground(child, brush);
        }
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private sealed record CardMenuItem(string Action, string Header, bool HasSubmenu = false, bool IsSeparator = false)
    {
        public static CardMenuItem Separator() => new("separator", string.Empty, IsSeparator: true);
    }

    private void CheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.NotifySelectionChanged();
    }

    private void CheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.NotifySelectionChanged();
    }
}
