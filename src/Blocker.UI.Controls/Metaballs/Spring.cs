namespace Blocker.UI.Controls.Metaballs;

/// <summary>Critically damped spring used to smooth cursor and body motion; exact for any time step.</summary>
internal static class Spring
{
    /// <summary>Root of <c>(1 + s) * e^-s = 0.5</c>: the error of a critically damped spring released at rest halves after <c>s / rate</c> seconds.</summary>
    private const float HalfLifeFactor = 1.6783470f;

    /// <summary>Advances <paramref name="position"/> towards <paramref name="goal"/>.</summary>
    /// <param name="position">Current value; updated in place.</param>
    /// <param name="velocity">Current velocity; updated in place.</param>
    /// <param name="goal">Target value.</param>
    /// <param name="halfLife">Seconds for the remaining error to halve when starting at rest.</param>
    /// <param name="dt">Elapsed seconds.</param>
    public static void Step(ref float position, ref float velocity, float goal, float halfLife, float dt)
    {
        float rate = HalfLifeFactor / MathF.Max(halfLife, 1e-4f);
        float offset = position - goal;
        float carry = velocity + offset * rate;
        float decay = MathF.Exp(-rate * dt);
        position = (decay * (offset + carry * dt)) + goal;
        velocity = decay * (velocity - (carry * rate * dt));
    }
}
