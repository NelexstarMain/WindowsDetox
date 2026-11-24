// Primitive builders of the Focus Forest low-poly models. Every solid is built so that its faces wind counter-clockwise seen from
// outside (the UiSceneFace contract): faces are created in any order and then flipped to point away from a reference direction.
// Undersides of prisms, lathes and limbs are left out because the camera always looks down on the scene.

using Blocker.UI.Sdk;

namespace FocusInsights;

/// <summary>A point or direction in model space.</summary>
internal readonly record struct Vec(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(Dot(this));

    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec operator *(Vec a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Dot(Vec o) => (X * o.X) + (Y * o.Y) + (Z * o.Z);

    public Vec Cross(Vec o) => new((Y * o.Z) - (Z * o.Y), (Z * o.X) - (X * o.Z), (X * o.Y) - (Y * o.X));

    public Vec Normalized() => Length < 1e-12 ? this : this * (1 / Length);
}

/// <summary>A colour with channels 0-255; converts implicitly from <c>"#RRGGBB"</c>.</summary>
internal readonly record struct Rgb(double R, double G, double B)
{
    public string Hex => $"#{Channel(R):X2}{Channel(G):X2}{Channel(B):X2}";

    public static implicit operator Rgb(string hex) => new(
        Convert.ToInt32(hex.Substring(1, 2), 16),
        Convert.ToInt32(hex.Substring(3, 2), 16),
        Convert.ToInt32(hex.Substring(5, 2), 16));

    /// <summary>Blends towards <paramref name="other"/> by <paramref name="t"/> (0-1).</summary>
    public Rgb Mix(Rgb other, double t) => new(R + ((other.R - R) * t), G + ((other.G - G) * t), B + ((other.B - B) * t));

    /// <summary>Multiplies the brightness.</summary>
    public Rgb Shade(double factor) => new(R * factor, G * factor, B * factor);

    /// <summary>A deterministic slight variation of brightness and hue, for a hand-painted look.</summary>
    public Rgb Vary(int seed, double amount)
    {
        double all = 1 + (amount * ((2 * ForestMeshes.Noise(seed)) - 1));
        double each = amount * 0.35;
        return new Rgb(
            R * all * (1 + (each * ((2 * ForestMeshes.Noise(seed + 7919)) - 1))),
            G * all * (1 + (each * ((2 * ForestMeshes.Noise(seed + 15838)) - 1))),
            B * all * (1 + (each * ((2 * ForestMeshes.Noise(seed + 23757)) - 1))));
    }

    private static int Channel(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
}

/// <summary>A set of coloured flat polygons.</summary>
internal sealed class Mesh
{
    private readonly List<Face> _faces = [];

    internal readonly record struct Face(Vec[] Points, Rgb Color);

    public IReadOnlyList<Face> Faces => _faces;

    public int Count => _faces.Count;

    /// <summary>Appends the faces of other meshes; returns this mesh.</summary>
    public Mesh Add(params Mesh[] others)
    {
        foreach (Mesh other in others)
        {
            _faces.AddRange(other._faces);
        }

        return this;
    }

    /// <summary>Adds a polygon, reversed when needed so that its normal points along <paramref name="outward"/>; degenerate polygons are dropped.</summary>
    public void AddFace(Vec[] points, Rgb color, Vec outward)
    {
        Vec normal = Normal(points);
        if (normal.Length < 1e-9)
        {
            return;
        }

        _faces.Add(new Face(normal.Dot(outward) < 0 ? [.. points.Reverse()] : points, color));
    }

    /// <summary>Newell normal of a polygon (length proportional to its area).</summary>
    public static Vec Normal(IReadOnlyList<Vec> polygon)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vec a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }

        return new Vec(nx, ny, nz);
    }

    public Mesh Moved(double x, double y, double z) => Map(p => new Vec(p.X + x, p.Y + y, p.Z + z));

    /// <summary>Scales around the origin; factors must be positive so that winding is kept.</summary>
    public Mesh Scaled(double sx, double sy, double sz) => Map(p => new Vec(p.X * sx, p.Y * sy, p.Z * sz));

    public Mesh Scaled(double s) => Scaled(s, s, s);

    /// <summary>Turns around the vertical axis through the origin, counter-clockwise seen from above (as the host's quarter turns).</summary>
    public Mesh Turned(double degrees)
    {
        (double sin, double cos) = Math.SinCos(degrees * Math.PI / 180);
        return Map(p => new Vec((p.X * cos) - (p.Z * sin), p.Y, (p.X * sin) + (p.Z * cos)));
    }

    /// <summary>Turns around the vertical axis through (<paramref name="cx"/>, <paramref name="cz"/>).</summary>
    public Mesh TurnedAround(double cx, double cz, double degrees) => Moved(-cx, 0, -cz).Turned(degrees).Moved(cx, 0, cz);

    /// <summary>Rotates around the x axis through the origin (positive tips +y towards +z).</summary>
    public Mesh RotatedX(double degrees)
    {
        (double sin, double cos) = Math.SinCos(degrees * Math.PI / 180);
        return Map(p => new Vec(p.X, (p.Y * cos) - (p.Z * sin), (p.Y * sin) + (p.Z * cos)));
    }

    /// <summary>Rotates around the z axis through the origin (positive tips +x towards +y).</summary>
    public Mesh RotatedZ(double degrees)
    {
        (double sin, double cos) = Math.SinCos(degrees * Math.PI / 180);
        return Map(p => new Vec((p.X * cos) - (p.Y * sin), (p.X * sin) + (p.Y * cos), p.Z));
    }

    /// <summary>Recolours the one face that looks most towards <paramref name="direction"/>: a lighter facet that makes a small sphere read as round.</summary>
    public Mesh Highlighted(Vec direction, Rgb color)
    {
        if (_faces.Count == 0)
        {
            return this;
        }

        Vec wanted = direction.Normalized();
        int best = 0;
        for (int i = 1; i < _faces.Count; i++)
        {
            if (Normal(_faces[i].Points).Normalized().Dot(wanted) > Normal(_faces[best].Points).Normalized().Dot(wanted))
            {
                best = i;
            }
        }

        return new Mesh().AddAll(_faces.Select((f, i) => i == best ? new Face(f.Points, color) : f));
    }

    public Mesh Recolored(Rgb color) => new Mesh().AddAll(_faces.Select(f => new Face(f.Points, color)));

    /// <summary>Gives every face a slightly different colour.</summary>
    public Mesh Varied(double amount, int seed) =>
        new Mesh().AddAll(_faces.Select((f, i) => new Face(f.Points, f.Color.Vary(seed + (i * 31), amount))));

    public Mesh Map(Func<Vec, Vec> transform) =>
        new Mesh().AddAll(_faces.Select(f => new Face([.. f.Points.Select(transform)], f.Color)));

    /// <summary>The scene model; coordinates are rounded to 2 decimals and degenerate or duplicate faces are dropped to keep the JSON small.</summary>
    public UiSceneModel ToModel(string key)
    {
        HashSet<string> seen = [];
        List<UiSceneFace> faces = [];
        foreach (Face face in _faces)
        {
            Vec[] rounded = [.. face.Points.Select(p => new Vec(Math.Round(p.X, 2), Math.Round(p.Y is < 0 and > -0.06 ? 0 : p.Y, 2), Math.Round(p.Z, 2)))];
            if (rounded.Distinct().Count() < 3 || Normal(rounded).Length < 1e-7)
            {
                continue;
            }

            string signature = string.Join('|', rounded.Select(p => p.ToString()).Order(StringComparer.Ordinal));
            if (seen.Add(signature))
            {
                faces.Add(new UiSceneFace([.. rounded.SelectMany(p => new[] { p.X, p.Y, p.Z })], face.Color.Hex));
            }
        }

        return new UiSceneModel(key, faces);
    }

    private Mesh AddAll(IEnumerable<Face> faces)
    {
        _faces.AddRange(faces);
        return this;
    }
}

