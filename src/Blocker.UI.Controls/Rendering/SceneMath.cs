using System.Windows;
using System.Windows.Media;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Rendering;

/// <summary>One shaded polygon of a projected scene, in unscaled screen units, in back-to-front drawing order.</summary>
/// <param name="Points">Screen vertices.</param>
/// <param name="Fill">Shaded colour.</param>
/// <param name="ItemId">Id of the scene item the polygon belongs to, or <see langword="null"/> for the ground.</param>
internal sealed record ScenePolygon(Point[] Points, Color Fill, string? ItemId);

/// <summary>A projected scene: polygons in drawing order plus the bounds used to fit it into the control.</summary>
/// <param name="Polygons">Back-to-front polygons.</param>
/// <param name="Bounds">Bounds of the ground slab and the tallest object.</param>
internal sealed record SceneProjection(IReadOnlyList<ScenePolygon> Polygons, Rect Bounds);

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

    /// <summary>Projects the scene for the given camera turn (0-3).</summary>
    public static SceneProjection Project(UiScene scene, int cameraTurns)
    {
        var view = new View(scene.Columns, scene.Rows, cameraTurns);
        List<ScenePolygon> polygons = [];
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
            foreach (UiSceneFace face in model.Faces)
            {
                Vec3[] world = new Vec3[face.Points.Count / 3];
                for (int i = 0; i < world.Length; i++)
                {
                    (double x, double z) = Turn(face.Points[3 * i], face.Points[(3 * i) + 2], item.Rotation);
                    double y = face.Points[(3 * i) + 1] * item.Scale;
                    world[i] = view.ToCamera(item.Column + 0.5 + (x * item.Scale), y, item.Row + 0.5 + (z * item.Scale));
                    maxHeight = Math.Max(maxHeight, y);
                }

                if (TryShade(world, ParseColour(face.Color), out Color fill))
                {
                    faces.Add((Centroid(world).Dot(ToCamera), new ScenePolygon([.. world.Select(Screen)], fill, item.Id)));
                }
            }

            polygons.AddRange(faces.OrderBy(f => f.Depth).Select(f => f.Polygon));
        }

        return new SceneProjection(polygons, Bounds(view, scene.ShowGround, maxHeight));
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

    private static Point Screen(Vec3 p) => new((p.X - p.Z) * Cos30, ((p.X + p.Z) * Sin30) - (p.Y * Vertical));

    private static void AddGround(UiScene scene, View view, List<ScenePolygon> polygons)
    {
        Color grass = ParseColour(scene.GroundColor);
        Color grassAlt = Color.FromRgb(Channel(grass.R, 0.94), Channel(grass.G, 0.94), Channel(grass.B, 0.94));
        int c = scene.Columns, r = scene.Rows;
        Vec3[][] sides =
        [
            [new(0, 0, r), new(0, -GroundDepth, r), new(c, -GroundDepth, r), new(c, 0, r)],   // z = r, normal +z
            [new(c, 0, 0), new(c, 0, r), new(c, -GroundDepth, r), new(c, -GroundDepth, 0)],   // x = c, normal +x
            [new(0, 0, 0), new(c, 0, 0), new(c, -GroundDepth, 0), new(0, -GroundDepth, 0)],   // z = 0, normal -z
            [new(0, 0, 0), new(0, -GroundDepth, 0), new(0, -GroundDepth, r), new(0, 0, r)],   // x = 0, normal -x
        ];
        foreach (Vec3[] side in sides)
        {
            Vec3[] world = [.. side.Select(p => view.ToCamera(p.X, p.Y, p.Z))];
            if (TryShade(world, Dirt, out Color fill))
            {
                polygons.Add(new ScenePolygon([.. world.Select(Screen)], fill, null));
            }
        }

        for (int row = 0; row < r; row++)
        {
            for (int column = 0; column < c; column++)
            {
                Vec3[] top = [view.ToCamera(column, 0, row), view.ToCamera(column, 0, row + 1), view.ToCamera(column + 1, 0, row + 1), view.ToCamera(column + 1, 0, row)];
                if (TryShade(top, ((row + column) & 1) == 0 ? grass : grassAlt, out Color fill))
                {
                    polygons.Add(new ScenePolygon([.. top.Select(Screen)], fill, null));
                }
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
