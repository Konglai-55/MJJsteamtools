using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class HomeView : UserControl
{
    private const double GameCardGap = 14;
    private const double MinimumGameCardWidth = 244;

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
    private int _detailTransitionVersion;
    private readonly GameDetailView _gameDetailView;
    private FrameworkElement? _transitionCardRoot;
    private Image? _transitionSourceImage;
    private Rect _transitionSourceRect;
    private bool _sharedDetailTransitionActive;
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
        if (AppMotion.Enabled)
            fe.BeginAnimation(OpacityProperty,
                AppMotion.To(1, AppMotion.Pace.Content, from: 0));
        else
        {
            fe.BeginAnimation(OpacityProperty, null);
            fe.Opacity = 1;
        }
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

        // ScrollViewer.ViewportWidth can retain the pre-toggle extent because the
        // ItemsControl has an explicit Width. Reading it here creates a loop:
        // the old item width keeps the old viewport width, even though the pane
        // and the ScrollViewer itself have already grown. Use the arranged
        // control width instead, less the visible vertical scrollbar.
        var viewportWidth = CardScrollViewer.ActualWidth;
        if (CardScrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            viewportWidth -= SystemParameters.VerticalScrollBarWidth;
        if (viewportWidth <= 0)
            return;

        // WPF layout rounding at 125% DPI can add several device-independent
        // pixels across item containers. Leave a small safety budget so the
        // final card never wraps into a new row at the minimum window width.
        var layoutWidth = Math.Max(1, Math.Floor(viewportWidth) - 8);
        var columns = Math.Max(1, (int)Math.Floor(layoutWidth / (MinimumGameCardWidth + GameCardGap)));
        // Keep a per-card rounding allowance as WPF can round each item
        // container outward at non-integer display scale factors.
        var cardWidth = Math.Floor(layoutWidth / columns - GameCardGap) - 2;
        if (cardWidth < MinimumGameCardWidth && columns > 1)
        {
            columns--;
            cardWidth = Math.Floor(layoutWidth / columns - GameCardGap) - 2;
        }

        // Both the card and its 16:9 cover respond to the pane's current width.
        // A 4-column compact layout becomes 3 wider cards when the pane opens.
        CardItemsControl.Width = layoutWidth;
        GameCardWidth = Math.Max(1, cardWidth);
        GameCardCoverHeight = Math.Round(GameCardWidth * 9 / 16);
    }

    /// <summary>
    /// Re-measures the card viewport after the shell pane changes width. The
    /// NavigationView template can animate its content bounds without raising
    /// a SizeChanged event, so an explicit pass prevents stale column counts
    /// until the user manually resizes the window.
    /// </summary>
    public void RefreshResponsiveLayout()
    {
        if (!IsLoaded || !CardScrollViewer.IsVisible)
            return;

        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
        CardScrollViewer.InvalidateMeasure();
        CardScrollViewer.InvalidateArrange();
        CardScrollViewer.UpdateLayout();
        UpdateGameCardLayout();
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

    private void FavoriteGameButton_Click(object sender, RoutedEventArgs e)
    {
        // The command runs synchronously before Click reaches this handler. Only
        // play the "add to favorites" flight; removing a favorite stays instant.
        if (sender is not Button button || button.DataContext is not GameInfo game || game.IsFavorite)
            return;

        // Capture both endpoints in the same visual tree before the command
        // changes the favorite count/layout. This keeps the flight tied to the
        // exact click-time card and favorites target positions.
        PlayFavoriteDock(button);
    }

    /// <summary>
    /// A deliberately simple shared-element transition: capture both rectangles
    /// at click time, then move one cached cover directly between their centers.
    /// No arc, layout animation, or shadow effect can introduce visual drift.
    /// </summary>
    private void PlayFavoriteDock(Button sourceButton)
    {
        if (!AppMotion.Enabled || FavoriteGamesTargetIcon.Visibility != Visibility.Visible)
            return;

        var cardRoot = FindCardRoot(sourceButton);
        var sourceImage = cardRoot is null ? null : FindNamedImage(cardRoot, "CoverImage");
        var sourceRect = Rect.Empty;
        if (sourceImage is not null && sourceImage.IsLoaded && sourceImage.ActualWidth > 0 && sourceImage.ActualHeight > 0)
        {
            var sourcePoint = sourceImage.TranslatePoint(new Point(0, 0), FavoriteAnimationLayer);
            sourceRect = new Rect(sourcePoint, new Size(sourceImage.ActualWidth, sourceImage.ActualHeight));
        }
        var targetPoint = FavoriteGamesTargetIcon.TranslatePoint(new Point(0, 0), FavoriteAnimationLayer);
        var targetRect = new Rect(targetPoint, new Size(FavoriteGamesTargetIcon.ActualWidth, FavoriteGamesTargetIcon.ActualHeight));
        if (sourceImage?.Source is not ImageSource imageSource ||
            !IsValidRect(sourceRect) || !IsValidRect(targetRect))
            return;

        var targetCenter = FavoriteGamesTargetIcon.TranslatePoint(
            new Point(FavoriteGamesTargetIcon.ActualWidth * 0.5, FavoriteGamesTargetIcon.ActualHeight * 0.5),
            FavoriteAnimationLayer);
        var targetWidth = Math.Max(22, FavoriteGamesTargetIcon.ActualWidth + 7);
        var targetHeight = Math.Max(22, FavoriteGamesTargetIcon.ActualHeight + 7);
        var flyer = new Border
        {
            Width = sourceRect.Width,
            Height = sourceRect.Height,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            CacheMode = new BitmapCache(),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Opacity = 0,
            Background = new ImageBrush(imageSource)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            }
        };
        Canvas.SetLeft(flyer, sourceRect.Left);
        Canvas.SetTop(flyer, sourceRect.Top);
        FavoriteAnimationLayer.Children.Add(flyer);

        var duration = TimeSpan.FromMilliseconds(320);
        var targetScale = Math.Clamp(Math.Min(targetWidth / sourceRect.Width, targetHeight / sourceRect.Height), 0.09, 0.28);
        var targetLeft = targetCenter.X - sourceRect.Width * targetScale * 0.5;
        var targetTop = targetCenter.Y - sourceRect.Height * targetScale * 0.5;
        var easing = AppMotion.Ease(AppMotion.Curve.InOut);
        var left = new DoubleAnimation(sourceRect.Left, targetLeft, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var top = new DoubleAnimation(sourceRect.Top, targetTop, duration)
        {
            EasingFunction = AppMotion.Ease(AppMotion.Curve.InOut),
            FillBehavior = FillBehavior.HoldEnd
        };
        var scale = new DoubleAnimation(1, targetScale, duration)
        {
            EasingFunction = AppMotion.Ease(AppMotion.Curve.Settle),
            FillBehavior = FillBehavior.HoldEnd
        };
        var opacity = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)), AppMotion.Ease(AppMotion.Curve.Enter)));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration), AppMotion.Ease(AppMotion.Curve.Exit)));
        opacity.Completed += (_, _) => FavoriteAnimationLayer.Children.Remove(flyer);

        flyer.BeginAnimation(Canvas.LeftProperty, left);
        flyer.BeginAnimation(Canvas.TopProperty, top);
        if (flyer.RenderTransform is ScaleTransform scaleTransform)
        {
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scale.Clone());
        }
        flyer.BeginAnimation(UIElement.OpacityProperty, opacity);
        PulseFavoritesTarget();
    }

    private void PulseFavoritesTarget()
    {
        var transform = new ScaleTransform(1, 1);
        FavoriteGamesToggle.RenderTransformOrigin = new Point(0.5, 0.5);
        FavoriteGamesToggle.RenderTransform = transform;
        var pulse = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        pulse.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.045, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), AppMotion.Ease(AppMotion.Curve.Enter)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), AppMotion.Ease(AppMotion.Curve.Settle)));
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse.Clone());
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is GameInfo game && DataContext is MainViewModel vm)
        {
            _activeMenuGame = game;
            _activeMenuViewModel = vm;
            _activeMenuButton = btn;
            _ = OpenGameDetailWithTransitionAsync(game, vm, btn);
        }
    }

    private async void GameDetailView_CloseRequested(object? sender, EventArgs e)
    {
        if (GameDetailHost.Visibility != Visibility.Visible) return;

        var version = ++_detailTransitionVersion;
        if (!_sharedDetailTransitionActive || _transitionSourceImage is null || !IsValidRect(_transitionSourceRect))
        {
            await CloseGameDetailImmediatelyAsync(version);
            return;
        }

        var targetRect = GetElementRect(_gameDetailView.SharedCoverImage, DetailTransitionLayer);
        if (!IsValidRect(targetRect))
        {
            await CloseGameDetailImmediatelyAsync(version);
            return;
        }

        _gameDetailView.PrepareForSharedTransitionExit();
        DetailTransitionCover.Source = _gameDetailView.SharedCoverImage.Source ?? _transitionSourceImage.Source;
        SetTransitionFrame(targetRect);
        DetailTransitionLayer.Visibility = Visibility.Visible;
        await Task.Delay(AppMotion.Enabled ? 42 : 0);
        await AnimateTransitionFrameAsync(targetRect, _transitionSourceRect, opening: false);
        if (version != _detailTransitionVersion) return;

        _gameDetailView.CloseGame();
        GameDetailHost.Visibility = Visibility.Collapsed;
        DetailTransitionLayer.Visibility = Visibility.Collapsed;
        _sharedDetailTransitionActive = false;
    }

    private async Task OpenGameDetailWithTransitionAsync(GameInfo game, MainViewModel vm, Button sourceButton)
    {
        await HideCardMenuAsync();
        var card = FindCardRoot(sourceButton);
        var sourceImage = card is not null
            ? FindNamedImage(card, "CoverImage")
            : null;
        var sourceRect = sourceImage is null ? Rect.Empty : GetElementRect(sourceImage, DetailTransitionLayer);
        if (sourceImage is null || sourceImage.Source is null || !IsValidRect(sourceRect))
        {
            ShowGameDetailImmediately(game, vm);
            return;
        }

        var version = ++_detailTransitionVersion;
        _transitionCardRoot = card;
        _transitionSourceImage = sourceImage;
        _transitionSourceRect = sourceRect;
        _sharedDetailTransitionActive = true;

        DetailTransitionCover.Source = sourceImage.Source;
        SetTransitionFrame(sourceRect);
        DetailTransitionLayer.Visibility = Visibility.Visible;
        _gameDetailView.PrepareForSharedTransition();
        GameDetailHost.Visibility = Visibility.Visible;
        GameDetailHost.BeginAnimation(OpacityProperty, null);
        GameDetailHost.Opacity = 1;
        _gameDetailView.ShowGame(game, vm);

        await Dispatcher.InvokeAsync(() => UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
        if (version != _detailTransitionVersion) return;
        var targetRect = GetElementRect(_gameDetailView.SharedCoverImage, DetailTransitionLayer);
        if (!IsValidRect(targetRect))
        {
            DetailTransitionLayer.Visibility = Visibility.Collapsed;
            _gameDetailView.CompleteSharedTransition();
            return;
        }

        _gameDetailView.PlaySharedDetailEntrance();
        await AnimateTransitionFrameAsync(sourceRect, targetRect, opening: true);
        if (version != _detailTransitionVersion) return;
        DetailTransitionLayer.Visibility = Visibility.Collapsed;
        _gameDetailView.CompleteSharedTransition();
    }

    private void ShowGameDetailImmediately(GameInfo game, MainViewModel vm)
    {
        _detailTransitionVersion++;
        _sharedDetailTransitionActive = false;
        DetailTransitionLayer.Visibility = Visibility.Collapsed;
        GameDetailHost.Visibility = Visibility.Visible;
        _gameDetailView.ShowGame(game, vm);
        MotionBehavior.PlayEntrance(GameDetailHost, fromX: 24, fromY: 6, pace: AppMotion.Pace.Page);
    }

    private async Task CloseGameDetailImmediatelyAsync(int version)
    {
        await Task.Delay(MotionBehavior.PlayExit(GameDetailHost, toX: 18));
        if (version != _detailTransitionVersion) return;
        _gameDetailView.CloseGame();
        GameDetailHost.Visibility = Visibility.Collapsed;
        GameDetailHost.BeginAnimation(OpacityProperty, null);
        GameDetailHost.Opacity = 1;
        DetailTransitionLayer.Visibility = Visibility.Collapsed;
        _sharedDetailTransitionActive = false;
    }

    private async Task AnimateTransitionFrameAsync(Rect from, Rect to, bool opening)
    {
        var duration = AppMotion.Enabled ? TimeSpan.FromMilliseconds(560) : TimeSpan.Zero;
        SetTransitionFrame(from);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        DetailTransitionFrame.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(from.Left, to.Left, duration) { EasingFunction = easing });
        DetailTransitionFrame.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(from.Top, to.Top, duration) { EasingFunction = easing });
        DetailTransitionFrame.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(from.Width, to.Width, duration) { EasingFunction = easing });
        DetailTransitionFrame.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(from.Height, to.Height, duration) { EasingFunction = easing });

        if (DetailTransitionFrame.RenderTransform is TransformGroup group && group.Children.Count >= 2 &&
            group.Children[0] is ScaleTransform scale && group.Children[1] is RotateTransform rotate)
        {
            scale.ScaleX = scale.ScaleY = opening ? 0.965 : 0.985;
            rotate.Angle = opening ? -0.55 : 0.45;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 1, duration) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 1, duration) { EasingFunction = easing });
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(rotate.Angle, 0, duration) { EasingFunction = easing });
        }

        var glow = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration), FillBehavior = FillBehavior.HoldEnd };
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(opening ? 0.42 : 0.30,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.32)), easing));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(duration), easing));
        DetailTransitionGlow.BeginAnimation(OpacityProperty, glow);
        await Task.Delay(duration);
        SetTransitionFrame(to);
    }

    private void SetTransitionFrame(Rect rect)
    {
        DetailTransitionFrame.BeginAnimation(Canvas.LeftProperty, null);
        DetailTransitionFrame.BeginAnimation(Canvas.TopProperty, null);
        DetailTransitionFrame.BeginAnimation(FrameworkElement.WidthProperty, null);
        DetailTransitionFrame.BeginAnimation(FrameworkElement.HeightProperty, null);
        DetailTransitionFrame.Width = Math.Max(1, rect.Width);
        DetailTransitionFrame.Height = Math.Max(1, rect.Height);
        Canvas.SetLeft(DetailTransitionFrame, rect.Left);
        Canvas.SetTop(DetailTransitionFrame, rect.Top);
    }

    private static Rect GetElementRect(FrameworkElement element, Visual ancestor)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return Rect.Empty;
        try
        {
            var origin = element.TransformToAncestor(ancestor).Transform(new Point(0, 0));
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private static bool IsValidRect(Rect rect) =>
        !rect.IsEmpty && rect.Width > 1 && rect.Height > 1 &&
        double.IsFinite(rect.Left) && double.IsFinite(rect.Top);

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static FrameworkElement? FindCardRoot(DependencyObject? child)
    {
        while (child is not null)
        {
            if (child is FrameworkElement element && element.Tag is GameInfo)
                return element;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static Image? FindNamedImage(DependencyObject root, string name)
    {
        if (root is Image image && image.Name == name) return image;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindNamedImage(VisualTreeHelper.GetChild(root, i), name);
            if (result is not null) return result;
        }
        return null;
    }

    private void HomeView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (GameDetailHost.Visibility != Visibility.Visible) return;
        _detailTransitionVersion++;
        _gameDetailView.CloseGame();
        GameDetailHost.Visibility = Visibility.Collapsed;
        DetailTransitionLayer.Visibility = Visibility.Collapsed;
        _sharedDetailTransitionActive = false;
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
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = 1;
        if (AppMotion.Enabled)
            panel.BeginAnimation(OpacityProperty,
                AppMotion.To(1, AppMotion.Pace.Press, from: 0));
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

        if (AppMotion.Enabled)
            panel.BeginAnimation(OpacityProperty,
                AppMotion.To(0, AppMotion.Pace.Press, AppMotion.Curve.Exit));
        await Task.Delay(AppMotion.EffectiveDuration(AppMotion.Pace.Press));
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
