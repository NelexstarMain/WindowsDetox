// The world layout of the Focus Forest: a pure, deterministic algorithm that arranges plants and decorations into a small village.
//
//   * A plaza sits at the centre. Every decoration is a point of interest that gets a path from the plaza; paths are routed one
//     decoration at a time (by id) and prefer existing path tiles, so they merge into streets instead of running in parallel.
//   * Plants never stand on paths. They grow in groves around one centre per species, bigger stages towards the edge and the back,
//     small bushes lining the paths, a little seeded jitter, and a margin of grass around the plaza.
//   * Objects that already have a position keep it. Paths depend only on the decorations and pinned objects, so adding a plant moves
//     nothing and adding a decoration moves only the plants its new path crosses.
//   * When fewer than 35% of the tiles would stay free the field grows (up to ForestLimits.MaxSize) and everything shifts to stay centred.

namespace FocusForest;

/// <summary>A tile of the field.</summary>
public readonly record struct GridTile(int Column, int Row);

/// <summary>An object as the layout sees it. <paramref name="Position"/> is where it stands now (null: not placed yet).</summary>
/// <param name="Pinned">The user put it there: paths route around it and it is never evicted.</param>
public sealed record WorldObject(int Id, string Species, ForestKind Kind, ForestStage Stage, GridTile? Position, bool Pinned);

/// <summary>The result of <see cref="ForestWorld.Arrange"/>: the final field size, a position per placed object, and the path tiles.</summary>
public sealed record WorldLayout(int Size, IReadOnlyDictionary<int, GridTile> Positions, IReadOnlyList<ForestPathTile> Paths);

/// <summary>The layout algorithm.</summary>
public static class ForestWorld
{
    /// <summary>The seed of every forest; the same objects always produce the same village.</summary>
    public const int DefaultSeed = 7;

    /// <summary>Share of the tiles kept free for paths and breathing room before the field grows.</summary>
    public const double FreeShare = 0.35;

    // East, south, west, north: one quarter turn of a scene item takes east to south.
    private static readonly GridTile[] Directions = [new(1, 0), new(0, 1), new(-1, 0), new(0, -1)];

    /// <summary>The plaza tile of a field.</summary>
    public static GridTile Plaza(int size) => new(size / 2, size / 2);

    /// <summary>Whether the tile is the plaza or one of the four tiles next to it. Nothing may be placed there by hand or by the layout, or it could cut the plaza off from the paths.</summary>
    public static bool IsPlazaApproach(int size, GridTile tile) =>
        Math.Abs(tile.Column - Plaza(size).Column) + Math.Abs(tile.Row - Plaza(size).Row) <= 1;

    /// <summary>
    /// Lays the objects out. Objects with a <see cref="WorldObject.Position"/> stay there when they can (positions are in the coordinates of
    /// <paramref name="size"/>); when the field grows the result is in the new coordinates. Objects that find no free tile are missing from
    /// the result.
    /// </summary>
    public static WorldLayout Arrange(int size, IReadOnlyList<WorldObject> objects, int seed = DefaultSeed)
    {
        size = Math.Clamp(size, ForestLimits.StartSize, ForestLimits.MaxSize);
        Dictionary<int, GridTile> kept = objects.Where(o => o.Position is not null).ToDictionary(o => o.Id, o => o.Position!.Value);
        while (true)
        {
            bool canGrow = size < ForestLimits.MaxSize;
            WorldLayout? layout = canGrow && objects.Count > Budget(size) ? null : new Planner(size, objects, kept, seed).Run();
            if (layout is not null && !(canGrow && objects.Count + layout.Paths.Count > Budget(size)))
            {
                return layout;
            }

            int shift = ForestLimits.GrowStep / 2;
            foreach (int id in kept.Keys.ToList())
            {
                kept[id] = new GridTile(kept[id].Column + shift, kept[id].Row + shift);
            }

            size = Math.Min(ForestLimits.MaxSize, size + ForestLimits.GrowStep);
        }
    }

