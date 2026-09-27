using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SteamLuaManager.Controls;

/// <summary>
/// Central Fluent-style motion behavior. Durations follow the Windows motion
/// guidance: 83 ms for press feedback, 167 ms for control feedback and 250 ms
/// for content entering the scene. All motion respects the Windows animation
/// accessibility setting.
/// </summary>
public static class MotionBehavior
{
    private static readonly Duration FasterDuration = new(TimeSpan.FromMilliseconds(83));
    private static readonly Duration FastDuration = new(TimeSpan.FromMilliseconds(167));
    private static readonly Duration NormalDuration = new(TimeSpan.FromMilliseconds(250));

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

    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    private static void OnButtonFeedbackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.PreviewMouseLeftButtonDown += Button_PreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp += Button_PreviewMouseLeftButtonUp;
            element.MouseLeave += Button_MouseLeave;
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= Button_PreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp -= Button_PreviewMouseLeftButtonUp;
            element.MouseLeave -= Button_MouseLeave;
        }
    }

    private static void Button_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateScale(element, 0.975, FasterDuration);
    }

    private static void Button_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateScale(element, element.IsMouseOver ? 1.01 : 1.0, FasterDuration);
    }

    private static void Button_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateScale(element, 1.0, FastDuration);
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
        if (sender is not FrameworkElement element) return;
        AnimateLift(element, -2, 1.006, FastDuration);
    }

    private static void HoverElement_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateLift(element, 0, 1.0, FastDuration);
    }

    private static void HoverElement_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateLift(element, 0, 0.99, FasterDuration);
    }

    private static void HoverElement_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        AnimateLift(element, element.IsMouseOver ? -2 : 0, element.IsMouseOver ? 1.006 : 1.0, FasterDuration);
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
        var transforms = EnsureTransforms(element);
        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            transforms.Translate.Y = 0;
            return;
        }

        element.Opacity = 0;
        transforms.Translate.Y = 8;
        element.BeginAnimation(UIElement.OpacityProperty, CreateAnimation(1, NormalDuration));
        transforms.Translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(0, NormalDuration));
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
        var transforms = EnsureTransforms(element);
        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            transforms.Translate.Y = 0;
            return;
        }

        element.Opacity = 0;
        transforms.Translate.Y = -6;
        element.BeginAnimation(UIElement.OpacityProperty, CreateAnimation(1, NormalDuration));
        transforms.Translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(0, NormalDuration));
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
        var transforms = EnsureTransforms(element);
        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            transforms.Translate.X = 0;
            return;
        }

        element.Opacity = 0.55;
        transforms.Translate.X = 10;
        element.BeginAnimation(UIElement.OpacityProperty, CreateAnimation(1, FastDuration));
        transforms.Translate.BeginAnimation(TranslateTransform.XProperty, CreateAnimation(0, FastDuration));
    }

    private static void AnimateScale(FrameworkElement element, double scale, Duration duration)
    {
        var transforms = EnsureTransforms(element);
        if (!AnimationsEnabled)
        {
            transforms.Scale.ScaleX = scale;
            transforms.Scale.ScaleY = scale;
            return;
        }

        transforms.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(scale, duration));
        transforms.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(scale, duration));
    }

    private static void AnimateLift(FrameworkElement element, double y, double scale, Duration duration)
    {
        var transforms = EnsureTransforms(element);
        if (!AnimationsEnabled)
        {
            transforms.Translate.Y = y;
            transforms.Scale.ScaleX = scale;
            transforms.Scale.ScaleY = scale;
            return;
        }

        transforms.Translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(y, duration));
        transforms.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(scale, duration));
        transforms.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(scale, duration));
    }

    private static DoubleAnimation CreateAnimation(double to, Duration duration) =>
        new()
        {
            To = to,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

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
