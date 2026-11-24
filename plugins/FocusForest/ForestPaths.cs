// Path pieces of the Focus Forest village: narrow, trodden footpaths of sandy earth worn into the grass. Each shape is drawn once, in its
// base orientation, and the scene rotates it by quarter turns.
//
// Geometry: the footprint is a centre square plus one arm per open side. Arms are about 0.3 of a tile wide, centred on the edge
// midpoints, with slightly wavy edges and a darker rut along the middle; closed sides of the centre bulge and closed corners are
// chamfered, and a soft fringe of packed earth surrounds everything. Only the points on the tile edge are exact (no wobble), which is
// what makes neighbouring pieces meet seamlessly. Nothing overlaps: the renderer sorts faces by their centre, so overlapping faces
// at equal heights would flicker.

using Blocker.UI.Sdk;
using static FocusForest.ForestMeshes;

namespace FocusForest;

/// <summary>How a path tile connects to its four neighbours.</summary>
public enum ForestPathShape
{
    /// <summary>No neighbour: a small paved spot.</summary>
    Single,

    /// <summary>One neighbour.</summary>
    End,

    /// <summary>Two opposite neighbours.</summary>
    Straight,

    /// <summary>Two neighbouring sides at a right angle.</summary>
    Corner,

    /// <summary>Three neighbours.</summary>
    Tee,

    /// <summary>Four neighbours.</summary>
    Cross,
}

/// <summary>A path tile of the village. <paramref name="Rotation"/> is quarter turns (0-3), as in <c>UiSceneItem.Rotation</c>.</summary>
public sealed record ForestPathTile(int Column, int Row, ForestPathShape Shape, int Rotation);

/// <summary>Scene models of the path shapes.</summary>
public static class ForestPaths
{
    /// <summary>
    /// Scene scale that makes a path piece fill its whole tile: the renderer draws every item at 0.85 of its tile so neighbours keep some
    /// room, which would leave gaps between pieces. Rounded down so that the pieces stay under the height that casts a blob shadow.
    /// </summary>
    public const double ItemScale = 1.17647;

    /// <summary>Half the width of the trodden strip, at the tile edge too.</summary>
    public const double HalfWidth = 0.15;

    /// <summary>Width of the soft fringe around the strip.</summary>
    public const double FringeWidth = 0.04;

    private const double RutHalfWidth = 0.022;
    private const double Height = 0.01;
    private const double Wobble = 0.02;
    private static readonly double[] Stations = [HalfWidth, 0.325, 0.5];
    private static readonly Rgb[] Sands = ["#D8C08A", "#D0B67D", "#C9A96E"];
    private static readonly Rgb Rut = "#B8995F";
    private static readonly Rgb Fringe = "#BEA36B";
    private static readonly Rgb[] Pebbles = ["#A8A196", "#8F897F"];
    private static readonly Rgb Tuft = "#5F9843";

    private static readonly object Gate = new();
    private static readonly Dictionary<ForestPathShape, UiSceneModel> Cache = [];

    /// <summary>The key of the model of a shape, e.g. <c>path-corner</c>.</summary>
    public static string Key(ForestPathShape shape) => "path-" + shape.ToString().ToLowerInvariant();

    /// <summary>
    /// The model of a shape in its base orientation. Its arms open towards: End east; Straight east and west; Corner east and south;
    /// Tee east, south and west; Cross all four (east is +x, south is +z; one quarter turn takes east to south).
    /// </summary>
    public static UiSceneModel Model(ForestPathShape shape)
    {
        lock (Gate)
        {
            if (!Cache.TryGetValue(shape, out UiSceneModel? model))
            {
                model = Build(shape).ToModel(Key(shape));
                Cache[shape] = model;
            }

            return model;
        }
    }

    /// <summary>Directions of a shape's arms in its base orientation, as quarter turns from east: 0 east, 1 south, 2 west, 3 north.</summary>
    internal static IReadOnlyList<int> BaseArms(ForestPathShape shape) => shape switch
    {
        ForestPathShape.End => [0],
        ForestPathShape.Straight => [0, 2],
        ForestPathShape.Corner => [0, 1],
        ForestPathShape.Tee => [0, 1, 2],
        ForestPathShape.Cross => [0, 1, 2, 3],
        _ => [],
    };

    private static (double X, double Z) Turn(double x, double z, int turns) => (turns & 3) switch
    {
        1 => (-z, x),
        2 => (-x, -z),
        3 => (z, -x),
        _ => (x, z),
    };

    // A point in arm coordinates (u along the arm from the centre, v across) of direction d, lying on the ground.
    private static Vec Ground(double u, double v, int d)
    {
        (double x, double z) = Turn(u, v, d);
        return new Vec(x, Height, z);
    }