/// <summary>Convex solids and surfaces of revolution; all deterministic.</summary>
internal static class ForestMeshes
{
    private static readonly Dictionary<int, (Vec[] Vertices, int[][] Triangles)> UnitSpheres = [];

    /// <summary>A stable pseudo-random number in [0, 1) for a seed.</summary>
    public static double Noise(int seed)
    {
        unchecked
        {
            uint x = (uint)seed * 2654435761u;
            x ^= x >> 15;
            x *= 2246822519u;
            x ^= x >> 13;
            x *= 3266489917u;
            x ^= x >> 16;
            return x / 4294967296.0;
        }
    }

    /// <summary>An axis-aligned box (five faces: the underside is never seen) standing on <paramref name="y0"/>, centred on (cx, cz), optionally turned around its own centre.</summary>
    public static Mesh Box(double cx, double y0, double cz, double width, double height, double depth, Rgb color, Rgb? top = null, double turn = 0)
    {
        double x0 = cx - (width / 2), x1 = cx + (width / 2), y1 = y0 + height, z0 = cz - (depth / 2), z1 = cz + (depth / 2);
        var centre = new Vec(cx, y0 + (height / 2), cz);
        var mesh = new Mesh();
        void Quad(Rgb c, params Vec[] p) =>
            mesh.AddFace(p, c, new Vec((p.Average(v => v.X)) - centre.X, p.Average(v => v.Y) - centre.Y, p.Average(v => v.Z) - centre.Z));

        Quad(top ?? color, new Vec(x0, y1, z0), new Vec(x0, y1, z1), new Vec(x1, y1, z1), new Vec(x1, y1, z0));
        Quad(color, new Vec(x0, y0, z1), new Vec(x1, y0, z1), new Vec(x1, y1, z1), new Vec(x0, y1, z1));
        Quad(color, new Vec(x1, y0, z1), new Vec(x1, y0, z0), new Vec(x1, y1, z0), new Vec(x1, y1, z1));
        Quad(color, new Vec(x1, y0, z0), new Vec(x0, y0, z0), new Vec(x0, y1, z0), new Vec(x1, y1, z0));
        Quad(color, new Vec(x0, y0, z0), new Vec(x0, y0, z1), new Vec(x0, y1, z1), new Vec(x0, y1, z0));
        return turn == 0 ? mesh : mesh.TurnedAround(cx, cz, turn);
    }