    /// <summary>The shape and rotation of every path tile, from its four-neighbour connectivity; ordered by row, then column.</summary>
    public static IReadOnlyList<ForestPathTile> Autotile(IEnumerable<GridTile> path)
    {
        HashSet<GridTile> tiles = [.. path];
        return
        [
            .. tiles.OrderBy(t => t.Row).ThenBy(t => t.Column).Select(t =>
            {
                int[] arms = [.. Enumerable.Range(0, 4).Where(d => tiles.Contains(new GridTile(t.Column + Directions[d].Column, t.Row + Directions[d].Row)))];
                (ForestPathShape shape, int rotation) = arms.Length switch
                {
                    0 => (ForestPathShape.Single, 0),
                    1 => (ForestPathShape.End, arms[0]),
                    2 when arms[1] - arms[0] == 2 => (ForestPathShape.Straight, arms[0]),
                    2 => (ForestPathShape.Corner, arms[1] - arms[0] == 1 ? arms[0] : arms[1]),
                    3 => (ForestPathShape.Tee, (Enumerable.Range(0, 4).Single(d => !arms.Contains(d)) + 1) % 4),
                    _ => (ForestPathShape.Cross, 0),
                };
                return new ForestPathTile(t.Column, t.Row, shape, rotation);
            }),
        ];
    }

    private static int Budget(int size) => (int)(size * size * (1 - FreeShare));

    private static int Hash(int a, int b, int c, int seed) =>
        unchecked((a * 73856093) ^ (b * 19349663) ^ (c * 83492791) ^ (seed * (int)2654435761u));

    private sealed class Planner
    {
        // Decorations that sit beside a street rather than at its end, and where each kind likes to be: the fraction of the field edge
        // between the plaza and the decoration.
        private static readonly Dictionary<string, (double Radius, bool Roadside)> Taste = new(StringComparer.Ordinal)
        {
            ["bench"] = (0.14, true),
            ["lantern"] = (0.2, true),
            ["flowers"] = (0.26, true),
            ["mushrooms"] = (0.36, true),
            ["rock"] = (0.4, true),
            ["well"] = (0.22, false),
            ["pond"] = (0.32, false),
            ["cabin"] = (0.4, false),
        };

        private const double TurnCost = 0.4;
        private const double ExistingPathCost = 0.1;

        private readonly int _size;
        private readonly IReadOnlyList<WorldObject> _objects;
        private readonly Dictionary<int, GridTile> _kept;
        private readonly int _seed;
        private readonly GridTile _plaza;
        private readonly int[] _owner;
        private readonly bool[] _path;
        private readonly Dictionary<int, GridTile> _positions = [];

        public Planner(int size, IReadOnlyList<WorldObject> objects, Dictionary<int, GridTile> kept, int seed)
        {
            _size = size;
            _objects = objects;
            _kept = kept;
            _seed = seed;
            _plaza = Plaza(size);
            _owner = new int[size * size];
            _path = new bool[size * size];
        }

        public WorldLayout Run()
        {
            _path[Index(_plaza)] = true;
            List<WorldObject> decorations = [.. _objects.Where(o => o.Kind == ForestKind.Decoration).OrderBy(o => o.Id)];
            List<WorldObject> plants = [.. _objects.Where(o => o.Kind != ForestKind.Decoration).OrderBy(o => o.Id)];

            foreach (WorldObject o in _objects.Where(o => o.Pinned).OrderBy(o => o.Id))
            {
                Reserve(o);
            }

            foreach (WorldObject d in decorations.Where(d => !d.Pinned))
            {
                Reserve(d);
            }

            foreach (WorldObject d in decorations)
            {
                if (_positions.TryGetValue(d.Id, out GridTile tile))
                {
                    Route(tile);
                }
                else
                {
                    PlaceDecoration(d);
                }
            }

            foreach (WorldObject p in plants.Where(p => !p.Pinned))
            {
                if (!OnPath(p))
                {
                    Reserve(p);
                }
            }

            bool[] besidePath = new bool[_size * _size];
            for (int i = 0; i < besidePath.Length; i++)
            {
                besidePath[i] = Neighbours(i).Any(n => _path[n]);
            }

            foreach (WorldObject p in plants.Where(p => !_positions.ContainsKey(p.Id)))
            {
                PlacePlant(p, besidePath);
            }

            return new WorldLayout(
                _size,
                _positions,
                Autotile(Enumerable.Range(0, _path.Length).Where(i => _path[i]).Select(i => new GridTile(i % _size, i / _size))));
        }

