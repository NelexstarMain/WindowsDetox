namespace Blocker.SDK.Tests;

public class PluginIdTests
{
    [Theory]
    [InlineData("com.example")]
    [InlineData("com.example.focus-counter")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d.e")]
    [InlineData("com.example2.app-3")]
    public void IsValid_ValidId_ReturnsTrue(string value) => Assert.True(PluginId.IsValid(value));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Com.example")]
    [InlineData("com.Example")]
    [InlineData("example")]
    [InlineData("a.b.c.d.e.f")]
    [InlineData("1com.example")]
    [InlineData("com.1example")]
    [InlineData("com..example")]
    [InlineData("com.example.")]
    [InlineData(".com.example")]
    [InlineData("com.exämple")]
    [InlineData("com.example_app")]
    [InlineData("com.example app")]
    [InlineData("com.example\n")]
    public void IsValid_InvalidId_ReturnsFalse(string? value) => Assert.False(PluginId.IsValid(value));

    [Fact]
    public void IsValid_Length128_ReturnsTrue() => Assert.True(PluginId.IsValid("a." + new string('b', 126)));

    [Fact]
    public void IsValid_Length129_ReturnsFalse() => Assert.False(PluginId.IsValid("a." + new string('b', 127)));

    [Fact]
    public void TryCreate_ValidId_ReturnsTrueAndValue()
    {
        bool ok = PluginId.TryCreate("com.example.app", out PluginId id);

        Assert.True(ok);
        Assert.Equal("com.example.app", id.Value);
        Assert.Equal("com.example.app", id.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bad.Id")]
    public void TryCreate_InvalidId_ReturnsFalseAndDefault(string? value)
    {
        bool ok = PluginId.TryCreate(value, out PluginId id);

        Assert.False(ok);
        Assert.Equal(default, id);
        Assert.Equal(string.Empty, id.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("nodots")]
    [InlineData("Com.Example")]
    public void Ctor_InvalidId_ThrowsArgumentException(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => new PluginId(value));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void Ctor_Null_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new PluginId(null!));

    [Fact]
    public void Equals_SameValue_AreEqual() =>
        Assert.Equal(new PluginId("com.example"), new PluginId("com.example"));
}
