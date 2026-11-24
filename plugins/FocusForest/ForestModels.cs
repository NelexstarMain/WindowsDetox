// Low-poly models of the Focus Forest: stylised trees, bushes and decorations built from the primitives of ForestMeshes.
// Footprints stay within one tile (about +-0.5), models stand on y = 0, trees grow up to about y 2.2 when majestic.

using Blocker.UI.Sdk;
using static FocusForest.ForestMeshes;

namespace FocusForest;

/// <summary>Scene models of every catalogue entry and stage.</summary>
public static class ForestModels
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, UiSceneModel> Cache = [];

    private static readonly Rgb Bark = "#8A6446";
    private static readonly Rgb Soil = "#7A5A43";

    /// <summary>The model of a tree or bush stage, or of a decoration (stage ignored); its key is <see cref="ForestCatalog.ModelKey"/>.</summary>
    public static UiSceneModel For(ForestSpecies species, ForestStage stage)
    {
        string key = ForestCatalog.ModelKey(species, stage);
        lock (Gate)
        {
            if (!Cache.TryGetValue(key, out UiSceneModel? model))
            {
                model = Build(species, stage).ToModel(key);
                Cache[key] = model;
            }

            return model;
        }
    }

    private static Mesh Build(ForestSpecies species, ForestStage stage)
    {
        int level = stage switch { ForestStage.Sapling => 0, ForestStage.Young => 1, ForestStage.Mature => 2, ForestStage.Majestic => 3, _ => -1 };
        return species.Id switch
        {
            "rock" => Rock(),
            "mushrooms" => Mushrooms(),
            "flowers" => FlowerBed(),
            "bench" => Bench(),
            "lantern" => Lantern(),
            "pond" => Pond(),
            "well" => Well(),
            "cabin" => Cabin(),
            _ when level == 0 => Sapling(species.Id),
            _ when level < 0 => Withered(species.Id),
            "oak" => Oak(level),
            "pine" => Conifer(level, pine: true),
            "birch" => Birch(level),
            "maple" => Maple(level),
            "cherry" => Cherry(level),
            "spruce" => Conifer(level, pine: false),
            "willow" => Willow(level),
            "palm" => Palm(level),
            "apple" => Apple(level),
            "cypress" => Cypress(level),
            "bush" => RoundBush(level),
            "rose" => Rose(level),
            "berry" => Blueberry(level),
            "fern" => Fern(level),
            _ => throw new ArgumentException($"Unknown catalogue entry '{species.Id}'.", nameof(species)),
        };
    }

    private static Vec Polar(double radius, double degrees, double y)
    {
        double a = degrees * Math.PI / 180;
        return new Vec(radius * Math.Cos(a), y, -radius * Math.Sin(a));
    }

    private static Vec TrunkAt(double height, double lean, double f) => new(lean * f * f, height * f, 0);

    // Two tapered segments and a flared foot; the joint is capped only on top.
    private static Mesh Trunk(double height, double rBottom, double rTop, Rgb color, double lean = 0)
    {
        double rMid = rBottom + ((rTop - rBottom) * 0.5);
        return new Mesh()
            .Add(Limb(TrunkAt(height, lean, 0), TrunkAt(height, lean, 0.5), rBottom, rMid, 5, color, null, 0, false, false))
            .Add(Limb(TrunkAt(height, lean, 0.5), TrunkAt(height, lean, 1), rMid, rTop, 5, color.Shade(0.93), color.Shade(1.12), 12))
            .Add(Prism(5, 0, 0, 0, rBottom * 1.7, rBottom * 1.05, height * 0.12, color.Shade(0.86), null, 20, false));
    }

    private static Mesh Roots(double radius, Rgb color, int count, int seed)
    {
        var mesh = new Mesh();
        for (int i = 0; i < count; i++)
        {
            double angle = (i * 360.0 / count) + (40 * Noise(seed + i));
            mesh.Add(Limb(Polar(radius * 0.5, angle, 0.11), Polar(radius * 2.5, angle, 0), radius * 0.6, 0, 3, color.Shade(0.88)));
        }

        return mesh;
    }

    private static Mesh Tuft(double x, double z, Rgb color)
    {
        var mesh = new Mesh();
        for (int i = 0; i < 3; i++)
        {
            Vec from = new Vec(x, 0, z) + Polar(0.02, i * 120, 0);
            mesh.Add(Limb(from, from + Polar(0.05, (i * 120) + 30, 0.12 + (0.04 * i)), 0.02, 0, 3, color.Shade(0.85 + (0.12 * i))));
        }

        return mesh;
    }

    private static Mesh Branch(Vec from, Vec to, double radius, Rgb color) => Limb(from, to, radius, radius * 0.5, 4, color, color.Shade(1.1));

    // A round berry, fruit, flower or cone: a low-poly sphere with one lighter facet so that it reads as drawn, not as a gem.
    private static Mesh Fruit(double x, double y, double z, double r, Rgb color, int detail = 0) =>
        Blob(x, y, z, r, r, r, color, detail, 0, 0, 0.05, false, null, null, detail < 0 ? -0.76 : -0.5).Highlighted(new Vec(0.4, 0.8, 0.45), color.Mix("#FFFFFF", 0.5));

    // A berry or blossom resting on the foliage: its centre sits a little outside the surface, along the outward direction.
    private static Mesh Berry((Vec At, Vec Outward) spot, double r, Rgb color)
    {
        Vec centre = spot.At + (spot.Outward.Normalized() * (0.35 * r));
        return Fruit(centre.X, centre.Y, centre.Z, r, color);
    }

    // A point on the upper outer shell of a canopy blob (where the isometric camera sees it) and the direction pointing out of the blob there.
    private static (Vec At, Vec Outward) OnSurface((double X, double Y, double Z, double R, double S) blob, int index, int seed, double lowest = 0.35, double highest = 0.9)
    {
        double lift = lowest + ((highest - lowest) * Noise(seed + (index * 3)));
        Vec ring = Polar(Math.Sqrt(Math.Max(0.05, 1 - (lift * lift))), (index * 137.5) + (30 * Noise(seed + (index * 5) + 1)), 0);
        return (new Vec(blob.X + (ring.X * blob.R), blob.Y + (lift * blob.R * blob.S), blob.Z + (ring.Z * blob.R)), new Vec(ring.X, lift, ring.Z));
    }

    private readonly record struct Palette(Rgb Dark, Rgb Mid, Rgb Light, Rgb? Shadow = null, Rgb? Glow = null)
    {
        public Rgb At(double t) => t < 0.5 ? Dark.Mix(Mid, Math.Clamp(t, 0, 1) * 2) : Mid.Mix(Light, (Math.Clamp(t, 0, 1) - 0.5) * 2);
    }

    // Offset blobs of different sizes; their colour runs from the dark palette end at the low blobs to the light end at the high ones.
    private static Mesh Canopy(Palette palette, int seed, double low, double high, params (double X, double Y, double Z, double R, double Squash)[] blobs) =>
        Canopy(palette, seed, low, high, 0.14, blobs);

    private static Mesh Canopy(Palette palette, int seed, double low, double high, double jitter, params (double X, double Y, double Z, double R, double Squash)[] blobs)
    {
        var mesh = new Mesh();
        for (int i = 0; i < blobs.Length; i++)
        {
            (double x, double y, double z, double r, double squash) = blobs[i];
            mesh.Add(Blob(x, y, z, r, r * squash, r, palette.At((y - low) / (high - low)), 0, jitter, seed + i, 0.07, false, palette.Shadow, palette.Glow));
        }

        return mesh;
    }

    private static Mesh Sapling(string id)
    {
        (Rgb leaf, double length, double scale) = id switch
        {
            "pine" => ((Rgb)"#2E8B57", 1.0, 1.0),
            "spruce" => ("#2F7D6A", 1.0, 1.0),
            "cypress" => ("#2D6A4F", 0.9, 1.0),
            "willow" => ("#A5CC6A", 1.3, 1.0),
            "palm" => ("#4FAE55", 1.4, 1.0),
            "birch" => ("#A9D262", 1.0, 1.0),
            "maple" => ("#E2561B", 1.0, 1.0),
            "cherry" => ("#F4A9C6", 1.0, 1.0),
            "rose" => ("#3F9A55", 1.0, 0.85),
            "berry" => ("#53A07F", 1.0, 0.85),
            "bush" => ("#5FAE4F", 1.0, 0.85),
            "fern" => ("#7CCB5B", 1.3, 0.55),
            _ => ("#6DBB4A", 1.0, 1.0),
        };
        var mesh = new Mesh();
        mesh.Add(Prism(7, 0, 0, 0, 0.19, 0.11, 0.05, Soil, "#8A6A4E", 10));
        mesh.Add(Limb(new Vec(0, 0.04, 0), new Vec(0.012, 0.22, 0), 0.026, 0.014, 4, "#8DBB5A", "#A5D06E"));
        for (int s = -1; s <= 1; s += 2)
        {
            Rgb c = s < 0 ? leaf : leaf.Mix("#FFFFFF", 0.2);
            Mesh blade = Blob(s * 0.09 * length, 0, 0, 0.1 * length, 0.03, 0.05, c, -1, 0, 0, 0.05);
            mesh.Add(blade.RotatedZ(s * 26).Turned(s * 25).Moved(0.012, 0.22, 0));
        }

        return scale < 1 ? mesh.Scaled(scale) : mesh;
    }

    private static Mesh Oak(int level)
    {
        Palette leaf = new("#2D7A3F", "#58A74A", "#9BD05F");
        Rgb bark = "#7B5A3E";
        var mesh = new Mesh();
        switch (level)
        {
            case 1:
                mesh.Add(Trunk(0.36, 0.075, 0.055, bark));
                mesh.Add(Canopy(leaf, 11, 0.4, 0.9, (0.02, 0.56, 0, 0.3, 0.8), (0.18, 0.48, 0.1, 0.19, 0.8), (-0.15, 0.5, -0.1, 0.2, 0.8)));
                break;
            case 2:
                mesh.Add(Trunk(0.5, 0.11, 0.075, bark));
                mesh.Add(Branch(TrunkAt(0.5, 0, 0.7), new Vec(0.24, 0.78, 0.06), 0.045, bark));
                mesh.Add(Branch(TrunkAt(0.5, 0, 0.75), new Vec(-0.22, 0.76, -0.06), 0.042, bark));
                mesh.Add(Roots(0.11, bark, 3, 5));
                mesh.Add(Canopy(leaf, 21, 0.7, 1.5, (0, 0.86, 0, 0.42, 0.72), (0.26, 0.92, 0.1, 0.24, 0.8), (-0.25, 0.97, -0.1, 0.23, 0.8), (0.08, 1.2, 0.1, 0.3, 0.82), (-0.1, 1.34, -0.12, 0.2, 0.9)));
                break;
            default:
                mesh.Add(Trunk(0.6, 0.15, 0.1, bark));
                mesh.Add(Branch(TrunkAt(0.6, 0, 0.65), new Vec(0.28, 0.98, 0.06), 0.055, bark));
                mesh.Add(Branch(TrunkAt(0.6, 0, 0.7), new Vec(-0.26, 0.98, -0.08), 0.05, bark));
                mesh.Add(Roots(0.15, bark, 3, 7));
                mesh.Add(Canopy(leaf, 31, 0.9, 2.1, (0, 1.12, 0, 0.52, 0.66), (0.27, 1.32, 0.1, 0.28, 0.8), (-0.27, 1.32, -0.1, 0.28, 0.8), (0.03, 1.58, 0.03, 0.42, 0.74), (-0.04, 1.84, 0.03, 0.3, 0.84)));
                mesh.Add(Tuft(0.3, -0.3, "#6BB84F"));
                break;
        }

        return mesh;
    }

    private static Mesh Conifer(int level, bool pine)
    {
        static (double[] Radii, double[] Bottoms, double Height) Tiers(double[] radii, double[] bottoms, double height) => (radii, bottoms, height);
        (double[] radii, double[] bottoms, double height) = (level, pine) switch
        {
            (1, true) => Tiers([0.3, 0.22, 0.14], [0.22, 0.46, 0.7], 0.2),
            (2, true) => Tiers([0.4, 0.34, 0.27, 0.19], [0.3, 0.62, 0.94, 1.26], 0.26),
            (3, true) => Tiers([0.47, 0.42, 0.37, 0.31, 0.24, 0.15], [0.3, 0.6, 0.9, 1.2, 1.5, 1.8], 0.28),
            (1, false) => Tiers([0.27, 0.21, 0.15, 0.09], [0.16, 0.34, 0.52, 0.7], 0.34),
            (2, false) => Tiers([0.34, 0.3, 0.25, 0.19, 0.13, 0.07], [0.18, 0.4, 0.62, 0.84, 1.06, 1.28], 0.42),
            _ => Tiers([0.4, 0.36, 0.32, 0.27, 0.22, 0.17, 0.12, 0.07], [0.2, 0.43, 0.66, 0.89, 1.12, 1.35, 1.58, 1.81], 0.44),
        };
        Palette green = pine ? new("#25673F", "#3D8D52", "#74C26E") : new("#244F55", "#356F66", "#5F9C88");
        Rgb trunk = "#7A5A3E";
        var mesh = new Mesh();
        mesh.Add(Prism(5, 0, 0, 0, 0.1 + (0.012 * level), 0.06, bottoms[0] + (pine ? 0 : 0.06), trunk.Shade(0.9), null, 0, false));
        for (int i = 0; i < radii.Length; i++)
        {
            double f = radii.Length == 1 ? 0 : i / (radii.Length - 1.0);
            bool last = i == radii.Length - 1;
            if (pine)
            {
                // Layered plates with a visible piece of trunk in every gap; each piece is its own mesh so it sorts with its tier.
                mesh.Add(last
                    ? Cone(7, 0, bottoms[i], 0, radii[i], height * 1.2, green.At(f), i * 25.7).Varied(0.07, i * 17)
                    : Prism(7, 0, bottoms[i], 0, radii[i], radii[i] * 0.34, height, green.At(f), green.At(f + 0.2), i * 25.7).Varied(0.07, i * 17));
                if (!last)
                {
                    mesh.Add(Prism(4, 0, bottoms[i] + height, 0, 0.05, 0.045, bottoms[i + 1] - bottoms[i] - height + 0.02, trunk, null, 0, false));
                }
            }
            else
            {
                double th = height * (1 - (0.12 * f));
                mesh.Add(Cone(7, 0, bottoms[i], 0, radii[i], th, green.At(f), i * 25.7).Varied(0.07, i * 17));
                // The cap radius follows the cone's own taper at the cap's lower edge, so the snow lies on the tier instead of hanging off it.
                double capStart = th * 0.55;
                mesh.Add(Cone(7, 0, bottoms[i] + capStart, 0, (radii[i] * 0.45) + 0.012, (th - capStart) + 0.01, i % 2 == 0 ? "#EAF2F8" : "#D7E5EE", i * 25.7).Varied(0.03, i));
            }
        }

        double top = bottoms[^1];
        if (pine && level == 3)
        {
            for (int i = 0; i < 4; i++)
            {
                Vec p = Polar(0.36 - (0.05 * i), (i * 95) + 20, bottoms[i] + height + 0.03);
                mesh.Add(Fruit(p.X, p.Y, p.Z, 0.045, i % 2 == 0 ? "#8B5A2B" : "#A56A35", -1));
            }

            mesh.Add(Tuft(0.34, 0.3, "#6BB84F"));
            mesh.Add(Fruit(0, top + 0.34, 0, 0.05, "#FFD54F", -1));
        }

        if (pine && level == 2)
        {
            mesh.Add(Fruit(0.22, 0.62 + height, 0.12, 0.045, "#8B5A2B", -1));
        }

        if (!pine)
        {
            mesh.Add(Flat(0.3, 0.01, 0.2, 0.09, 6, "#F6F9FC", 10));
            if (level >= 2)
            {
                mesh.Add(Flat(-0.28, 0.01, -0.2, 0.09, 6, "#E9F0F6", 40));
            }

            if (level == 3)
            {
                mesh.Add(Flat(-0.2, 0.01, 0.3, 0.08, 5, "#F6F9FC", 20));
                mesh.Add(Fruit(0, top + 0.42, 0, 0.05, "#FFF3B0", -1));
            }
        }

        return mesh;
    }

    private static Mesh Birch(int level)
    {
        Palette leaf = new("#82B64D", "#B2D766", "#E6F28F", "#3A5F2E", "#FFF9B0");
        double h = level switch { 1 => 0.6, 2 => 1.0, _ => 1.38 };
        double r0 = level switch { 1 => 0.045, 2 => 0.06, _ => 0.075 };
        var mesh = new Mesh();
        mesh.Add(Trunk(h, r0, r0 * 0.55, "#EFEDE3"));
        for (int i = 0; i < (level * 3) - 1; i++)
        {
            double f = 0.12 + (0.76 * ((i * 0.41) % 1));
            double r = (r0 + (((r0 * 0.55) - r0) * f)) * 0.9;
            mesh.Add(Patch(r + 0.004, (i * 97) + 20, h * f, 0.06 + (0.025 * (i % 2)), 0.03, "#26262B"));
        }

        switch (level)
        {
            case 1:
                mesh.Add(Canopy(leaf, 41, 0.5, 1.0, (0, 0.76, 0, 0.19, 1.3), (0.1, 0.62, 0.05, 0.13, 1.2)));
                break;
            case 2:
                mesh.Add(Branch(TrunkAt(h, 0, 0.6), new Vec(0.17, 1.02, 0.04), 0.022, "#E6E3D8"));
                mesh.Add(Branch(TrunkAt(h, 0, 0.7), new Vec(-0.15, 1.1, -0.04), 0.02, "#E6E3D8"));
                mesh.Add(Canopy(leaf, 51, 0.7, 1.65, (0.02, 1.3, 0, 0.26, 1.3), (0.17, 1.12, 0.05, 0.15, 1.15), (-0.15, 1.15, -0.05, 0.15, 1.15)));
                break;
            default:
                mesh.Add(Branch(TrunkAt(h, 0, 0.5), new Vec(0.24, 1.12, 0.04), 0.03, "#E6E3D8"));
                mesh.Add(Branch(TrunkAt(h, 0, 0.62), new Vec(-0.22, 1.26, -0.05), 0.028, "#E6E3D8"));
                // One tall oval crown with overlapping satellites; the pale twigs hang from its rim.
                mesh.Add(Canopy(leaf, 61, 0.9, 2.2, (0.03, 1.6, 0, 0.34, 1.3), (0.22, 1.42, 0.05, 0.2, 1.15), (-0.2, 1.46, -0.06, 0.2, 1.15), (0.04, 1.9, 0.04, 0.2, 1.2), (0, 1.32, 0.2, 0.18, 1.1)));
                for (int i = 0; i < 3; i++)
                {
                    Vec from = Polar(0.3, (i * 120) + 40, 1.38);
                    mesh.Add(Limb(from, from + Polar(0.02, (i * 120) + 40, -0.24), 0.012, 0.004, 3, "#E6E3D8", null, 0, false, false));
                }

                mesh.Add(Tuft(0.3, 0.3, "#6BB84F"));
                break;
        }

        return mesh;
    }

    private static Mesh Maple(int level)
    {
        Palette leaf = new("#A12818", "#E4601C", "#F8B93C", "#4A1020", "#FFE08A");
        Rgb bark = "#6B4630";
        var mesh = new Mesh();
        switch (level)
        {
            case 1:
                mesh.Add(Trunk(0.36, 0.07, 0.05, bark));
                mesh.Add(Canopy(leaf, 71, 0.4, 0.9, (0, 0.55, 0, 0.3, 0.7), (0.17, 0.5, 0.08, 0.19, 0.8), (-0.14, 0.62, -0.1, 0.19, 0.8)));
                break;
            case 2:
                mesh.Add(Trunk(0.5, 0.1, 0.07, bark));
                mesh.Add(Branch(TrunkAt(0.5, 0, 0.7), new Vec(0.24, 0.78, 0.05), 0.04, bark));
                mesh.Add(Branch(TrunkAt(0.5, 0, 0.75), new Vec(-0.22, 0.76, -0.05), 0.038, bark));
                mesh.Add(Roots(0.1, bark, 3, 11));
                mesh.Add(Canopy(leaf, 81, 0.7, 1.5, (0, 0.82, 0, 0.44, 0.6), (0.24, 1.0, 0.1, 0.28, 0.7), (-0.22, 1.05, -0.1, 0.28, 0.7), (0.05, 1.3, 0.05, 0.3, 0.75), (-0.04, 1.5, 0, 0.17, 0.9)));
                mesh.Add(Flat(0.34, 0.01, 0.2, 0.05, 4, "#E2561B", 20));
                mesh.Add(Flat(-0.3, 0.01, 0.3, 0.05, 4, "#F0A030", 50));
                mesh.Add(Flat(0.1, 0.01, -0.38, 0.05, 4, "#C0391E", 70));
                break;
            default:
                mesh.Add(Trunk(0.62, 0.14, 0.095, bark));
                mesh.Add(Branch(TrunkAt(0.62, 0, 0.65), new Vec(0.3, 0.96, 0.06), 0.052, bark));
                mesh.Add(Branch(TrunkAt(0.62, 0, 0.7), new Vec(-0.28, 0.96, -0.08), 0.05, bark));
                mesh.Add(Roots(0.14, bark, 3, 13));
                mesh.Add(Canopy(leaf, 101, 0.9, 2.1, (0, 1.0, 0, 0.5, 0.62), (0.27, 1.18, 0.1, 0.27, 0.72), (-0.26, 1.2, -0.1, 0.27, 0.72), (0.04, 1.46, 0.04, 0.4, 0.72), (-0.04, 1.74, 0, 0.27, 0.82)));
                for (int i = 0; i < 9; i++)
                {
                    Vec p = Polar(0.2 + (0.2 * Noise(111 + i)), i * 40 + 10, 0.01);
                    mesh.Add(Flat(p.X, 0.01, p.Z, 0.045, 4, i % 3 == 0 ? "#E2561B" : i % 3 == 1 ? "#F0A030" : "#C0391E", i * 31));
                }

                break;
        }

        return mesh;
    }

    private static Mesh Cherry(int level)
    {
        Palette leaf = new("#D8719B", "#F4A3C3", "#FFDCE9", "#7A2A5E", "#FFFFFF");
        Rgb bark = "#5A3A33";
        var mesh = new Mesh();
        // A low, wide umbrella centred on the top of the leaning trunk; every stage stays well under an oak of the same stage.
        (double X, double Y, double Z, double R, double S)[] crown;
        int blossoms;
        switch (level)
        {
            case 1:
                mesh.Add(Trunk(0.32, 0.07, 0.045, bark, 0.05));
                crown = [(0.05, 0.38, 0, 0.27, 0.55), (0.2, 0.34, 0.08, 0.17, 0.6), (-0.1, 0.35, -0.08, 0.17, 0.6)];
                blossoms = 0;
                mesh.Add(Flat(0.3, 0.01, 0.2, 0.04, 5, "#FFC1D6", 10));
                mesh.Add(Flat(-0.25, 0.01, -0.28, 0.04, 5, "#FFE0EA", 40));
                break;
            case 2:
                mesh.Add(Trunk(0.46, 0.1, 0.06, bark, 0.08));
                mesh.Add(Roots(0.1, bark, 3, 15));
                crown = [(0.08, 0.74, 0, 0.36, 0.52), .. Enumerable.Range(0, 4).Select(i => { Vec p = Polar(0.26, (i * 90) + 20, 0.68); return (0.08 + p.X, p.Y, p.Z, 0.19, 0.55); })];
                for (int i = 0; i < 2; i++)
                {
                    Vec to = Polar(0.2, (i * 180) + 20, 0.66) + new Vec(0.08, 0, 0);
                    mesh.Add(Branch(TrunkAt(0.46, 0.08, 0.72 + (0.08 * i)), to, 0.04 - (0.004 * i), bark));
                }

                blossoms = 3;
                break;
            default:
                mesh.Add(Trunk(0.62, 0.14, 0.08, bark, 0.08));
                mesh.Add(Roots(0.14, bark, 3, 17));
                crown =
                [
                    (0.06, 1.06, 0, 0.46, 0.5),
                    .. Enumerable.Range(0, 4).Select(i => { Vec p = Polar(0.27, (i * 90) + 20, 1.0 + (0.04 * (i % 2))); return (0.06 + p.X, p.Y, p.Z, 0.2, 0.6); }),
                    (0.06, 1.2, 0.02, 0.28, 0.6),
                ];
                for (int i = 0; i < 3; i++)
                {
                    Vec to = Polar(0.24, (i * 120) + 60, 0.98) + new Vec(0.06, 0, 0);
                    mesh.Add(Branch(TrunkAt(0.62, 0.08, 0.75 + (0.07 * i)), to, 0.048 - (0.005 * i), bark));
                }

                blossoms = 4;
                break;
        }

        if (level >= 2)
        {
            mesh.Add(Canopy(leaf, 121 + (10 * level), 0.4, 1.4, 0.1, crown));
            for (int i = 0; i < blossoms; i++)
            {
                mesh.Add(Berry(OnSurface(crown[i % 2 == 0 ? 0 : 1 + (i % 4)], i, 161), 0.07, i % 2 == 0 ? "#FFEEF4" : "#FFC2D8"));
            }

            for (int i = 0; i < 4 + (2 * (level - 2)); i++)
            {
                Vec p = Polar(0.18 + (0.25 * Noise(181 + i)), (i * 60) + 10, 0);
                mesh.Add(Flat(p.X, 0.01, p.Z, 0.04, 5, i % 2 == 0 ? "#FFC1D6" : "#FFE0EA", i * 23));
            }
        }
        else
        {
            mesh.Add(Canopy(leaf, 121, 0.3, 0.6, 0.1, crown));
        }

        return mesh;
    }

    private static Mesh Willow(int level)
    {
        Palette leaf = new("#7FAF55", "#A5CC6A", "#D2E98E", "#2F5A3A", "#FFF7B0");
        Rgb bark = "#775A44";
        var mesh = new Mesh();
        (double height, int outer, int inner, double dome) = level switch { 1 => (0.4, 7, 0, 0.27), 2 => (0.62, 11, 5, 0.38), _ => (0.9, 16, 8, 0.46) };
        mesh.Add(Trunk(height, 0.06 + (0.03 * level), 0.045 + (0.012 * level), bark, 0.05));
        double top = height + (0.04 * level);
        mesh.Add(Canopy(leaf, 191, top - 0.1, top + (dome * 1.6), 0.1, (0.05, top + (dome * 0.5), 0, dome, 0.8)));
        if (level >= 2)
        {
            mesh.Add(Canopy(leaf, 201, top - 0.1, top + (dome * 1.6), 0.1, (0.1, top + (dome * 0.95), 0.03, dome * 0.62, 0.72)));
            // The hem the strands grow from, wider than the dome so that they emerge from under it.
            mesh.Add(Canopy(leaf, 205, top - 0.1, top + (dome * 1.6), 0.08, (0.03, top + (dome * 0.12), 0, dome, 0.35)));
        }

        if (level == 3)
        {
            mesh.Add(Canopy(leaf, 211, top - 0.1, top + (dome * 1.6), 0.1, (0.12, top + (dome * 1.3), 0, dome * 0.4, 0.8)));
            mesh.Add(Roots(0.12, bark, 3, 19));
        }

        // Hanging ribbons in two rings; lengths vary so that only some reach the ground, and the tips are lighter.
        for (int i = 0; i < outer + inner; i++)
        {
            bool rim = i < outer;
            int count = rim ? outer : inner;
            double angle = (((rim ? i : i - outer) + (rim ? 0 : 0.5)) * 360.0 / count) + (12 * Noise(221 + i));
            double startY = top + (dome * (rim ? 0.12 : 0.3));
            Vec start = Polar(dome * (rim ? 0.95 : 0.7), angle, startY) + new Vec(0.03, 0, 0);
            double length = (startY - 0.05) * (0.7 + (0.3 * Noise(231 + i)));
            Mesh ribbon = Blade(length, 0.08 + (0.01 * level), -78, 0.25, -angle, leaf.At(0.25 + (0.25 * Noise(241 + i))), leaf.At(0.7 + (0.3 * Noise(251 + i))), 2);
            mesh.Add(ribbon.Moved(start.X, start.Y, start.Z));
        }

        return mesh;
    }

    private static Mesh Palm(int level)
    {
        (double height, double lean, int fronds, double length) = level switch { 1 => (0.55, 0.04, 7, 0.34), 2 => (1.1, 0.05, 8, 0.38), _ => (1.75, 0.06, 10, 0.42) };
        var mesh = new Mesh();
        int segments = level + 2;
        for (int k = 0; k < segments; k++)
        {
            double f0 = k / (double)segments, f1 = (k + 1) / (double)segments;
            mesh.Add(Limb(TrunkAt(height, lean, f0), TrunkAt(height, lean, f1), (0.085 - (0.033 * f0)) * (k == 0 ? 1.2 : 1), 0.085 - (0.033 * f1), 5,
                k % 2 == 0 ? "#B98F5C" : "#8F6C44", "#C9A06D", k * 15, false, k == segments - 1));
        }

        // Fronds alternate between rising and drooping; their reach is capped so that the crown stays on its tile.
        Vec crown = TrunkAt(height, lean, 1);
        for (int i = 0; i < fronds; i++)
        {
            double elevation = i % 2 == 0 ? 35 + (5 * Noise(251 + i)) : -5;
            Mesh frond = Blade(length * (0.9 + (0.2 * Noise(261 + i))), 0.2, elevation, 1.4, (i * 360.0 / fronds) + (10 * Noise(271 + i)), "#3C9A4E", "#63BC5E", 4);
            mesh.Add(frond.Moved(crown.X, crown.Y + 0.03, crown.Z));
        }

        if (level >= 2)
        {
            mesh.Add(Blade(0.3, 0.1, 75, 0.6, 20, "#63BC5E", "#7ACB6A", 3).Moved(crown.X, crown.Y + 0.03, crown.Z));
            mesh.Add(Blade(0.28, 0.1, 75, 0.6, 200, "#63BC5E", "#7ACB6A", 3).Moved(crown.X, crown.Y + 0.03, crown.Z));
        }

        mesh.Add(Blob(crown.X, crown.Y + 0.03, crown.Z, 0.07, 0.05, 0.07, "#5C8F3D", -1, 0, 5));
        if (level >= 2)
        {
            int coconuts = level == 3 ? 4 : 2;
            for (int i = 0; i < coconuts; i++)
            {
                Vec p = Polar(0.09, (i * 360.0 / coconuts) + 30, -0.2);
                mesh.Add(Fruit(crown.X + p.X, crown.Y + p.Y, crown.Z + p.Z, 0.08, i % 2 == 0 ? "#5B3A1E" : "#6B4528"));
            }
        }

        if (level == 3)
        {
            mesh.Add(Tuft(0.3, 0.28, "#6BB84F"));
        }

        return mesh;
    }

    private static Mesh Apple(int level)
    {
        Palette leaf = new("#35833A", "#5FAE48", "#9ED35E", "#1D4A3A", "#FFF4B0");
        var mesh = new Mesh();
        (double X, double Y, double Z, double R, double S)[] blobs;
        switch (level)
        {
            case 1:
                mesh.Add(Trunk(0.3, 0.07, 0.05, Bark));
                blobs = [(0.02, 0.48, 0, 0.29, 0.82), (0.17, 0.42, 0.1, 0.18, 0.85), (-0.14, 0.45, -0.1, 0.19, 0.85)];
                break;
            case 2:
                mesh.Add(Trunk(0.42, 0.1, 0.07, Bark));
                mesh.Add(Roots(0.1, Bark, 3, 23));
                blobs = [(0, 0.78, 0, 0.38, 0.8), (0.27, 0.74, 0.1, 0.22, 0.85), (-0.26, 0.8, -0.08, 0.22, 0.85), (0.05, 1.05, 0.06, 0.26, 0.85)];
                break;
            default:
                mesh.Add(Trunk(0.65, 0.115, 0.08, Bark));
                mesh.Add(Roots(0.14, Bark, 3, 25));
                blobs = [(0, 1.2, 0, 0.46, 0.8), (0.28, 1.1, 0.1, 0.24, 0.85), (-0.27, 1.15, -0.1, 0.24, 0.85), (0.06, 1.55, 0.08, 0.32, 0.85), (-0.04, 1.84, 0, 0.22, 0.9)];
                break;
        }

        mesh.Add(Canopy(leaf, 281 + level, 0.35, level == 1 ? 0.8 : level == 2 ? 1.2 : 1.9, blobs));
        int apples = level switch { 1 => 2, 2 => 4, _ => 6 };
        for (int i = 0; i < apples; i++)
        {
            mesh.Add(Berry(OnSurface(blobs[i % blobs.Length], i, 291), level == 3 ? 0.1 : 0.085, i % 2 == 0 ? "#E02B2B" : "#C81F2D"));
        }

        return mesh;
    }

    private static Mesh Cypress(int level)
    {
        (double height, double width) = level switch { 1 => (0.85, 0.6), 2 => (1.5, 0.85), _ => (2.1, 1.0) };
        var mesh = new Mesh();
        mesh.Add(Trunk(0.2, 0.05, 0.04, "#6D4C3A"));
        mesh.Add(Flame(0, 0, height, width, 301, 6));
        if (level == 3)
        {
            mesh.Add(Flame(0.3, -0.2, 0.8, 0.4, 311, 5));
            mesh.Add(Flame(-0.3, 0.12, 0.6, 0.32, 321, 5));
            mesh.Add(Tuft(0.12, 0.38, "#6BB84F"));
        }

        return mesh;
    }

    private static Mesh Flame(double cx, double cz, double height, double width, int seed, int sides)
    {
        Palette green = new("#265E42", "#3A7A50", "#68AE76");
        (double R, double Y)[] profile =
        [
            (0.1 * width, 0.14 * height), (0.2 * width, 0.32 * height), (0.22 * width, 0.52 * height), (0.15 * width, 0.75 * height), (0.07 * width, 0.9 * height), (0, height),
        ];
        return Lathe(sides, profile, k => green.At(k / 5.0), cx, cz, 0.12, seed, seed % 50).Varied(0.09, seed);
    }

    // Many small, slightly different blobs make a bumpy, leafy silhouette; neighbours alternate between two greens and the shadow tone stays close
    // to the foliage so that no dark rocky planes appear.
    private static Mesh Bush(Palette palette, int seed, params (double X, double Y, double Z, double R, double Squash)[] blobs)
    {
        var mesh = new Mesh();
        for (int i = 0; i < blobs.Length; i++)
        {
            (double x, double y, double z, double r, double squash) = blobs[i];
            Rgb colour = palette.At(i % 2 == 0 ? 0.3 : 0.7);
            mesh.Add(Blob(x, y, z, r, r * squash, r, colour, 0, 0.07, seed + i, 0.07, false, palette.Dark, palette.Glow));
        }

        return mesh;
    }

    // Overlapping blobs: neighbours are closer than the sum of their radii so that the crown reads as one clump.
    private static (double X, double Y, double Z, double R, double S)[] BushBlobs(int level) => level switch
    {
        1 => [(0, 0.15, 0, 0.2, 0.9), (0.12, 0.09, 0.06, 0.14, 0.9), (-0.11, 0.09, -0.05, 0.14, 0.9)],
        2 => [(0, 0.22, 0, 0.24, 0.88), (0.17, 0.12, 0.05, 0.17, 0.9), (-0.17, 0.13, -0.04, 0.17, 0.9), (0.03, 0.12, 0.18, 0.16, 0.9), (0.04, 0.36, -0.02, 0.16, 0.9)],
        _ => [(0, 0.3, 0, 0.27, 0.85), (0.2, 0.15, 0.07, 0.2, 0.9), (-0.2, 0.16, -0.05, 0.2, 0.9), (0.04, 0.15, 0.2, 0.19, 0.9), (-0.05, 0.15, -0.2, 0.18, 0.9), (0.05, 0.46, 0, 0.2, 0.9)],
    };

    // Round bush, rose and blueberry share the lumpy shape; the decoration sits on the outer foliage.
    private static Mesh Flowering(Palette leaf, int seed, int level, int[] counts, Rgb[] colors, double size)
    {
        (double X, double Y, double Z, double R, double S)[] blobs = BushBlobs(level);
        Mesh mesh = Bush(leaf, seed, blobs);
        for (int i = 0; i < counts[level - 1]; i++)
        {
            mesh.Add(Berry(OnSurface(blobs[i % blobs.Length], i, seed + 9), size, colors[i % colors.Length]));
        }

        return mesh;
    }

    private static Mesh RoundBush(int level) =>
        Flowering(new Palette("#3F9045", "#68BA52", "#A8DE6C", "#1F4A44", "#FFF8B8"), 401, level, [0, 3, 5], ["#FFF3B0", "#FFFFFF", "#FFB3D1"], 0.07);

    private static Mesh Rose(int level) =>
        Flowering(new Palette("#2F8450", "#48AA60", "#78CB86", "#14403A", "#FFF8C8"), 451, level, [3, 5, 7], ["#E5334F", "#F25C86", "#B0153F", "#FFD1DC"], 0.085);

    private static Mesh Blueberry(int level) =>
        Flowering(new Palette("#2E7442", "#4C9A55", "#84C66E", "#183B2A", "#F4FFE0"), 501, level, [3, 5, 8], ["#2B3FBF", "#3A4FD0", "#2433A8"], 0.075);

    private static Mesh Fern(int level)
    {
        (int count, double length, double width) = level switch { 1 => (7, 0.3, 0.06), 2 => (10, 0.36, 0.07), _ => (14, 0.42, 0.075) };
        var mesh = new Mesh();
        for (int i = 0; i < count; i++)
        {
            // A vase of arching fronds: a few rise steeply in the middle, the rest lean out and bend down; the outer ones are shorter to stay on the tile.
            int rank = i % 3;
            double t = rank / 2.0;
            Rgb a = ((Rgb)"#4DA65A").Mix("#8FD66A", t * 0.6);
            Rgb b = ((Rgb)"#6CBB5F").Mix("#B4E67A", 0.3 + (t * 0.6));
            double elevation = rank switch { 0 => 70, 1 => 45, _ => 28 } + (8 * Noise(561 + i));
            double reach = (rank == 1 ? 1.0 : 0.8) * (0.85 + (0.2 * Noise(551 + i)));
            double yaw = (i * 360.0 / count) + (10 * Noise(571 + i));
            mesh.Add(Blade(length * reach, width, elevation, 1.4, yaw, a, b, 4));
            if (rank == 1 && level >= 2)
            {
                // Two leaflets from the middle of the frond make it feathery.
                double along = length * reach * 0.55;
                (double sin, double cos) = Math.SinCos(elevation * Math.PI / 180);
                double rise = (along * sin) - (1.4 * along * along * cos), outward = (along * cos) + (1.4 * along * along * sin);
                (double ySin, double yCos) = Math.SinCos(yaw * Math.PI / 180);
                for (int side = -1; side <= 1; side += 2)
                {
                    mesh.Add(Blade(length * reach * 0.45, 0.04, elevation * 0.4, 1.4, yaw + (side * 60), a, b, 2).Moved(outward * yCos, rise, outward * ySin));
                }
            }
        }

        mesh.Add(Blob(0, 0.04, 0, 0.07, 0.04, 0.07, "#4C8F3F", 0, 0.1, 7));
        return mesh.Moved(0, 0.03, 0);
    }

    private static Mesh Withered(string id)
    {
        var mesh = new Mesh();
        switch (id)
        {
            case "oak": return Bare(0.8, "#6B5846", 0.28, 1, ["#8B6B3A", "#7A5B30"], 0.095);
            case "birch": return Bare(1.15, "#C9C4B8", 0.2, 2, ["#A8935A", "#8F7C4C"], 0.055).Add(BarkMark(1.15, 0.055, 0.35, 30), BarkMark(1.15, 0.055, 0.6, 200));
            case "maple": return Bare(0.8, "#5E4636", 0.28, 3, ["#8A4A2A", "#7A3E24"], 0.09);
            case "cherry": return Bare(0.75, "#4E3B33", 0.3, 4, ["#B58A94", "#9A7480"], 0.08);
            case "apple": return Bare(0.62, "#5B4636", 0.28, 5, ["#8B6B3A", "#7A5B30"], 0.09).Add(Fruit(0.28, 0.045, 0.25, 0.05, "#6B3A2A"));
            case "pine": return DeadConifer(1.0, 0.26, "#8A7B55", 3);
            case "spruce": return DeadConifer(0.9, 0.2, "#7D7360", 3);
            case "cypress": return DeadConifer(1.15, 0.12, "#8B7A55", 3);
            case "willow":
                mesh.Add(Bare(0.65, "#7A6A55", 0.2, 6, ["#9A8A6A", "#867757"], 0.09));
                // Dead strands grow out of the trunk top and hang down to the ground.
                Vec crownTop = TrunkAt(0.65, 0.06, 0.92);
                for (int i = 0; i < 6; i++)
                {
                    Vec start = crownTop + Polar(0.03, i * 60, 0);
                    Vec mid = start + Polar(0.14, i * 60, -0.22);
                    Vec end = start + Polar(0.2, i * 60, 0.02 - start.Y);
                    mesh.Add(Limb(start, mid, 0.02, 0.016, 3, "#9A8A6A", null, 0, false, false));
                    mesh.Add(Limb(mid, end, 0.016, 0.006, 3, "#9A8A6A"));
                }

                return mesh;
            case "palm":
                for (int k = 0; k < 3; k++)
                {
                    mesh.Add(Limb(TrunkAt(0.7, 0.14, k / 3.0), TrunkAt(0.7, 0.14, (k + 1) / 3.0), 0.065 - (0.016 * k), 0.065 - (0.016 * (k + 1)), 5, k % 2 == 0 ? "#8F7A5C" : "#7D6A4F", "#8F7A5C", 0, false, k == 2));
                }

                Vec hub = TrunkAt(0.7, 0.14, 1);
                for (int i = 0; i < 6; i++)
                {
                    mesh.Add(Blade(0.32, 0.08, i % 2 == 0 ? 15 : -25, 2.2, i * 60, "#8A6A3A", "#7A5C32", 3).Moved(hub.X, hub.Y + 0.02, hub.Z));
                }

                mesh.Add(Blob(hub.X, hub.Y + 0.02, hub.Z, 0.05, 0.035, 0.05, "#6E5530", -1, 0, 3));
                mesh.Add(Fruit(hub.X + 0.03, 0.58, 0.05, 0.06, "#5A4028"));
                return mesh;
            case "bush":
            case "rose":
            case "berry":
                mesh.Add(Canopy(new Palette("#7A5E34", "#8E6F3F", "#A5854E", "#3A2A18", "#E2C98A"), 591, 0.05, 0.4, (0, 0.12, 0, 0.21, 0.7), (0.17, 0.09, 0.08, 0.13, 0.7)));
                for (int i = 0; i < 5; i++)
                {
                    Vec p = Polar(0.1, i * 72, 0.12);
                    mesh.Add(Limb(p, p + Polar(0.14, i * 72, 0.1), 0.018, 0, 3, "#5E4A32"));
                }

                mesh.Add(Flat(0.3, 0.01, 0.25, 0.04, 4, "#8B6B3A", 30));
                mesh.Add(Flat(-0.26, 0.01, 0.3, 0.04, 4, "#7A5B30", 70));
                return mesh;
            default:
                for (int i = 0; i < 7; i++)
                {
                    mesh.Add(Blade(0.3 * (0.8 + (0.3 * Noise(601 + i))), 0.06, 30 - (15 * (i % 2)), 1.3, i * 51, "#8A6A3A", "#9C7B45", 3).Moved(0, 0.05, 0));
                }

                mesh.Add(Blob(0, 0.03, 0, 0.06, 0.04, 0.06, "#6E5530", -1, 0, 4));
                return mesh;
        }
    }

    // A dark bark mark on the leaning trunk of Bare(), following the trunk's axis and taper so that it sits on the bark.
    private static Mesh BarkMark(double height, double radius, double f, double degrees)
    {
        Vec axis = TrunkAt(height, 0.06, f);
        return Patch(radius + ((radius * 0.45 - radius) * f) + 0.004, degrees, 0, 0.04, 0.02, "#3A3A3A").Moved(axis.X, axis.Y, 0);
    }

    private static Mesh Bare(double height, Rgb bark, double spread, int seed, Rgb[] fallen, double trunkRadius)
    {
        var mesh = new Mesh();
        mesh.Add(Trunk(height, trunkRadius, trunkRadius * 0.45, bark, 0.06));
        mesh.Add(Roots(trunkRadius, bark, 3, seed + 30));
        double[] fs = [0.5, 0.66, 0.84];
        for (int i = 0; i < fs.Length; i++)
        {
            Vec from = TrunkAt(height, 0.06, fs[i]);
            double angle = (i * 125) + (30 * Noise(seed + i));
            Vec to = from + Polar(spread * (1 - (0.2 * i)), angle, spread * 0.8);
            mesh.Add(Limb(from, to, 0.026 - (0.004 * i), 0.01, 4, bark.Shade(0.95), bark.Shade(1.1)));
            mesh.Add(Limb(to, to + Polar(spread * 0.4, angle + 35, spread * 0.45), 0.012, 0, 3, bark.Shade(0.9)));
        }

        for (int i = 0; i < 5; i++)
        {
            Vec p = Polar(0.2 + (0.2 * Noise(seed + 70 + i)), (i * 72) + 30, 0.01);
            mesh.Add(Flat(p.X, 0.01, p.Z, 0.04, 4, fallen[i % 2], i * 40));
        }

        return mesh;
    }

    private static Mesh DeadConifer(double height, double radius, Rgb foliage, int tiers)
    {
        var mesh = new Mesh();
        mesh.Add(Trunk(height, 0.06, 0.03, "#6B5846", 0.04));
        mesh.Add(Roots(0.06, "#6B5846", 3, 40));
        for (int i = 0; i < tiers - 1; i++)
        {
            Vec at = TrunkAt(height, 0.04, 0.15 + (0.25 * i));
            mesh.Add(Cone(6, at.X, at.Y, at.Z, radius * 0.8 * (1 - (0.45 * i)), 0.2, foliage.Shade(1 - (0.1 * i)), i * 30).Varied(0.08, i));
        }

        for (int i = 0; i < 3; i++)
        {
            Vec from = TrunkAt(height, 0.04, 0.72 + (0.07 * i));
            mesh.Add(Limb(from, from + Polar(0.15, i * 130, 0.1), 0.014, 0, 3, "#5E4A38"));
        }

        mesh.Add(Flat(0.22, 0.01, 0.2, 0.04, 4, foliage, 20));
        mesh.Add(Flat(-0.2, 0.01, 0.25, 0.04, 4, foliage.Shade(0.9), 60));
        return mesh;
    }

    private static Mesh Rock()
    {
        Rgb shadow = "#2F3A4A";
        Rgb glow = "#F4F1E4";
        var mesh = new Mesh();
        mesh.Add(Blob(-0.02, 0.22, 0, 0.36, 0.24, 0.3, "#9AA3AB", 0, 0.12, 3, 0.07, false, shadow, glow));
        mesh.Add(Blob(0.27, 0.13, 0.2, 0.19, 0.13, 0.17, "#848E96", 0, 0.12, 4, 0.07, false, shadow, glow));
        mesh.Add(Blob(-0.27, 0.12, -0.17, 0.17, 0.12, 0.15, "#A9B1B8", 0, 0.12, 5, 0.07, false, shadow, glow));
        mesh.Add(Blob(-0.02, 0.43, -0.02, 0.25, 0.1, 0.21, "#6BAE4F", 0, 0.1, 6, 0.09, false, "#2F5A3A", "#D8F08A"));
        mesh.Add(Blob(0.28, 0.25, 0.2, 0.14, 0.07, 0.12, "#7DBE57", 0, 0.1, 7, 0.09, false, "#2F5A3A", "#D8F08A"));
        mesh.Add(Fruit(-0.04, 0.06, 0.34, 0.06, "#B7BEC4", -1));
        mesh.Add(Limb(new Vec(-0.1, 0.46, 0.05), new Vec(-0.1, 0.56, 0.05), 0.008, 0.006, 3, "#4C9A3C", null, 0, false, false));
        mesh.Add(Fruit(-0.1, 0.59, 0.05, 0.05, "#FFFFFF"));
        mesh.Add(Limb(new Vec(0.1, 0.47, -0.06), new Vec(0.1, 0.55, -0.06), 0.008, 0.006, 3, "#4C9A3C", null, 0, false, false));
        mesh.Add(Fruit(0.1, 0.58, -0.06, 0.045, "#FFD54F"));
        return mesh;
    }

    private static Mesh Mushroom(double cx, double cz, double s, Rgb cap, Rgb spot)
    {
        var mesh = new Mesh();
        mesh.Add(Prism(5, cx, 0, cz, 0.055 * s, 0.04 * s, 0.17 * s, "#F3E8D2", null, 0, false));
        (double R, double Y)[] profile = [(0.19 * s, 0.14 * s), (0.22 * s, 0.22 * s), (0.12 * s, 0.31 * s), (0, 0.35 * s)];
        mesh.Add(Lathe(6, profile, k => k == 0 ? cap.Shade(0.88) : cap, cx, cz, 0.03, (int)(s * 10)).Varied(0.05, (int)(s * 20)));
        for (int i = 0; i < 3; i++)
        {
            Vec p = Polar(0.1 * s, (i * 120) + 25, 0.285 * s);
            mesh.Add(Fruit(cx + p.X, p.Y, cz + p.Z, Math.Max(0.04, 0.03 * s), spot, -1));
        }

        return mesh;
    }

    private static Mesh Mushrooms()
    {
        var mesh = new Mesh();
        mesh.Add(Mushroom(-0.06, -0.04, 1.35, "#D8412F", "#FFFFFF"));
        mesh.Add(Mushroom(0.22, 0.14, 0.85, "#E58A2A", "#FFF3D6"));
        mesh.Add(Mushroom(-0.2, 0.2, 0.6, "#D8412F", "#FFFFFF"));
        mesh.Add(Tuft(0.3, -0.25, "#6BB84F"));
        return mesh;
    }

    private static Mesh FlowerBed()
    {
        var mesh = new Mesh();
        mesh.Add(Box(0, 0, 0, 0.74, 0.06, 0.74, "#5D4037", "#6B4A3E"));
        Rgb wood = "#9A6B42";
        mesh.Add(Box(0, 0, 0.38, 0.82, 0.1, 0.07, wood, wood.Shade(1.1)));
        mesh.Add(Box(0, 0, -0.38, 0.82, 0.1, 0.07, wood, wood.Shade(1.1)));
        mesh.Add(Box(0.38, 0, 0, 0.07, 0.1, 0.7, wood.Shade(0.95), wood.Shade(1.1)));
        mesh.Add(Box(-0.38, 0, 0, 0.07, 0.1, 0.7, wood.Shade(0.95), wood.Shade(1.1)));
        Rgb[] petals = ["#F06292", "#FFEE58", "#BA68C8", "#FFFFFF", "#FF8A65", "#64B5F6"];
        for (int i = 0; i < 7; i++)
        {
            double x = -0.22 + (0.22 * (i % 3)) + (0.05 * (Noise(611 + i) - 0.5));
            double z = -0.22 + (0.22 * (i / 3)) + (0.05 * (Noise(621 + i) - 0.5));
            double h = 0.14 + (0.14 * Noise(631 + i));
            mesh.Add(Limb(new Vec(x, 0.06, z), new Vec(x, 0.06 + h, z), 0.012, 0.008, 3, "#4C9A3C", null, 0, false, false));
            mesh.Add(Prism(5, x, 0.06 + h - 0.01, z, 0.065, 0.035, 0.03, petals[i % petals.Length], "#FFC107", i * 20));
        }

        mesh.Add(Blob(0.27, 0.1, 0.27, 0.09, 0.05, 0.08, "#5FAE4F", -1, 0.1, 12));
        mesh.Add(Blob(-0.28, 0.1, -0.26, 0.08, 0.05, 0.08, "#4F9E45", -1, 0.1, 13));
        return mesh;
    }

    private static Mesh Bench()
    {
        var mesh = new Mesh();
        Rgb wood = "#B07A4A";
        Rgb dark = "#6B4A2F";
        for (int i = 0; i < 3; i++)
        {
            mesh.Add(Box(0, 0.22, -0.065 + (0.066 * i), 0.72, 0.04, 0.058, wood.Shade(i % 2 == 0 ? 1 : 0.94), wood.Shade(1.1)));
        }

        foreach (double sx in new[] { -0.3, 0.3 })
        {
            mesh.Add(Box(sx, 0, 0.07, 0.05, 0.22, 0.05, dark));
            mesh.Add(Box(sx, 0, -0.07, 0.05, 0.22, 0.05, dark));
            mesh.Add(Box(sx, 0.22, -0.12, 0.05, 0.34, 0.04, dark));
            mesh.Add(Box(sx + (sx > 0 ? 0.03 : -0.03), 0.3, 0.0, 0.04, 0.035, 0.26, wood.Shade(0.9), wood.Shade(1.05)));
        }

        mesh.Add(Box(0, 0.36, -0.125, 0.7, 0.07, 0.03, wood.Shade(1.05), wood.Shade(1.12)));
        mesh.Add(Box(0, 0.47, -0.125, 0.7, 0.07, 0.03, wood, wood.Shade(1.12)));
        mesh.Add(Tuft(0.36, 0.2, "#6BB84F"));
        mesh.Add(Fruit(-0.38, 0.05, 0.2, 0.05, "#F06292"));
        return mesh;
    }

    private static Mesh Lantern()
    {
        var mesh = new Mesh();
        Rgb stone = "#A2A7AC";
        mesh.Add(Flat(0, 0.01, 0, 0.34, 8, "#F3E7A8"));
        mesh.Add(Prism(6, 0, 0, 0, 0.22, 0.19, 0.06, stone.Shade(0.9), stone.Shade(1.05), 0));
        mesh.Add(Prism(6, 0, 0.06, 0, 0.09, 0.075, 0.24, stone, null, 10, false));
        mesh.Add(Prism(6, 0, 0.3, 0, 0.19, 0.15, 0.05, stone.Shade(0.95), stone.Shade(1.1)));
        mesh.Add(Prism(4, 0, 0.35, 0, 0.105, 0.105, 0.24, "#FFE08A", "#FFF3C4", 45));
        mesh.Add(Prism(6, 0, 0.59, 0, 0.2, 0.17, 0.04, stone.Shade(0.95), stone.Shade(1.08)));
        mesh.Add(Prism(6, 0, 0.63, 0, 0.32, 0.06, 0.18, "#7C838A", "#8B9299", 30));
        mesh.Add(Fruit(0, 0.84, 0, 0.05, "#C8CDD2", -1));
        mesh.Add(Tuft(0.22, 0.16, "#6BB84F"));
        mesh.Add(Fruit(-0.26, 0.06, -0.12, 0.06, "#8E979E", -1));
        return mesh;
    }

    private static Mesh Pond()
    {
        var mesh = new Mesh();
        (double R, double Y)[] water = [(0.36, 0), (0.36, 0.035)];
        mesh.Add(Lathe(12, water, _ => "#4FA9D2", 0, 0, 0.05, 17, 0, "#69C3E8"));
        mesh.Add(Prism(8, -0.06, 0.036, 0.02, 0.15, 0.13, 0.004, "#8AD6F0", null, 15));
        for (int i = 0; i < 7; i++)
        {
            Vec p = Polar(0.385 + (0.02 * Noise(641 + i)), (i * 51) + (8 * Noise(651 + i)), 0.02);
            double r = 0.09 + (0.035 * Noise(661 + i));
            mesh.Add(Blob(p.X, p.Y, p.Z, r, r * 0.7, r, i % 2 == 0 ? "#9AA3AB" : "#848E96", 0, 0.1, 20 + i, 0.07));
        }

        mesh.Add(Prism(7, 0.1, 0.037, 0.1, 0.085, 0.08, 0.008, "#4C9A4A", "#5CAE55", 10));
        mesh.Add(Prism(7, -0.14, 0.037, -0.04, 0.065, 0.06, 0.008, "#468F46", "#58A852", 40));
        mesh.Add(Prism(6, 0.1, 0.045, 0.1, 0.04, 0.02, 0.035, "#FFB7D0", "#FFD6E5"));
        for (int i = 0; i < 3; i++)
        {
            Vec p = Polar(0.31, 125 + (i * 20), 0.035);
            double h = 0.3 + (0.12 * Noise(671 + i));
            mesh.Add(Limb(p, p + new Vec(0, h, 0), 0.014, 0.006, 3, "#5FA14A", "#7BBF5E"));
            if (i % 2 == 0)
            {
                mesh.Add(Prism(5, p.X, p.Y + h - 0.02, p.Z, 0.022, 0.018, 0.07, "#6D4C41", "#8D6E63"));
            }
        }

        return mesh;
    }

    private static Mesh Well()
    {
        var mesh = new Mesh();
        for (int i = 0; i < 10; i++)
        {
            Vec p = Polar(0.2, i * 36, 0);
            mesh.Add(Box(p.X, 0, p.Z, 0.075, 0.27, 0.15, i % 2 == 0 ? "#A9AFB5" : "#939AA1", "#B9BFC5", (i * 36) + 90));
        }

        mesh.Add(Prism(8, 0, 0.1, 0, 0.17, 0.17, 0.16, "#4A8FB5", "#5FA8CC", 0, true));
        foreach (double sx in new[] { -0.29, 0.29 })
        {
            mesh.Add(Box(sx, 0.27, 0, 0.05, 0.5, 0.05, "#7A5438", "#8D6445"));
        }

        mesh.Add(Box(0, 0.58, 0, 0.62, 0.045, 0.045, "#6E4A30", "#8D6445"));
        mesh.Add(Limb(new Vec(0, 0.58, 0), new Vec(0, 0.4, 0), 0.01, 0.01, 3, "#D7C29A", null, 0, false, false));
        mesh.Add(Prism(6, 0, 0.3, 0, 0.055, 0.048, 0.085, "#8D6E4F", "#5D4A38"));
        mesh.Add(Gable(0, 0.77, 0, 0.74, 0.56, 0.24, "#B5543C", "#8D6445"));
        mesh.Add(Tuft(0.36, 0.27, "#6BB84F"));
        return mesh;
    }

    private static Mesh Cabin()
    {
        var mesh = new Mesh();
        mesh.Add(Box(0, 0, 0, 0.8, 0.07, 0.66, "#8F8F93", "#A3A3A8"));
        for (int i = 0; i < 5; i++)
        {
            double inset = i % 2 == 0 ? 0 : 0.012;
            mesh.Add(Box(0, 0.07 + (0.09 * i), 0, 0.72 - inset, 0.09, 0.58 - inset, i % 2 == 0 ? "#A9714B" : "#96603F", "#B98056"));
        }

        double wallTop = 0.07 + (0.09 * 5);
        mesh.Add(Gable(0, wallTop, 0, 0.92, 0.8, 0.36, "#B5543C", "#A9714B"));
        mesh.Add(Box(0, wallTop + 0.355, 0, 0.94, 0.03, 0.05, "#8D3F2B"));
        mesh.Add(Box(0.18, 0.12, 0.31, 0.2, 0.2, 0.02, "#6D4426"));
        mesh.Add(Box(0.18, 0.14, 0.325, 0.15, 0.16, 0.02, "#FFD66B", "#FFE08A"));
        mesh.Add(Box(-0.14, 0.07, 0.31, 0.17, 0.32, 0.02, "#6D4426"));
        mesh.Add(Box(-0.14, 0.1, 0.325, 0.13, 0.26, 0.015, "#7E5230"));
        mesh.Add(Box(-0.14, 0, 0.4, 0.28, 0.04, 0.12, "#8F8F93", "#A3A3A8"));
        mesh.Add(Box(0.27, 0.45, -0.14, 0.12, 0.52, 0.12, "#7D7D82", "#929297"));
        mesh.Add(Box(0.27, 0.97, -0.14, 0.15, 0.04, 0.15, "#66666B", "#7B7B80"));
        mesh.Add(Fruit(0.27, 1.12, -0.14, 0.06, "#E8EAED", -1));
        mesh.Add(Fruit(0.33, 1.26, -0.1, 0.08, "#F4F6F8", -1));
        mesh.Add(Blob(0.36, 0.08, 0.37, 0.09, 0.07, 0.08, "#5FAE4F", -1, 0.1, 24));
        mesh.Add(Blob(-0.38, 0.08, 0.3, 0.08, 0.07, 0.08, "#4F9E45", -1, 0.1, 25));
        mesh.Add(Fruit(0.37, 0.15, 0.4, 0.04, "#F06292", -1));
        return mesh;
    }
}