        private int Index(GridTile t) => (t.Row * _size) + t.Column;

        private bool OnPath(WorldObject o) => _kept.TryGetValue(o.Id, out GridTile t) && InBounds(t) && _path[Index(t)];

        private bool InBounds(GridTile t) => t.Column >= 0 && t.Row >= 0 && t.Column < _size && t.Row < _size;

        private IEnumerable<int> Neighbours(int index)
        {
            int column = index % _size, row = index / _size;
            foreach (GridTile d in Directions)
            {
                if (InBounds(new GridTile(column + d.Column, row + d.Row)))
                {
                    yield return ((row + d.Row) * _size) + column + d.Column;
                }
            }
        }

        // Takes the object's own position when it is still valid.
        private void Reserve(WorldObject o)
        {
            if (_kept.TryGetValue(o.Id, out GridTile t) && InBounds(t) && t != _plaza && _owner[Index(t)] == 0)
            {
                _owner[Index(t)] = o.Id;
                _positions[o.Id] = t;
            }
        }

        private void PlaceDecoration(WorldObject d)
        {
            (double radius, bool roadside) = Taste.GetValueOrDefault(d.Species, (0.3, true));
            double angle = 2 * Math.PI * ForestMeshes.Noise(roadside ? Hash(d.Id, 3, 5, _seed) : Hash(IndexInCatalogue(d.Species), 1, 2, _seed));
            double centre = (_size - 1) / 2.0;
            double targetX = centre + (radius * _size * Math.Cos(angle)), targetY = centre + (radius * _size * Math.Sin(angle));
            List<(double Score, int Index)> candidates = [];
            for (int i = 0; i < _owner.Length; i++)
            {
                if (_owner[i] != 0 || _path[i] || IsPlazaApproach(_size, new GridTile(i % _size, i / _size)))
                {
                    continue;
                }

                double score = Math.Sqrt(Math.Pow((i % _size) - targetX, 2) + Math.Pow((i / _size) - targetY, 2))
                    + (roadside && !Neighbours(i).Any(n => _path[n]) ? 4 : 0)
                    + (Neighbours(i).Any(n => _owner[n] != 0 && DecorationIds.Contains(_owner[n])) ? 2 : 0)
                    + (0.8 * ForestMeshes.Noise(Hash(d.Id, i, 9, _seed)));
                candidates.Add((score, i));
            }

            foreach ((double _, int index) in candidates.OrderBy(c => c.Score).ThenBy(c => c.Index).Take(6))
            {
                _owner[index] = d.Id;
                if (Route(new GridTile(index % _size, index / _size)))
                {
                    _positions[d.Id] = new GridTile(index % _size, index / _size);
                    return;
                }

                _owner[index] = 0;
            }
        }

        private HashSet<int> DecorationIds => _decorationIds ??= [.. _objects.Where(o => o.Kind == ForestKind.Decoration).Select(o => o.Id)];

        private HashSet<int>? _decorationIds;