    /// <summary>A prism or frustum along the segment a-b, with an end cap on b (and on a when asked); a radius of 0 makes a point.</summary>
    public static Mesh Limb(Vec a, Vec b, double ra, double rb, int sides, Rgb color, Rgb? cap = null, double rotation = 0, bool capBottom = false, bool capTop = true)
    {
        Vec axis = (b - a).Normalized();
        Vec helper = Math.Abs(axis.Y) > 0.9 ? new Vec(1, 0, 0) : new Vec(0, 1, 0);
        Vec u = helper.Cross(axis).Normalized();
        Vec v = axis.Cross(u);
        double Angle(double i) => (rotation * Math.PI / 180) + (2 * Math.PI * i / sides);
        Vec Ring(int i, double r, Vec centre) => centre + (((u * Math.Cos(Angle(i))) + (v * Math.Sin(Angle(i)))) * r);

        var mesh = new Mesh();
        for (int i = 0; i < sides; i++)
        {
            Vec radial = (u * Math.Cos(Angle(i + 0.5))) + (v * Math.Sin(Angle(i + 0.5)));
            Vec[] side = ra < 1e-6
                ? [a, Ring(i + 1, rb, b), Ring(i, rb, b)]
                : rb < 1e-6
                    ? [Ring(i, ra, a), Ring(i + 1, ra, a), b]
                    : [Ring(i, ra, a), Ring(i + 1, ra, a), Ring(i + 1, rb, b), Ring(i, rb, b)];
            mesh.AddFace(side, color, radial);
        }

        if (rb >= 1e-6 && capTop)
        {
            mesh.AddFace([.. Enumerable.Range(0, sides).Select(i => Ring(i, rb, b))], cap ?? color, axis);
        }

        if (capBottom && ra >= 1e-6)
        {
            mesh.AddFace([.. Enumerable.Range(0, sides).Select(i => Ring(i, ra, a))], cap ?? color, axis * -1);
        }

        return mesh;
    }