    private static Mesh Build(ForestPathShape shape)
    {
        IReadOnlyList<int> arms = BaseArms(shape);
        bool Open(int d) => arms.Contains(((d % 4) + 4) % 4);
        var mesh = new Mesh();
        Vec up = new(0, 1, 0);
        void Face(Rgb colour, params Vec[] points) => mesh.AddFace(points, colour, up);

        // Wobble of an arm edge at each station (zero at both ends: the centre corner and the tile edge stay exact).
        double Edge(int d, int side, int station) =>
            HalfWidth + (station is 0 or 2 ? 0 : Wobble * ((2 * Noise((d * 31) + (side * 7) + 3)) - 1));

        // The outline of the whole footprint, going round the centre; the vertex after a Break has no edge back (the open end of an arm).
        List<(Vec P, bool Break)> outline = [];
        List<(Vec P, bool Rut)> centre = [];
        for (int d = 0; d < 4; d++)
        {
            if (Open(d))
            {
                for (int k = 1; k < Stations.Length; k++)
                {
                    outline.Add((Ground(Stations[k], -Edge(d, 0, k), d), false));
                }

                outline[^1] = (outline[^1].P, true);
                for (int k = Stations.Length - 1; k >= 1; k--)
                {
                    outline.Add((Ground(Stations[k], Edge(d, 1, k), d), false));
                }

                centre.Add((Ground(HalfWidth, -RutHalfWidth, d), false));
                centre.Add((Ground(HalfWidth, RutHalfWidth, d), true));
            }
            else
            {
                Vec bulge = Ground(HalfWidth + 0.02, 0, d);
                outline.Add((bulge, false));
                centre.Add((bulge, false));
            }

            // The corner after side d: chamfered when both sides are closed, which rounds the ends of the path.
            if (!Open(d) && !Open(d + 1))
            {
                Vec before = Ground(HalfWidth, HalfWidth - 0.05, d), after = Ground(HalfWidth, -HalfWidth + 0.05, d + 1);
                outline.Add((before, false));
                outline.Add((after, false));
                centre.Add((before, false));
                centre.Add((after, false));
            }
            else
            {
                Vec corner = Ground(HalfWidth, HalfWidth, d);
                outline.Add((corner, false));
                centre.Add((corner, false));
            }
        }

        for (int i = 0; i < centre.Count; i++)
        {
            (Vec a, bool rut) = centre[i];
            Vec b = centre[(i + 1) % centre.Count].P;
            Face(rut ? Rut : Sands[(int)(Noise(i * 17) * Sands.Length)].Vary(i, 0.03), new Vec(0, Height, 0), a, b);
        }

        foreach (int d in arms)
        {
            for (int k = 0; k < Stations.Length - 1; k++)
            {
                double l0 = -Edge(d, 0, k), l1 = -Edge(d, 0, k + 1), r0 = Edge(d, 1, k), r1 = Edge(d, 1, k + 1);
                int tone = (d * 7) + k;
                Face(Sands[(int)(Noise(tone * 3 + 1) * Sands.Length)].Vary(tone, 0.03), Ground(Stations[k], l0, d), Ground(Stations[k], -RutHalfWidth, d), Ground(Stations[k + 1], -RutHalfWidth, d), Ground(Stations[k + 1], l1, d));
                Face(Rut, Ground(Stations[k], -RutHalfWidth, d), Ground(Stations[k], RutHalfWidth, d), Ground(Stations[k + 1], RutHalfWidth, d), Ground(Stations[k + 1], -RutHalfWidth, d));
                Face(Sands[(int)(Noise(tone * 3 + 2) * Sands.Length)].Vary(tone + 50, 0.03), Ground(Stations[k], RutHalfWidth, d), Ground(Stations[k], r0, d), Ground(Stations[k + 1], r1, d), Ground(Stations[k + 1], RutHalfWidth, d));
            }

            // One pebble and one tuft of grass on the grass beside the fringe, on opposite sides.
            int side = Noise(d * 5 + 1) < 0.5 ? -1 : 1;
            Vec pebble = Ground(0.3 + (0.08 * Noise(d + 40)), side * (HalfWidth + FringeWidth + 0.09), d);
            mesh.Add(Flat(pebble.X, Height, pebble.Z, 0.016, 5, Pebbles[d % 2], d * 20));
            Vec tuft = Ground(0.28 + (0.1 * Noise(d + 50)), -side * (HalfWidth + FringeWidth + 0.06), d);
            mesh.Add(Flat(tuft.X, Height, tuft.Z, 0.03, 3, Tuft, d * 35));
        }

        // The fringe: a strip of packed earth round every real edge of the outline, pushed outwards along the vertex normals.
        int n = outline.Count;
        (double X, double Z) Normal(int i)
        {
            double x = 0, z = 0;
            foreach ((int from, int to) in new[] { ((i + n - 1) % n, i), (i, (i + 1) % n) })
            {
                if (outline[from].Break)
                {
                    continue;
                }

                double dx = outline[to].P.X - outline[from].P.X, dz = outline[to].P.Z - outline[from].P.Z, length = Math.Sqrt((dx * dx) + (dz * dz));
                (double nx, double nz) = (dz / length, -dx / length);
                double outward = (nx * (outline[from].P.X + outline[to].P.X)) + (nz * (outline[from].P.Z + outline[to].P.Z));
                (nx, nz) = outward < 0 ? (-nx, -nz) : (nx, nz);
                x += nx;
                z += nz;
            }

            double norm = Math.Sqrt((x * x) + (z * z));
            return (x / norm, z / norm);
        }

        for (int i = 0; i < n; i++)
        {
            if (outline[i].Break)
            {
                continue;
            }

            int j = (i + 1) % n;
            (double ax, double az) = Normal(i);
            (double bx, double bz) = Normal(j);
            Vec a = outline[i].P, b = outline[j].P;
            Face(Fringe.Vary(i + 90, 0.03), a, b, new Vec(b.X + (bx * FringeWidth), Height, b.Z + (bz * FringeWidth)), new Vec(a.X + (ax * FringeWidth), Height, a.Z + (az * FringeWidth)));
        }

        return mesh;
    }
}
