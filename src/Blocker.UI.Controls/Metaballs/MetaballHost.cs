using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Blocker.UI.Controls.Metaballs;

/// <summary>
/// A <see cref="Grid"/> that owns a <see cref="MetaballSurface"/> as its bottom layer. Elements marked with
/// <see cref="Metaball.IsBodyProperty"/> anywhere below it become bodies of the field; the pointer position becomes the
/// cursor blob. A frame loop runs only while something is still moving and is idle (0 fps) otherwise.
/// </summary>
public sealed class MetaballHost : Grid
{
    private readonly MetaballSurface _surface = new();
    private readonly List<Body> _bodies = [];
    private readonly MetaballScene _scene = new();
    private readonly BodyTarget[] _scratch = new BodyTarget[MetaballScene.MaxBodies];
    private int _nextId;
    private bool _running;
    private TimeSpan? _lastRender;
    private ThemeColors _colors = ThemeColors.Default;
    private MetaballQuality _effective = MetaballQuality.High;

    /// <summary>Initializes a new instance of the <see cref="MetaballHost"/> class.</summary>
    public MetaballHost()
    {
        Background = Brushes.Transparent;
        SetZIndex(_surface, -1);
        SetRowSpan(_surface, 1000);
        SetColumnSpan(_surface, 1000);
        Children.Add(_surface);
        _surface.Configure(_scene, _colors, _effective);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewMouseMove += OnPreviewMouseMove;
        MouseLeave += OnMouseLeave;
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    /// <summary>Gets or sets the requested quality. Reduced-motion settings force <see cref="MetaballQuality.Off"/>.</summary>
    public MetaballQuality Quality
    {
        get => Metaball.GetQuality(this);
        set => Metaball.SetQuality(this, value);
    }

    /// <summary>Gets the surface owned by this host.</summary>
    public MetaballSurface Surface => _surface;

    internal void Register(FrameworkElement element)
    {
        if (_bodies.Exists(b => ReferenceEquals(b.Element, element)))
        {
            return;
        }

        _bodies.Add(new Body(element, _nextId++));
        Refresh(snap: false);
    }

    internal void Unregister(FrameworkElement element)
    {
        if (_bodies.RemoveAll(b => ReferenceEquals(b.Element, element)) > 0)
        {
            Refresh(snap: false);
        }
    }

    internal void UpdateQuality()
    {
        _effective = MetaballQualityResolver.Resolve(Quality, SystemParameters.ClientAreaAnimation);
        if (_effective == MetaballQuality.Off)
        {
            StopLoop();
        }

        _surface.Configure(_scene, _colors, _effective);
        Refresh(snap: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated += OnLayoutUpdated;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        ResolveColors();
        UpdateQuality();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        StopLoop();
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            UpdateQuality();
        }
    }

    private void ResolveColors()
    {
        Rgba card = ColorOf("Blocker.Bg.Card") ?? ThemeColors.Default.Fill;
        Rgba accent = ColorOf("Blocker.Accent") ?? Rgba.FromArgb(0xFF3DD9C1);
        _colors = ThemeColors.From(card, accent);
    }

    private Rgba? ColorOf(string key) =>
        TryFindResource(key) is SolidColorBrush { Color: var c } ? Rgba.FromBytes(c.A, c.R, c.G, c.B) : null;

    private void Refresh(bool snap)
    {
        if (!IsLoaded || _effective == MetaballQuality.Off || !RecomputeTargets())
        {
            return;
        }

        if (snap)
        {
            _scene.SnapBodies();
        }

        StartLoop();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Refresh(snap: false);

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.HorizontalChange != 0)
        {
            Refresh(snap: true);
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_effective == MetaballQuality.Off)
        {
            return;
        }

        Point p = e.GetPosition(this);
        _scene.SetCursor((float)p.X, (float)p.Y);
        StartLoop();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _scene.ClearCursor();
        if (_effective != MetaballQuality.Off)
        {
            StartLoop();
        }
    }

    private bool RecomputeTargets()
    {
        int count = 0;
        var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
        viewport.Inflate(64, 64);

        foreach (Body body in _bodies)
        {
            if (count == MetaballScene.MaxBodies)
            {
                break;
            }

            FrameworkElement element = body.Element;
            if (!element.IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1 || !element.IsDescendantOf(this))
            {
                continue;
            }

            Rect bounds = element.TransformToAncestor(this).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (!bounds.IntersectsWith(viewport))
            {
                continue;
            }

            _scratch[count++] = new BodyTarget(body.Id, (float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height, (float)Metaball.GetCornerRadius(element));
        }

        return _scene.SetBodies(_scratch.AsSpan(0, count));
    }

    private void StartLoop()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _lastRender = null;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopLoop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        if (_lastRender == now)
        {
            return;
        }

        float dt = _lastRender is { } last ? (float)Math.Clamp((now - last).TotalSeconds, 0.001, 0.05) : 1f / 60f;
        _lastRender = now;

        bool moving = _scene.Step(dt);
        _surface.Configure(_scene, _colors, _effective);

        if (!moving)
        {
            StopLoop();
        }
    }

    private sealed record Body(FrameworkElement Element, int Id);
}