    /// <summary>A vertical prism or frustum standing on <paramref name="y0"/>.</summary>
    public static Mesh Prism(int sides, double cx, double y0, double cz, double rBottom, double rTop, double height, Rgb color, Rgb? top = null, double rotation = 0, bool capTop = true) =>
        Limb(new Vec(cx, y0, cz), new Vec(cx, y0 + height, cz), rBottom, rTop, sides, color, top, rotation, false, capTop);

    /// <summary>A vertical cone or pyramid (4 sides) standing on <paramref name="y0"/>.</summary>
    public static Mesh Cone(int sides, double cx, double y0, double cz, double radius, double height, Rgb color, double rotation = 0) =>
        Prism(sides, cx, y0, cz, radius, 0, height, color, null, rotation);

    /// <summary>
    /// A low-poly ellipsoid: an octahedron (detail -1, 8 faces), icosahedron (0, 20 faces) or its subdivision (1, 80 faces), with radial
    /// jitter for organic canopies and a slight colour variation per face.
    /// </summary>
    public static Mesh Blob(Vec centre, Vec radii, Rgb color, int detail = 0, double jitter = 0, int seed = 0, double variation = 0.07, bool keepUnderside = false, Rgb? shadow = null, Rgb? highlight = null, double undersideY = -0.76)
    {
        (Vec[] vertices, int[][] triangles) = UnitSphere(detail);
        Vec[] placed =
        [
            .. vertices.Select((p, i) =>
            {
                double f = 1 + (jitter * ((2 * Noise((seed * 131) + i)) - 1));
                return new Vec(centre.X + (p.X * radii.X * f), centre.Y + (p.Y * radii.Y * f), centre.Z + (p.Z * radii.Z * f));
            }),
        ];
        Rgb dark = color.Mix(shadow ?? "#1F3A4D", 0.32);
        Rgb light = color.Mix(highlight ?? "#FFF4B0", 0.26);
        var mesh = new Mesh();
        for (int t = 0; t < triangles.Length; t++)
        {
            Vec[] face = [placed[triangles[t][0]], placed[triangles[t][1]], placed[triangles[t][2]]];
            Vec middle = (face[0] + face[1] + face[2]) * (1.0 / 3);
            Vec outward = middle - centre;
            Vec faceNormal = Mesh.Normal(face);
            double ny = faceNormal.Normalized().Y * (faceNormal.Dot(outward) < 0 ? -1 : 1);
            if (ny < undersideY && !keepUnderside)
            {
                continue;
            }

            Rgb tone = ny >= 0 ? color.Mix(light, ny) : color.Mix(dark, -ny);
            mesh.AddFace(face, tone.Vary((seed * 977) + (t * 13), variation), outward);
        }

        return mesh;
    }

    public static Mesh Blob(double cx, double cy, double cz, double rx, double ry, double rz, Rgb color, int detail = 0, double jitter = 0, int seed = 0, double variation = 0.07, bool keepUnderside = false, Rgb? shadow = null, Rgb? highlight = null, double undersideY = -0.76) =>
        Blob(new Vec(cx, cy, cz), new Vec(rx, ry, rz), color, detail, jitter, seed, variation, keepUnderside, shadow, highlight, undersideY);

    /// <summary>A single flat polygon lying on y with its normal pointing up (fallen leaves, petals, snow patches).</summary>
    public static Mesh Flat(double cx, double y, double cz, double radius, int sides, Rgb color, double rotation = 0)
    {
        var mesh = new Mesh();
        mesh.AddFace([.. Enumerable.Range(0, sides).Select(i => new Vec(cx + (radius * Math.Cos((rotation * Math.PI / 180) + (2 * Math.PI * i / sides))), y, cz - (radius * Math.Sin((rotation * Math.PI / 180) + (2 * Math.PI * i / sides)))))], color, new Vec(0, 1, 0));
        return mesh;
    }

