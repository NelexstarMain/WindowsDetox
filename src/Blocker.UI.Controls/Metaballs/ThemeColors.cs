namespace Blocker.UI.Controls.Metaballs;

/// <summary>Non-premultiplied colour with float channels in 0..1.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
internal readonly record struct Rgba(float R, float G, float B, float A)
{
    /// <summary>Creates a colour from 8-bit channels.</summary>
    /// <param name="a">Alpha.</param>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <returns>The colour.</returns>
    public static Rgba FromBytes(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>Creates a colour from a packed <c>0xAARRGGBB</c> value.</summary>
    /// <param name="argb">Packed colour.</param>
    /// <returns>The colour.</returns>
    public static Rgba FromArgb(uint argb) =>
        FromBytes((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>Returns the colour with a different alpha.</summary>
    /// <param name="alpha">New alpha, 0..1.</param>
    /// <returns>The adjusted colour.</returns>
    public Rgba WithAlpha(float alpha) => this with { A = alpha };

    /// <summary>Returns the colour with RGB multiplied by <paramref name="factor"/> (and by alpha) and alpha multiplied by <paramref name="factor"/>.</summary>
    /// <param name="factor">Extra opacity factor, 0..1.</param>
    /// <returns>A premultiplied colour stored in the same shape.</returns>
    public Rgba Premultiplied(float factor = 1f)
    {
        float a = A * factor;
        return new Rgba(R * a, G * a, B * a, a);
    }

    /// <summary>Writes the four channels to <paramref name="destination"/> (R, G, B, A).</summary>
    /// <param name="destination">At least four floats.</param>
    public void WriteTo(Span<float> destination)
    {
        destination[0] = R;
        destination[1] = G;
        destination[2] = B;
        destination[3] = A;
    }

    /// <summary>Converts a channel to a byte.</summary>
    /// <param name="channel">Channel in 0..1.</param>
    /// <returns>The byte.</returns>
    public static byte ToByte(float channel) => (byte)Math.Clamp((int)MathF.Round(channel * 255f), 0, 255);
}

/// <summary>Colours used by the metaball surface, derived from the Blocker palette.</summary>
/// <param name="Fill">Body fill (matches the card background so bridges are seamless).</param>
/// <param name="Glow">Edge glow ring.</param>
/// <param name="Haze">Cursor haze over empty space.</param>
/// <param name="GridSubtle">Minor grid lines.</param>
/// <param name="GridStrong">Major grid lines (every fourth).</param>
/// <param name="GridBoost">Grid lines under the cursor spotlight.</param>
internal sealed record ThemeColors(Rgba Fill, Rgba Glow, Rgba Haze, Rgba GridSubtle, Rgba GridStrong, Rgba GridBoost)
{
    /// <summary>Gets the built-in dark palette (the Blocker theme values).</summary>
    public static ThemeColors Default { get; } = From(Rgba.FromArgb(0xFF181A1F), Rgba.FromArgb(0xFF3DD9C1));

    /// <summary>Derives all surface colours from the card background and the accent.</summary>
    /// <param name="cardBackground">Card background colour.</param>
    /// <param name="accent">Accent colour.</param>
    /// <returns>The colour set.</returns>
    public static ThemeColors From(Rgba cardBackground, Rgba accent) => new(
        cardBackground.WithAlpha(1f),
        accent.WithAlpha(0.55f),
        accent.WithAlpha(0.07f),
        new Rgba(1f, 1f, 1f, 0.035f),
        new Rgba(1f, 1f, 1f, 0.06f),
        accent.WithAlpha(0.22f));
}
