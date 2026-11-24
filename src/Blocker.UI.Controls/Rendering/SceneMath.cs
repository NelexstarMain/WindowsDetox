using System.Windows;
using System.Windows.Media;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Rendering;

/// <summary>Which texture a polygon gets.</summary>
internal enum SceneMaterial
{
    /// <summary>Part of a scene item.</summary>
    Item,

    /// <summary>A tile top.</summary>
    Grass,

    /// <summary>Slab sides and pebbles.</summary>
    Soil,

    /// <summary>Small details drawn plain.</summary>
    Detail,
}

/// <summary>One shaded polygon of a projected scene, in unscaled screen units, in back-to-front drawing order.</summary>
/// <param name="Points">Screen vertices.</param>
/// <param name="Fill">Shaded colour.</param>
/// <param name="ItemId">Id of the scene item the polygon belongs to, or <see langword="null"/> for the ground.</param>
/// <param name="Surface">Texture class.</param>
internal sealed record ScenePolygon(Point[] Points, Color Fill, string? ItemId, SceneMaterial Surface = SceneMaterial.Item);

/// <summary>A soft contact shadow on the ground, in unscaled screen units.</summary>
/// <param name="Centre">Centre of the shadow ellipse.</param>
/// <param name="RadiusX">Horizontal radius.</param>
/// <param name="RadiusY">Vertical radius.</param>
internal readonly record struct SceneShadow(Point Centre, double RadiusX, double RadiusY);

/// <summary>A projected scene: polygons in drawing order, ground shadows and the bounds used to fit it into the control.</summary>
/// <param name="Polygons">Back-to-front polygons; ground polygons (no item) come first.</param>
/// <param name="Shadows">Soft contact shadows, drawn after the ground and before the items.</param>
/// <param name="CastShadows">Item faces projected onto the ground along the light; drawn as one translucent layer clipped to <paramref name="GroundOutline"/>.</param>
/// <param name="GroundOutline">Outline of the ground top, or empty without ground.</param>
/// <param name="Bounds">Bounds of the ground slab and the tallest object.</param>
internal sealed record SceneProjection(
    IReadOnlyList<ScenePolygon> Polygons,
    IReadOnlyList<SceneShadow> Shadows,
    IReadOnlyList<Point[]> CastShadows,
    Point[] GroundOutline,
    Rect Bounds);

/// <summary>
/// Isometric projection of a <see cref="UiScene"/>. World space: tile (c, r) covers x in c..c+1 and z in r..r+1, y up. The camera turns in
/// quarter turns around the grid centre; light and view are fixed relative to the camera so every view is shaded the same way.
/// </summary>
internal static class SceneMath
{
    /// <summary>Screen x per unit of (x - z).</summary>
    public const double Cos30 = 0.8660254037844386;

    /// <summary>Screen y per unit of (x + z).</summary>
    public const double Sin30 = 0.5;

    /// <summary>Screen y per unit of height; true isometric foreshortening of the vertical axis.</summary>
    public const double Vertical = 0.8165;

    /// <summary>Thickness of the ground slab below the tile tops.</summary>
    public const double GroundDepth = 0.35;

    // Direction towards the camera: the vector the projection maps to a single point (x = z, (x + z) * Sin30 = y * Vertical).
    private static readonly Vec3 ToCamera = new(1, 2 * Sin30 / Vertical, 1);

    // Fixed light from the upper left of the screen: tops brightest, left-facing sides lighter than right-facing ones.
    private static readonly Vec3 Light = new Vec3(0.3, 1.0, 0.6).Normalized();

    private static readonly Color Dirt = Color.FromRgb(0x8D, 0x6E, 0x63);
    private static readonly Color DeepDirt = Color.FromRgb(0x6D, 0x52, 0x48);
    private static readonly Color Pebble = Color.FromRgb(0xA8, 0x9F, 0x94);

    // Depth of the turf band at the top of the slab sides.
    private const double TurfDepth = 0.07;

    /// <summary>Models are drawn this much smaller than their tile so neighbours keep some breathing room.</summary>
    public const double ItemScale = 0.85;