    /// <summary>A small flat plate on the surface of a vertical trunk of radius <paramref name="radius"/> (bark marks).</summary>
    public static Mesh Patch(double radius, double degrees, double y, double width, double height, Rgb color)
    {
        double a = degrees * Math.PI / 180;
        var radial = new Vec(Math.Cos(a), 0, -Math.Sin(a));
        var tangent = new Vec(-radial.Z, 0, radial.X);
        Vec centre = (radial * radius) + new Vec(0, y, 0);
        var mesh = new Mesh();
        mesh.AddFace(
            [centre - (tangent * (width / 2)) - new Vec(0, height / 2, 0), centre + (tangent * (width / 2)) - new Vec(0, height / 2, 0),
             centre + (tangent * (width / 2)) + new Vec(0, height / 2, 0), centre - (tangent * (width / 2)) + new Vec(0, height / 2, 0)], color, radial);
        return mesh;
    }

    /// <summary>A flat arched leaf blade (two faces per segment, folded along its midrib) growing along +x before the transforms, seen from above.</summary>
    public static Mesh Blade(double length, double width, double elevationDegrees, double droop, double yawDegrees, Rgb a, Rgb b, int segments = 3)
    {
        (double X, double Half, double Y)[] rings = [.. Enumerable.Range(0, segments).Select(k =>
        {
            double f = k / (double)segments;
            return (length * f, width * (0.35 + (0.65 * Math.Sin(Math.PI * Math.Pow(Math.Min(1, f + 0.12), 0.8)))), width * 0.35);
        })];
        var mesh = new Mesh();
        Vec Left(int k) => new(rings[k].X, 0, -rings[k].Half);
        Vec Mid(int k) => new(rings[k].X, rings[k].Y, 0);
        Vec Right(int k) => new(rings[k].X, 0, rings[k].Half);
        for (int k = 0; k + 1 < segments; k++)
        {
            Rgb c = k % 2 == 0 ? a : b;
            mesh.AddFace([Left(k), Mid(k), Mid(k + 1), Left(k + 1)], c, new Vec(0, 1, -0.6));
            mesh.AddFace([Mid(k), Right(k), Right(k + 1), Mid(k + 1)], c.Shade(1.08), new Vec(0, 1, 0.6));
        }

        int last = segments - 1;
        var tip = new Vec(length, 0, 0);
        mesh.AddFace([Left(last), Mid(last), tip], last % 2 == 0 ? a : b, new Vec(0, 1, -0.6));
        mesh.AddFace([Mid(last), Right(last), tip], (last % 2 == 0 ? a : b).Shade(1.08), new Vec(0, 1, 0.6));
        return mesh.Map(p => new Vec(p.X, p.Y - (droop * p.X * p.X), p.Z)).RotatedZ(elevationDegrees).Turned(yawDegrees);
    }

    /// <summary>
    /// A surface of revolution around the vertical axis through (cx, cz). <paramref name="profile"/> lists (radius, y) from the bottom up;
    /// band k between ring k and k+1 gets <paramref name="band"/>(k); a last ring with a radius gets a top cap.
    /// </summary>
    public static Mesh Lathe(int sides, (double R, double Y)[] profile, Func<int, Rgb> band, double cx = 0, double cz = 0, double jitter = 0, int seed = 0, double rotation = 0, Rgb? top = null)
    {
        Vec Point(int ring, int i)
        {
            (double r, double y) = profile[ring];
            double f = r < 1e-6 ? 0 : 1 + (jitter * ((2 * Noise((seed * 53) + (ring * 17) + (i % sides))) - 1));
            double t = (rotation * Math.PI / 180) + (2 * Math.PI * (i % sides) / sides);
            return new Vec(cx + (r * f * Math.Cos(t)), y, cz - (r * f * Math.Sin(t)));
        }

        var mesh = new Mesh();
        for (int k = 0; k + 1 < profile.Length; k++)
        {
            for (int i = 0; i < sides; i++)
            {
                double t = (rotation * Math.PI / 180) + (2 * Math.PI * (i + 0.5) / sides);
                var radial = new Vec(Math.Cos(t), 0, -Math.Sin(t));
                Vec[] face = profile[k].R < 1e-6
                    ? [Point(k, i), Point(k + 1, i + 1), Point(k + 1, i)]
                    : profile[k + 1].R < 1e-6
                        ? [Point(k, i), Point(k, i + 1), Point(k + 1, i)]
                        : [Point(k, i), Point(k, i + 1), Point(k + 1, i + 1), Point(k + 1, i)];
                mesh.AddFace(face, band(k), radial);
            }
        }

        int last = profile.Length - 1;
        if (profile[last].R >= 1e-6)
        {
            mesh.AddFace([.. Enumerable.Range(0, sides).Select(i => Point(last, i))], top ?? band(last - 1), new Vec(0, 1, 0));
        }

        return mesh;
    }

