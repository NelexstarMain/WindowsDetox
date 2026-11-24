using Blocker.UI.Sdk;

namespace Blocker.SDK.Tests;

public class UiSceneValidatorTests
{
    private static readonly UiSceneFace Top = new([-0.4, 0.2, -0.4, -0.4, 0.2, 0.4, 0.4, 0.2, 0.4, 0.4, 0.2, -0.4], "#4CAF50");

    private static readonly UiSceneModel Bush = new("bush", [Top]);

    private static IReadOnlyList<string> Errors(UiNode node) => UiTreeValidator.Validate(Ui.Page("T", [node])).Errors;

    private static UiScene Scene(params UiSceneItem[] items) => new("forest", 6, 6, [Bush], items);

    [Fact]
    public void Validate_SceneWithItems_IsValidAndSurvivesJson()
    {
        UiScene scene = Scene(new UiSceneItem("t1", "bush", 2, 3, Rotation: 1, Scale: 1.2, IsSelected: true, Label: "Bush")) with { AllowPick = true };

        Assert.Empty(Errors(scene));

        UiScene round = Assert.IsType<UiScene>(UiJson.Deserialize(UiJson.Serialize(scene)));
        Assert.Equal(scene.Items, round.Items);
        Assert.Equal(Top.Points, round.Models[0].Faces[0].Points);
        Assert.True(round.AllowPick);
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(6, 25)]
    public void Validate_GridSizeOutOfRange_Rejected(int columns, int rows) =>
        Assert.Contains(Errors(Scene() with { Columns = columns, Rows = rows }), e => e.Contains("UiScene.Columns", StringComparison.Ordinal));

    [Fact]
    public void Validate_ItemOutsideGridOrOnOccupiedTile_Rejected()
    {
        Assert.Contains(Errors(Scene(new UiSceneItem("a", "bush", 6, 0))), e => e.Contains("outside", StringComparison.Ordinal));
        Assert.Contains(Errors(Scene(new UiSceneItem("a", "bush", 1, 1), new UiSceneItem("b", "bush", 1, 1))), e => e.Contains("same tile", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnknownModelOrDuplicateKey_Rejected()
    {
        Assert.Contains(Errors(Scene(new UiSceneItem("a", "oak", 0, 0))), e => e.Contains("unknown model", StringComparison.Ordinal));
        Assert.Contains(Errors(Scene() with { Models = [Bush, Bush] }), e => e.Contains("used more than once", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("4CAF50")]
    [InlineData("#4CAF5")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void Validate_BadColour_Rejected(string color) =>
        Assert.Contains(Errors(Scene() with { Models = [new UiSceneModel("bush", [Top with { Color = color }])] }), e => e.Contains("#RRGGBB", StringComparison.Ordinal));

    [Theory]
    [InlineData(new[] { 0.0, 0, 0, 1, 0, 0 })]                  // two vertices
    [InlineData(new[] { 0.0, 0, 0, 1, 0, 0, 1, 1 })]            // not triples
    [InlineData(new[] { 0.0, 0, 0, 1.5, 0, 0, 0, 1, 0 })]       // x outside -1..1
    [InlineData(new[] { 0.0, 0, 0, 1, 9, 0, 0, 1, 0 })]         // y above 8
    [InlineData(new[] { 0.0, 0, 0, double.NaN, 0, 0, 0, 1, 0 })] // not finite
    public void Validate_BadFacePoints_Rejected(double[] points) =>
        Assert.Contains(Errors(Scene() with { Models = [new UiSceneModel("bush", [new UiSceneFace(points, "#123456")])] }), e => e.Contains("UiSceneFace.Points", StringComparison.Ordinal));

    [Theory]
    [InlineData(4, 1.0)]
    [InlineData(-1, 1.0)]
    [InlineData(0, 0.1)]
    [InlineData(0, 2.5)]
    public void Validate_BadRotationOrScale_Rejected(int rotation, double scale) =>
        Assert.NotEmpty(Errors(Scene(new UiSceneItem("a", "bush", 0, 0, rotation, scale))));

    [Fact]
    public void Validate_TooManyFacesInTotal_Rejected()
    {
        UiSceneFace[] faces = [.. Enumerable.Repeat(Top, 400)];
        UiSceneModel[] models = [.. Enumerable.Range(0, 31).Select(i => new UiSceneModel("m" + i, faces))];
        Assert.Contains(Errors(Scene() with { Models = models }), e => e.Contains("12000", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ItemIdCollidesWithSceneId_Rejected() =>
        Assert.Contains(Errors(Scene(new UiSceneItem("forest", "bush", 0, 0))), e => e.Contains("more than once", StringComparison.Ordinal));
}
