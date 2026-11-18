namespace Blocker.UI.Controls.Metaballs;

/// <summary>Layout rectangle of one metaball body in host DIPs.</summary>
/// <param name="Id">Stable identity so a body keeps its spring state while others come and go.</param>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Radius">Corner radius.</param>
internal readonly record struct BodyTarget(int Id, float X, float Y, float Width, float Height, float Radius);

/// <summary>
/// Pure animation state of the metaball layer: up to 32 spring-smoothed rounded rectangles plus the cursor blob.
/// Holds no WPF or Skia types and never allocates while stepping.
/// </summary>
internal sealed class MetaballScene
{
    /// <summary>Maximum number of bodies the shader supports.</summary>
    public const int MaxBodies = 32;

    /// <summary>Floats per body: x, y, width, height, radius.</summary>
    public const int Stride = 5;

    /// <summary>Default spring half-life in seconds.</summary>
    public const float DefaultHalfLife = 0.07f;

    private const float PositionEpsilon = 0.05f;
    private const float VelocityEpsilon = 0.5f;
    private const float ChangeEpsilon = 0.01f;
    private const float StrengthEpsilon = 0.002f;

    private int[] _ids = new int[MaxBodies];
    private float[] _target = new float[MaxBodies * Stride];
    private float[] _current = new float[MaxBodies * Stride];
    private float[] _velocity = new float[MaxBodies * Stride];
    private int[] _nextIds = new int[MaxBodies];
    private float[] _nextTarget = new float[MaxBodies * Stride];
    private float[] _nextCurrent = new float[MaxBodies * Stride];
    private float[] _nextVelocity = new float[MaxBodies * Stride];

    private float _cursorTargetX;
    private float _cursorTargetY;
    private float _cursorX;
    private float _cursorY;
    private float _cursorVelocityX;
    private float _cursorVelocityY;
    private float _strength;
    private float _strengthVelocity;
    private bool _cursorActive;

    /// <summary>Gets the number of live bodies.</summary>
    public int Count { get; private set; }

    /// <summary>Gets or sets the spring half-life in seconds.</summary>
    public float HalfLife { get; set; } = DefaultHalfLife;

    /// <summary>Gets the smoothed bodies as <c>x, y, width, height, radius</c> groups.</summary>
    public ReadOnlySpan<float> Bodies => _current.AsSpan(0, Count * Stride);

    /// <summary>Gets the smoothed cursor X.</summary>
    public float CursorX => _cursorX;

    /// <summary>Gets the smoothed cursor Y.</summary>
    public float CursorY => _cursorY;

    /// <summary>Gets the smoothed cursor presence from 0 (hidden) to 1 (fully shown).</summary>
    public float CursorStrength => Math.Clamp(_strength, 0f, 1f);

    /// <summary>Replaces the target layout. Bodies keep their spring state by id; new bodies appear in place.</summary>
    /// <param name="bodies">Targets, at most <see cref="MaxBodies"/> (extra entries are ignored).</param>
    /// <returns><see langword="true"/> when anything differs from the previous targets.</returns>
    public bool SetBodies(ReadOnlySpan<BodyTarget> bodies)
    {
        int count = Math.Min(bodies.Length, MaxBodies);
        bool changed = count != Count;

        for (int i = 0; i < count; i++)
        {
            BodyTarget b = bodies[i];
            int slot = IndexOf(b.Id);
            int o = i * Stride;
            _nextIds[i] = b.Id;
            _nextTarget[o] = b.X;
            _nextTarget[o + 1] = b.Y;
            _nextTarget[o + 2] = b.Width;
            _nextTarget[o + 3] = b.Height;
            _nextTarget[o + 4] = b.Radius;

            if (slot >= 0)
            {
                int s = slot * Stride;
                for (int j = 0; j < Stride; j++)
                {
                    _nextCurrent[o + j] = _current[s + j];
                    _nextVelocity[o + j] = _velocity[s + j];
                    changed |= MathF.Abs(_target[s + j] - _nextTarget[o + j]) > ChangeEpsilon;
                }

                changed |= slot != i;
            }
            else
            {
                changed = true;
                for (int j = 0; j < Stride; j++)
                {
                    _nextCurrent[o + j] = _nextTarget[o + j];
                    _nextVelocity[o + j] = 0f;
                }
            }
        }

        (_ids, _nextIds) = (_nextIds, _ids);
        (_target, _nextTarget) = (_nextTarget, _target);
        (_current, _nextCurrent) = (_nextCurrent, _current);
        (_velocity, _nextVelocity) = (_nextVelocity, _velocity);
        Count = count;
        return changed;
    }

    /// <summary>Moves the cursor blob target and shows it.</summary>
    /// <param name="x">X in host DIPs.</param>
    /// <param name="y">Y in host DIPs.</param>
    public void SetCursor(float x, float y)
    {
        if (!_cursorActive && _strength < 0.01f)
        {
            _cursorX = x;
            _cursorY = y;
            _cursorVelocityX = 0f;
            _cursorVelocityY = 0f;
        }

        _cursorTargetX = x;
        _cursorTargetY = y;
        _cursorActive = true;
    }

    /// <summary>Fades the cursor blob out (pointer left the host).</summary>
    public void ClearCursor() => _cursorActive = false;

    /// <summary>Jumps every body to its target immediately (used while scrolling and for reduced motion).</summary>
    public void SnapBodies()
    {
        Array.Copy(_target, _current, Count * Stride);
        Array.Clear(_velocity, 0, Count * Stride);
    }

    /// <summary>Advances all springs.</summary>
    /// <param name="dt">Elapsed seconds.</param>
    /// <returns><see langword="true"/> while anything is still moving, so the render loop must keep running.</returns>
    public bool Step(float dt)
    {
        bool moving = false;
        int n = Count * Stride;
        for (int i = 0; i < n; i++)
        {
            moving |= Advance(ref _current[i], ref _velocity[i], _target[i], dt);
        }

        moving |= Advance(ref _cursorX, ref _cursorVelocityX, _cursorTargetX, dt);
        moving |= Advance(ref _cursorY, ref _cursorVelocityY, _cursorTargetY, dt);
        moving |= Advance(ref _strength, ref _strengthVelocity, _cursorActive ? 1f : 0f, dt, StrengthEpsilon, StrengthEpsilon * 4f);
        return moving;
    }

    private bool Advance(ref float position, ref float velocity, float goal, float dt, float positionEpsilon = PositionEpsilon, float velocityEpsilon = VelocityEpsilon)
    {
        Spring.Step(ref position, ref velocity, goal, HalfLife, dt);
        if (MathF.Abs(position - goal) <= positionEpsilon && MathF.Abs(velocity) <= velocityEpsilon)
        {
            position = goal;
            velocity = 0f;
            return false;
        }

        return true;
    }

    private int IndexOf(int id)
    {
        for (int i = 0; i < Count; i++)
        {
            if (_ids[i] == id)
            {
                return i;
            }
        }

        return -1;
    }
}