    /// <summary>A gable-roof solid (triangular prism, ridge along x before <paramref name="turn"/>) standing on <paramref name="y0"/>.</summary>
    public static Mesh Gable(double cx, double y0, double cz, double width, double depth, double rise, Rgb roof, Rgb ends, double turn = 0)
    {
        double x0 = cx - (width / 2), x1 = cx + (width / 2), z0 = cz - (depth / 2), z1 = cz + (depth / 2), ridge = y0 + rise;
        var centre = new Vec(cx, y0 + (rise / 3), cz);
        var mesh = new Mesh();
        void Face(Rgb c, params Vec[] p) =>
            mesh.AddFace(p, c, new Vec(p.Average(v => v.X) - centre.X, p.Average(v => v.Y) - centre.Y, p.Average(v => v.Z) - centre.Z));

        Face(roof, new Vec(x0, y0, z1), new Vec(x1, y0, z1), new Vec(x1, ridge, cz), new Vec(x0, ridge, cz));
        Face(roof.Shade(0.94), new Vec(x1, y0, z0), new Vec(x0, y0, z0), new Vec(x0, ridge, cz), new Vec(x1, ridge, cz));
        Face(ends, new Vec(x1, y0, z1), new Vec(x1, y0, z0), new Vec(x1, ridge, cz));
        Face(ends, new Vec(x0, y0, z0), new Vec(x0, y0, z1), new Vec(x0, ridge, cz));
        return turn == 0 ? mesh : mesh.TurnedAround(cx, cz, turn);
    }

    private static (Vec[] Vertices, int[][] Triangles) UnitSphere(int detail)
    {
        if (UnitSpheres.TryGetValue(detail, out (Vec[], int[][]) cached))
        {
            return cached;
        }

        List<Vec> vertices;
        List<int[]> triangles;
        if (detail < 0)
        {
            vertices = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
            triangles = [[0, 2, 4], [4, 2, 1], [1, 2, 5], [5, 2, 0], [4, 3, 0], [1, 3, 4], [5, 3, 1], [0, 3, 5]];
        }
        else
        {
            double t = (1 + Math.Sqrt(5)) / 2;
            vertices = [.. new Vec[]
            {
                new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
                new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
            }.Select(v => v.Normalized())];
            triangles =
            [
                [0, 11, 5], [0, 5, 1], [0, 1, 7], [0, 7, 10], [0, 10, 11], [1, 5, 9], [5, 11, 4], [11, 10, 2], [10, 7, 6], [7, 1, 8],
                [3, 9, 4], [3, 4, 2], [3, 2, 6], [3, 6, 8], [3, 8, 9], [4, 9, 5], [2, 4, 11], [6, 2, 10], [8, 6, 7], [9, 8, 1],
            ];
            for (int level = 0; level < detail; level++)
            {
                Dictionary<(int, int), int> midpoints = [];
                int Mid(int a, int b)
                {
                    (int, int) key = a < b ? (a, b) : (b, a);
                    if (!midpoints.TryGetValue(key, out int index))
                    {
                        index = vertices.Count;
                        vertices.Add(((vertices[a] + vertices[b]) * 0.5).Normalized());
                        midpoints[key] = index;
                    }

                    return index;
                }

                List<int[]> next = [];
                foreach (int[] tri in triangles)
                {
                    int ab = Mid(tri[0], tri[1]), bc = Mid(tri[1], tri[2]), ca = Mid(tri[2], tri[0]);
                    next.Add([tri[0], ab, ca]);
                    next.Add([tri[1], bc, ab]);
                    next.Add([tri[2], ca, bc]);
                    next.Add([ab, bc, ca]);
                }

                triangles = next;
            }
        }

        var result = (vertices.ToArray(), triangles.ToArray());
        UnitSpheres[detail] = result;
        return result;
    }
}
