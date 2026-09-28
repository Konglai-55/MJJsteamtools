using System.Windows;
using System.Windows.Media.Animation;

namespace SteamLuaManager.Controls;

/// <summary>
/// Single timing and easing vocabulary for the app. Motion communicates state;
/// it never delays an action when Windows animations are disabled.
/// </summary>
public static class AppMotion
{
    public enum Pace { Press, Feedback, Settle, Content, Page, Exit }
    public enum Curve { Enter, Exit, InOut, Settle }

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static TimeSpan Duration(Pace pace) => pace switch
    {
        Pace.Press => TimeSpan.FromMilliseconds(85),
        Pace.Feedback => TimeSpan.FromMilliseconds(155),
        Pace.Settle => TimeSpan.FromMilliseconds(190),
        Pace.Content => TimeSpan.FromMilliseconds(230),
        Pace.Page => TimeSpan.FromMilliseconds(260),
        Pace.Exit => TimeSpan.FromMilliseconds(140),
        _ => TimeSpan.Zero
    };

    public static TimeSpan EffectiveDuration(Pace pace) => Enabled ? Duration(pace) : TimeSpan.Zero;

    public static TimeSpan Stagger(int order) => Enabled
        ? TimeSpan.FromMilliseconds(Math.Clamp(order, 0, 4) * 28)
        : TimeSpan.Zero;

    public static IEasingFunction Ease(Curve curve) => curve switch
    {
        Curve.Exit => new CubicEase { EasingMode = EasingMode.EaseIn },
        Curve.InOut => new CubicEase { EasingMode = EasingMode.EaseInOut },
        // The tiny overshoot is reserved for release, not every content transition.
        Curve.Settle => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 },
        _ => new CubicEase { EasingMode = EasingMode.EaseOut }
    };

    public static DoubleAnimation To(double target, Pace pace, Curve curve = Curve.Enter,
        double? from = null, TimeSpan? delay = null) => new()
    {
        From = from,
        To = target,
        Duration = new Duration(EffectiveDuration(pace)),
        BeginTime = delay ?? TimeSpan.Zero,
        EasingFunction = Enabled ? Ease(curve) : null,
        FillBehavior = FillBehavior.HoldEnd
    };

    public static DoubleAnimationUsingKeyFrames ArcY(double from, Pace pace, TimeSpan delay)
    {
        var total = Duration(pace);
        var animation = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = delay,
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-1,
            KeyTime.FromTimeSpan(TimeSpan.FromTicks((long)(total.Ticks * 0.72))), Ease(Curve.Enter)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(total), Ease(Curve.Enter)));
        return animation;
    }

    public static DoubleAnimationUsingKeyFrames OpacityEntrance(double from, double to,
        Pace pace, TimeSpan delay)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from,
            KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to,
            KeyTime.FromTimeSpan(delay + Duration(pace)), Ease(Curve.Enter)));
        return animation;
    }
}
