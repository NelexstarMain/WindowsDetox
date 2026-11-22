using System.Text.Json;
using Blocker.UI.Sdk;

namespace Blocker.SDK.Tests;

public class UiJsonTests
{
    private static UiPage FullTree() =>
        Ui.Page(
            "Everything",
            "Sub",
            Ui.Column(Ui.Text("Hello", UiTextStyle.Title, UiTone.Success)),
            Ui.Row(Ui.Stat("Blocked", "12", "today", UiTone.Warning, UiIcon.Shield)),
            Ui.Grid(3, Ui.Progress(2.5, 10, "Goal")),
            new UiStack([Ui.Text("g")], UiOrientation.Horizontal, UiGap.Large),
            new UiCard([Ui.Text("in")], "Title", "Sub", UiIcon.Star),
            Ui.BarChart("Bars", Ui.Point("a", 1), Ui.Point("b", 2.5)),
            new UiBarChart([Ui.Point("x", 3)], "Suffix", "min"),
            new UiLineChart([new UiSeries("s1", [Ui.Point("a", 1), Ui.Point("b", 2)])], "Lines", "h"),
            new UiTable(
                [new UiTableColumn("Name"), new UiTableColumn("N", UiAlignment.End, Monospace: true)],
                [new UiTableRow(["a", "1"]), new UiTableRow(["b", "2"])]),
            new UiButton("btn", "Go", UiButtonVariant.Primary, UiIcon.Play, IsEnabled: false),
            Ui.Toggle("tog", "Toggle", true, "Why"),
            Ui.Form(
                "frm",
                "Save",
                new UiTextField("txt", "Text", "val", "ph", 50),
                new UiNumberField("num", "Number", 5, 0, 10, 0.5),
                new UiSelectField("sel", "Select", [new UiOption("a", "A"), new UiOption("b", "B")], "b"),
                new UiCheckboxField("chk", "Check", true)));

    [Fact]
    public void Roundtrip_TreeWithEveryNodeAndField_PreservesStructure()
    {
        UiPage original = FullTree();
        string json = UiJson.Serialize(original);

        UiNode restored = UiJson.Deserialize(json);

        Assert.Equal(json, UiJson.Serialize(restored));
        UiPage page = Assert.IsType<UiPage>(restored);
        Assert.Equal("Everything", page.Title);
        Assert.Equal("Sub", page.Subtitle);
        Assert.Equal(original.Children.Count, page.Children.Count);
        Assert.Equal(original.Children.Select(c => c.GetType()), page.Children.Select(c => c.GetType()));

        var button = Assert.IsType<UiButton>(page.Children[9]);
        Assert.Equal(new UiButton("btn", "Go", UiButtonVariant.Primary, UiIcon.Play, false), button);

        var form = Assert.IsType<UiForm>(page.Children[11]);
        Assert.Equal(
            [typeof(UiTextField), typeof(UiNumberField), typeof(UiSelectField), typeof(UiCheckboxField)],
            form.Fields.Select(f => f.GetType()));
        Assert.Equal(new UiNumberField("num", "Number", 5, 0, 10, 0.5), form.Fields[1]);
        var select = Assert.IsType<UiSelectField>(form.Fields[2]);
        Assert.Equal("b", select.SelectedValue);
        Assert.Equal(["a", "b"], select.Options.Select(o => o.Value));
    }

    [Fact]
    public void Serialize_Text_UsesDiscriminatorCamelCaseAndStringEnums() =>
        Assert.Equal(
            """{"$type":"text","text":"x","style":"Title","tone":"Danger"}""",
            UiJson.Serialize(Ui.Text("x", UiTextStyle.Title, UiTone.Danger)));

    [Fact]
    public void Deserialize_UnknownProperty_ThrowsJsonException() =>
        Assert.Throws<JsonException>(() => UiJson.Deserialize("""{"$type":"text","text":"x","bogus":1}"""));

    [Fact]
    public void Deserialize_UnknownType_ThrowsJsonException() =>
        Assert.Throws<JsonException>(() => UiJson.Deserialize("""{"$type":"nope"}"""));

    [Fact]
    public void Deserialize_MalformedJson_ThrowsJsonException() =>
        Assert.Throws<JsonException>(() => UiJson.Deserialize("{"));

    [Fact]
    public void Deserialize_JsonNull_ThrowsJsonException() =>
        Assert.Throws<JsonException>(() => UiJson.Deserialize("null"));

    [Fact]
    public void Deserialize_DocumentOverMaxBytes_ThrowsJsonException()
    {
        string json = UiJson.Serialize(Ui.Text(new string('a', UiJson.MaxDocumentBytes)));

        var ex = Assert.Throws<JsonException>(() => UiJson.Deserialize(json));

        Assert.Equal("UI document exceeds 524288 bytes.", ex.Message);
    }

    [Fact]
    public void Deserialize_Null_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => UiJson.Deserialize(null!));

    [Fact]
    public void Serialize_Null_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => UiJson.Serialize(null!));

    [Fact]
    public void Roundtrip_StylesNotesFooterIconOnlyAndPast_ArePreserved()
    {
        UiPage original = Ui.Page(
            "T",
            [
                new UiForm(
                    "frm",
                    [
                        new UiTextField("title", "Title") { Style = UiFieldStyle.Title },
                        new UiSelectField("kind", "Kind", [new UiOption("a", "A"), new UiOption("b", "B")]) { Style = UiFieldStyle.Chips },
                        new UiNoteField("when", "When", "Monday", "Does not repeat", UiIcon.Clock),
                    ],
                    "Save") { FooterAction = Ui.Button("more", "More") },
                new UiButton("edit", "Edit", UiButtonVariant.Subtle, UiIcon.Edit) { IconOnly = true },
                new UiTimeGrid("g", [new UiTimeGridDay("d", "Mon")], [new UiTimeGridEvent("e", 0, 60, 120, "Past") { IsPast = true }]),
            ]);

        string json = UiJson.Serialize(original);
        var restored = Assert.IsType<UiPage>(UiJson.Deserialize(json));

        var form = Assert.IsType<UiForm>(restored.Children[0]);
        Assert.Equal([UiFieldStyle.Title, UiFieldStyle.Chips, UiFieldStyle.Standard], form.Fields.Select(f => f.Style));
        Assert.Equal(new UiNoteField("when", "When", "Monday", "Does not repeat", UiIcon.Clock), form.Fields[2]);
        Assert.Equal("more", form.FooterAction?.Id);
        Assert.True(Assert.IsType<UiButton>(restored.Children[1]).IconOnly);
        Assert.True(Assert.IsType<UiTimeGrid>(restored.Children[2]).Events[0].IsPast);
        Assert.DoesNotContain("\"style\":\"Standard\"", json);
    }
}
