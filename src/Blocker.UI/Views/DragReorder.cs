namespace Blocker.UI.Views;

/// <summary>
/// Decides when a dragged widget swaps places with the one under the pointer. After a swap the pointer must come back into the dragged
/// widget's own slot (or travel <see cref="SwapDistance"/>) before it swaps again, so widgets of different sizes cannot flip back and
/// forth under a pointer that stands still.
/// </summary>
internal sealed class DragReorder
{
    /// <summary>Distance in DIPs the pointer must travel after a swap to allow another one without re-entering the dragged slot.</summary>
    public const double SwapDistance = 24;

    private bool _armed;
    private double _x;
    private double _y;

    /// <summary>Starts a drag at a pointer position.</summary>
    /// <param name="x">Pointer X.</param>
    /// <param name="y">Pointer Y.</param>
    public void Begin(double x, double y)
    {
        _armed = true;
        (_x, _y) = (x, y);
    }

    /// <summary>Gets the position to move the dragged widget to for the current pointer.</summary>
    /// <param name="dragged">Position of the dragged widget in the current order.</param>
    /// <param name="hit">Position of the widget under the pointer, or -1 for none.</param>
    /// <param name="x">Pointer X.</param>
    /// <param name="y">Pointer Y.</param>
    /// <returns>The position to move to, or -1 to stay.</returns>
    public int TargetIndex(int dragged, int hit, double x, double y)
    {
        if (hit < 0)
        {
            return -1;
        }

        if (hit == dragged)
        {
            _armed = true;
            return -1;
        }

        return _armed || Math.Sqrt(((x - _x) * (x - _x)) + ((y - _y) * (y - _y))) > SwapDistance ? hit : -1;
    }

    /// <summary>Records that the order changed at a pointer position.</summary>
    /// <param name="x">Pointer X.</param>
    /// <param name="y">Pointer Y.</param>
    public void Swapped(double x, double y)
    {
        _armed = false;
        (_x, _y) = (x, y);
    }
}