    /// <summary>Projects the scene for the given camera turn (0-3).</summary>
    public static SceneProjection Project(UiScene scene, int cameraTurns)
    {
        var view = new View(scene.Columns, scene.Rows, cameraTurns);
        List<ScenePolygon> polygons = [];
        List<SceneShadow> shadows = [];
        List<Point[]> cast = [];
        double maxHeight = 0;
        if (scene.ShowGround)
        {
            AddGround(scene, view, polygons);
        }

        Dictionary<string, UiSceneModel> models = scene.Models.ToDictionary(m => m.Key, StringComparer.Ordinal);
        foreach (UiSceneItem item in scene.Items.OrderBy(i => view.Depth(i.Column + 0.5, i.Row + 0.5)))
        {
            if (!models.TryGetValue(item.Model, out UiSceneModel? model))
            {
                continue;
            }

            List<(double Depth, ScenePolygon Polygon)> faces = [];
            double itemHeight = 0;
            foreach (UiSceneFace face in model.Faces)
            {
                Vec3[] world = new Vec3[face.Points.Count / 3];
                for (int i = 0; i < world.Length; i++)
                {
                    (double x, double z) = Turn(face.Points[3 * i], face.Points[(3 * i) + 2], item.Rotation);
                    double scale = item.Scale * ItemScale;
                    double y = face.Points[(3 * i) + 1] * scale;
                    world[i] = view.ToCamera(item.Column + 0.5 + (x * scale), y, item.Row + 0.5 + (z * scale));
                    maxHeight = Math.Max(maxHeight, y);
                    itemHeight = Math.Max(itemHeight, y);
                }

                if (scene.ShowGround && world.Any(p => p.Y > 0.005))
                {
                    cast.Add(CastOnGround(world));
                }

                if (TryShade(world, ParseColour(face.Color), out Color fill))
                {
                    // Ambient occlusion: faces close to the ground (trunk bases, canopy undersides near it) get less sky light.
                    fill = Scale(fill, 0.74 + (0.26 * Math.Clamp(Centroid(world).Y / 0.55, 0, 1)));
                    faces.Add((Centroid(world).Dot(ToCamera), new ScenePolygon([.. world.Select(Screen)], fill, item.Id)));
                }
            }

            polygons.AddRange(faces.OrderBy(f => f.Depth).Select(f => f.Polygon));
            if (scene.ShowGround && itemHeight > 0.02)
            {
                shadows.Add(Shadow(view, item, itemHeight));
            }
        }

        Point[] outline = scene.ShowGround ? TileOutlineSpan(view, 0, 0, scene.Columns, scene.Rows) : [];
        return new SceneProjection(polygons, shadows, cast, outline, Bounds(view, scene.ShowGround, maxHeight));
    }

    /// <summary>The top outline of a tile, for selection and hover marks.</summary>
    public static Point[] TileOutline(int columns, int rows, int cameraTurns, int column, int row)
    {
        var view = new View(columns, rows, cameraTurns);
        return
        [
            Screen(view.ToCamera(column, 0, row)),
            Screen(view.ToCamera(column + 1, 0, row)),
            Screen(view.ToCamera(column + 1, 0, row + 1)),
            Screen(view.ToCamera(column, 0, row + 1)),
        ];
    }

    /// <summary>The tile under an unscaled screen point on the ground plane, or <see langword="null"/> outside the grid.</summary>
    public static (int Column, int Row)? TileAt(int columns, int rows, int cameraTurns, Point screen)
    {
        double a = screen.X / Cos30;   // x - z
        double b = screen.Y / Sin30;   // x + z
        var view = new View(columns, rows, cameraTurns);
        (double x, double z) = view.FromCamera((a + b) / 2, (b - a) / 2);
        int column = (int)Math.Floor(x);
        int row = (int)Math.Floor(z);
        return column >= 0 && column < columns && row >= 0 && row < rows ? (column, row) : null;
    }

    /// <summary>Flat shading of a polygon; <see langword="false"/> when it faces away from the camera.</summary>
    public static bool TryShade(IReadOnlyList<Vec3> polygon, Color baseColour, out Color shaded)
    {
        shaded = baseColour;
        Vec3 normal = Normal(polygon);
        if (normal.Dot(ToCamera) <= 1e-9)
        {
            return false;
        }

        double light = 0.5 + (0.55 * Math.Max(0, normal.Normalized().Dot(Light)));
        shaded = Color.FromRgb(Channel(baseColour.R, light), Channel(baseColour.G, light), Channel(baseColour.B, light));
        return true;
    }

