using System.Windows;
using System.Windows.Media.Animation;

namespace Blocker.UI.Controls;

/// <summary>
/// The motion language of the app: a few durations, two easing curves and some limits. Every animation uses these instead of its own
/// numbers, so the interface moves consistently: quick fades for hover and press, a short ease-out for things that move or appear and an
/// ease-in for things that leave; no bounce, no overshoot, nothing larger than a nudge. When Windows has animations switched off every
/// duration collapses to zero. The same durations exist as <c>Blocker.Motion.*</c> resources for XAML storyboards.
/// </summary>
public static class Motion
{
    /// <summary>Hover and press feedback, the lift of a dragged widget.</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);

    /// <summary>Layout changes (reflow, resize snaps) and small appearing elements.</summary>
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(180);

    /// <summary>A panel or page entering.</summary>
    public static readonly TimeSpan Emphasis = TimeSpan.FromMilliseconds(240);

    /// <summary>Anything leaving; shorter than it took to enter.</summary>
    public static readonly TimeSpan Exit = TimeSpan.FromMilliseconds(160);

    /// <summary>Largest scale a lifted widget grows to.</summary>
    public const double LiftScale = 1.03;

    /// <summary>Distance in DIPs a side panel slides while it fades in or out.</summary>
    public const double PanelOffset = 20;

    /// <summary>Distance in DIPs a flyout slides while it fades in or out.</summary>
    public const double FlyoutOffset = 8;

    /// <summary>Opacity the dimmed backdrop of a panel fades to.</summary>
    public const double ScrimOpacity = 0.2;

    private const string ResourcePrefix = "Blocker.Motion.";

    /// <summary>Gets whether Windows allows animations (Settings, Accessibility, Visual effects, Animation effects).</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Gets a duration, or zero when animations are switched off.</summary>
    /// <param name="duration">The duration.</param>
    /// <returns><paramref name="duration"/> or <see cref="TimeSpan.Zero"/>.</returns>
    public static TimeSpan Scaled(TimeSpan duration) => Enabled ? duration : TimeSpan.Zero;

    /// <summary>Cubic ease-out: fast start, gentle stop. Used for everything that appears or moves.</summary>
    /// <param name="t">Linear progress from 0 to 1.</param>
    /// <returns>Eased progress from 0 to 1.</returns>
    public static double EaseOut(double t)
    {
        double rest = 1 - Math.Clamp(t, 0, 1);
        return 1 - (rest * rest * rest);
    }

    /// <summary>Cubic ease-in: gentle start, fast finish. Used for everything that leaves.</summary>
    /// <param name="t">Linear progress from 0 to 1.</param>
    /// <returns>Eased progress from 0 to 1.</returns>
    public static double EaseIn(double t)
    {
        double clamped = Math.Clamp(t, 0, 1);
        return clamped * clamped * clamped;
    }

    /// <summary>Creates a double animation with the app's easing; durations are scaled by <see cref="Scaled"/>.</summary>
    /// <param name="from">Start value.</param>
    /// <param name="to">End value.</param>
    /// <param name="duration">One of the shared durations.</param>
    /// <param name="leaving">Whether the element is leaving (ease-in) rather than arriving or moving (ease-out).</param>
    /// <param name="hold">Whether the end value stays after the animation instead of falling back to the base value.</param>
    /// <returns>The animation.</returns>
    public static DoubleAnimation Animate(double from, double to, TimeSpan duration, bool leaving = false, bool hold = false) => new(from, to, Scaled(duration))
    {
        EasingFunction = new CubicEase { EasingMode = leaving ? EasingMode.EaseIn : EasingMode.EaseOut },
        FillBehavior = hold ? FillBehavior.HoldEnd : FillBehavior.Stop,
    };

    /// <summary>
    /// Writes the shared timings into the <c>Blocker.Motion.*</c> resources of a dictionary tree (zero when Windows has animations
    /// switched off). Call once at startup, before any page that uses them is created.
    /// </summary>
    /// <param name="resources">The application resources.</param>
    public static void Apply(ResourceDictionary resources)
    {
        Dictionary<string, TimeSpan> durations = new()
        {
            ["Fast"] = Fast,
            ["Normal"] = Normal,
            ["Emphasis"] = Emphasis,
            ["Exit"] = Exit,
        };
        foreach ((string name, TimeSpan duration) in durations)
        {
            Set(resources, ResourcePrefix + name, new Duration(Scaled(duration)));
        }

        Set(resources, ResourcePrefix + "PanelOffset", Enabled ? PanelOffset : 0d);
        Set(resources, ResourcePrefix + "FlyoutOffset", Enabled ? FlyoutOffset : 0d);
    }

    private static void Set(ResourceDictionary resources, string key, object value)
    {
        if (resources.Contains(key))
        {
            resources[key] = value;
        }

        foreach (ResourceDictionary merged in resources.MergedDictionaries)
        {
            Set(merged, key, value);
        }
    }
}
