using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SteamLuaManager.Controls;

/// <summary>
/// Reusable motion gestures. AppMotion owns timing and curves; this behavior
/// owns transforms, so pages can reuse the same language without fighting
/// each other's RenderTransform or ignoring reduced-motion preferences.
/// </summary>
public static class MotionBehavior
{
    public static readonly DependencyProperty IsButtonFeedbackEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsButtonFeedbackEnabled",
            typeof(bool),
            typeof(MotionBehavior),
            new PropertyMetadata(false, OnButtonFeedbackChanged));

    public static readonly DependencyProperty IsHoverLiftEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsHoverLiftEnabled",
            typeof(bool),
            typeof(MotionBehavior),
            new PropertyMetadata(false, OnHoverLiftChanged));

    public static readonly DependencyProperty IsEntranceEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEntranceEnabled",
            typeof(bool),
            typeof(MotionBehavior),
            new PropertyMetadata(false, OnEntranceChanged));

    public static readonly DependencyProperty IsContentTransitionEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsContentTransitionEnabled",
            typeof(bool),
            typeof(MotionBehavior),
            new PropertyMetadata(false, OnContentTransitionChanged));

    public static readonly DependencyProperty IsRevealEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsRevealEnabled",
            typeof(bool),
            typeof(MotionBehavior),
            new PropertyMetadata(false, OnRevealChanged));

    public static readonly DependencyProperty EntranceOrderProperty =
        DependencyProperty.RegisterAttached(
            "EntranceOrder",
            typeof(int),
            typeof(MotionBehavior),
            new PropertyMetadata(0));

    public static readonly DependencyProperty IsEmphasisEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEmphasisEnabled",
            typeof(bool),
            typeof(MotionBehavior),
            new PropertyMetadata(false));

    private static readonly DependencyProperty MotionTransformsProperty =
        DependencyProperty.RegisterAttached(
            "MotionTransforms",
            typeof(MotionTransforms),
            typeof(MotionBehavior));

    public static void SetIsButtonFeedbackEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsButtonFeedbackEnabledProperty, value);

    public static bool GetIsButtonFeedbackEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsButtonFeedbackEnabledProperty);

    public static void SetIsHoverLiftEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsHoverLiftEnabledProperty, value);

    public static bool GetIsHoverLiftEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsHoverLiftEnabledProperty);

    public static void SetIsEntranceEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEntranceEnabledProperty, value);

    public static bool GetIsEntranceEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEntranceEnabledProperty);

    public static void SetIsContentTransitionEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsContentTransitionEnabledProperty, value);

    public static bool GetIsContentTransitionEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsContentTransitionEnabledProperty);

    public static void SetIsRevealEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsRevealEnabledProperty, value);

    public static bool GetIsRevealEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsRevealEnabledProperty);

    public static void SetEntranceOrder(DependencyObject element, int value) =>
        element.SetValue(EntranceOrderProperty, value);

    public static int GetEntranceOrder(DependencyObject element) =>
        (int)element.GetValue(EntranceOrderProperty);

    public static void SetIsEmphasisEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEmphasisEnabledProperty, value);

    public static bool GetIsEmphasisEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEmphasisEnabledProperty);

    private static bool AnimationsEnabled => AppMotion.Enabled;

    private static void OnButtonFeedbackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.MouseEnter += Button_MouseEnter;
            element.PreviewMouseLeftButtonDown += Button_PreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp += Button_PreviewMouseLeftButtonUp;
            element.MouseLeave += Button_MouseLeave;
            element.IsEnabledChanged += Element_IsEnabledChanged;
        }
        else
        {
            element.MouseEnter -= Button_MouseEnter;
            element.PreviewMouseLeftButtonDown -= Button_PreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp -= Button_PreviewMouseLeftButtonUp;
            element.MouseLeave -= Button_MouseLeave;
            element.IsEnabledChanged -= Element_IsEnabledChanged;
        }
    }

    private static void Button_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { IsEnabled: true } element)
        {
            var hoverScale = GetIsEmphasisEnabled(element) ? 1.012 : 1.008;
            AnimateScale(element, hoverScale, hoverScale, AppMotion.Pace.Feedback);
        }
    }

    private static void Button_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { IsEnabled: true } element) return;
        // Anticipation + restrained squash; the release supplies follow-through.
        var emphasized = GetIsEmphasisEnabled(element);
        AnimateScale(element, emphasized ? 1.018 : 1.012, emphasized ? 0.97 : 0.978,
            AppMotion.Pace.Press);
    }

    private static void Button_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { IsEnabled: true } element) return;
        var target = element.IsMouseOver ? (GetIsEmphasisEnabled(element) ? 1.012 : 1.008) : 1.0;
        AnimateScale(element, target, target, AppMotion.Pace.Settle, AppMotion.Curve.Settle);
    }

    private static void Button_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateScale(element, 1.0, 1.0, AppMotion.Pace.Feedback);
    }

    private static void Element_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || e.NewValue is not false) return;
        var transforms = EnsureTransforms(element);
        transforms.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transforms.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        transforms.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        transforms.Scale.ScaleX = 1;
        transforms.Scale.ScaleY = 1;
        transforms.Translate.Y = 0;
    }

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.MouseEnter += HoverElement_MouseEnter;
            element.MouseLeave += HoverElement_MouseLeave;
            element.PreviewMouseLeftButtonDown += HoverElement_PreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp += HoverElement_PreviewMouseLeftButtonUp;
        }
        else
        {
            element.MouseEnter -= HoverElement_MouseEnter;
            element.MouseLeave -= HoverElement_MouseLeave;
            element.PreviewMouseLeftButtonDown -= HoverElement_PreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp -= HoverElement_PreviewMouseLeftButtonUp;
        }
    }

    private static void HoverElement_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { IsEnabled: true } element) return;
        AnimateLift(element, -2, 1.006, AppMotion.Pace.Feedback);
    }

    private static void HoverElement_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateLift(element, 0, 1.0, AppMotion.Pace.Feedback);
    }

    private static void HoverElement_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateLift(element, 0, 0.99, AppMotion.Pace.Press);
    }

    private static void HoverElement_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateLift(element, element.IsMouseOver ? -2 : 0, element.IsMouseOver ? 1.006 : 1.0,
            AppMotion.Pace.Settle, AppMotion.Curve.Settle);
    }

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue)
            element.Loaded += EntranceElement_Loaded;
        else
            element.Loaded -= EntranceElement_Loaded;
    }

    private static void EntranceElement_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var order = GetEntranceOrder(element);
        if (element is ContentPresenter or ListBoxItem)
        {
            var owner = ItemsControl.ItemsControlFromItemContainer(element);
            var index = owner?.ItemContainerGenerator.IndexFromContainer(element) ?? -1;
            // Limit simultaneous work: later items appear immediately, so long
            // libraries and virtualized lists never run a wall of animations.
            if (index >= 6)
            {
                var translate = EnsureTransforms(element).Translate;
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0;
                element.BeginAnimation(UIElement.OpacityProperty, null);
                return;
            }
            if (index >= 0) order = index;
        }
        PlayEntrance(element, fromY: 8, order: order);
    }

    private static void OnContentTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.DataContextChanged += ContentElement_DataContextChanged;
            Binding.AddTargetUpdatedHandler(element, ContentElement_TargetUpdated);
        }
        else
        {
            element.DataContextChanged -= ContentElement_DataContextChanged;
            Binding.RemoveTargetUpdatedHandler(element, ContentElement_TargetUpdated);
        }
    }

    private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue)
            element.IsVisibleChanged += RevealElement_IsVisibleChanged;
        else
            element.IsVisibleChanged -= RevealElement_IsVisibleChanged;
    }

    private static void RevealElement_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || e.NewValue is not true) return;
        PlayEntrance(element, fromY: -6);
    }

    private static void ContentElement_TargetUpdated(object? sender, DataTransferEventArgs e)
    {
        if (sender is FrameworkElement element && element.IsLoaded)
            PlayContentTransition(element);
    }

    private static void ContentElement_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || !element.IsLoaded || e.NewValue is null) return;
        PlayContentTransition(element);
    }

    private static void PlayContentTransition(FrameworkElement element)
    {
        PlayEntrance(element, fromX: 10, pace: AppMotion.Pace.Feedback, initialOpacity: 0.55);
    }

    public static void PlayEntrance(FrameworkElement element, double fromX = 0, double fromY = 0,
        int order = 0, AppMotion.Pace pace = AppMotion.Pace.Content, double initialOpacity = 0)
    {
        var transforms = EnsureTransforms(element);
        var targetOpacity = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        if (!AnimationsEnabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            transforms.Translate.BeginAnimation(TranslateTransform.XProperty, null);
            transforms.Translate.BeginAnimation(TranslateTransform.YProperty, null);
            transforms.Translate.X = 0;
            transforms.Translate.Y = 0;
            return;
        }

        var delay = AppMotion.Stagger(order);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        transforms.Translate.BeginAnimation(TranslateTransform.XProperty, null);
        transforms.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        transforms.Translate.X = fromX;
        transforms.Translate.Y = fromY;
        if (fromX != 0)
        {
            transforms.Translate.BeginAnimation(TranslateTransform.XProperty,
                AppMotion.To(0, pace, from: fromX, delay: delay), HandoffBehavior.SnapshotAndReplace);
        }
        if (fromY != 0)
        {
            transforms.Translate.BeginAnimation(TranslateTransform.YProperty,
                fromX != 0
                    ? AppMotion.ArcY(fromY, pace, delay)
                    : AppMotion.To(0, pace, from: fromY, delay: delay),
                HandoffBehavior.SnapshotAndReplace);
        }
        element.BeginAnimation(UIElement.OpacityProperty,
            AppMotion.OpacityEntrance(initialOpacity, targetOpacity, pace, delay),
            HandoffBehavior.SnapshotAndReplace);
    }

    public static TimeSpan PlayExit(FrameworkElement element, double toX = 0, double toY = 0)
    {
        var transforms = EnsureTransforms(element);
        if (!AnimationsEnabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 0;
            return TimeSpan.Zero;
        }

        element.BeginAnimation(UIElement.OpacityProperty,
            AppMotion.To(0, AppMotion.Pace.Exit, AppMotion.Curve.Exit), HandoffBehavior.SnapshotAndReplace);
        if (toX != 0)
            transforms.Translate.BeginAnimation(TranslateTransform.XProperty,
                AppMotion.To(toX, AppMotion.Pace.Exit, AppMotion.Curve.Exit), HandoffBehavior.SnapshotAndReplace);
        if (toY != 0)
            transforms.Translate.BeginAnimation(TranslateTransform.YProperty,
                AppMotion.To(toY, AppMotion.Pace.Exit, AppMotion.Curve.Exit), HandoffBehavior.SnapshotAndReplace);
        return AppMotion.Duration(AppMotion.Pace.Exit);
    }

    private static void AnimateScale(FrameworkElement element, double x, double y, AppMotion.Pace pace,
        AppMotion.Curve curve = AppMotion.Curve.Enter)
    {
        var transforms = EnsureTransforms(element);
        if (!AnimationsEnabled)
        {
            transforms.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transforms.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transforms.Scale.ScaleX = 1;
            transforms.Scale.ScaleY = 1;
            return;
        }

        transforms.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            AppMotion.To(x, pace, curve), HandoffBehavior.SnapshotAndReplace);
        transforms.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            AppMotion.To(y, pace, curve), HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateLift(FrameworkElement element, double y, double scale, AppMotion.Pace pace,
        AppMotion.Curve curve = AppMotion.Curve.Enter)
    {
        var transforms = EnsureTransforms(element);
        if (!AnimationsEnabled)
        {
            transforms.Translate.BeginAnimation(TranslateTransform.YProperty, null);
            transforms.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transforms.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transforms.Translate.Y = 0;
            transforms.Scale.ScaleX = 1;
            transforms.Scale.ScaleY = 1;
            return;
        }

        transforms.Translate.BeginAnimation(TranslateTransform.YProperty,
            AppMotion.To(y, pace, curve), HandoffBehavior.SnapshotAndReplace);
        transforms.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            AppMotion.To(scale, pace, curve), HandoffBehavior.SnapshotAndReplace);
        transforms.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            AppMotion.To(scale, pace, curve), HandoffBehavior.SnapshotAndReplace);
    }

    private static MotionTransforms EnsureTransforms(FrameworkElement element)
    {
        if (element.GetValue(MotionTransformsProperty) is MotionTransforms existing)
            return existing;

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        if (element.RenderTransform is { } current && current != Transform.Identity)
            group.Children.Add(current);
        group.Children.Add(scale);
        group.Children.Add(translate);
        element.RenderTransform = group;
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        var transforms = new MotionTransforms(scale, translate);
        element.SetValue(MotionTransformsProperty, transforms);
        return transforms;
    }

    private sealed record MotionTransforms(ScaleTransform Scale, TranslateTransform Translate);
}
