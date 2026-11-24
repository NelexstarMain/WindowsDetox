// Shared catalogue of the Focus Forest: what can grow or be placed, and how a session's length maps to a growth stage.
// Mechanics (ForestStore, ForestShop) and models (ForestModels) both key off these ids, so ids are stable storage keys.

namespace FocusInsights;

/// <summary>What a catalogue entry is.</summary>
public enum ForestKind
{
    /// <summary>Planted by a completed session; grows in stages.</summary>
    Tree,

    /// <summary>Planted by a completed session; grows in stages, smaller than a tree.</summary>
    Bush,

    /// <summary>Bought in the shop and placed by hand; has a single look.</summary>
    Decoration,
}

/// <summary>Look of a planted tree or bush, chosen by the session that planted it.</summary>
public enum ForestStage
{
    /// <summary>Session shorter than 25 minutes.</summary>
    Sapling,

    /// <summary>25-49 minutes.</summary>
    Young,

    /// <summary>50-89 minutes.</summary>
    Mature,

    /// <summary>90 minutes or more.</summary>
    Majestic,

    /// <summary>The session was ended early with break-glass.</summary>
    Withered,
}

/// <summary>One catalogue entry.</summary>
/// <param name="Id">Stable storage key and model key prefix.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">Tree, bush or decoration.</param>
/// <param name="Price">Coins: unlock price for trees and bushes (0 = available from the start), purchase price per piece for decorations.</param>
public sealed record ForestSpecies(string Id, string Name, ForestKind Kind, int Price);

/// <summary>The catalogue and the stage rules.</summary>
public static class ForestCatalog
{
    /// <summary>All entries, in shop order.</summary>
    public static IReadOnlyList<ForestSpecies> All { get; } =
    [
        new("oak", "Oak", ForestKind.Tree, 0),
        new("pine", "Pine", ForestKind.Tree, 0),
        new("birch", "Birch", ForestKind.Tree, 150),
        new("maple", "Autumn maple", ForestKind.Tree, 250),
        new("cherry", "Cherry blossom", ForestKind.Tree, 400),
        new("spruce", "Snowy spruce", ForestKind.Tree, 400),
        new("willow", "Willow", ForestKind.Tree, 600),
        new("palm", "Palm", ForestKind.Tree, 600),
        new("apple", "Apple tree", ForestKind.Tree, 800),
        new("cypress", "Cypress", ForestKind.Tree, 800),
        new("bush", "Round bush", ForestKind.Bush, 0),
        new("rose", "Rose bush", ForestKind.Bush, 200),
        new("berry", "Blueberry bush", ForestKind.Bush, 300),
        new("fern", "Fern", ForestKind.Bush, 300),
        new("rock", "Mossy rock", ForestKind.Decoration, 40),
        new("mushrooms", "Mushrooms", ForestKind.Decoration, 60),
        new("flowers", "Flower bed", ForestKind.Decoration, 80),
        new("bench", "Bench", ForestKind.Decoration, 120),
        new("lantern", "Stone lantern", ForestKind.Decoration, 180),
        new("pond", "Pond", ForestKind.Decoration, 250),
        new("well", "Well", ForestKind.Decoration, 350),
        new("cabin", "Cabin", ForestKind.Decoration, 900),
    ];

    /// <summary>Finds an entry by id.</summary>
    public static ForestSpecies? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>The stage a completed session of <paramref name="minutes"/> grows.</summary>
    public static ForestStage StageFor(int minutes) => minutes switch
    {
        < 25 => ForestStage.Sapling,
        < 50 => ForestStage.Young,
        < 90 => ForestStage.Mature,
        _ => ForestStage.Majestic,
    };

    /// <summary>Scene model key of a tree or bush stage, or of a decoration (whose stage is ignored).</summary>
    public static string ModelKey(ForestSpecies species, ForestStage stage) =>
        species.Kind == ForestKind.Decoration ? species.Id : $"{species.Id}-{stage.ToString().ToLowerInvariant()}";
}