    /// <summary>Parses a validated <c>#RRGGBB</c> colour.</summary>
    public static Color ParseColour(string hex) => Color.FromRgb(
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    /// <summary>Quarter turn(s) of a point around the vertical axis (counter-clockwise seen from above).</summary>
    public static (double X, double Z) Turn(double x, double z, int turns) => (turns & 3) switch
    {
        1 => (-z, x),
        2 => (-x, -z),
        3 => (z, -x),
        _ => (x, z),
    };

    // A planar shadow: each vertex slides down the light direction onto y = 0.
    private static Point[] CastOnGround(Vec3[] world) =>
        [.. world.Select(p => Screen(new Vec3(p.X - (Light.X * p.Y / Light.Y), 0, p.Z - (Light.Z * p.Y / Light.Y))))];

    private static Point[] TileOutlineSpan(View view, int column, int row, int columns, int rows) =>
    [
        Screen(view.ToCamera(column, 0, row)),
        Screen(view.ToCamera(column + columns, 0, row)),
        Screen(view.ToCamera(column + columns, 0, row + rows)),
        Screen(view.ToCamera(column, 0, row + rows)),
    ];

    // The shadow falls away from the light (which comes from +x, +z in camera space), further for taller objects.
    private static SceneShadow Shadow(View view, UiSceneItem item, double height)
    {
        Vec3 centre = view.ToCamera(item.Column + 0.5, 0, item.Row + 0.5);
        double reach = Math.Min(height, 2.2) * 0.09;
        double radius = Math.Clamp(0.24 + (height * 0.04), 0.24, 0.36) * Math.Min(item.Scale, 1.3) * ItemScale;
        Point at = Screen(new Vec3(centre.X - (Light.X * reach / Light.Y), 0, centre.Z - (Light.Z * reach / Light.Y)));
        return new SceneShadow(at, radius * Cos30 * Math.Sqrt(2), radius * Sin30 * Math.Sqrt(2));
    }

    private static Color Scale(Color c, double f) => Color.FromRgb(Channel(c.R, f), Channel(c.G, f), Channel(c.B, f));

    // Deterministic per-tile noise in 0..1 so the ground texture is stable across renders and camera turns.
    private static double Noise(int column, int row, int salt)
    {
        uint h = (uint)((column * 73856093) ^ (row * 19349663) ^ (salt * 83492791));
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (h & 0xFFFF) / 65535.0;
    }

    private static Point Screen(Vec3 p) => new((p.X - p.Z) * Cos30, ((p.X + p.Z) * Sin30) - (p.Y * Vertical));

    private static void AddGround(UiScene scene, View view, List<ScenePolygon> polygons)
    {
        Color grass = ParseColour(scene.GroundColor);
        Color turf = Scale(grass, 0.72);
        int c = scene.Columns, r = scene.Rows;
        double d = GroundDepth, t = TurfDepth;
        foreach ((Vec3 a, Vec3 b, Vec3 cc, Vec3 dd) in Sides(c, r))
        {
            // Each side: a turf band, then soil fading to deeper soil towards the bottom.
            AddFace(view, polygons, [a, Down(a, t), Down(dd, t), dd], turf, SceneMaterial.Grass);
            AddFace(view, polygons, [Down(a, t), Down(a, d * 0.62), Down(dd, d * 0.62), Down(dd, t)], Dirt, SceneMaterial.Soil);
            AddFace(view, polygons, [Down(a, d * 0.62), Down(a, d), Down(dd, d), Down(dd, d * 0.62)], DeepDirt, SceneMaterial.Soil);
        }

        AddPebbles(view, polygons, c, r);
        for (int row = 0; row < r; row++)
        {
            for (int column = 0; column < c; column++)
            {
                Color tile = Scale(grass, 0.93 + (0.1 * Noise(column, row, 1)));
                AddFace(view, polygons, [new(column, 0, row), new(column, 0, row + 1), new(column + 1, 0, row + 1), new(column + 1, 0, row)], tile, SceneMaterial.Grass);
                AddTufts(view, polygons, column, row, Scale(grass, 0.78), Scale(grass, 1.12));
            }
        }
    }

    // The four slab sides as (top start, -, -, top end), wound so their outward normals face away from the slab.
    private static IEnumerable<(Vec3, Vec3, Vec3, Vec3)> Sides(int c, int r)
    {
        yield return (new(0, 0, r), default, default, new(c, 0, r));
        yield return (new(c, 0, r), default, default, new(c, 0, 0));
        yield return (new(c, 0, 0), default, default, new(0, 0, 0));
        yield return (new(0, 0, 0), default, default, new(0, 0, r));
    }

    private static Vec3 Down(Vec3 p, double depth) => p with { Y = p.Y - depth };

    private static void AddFace(View view, List<ScenePolygon> polygons, Vec3[] points, Color colour, SceneMaterial surface)
    {
        Vec3[] world = [.. points.Select(p => view.ToCamera(p.X, p.Y, p.Z))];
        if (TryShade(world, colour, out Color fill))
        {
            polygons.Add(new ScenePolygon([.. world.Select(Screen)], fill, null, surface));
        }
    }

    // A few small two-sided grass blades per tile; flat to the camera so they read at any turn.
    private static void AddTufts(View view, List<ScenePolygon> polygons, int column, int row, Color dark, Color light)
    {
        int count = 2 + (int)(Noise(column, row, 2) * 3);
        for (int i = 0; i < count; i++)
        {
            double x = column + 0.12 + (0.76 * Noise(column, row, 10 + i));
            double z = row + 0.12 + (0.76 * Noise(column, row, 20 + i));
            Vec3 c = view.ToCamera(x, 0, z);
            Point foot = Screen(c);
            double h = 0.05 + (0.04 * Noise(column, row, 30 + i));
            Color colour = i % 2 == 0 ? dark : light;
            foreach (double lean in new[] { -0.035, 0.0, 0.035 })
            {
                polygons.Add(new ScenePolygon(
                [
                    new(foot.X - 0.012, foot.Y),
                    new(foot.X + lean, foot.Y - (h * Vertical)),
                    new(foot.X + 0.012, foot.Y),
                ], colour, null, SceneMaterial.Detail));
            }
        }
    }

    // Pebbles embedded in the two front sides, only where the side faces the camera.
    private static void AddPebbles(View view, List<ScenePolygon> polygons, int c, int r)
    {
        foreach ((Vec3 a, _, _, Vec3 b) in Sides(c, r))
        {
            Vec3[] probe = [view.ToCamera(a.X, 0, a.Z), view.ToCamera(a.X, -1, a.Z), view.ToCamera(b.X, -1, b.Z)];
            if (!TryShade(probe, Pebble, out Color fill))
            {
                continue;
            }

            double length = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Z - a.Z));
            for (int i = 0; i < (int)(length * 2); i++)
            {
                double f = (i + 0.3 + (0.4 * Noise(i, (int)a.X + (int)a.Z, 40))) / (length * 2);
                double y = -TurfDepth - 0.05 - ((GroundDepth - TurfDepth - 0.1) * Noise(i, (int)b.X, 41));
                Point p = Screen(view.ToCamera(a.X + ((b.X - a.X) * f), y, a.Z + ((b.Z - a.Z) * f)));
                double w = 0.025 + (0.02 * Noise(i, 3, 42));
                polygons.Add(new ScenePolygon([new(p.X - w, p.Y), new(p.X - (w * 0.5), p.Y - (w * 0.6)), new(p.X + (w * 0.6), p.Y - (w * 0.5)), new(p.X + w, p.Y + (w * 0.2)), new(p.X, p.Y + (w * 0.5))], fill, null, SceneMaterial.Detail));
            }
        }
    }

    private static Rect Bounds(View view, bool ground, double maxHeight)
    {
        double bottom = ground ? -GroundDepth : 0;
        double top = Math.Max(maxHeight, 0.5);
        Rect bounds = Rect.Empty;
        foreach ((double x, double z) in new[] { (0.0, 0.0), (view.Columns, 0.0), (view.Columns, view.Rows), (0.0, view.Rows) })
        {
            bounds.Union(Screen(view.ToCamera(x, bottom, z)));
            bounds.Union(Screen(view.ToCamera(x, top, z)));
        }

        return bounds;
    }

    private static Vec3 Normal(IReadOnlyList<Vec3> polygon)
    {
        // Newell's method: robust for slightly non-planar polygons; the right-hand winding of the contract gives an outward normal.
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vec3 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }

        return new Vec3(nx, ny, nz);
    }

    private static Vec3 Centroid(Vec3[] points) =>
        new(points.Average(p => p.X), points.Average(p => p.Y), points.Average(p => p.Z));

    private static byte Channel(byte value, double factor) => (byte)Math.Clamp(Math.Round(value * factor), 0, 255);

    /// <summary>A world-space vector.</summary>
    internal readonly record struct Vec3(double X, double Y, double Z)
    {
        public double Dot(Vec3 o) => (X * o.X) + (Y * o.Y) + (Z * o.Z);

        public Vec3 Normalized()
        {
            double length = Math.Sqrt(Dot(this));
            return length < 1e-12 ? this : new Vec3(X / length, Y / length, Z / length);
        }
    }

    /// <summary>Camera turn around the grid centre.</summary>
    private readonly record struct View(int Columns, int Rows, int Turns)
    {
        public Vec3 ToCamera(double x, double y, double z)
        {
            (double tx, double tz) = Turn(x - (Columns / 2.0), z - (Rows / 2.0), Turns);
            return new Vec3(tx, y, tz);
        }

        public (double X, double Z) FromCamera(double x, double z)
        {
            (double tx, double tz) = Turn(x, z, 4 - (Turns & 3));
            return (tx + (Columns / 2.0), tz + (Rows / 2.0));
        }

        public double Depth(double x, double z)
        {
            Vec3 p = ToCamera(x, 0, z);
            return p.X + p.Z;
        }
    }
}