        // Dijkstra from the decoration's free neighbours to the nearest path tile; turning is penalised so streets run straight.
        private bool Route(GridTile poi)
        {
            int tiles = _size * _size;
            double[] best = new double[tiles * 5];
            int[] previous = new int[tiles * 5];
            Array.Fill(best, double.PositiveInfinity);
            Array.Fill(previous, -1);
            PriorityQueue<int, double> queue = new();
            for (int d = 0; d < 4; d++)
            {
                var next = new GridTile(poi.Column + Directions[d].Column, poi.Row + Directions[d].Row);
                if (InBounds(next) && _owner[Index(next)] == 0)
                {
                    int state = (Index(next) * 5) + d;
                    best[state] = StepCost(Index(next));
                    queue.Enqueue(state, best[state]);
                }
            }

            while (queue.TryDequeue(out int current, out double cost))
            {
                if (cost > best[current])
                {
                    continue;
                }

                if (_path[current / 5])
                {
                    for (int s = current; s >= 0; s = previous[s])
                    {
                        _path[s / 5] = true;
                    }

                    return true;
                }

                int column = current / 5 % _size, row = current / 5 / _size, heading = current % 5;
                for (int d = 0; d < 4; d++)
                {
                    var next = new GridTile(column + Directions[d].Column, row + Directions[d].Row);
                    if (!InBounds(next) || _owner[Index(next)] != 0)
                    {
                        continue;
                    }

                    int state = (Index(next) * 5) + d;
                    double total = cost + StepCost(Index(next)) + (d == heading ? 0 : TurnCost);
                    if (total < best[state])
                    {
                        best[state] = total;
                        previous[state] = current;
                        queue.Enqueue(state, total);
                    }
                }
            }

            return false;
        }

        private double StepCost(int index) =>
            _path[index] ? ExistingPathCost : 1 + (0.15 * ForestMeshes.Noise(Hash(index, 4, 8, _seed)));

        private void PlacePlant(WorldObject p, bool[] besidePath)
        {
            (double cx, double cy) = GroveCentre(p.Species);
            int level = p.Stage switch { ForestStage.Sapling => 0, ForestStage.Young => 1, ForestStage.Mature => 2, ForestStage.Majestic => 3, _ => 1 };
            double best = double.PositiveInfinity;
            int bestIndex = -1;
            for (int i = 0; i < _owner.Length; i++)
            {
                if (_owner[i] != 0 || _path[i])
                {
                    continue;
                }

                int column = i % _size, row = i / _size;
                double edge = Math.Min(Math.Min(column, _size - 1 - column), Math.Min(row, _size - 1 - row)) / (_size / 2.0);
                double back = (column + row) / (2.0 * (_size - 1));
                double score = Math.Sqrt(Math.Pow(column - cx, 2) + Math.Pow(row - cy, 2)) + (0.9 * ForestMeshes.Noise(Hash(p.Id, column, row, _seed)));
                if (p.Kind == ForestKind.Tree)
                {
                    score += level * ((0.55 * edge) + (0.3 * back)) + (besidePath[i] ? 0.6 : 0);
                }
                else
                {
                    score += (level * 0.25 * edge) - (besidePath[i] ? 1.2 : 0);
                }

                if (Math.Max(Math.Abs(column - _plaza.Column), Math.Abs(row - _plaza.Row)) <= 1)
                {
                    score += 50;
                }

                if (score < best)
                {
                    best = score;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
            {
                _owner[bestIndex] = p.Id;
                _positions[p.Id] = new GridTile(bestIndex % _size, bestIndex / _size);
            }
        }

        // One centre per catalogue species, spread round the plaza by the golden angle so that no two groves start on top of each other.
        private (double X, double Y) GroveCentre(string species)
        {
            int index = Math.Max(0, IndexInCatalogue(species));
            double angle = (index * 2.399963) + (_seed * 0.37);
            double fraction = ((index * 0.6180339) + (_seed * 0.13)) % 1;
            double radius = _size * (0.2 + (0.22 * fraction));
            double centre = (_size - 1) / 2.0;
            return (Math.Clamp(centre + (radius * Math.Cos(angle)), 1, _size - 2), Math.Clamp(centre + (radius * Math.Sin(angle)), 1, _size - 2));
        }

        private static int IndexInCatalogue(string species)
        {
            for (int i = 0; i < ForestCatalog.All.Count; i++)
            {
                if (ForestCatalog.All[i].Id == species)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
