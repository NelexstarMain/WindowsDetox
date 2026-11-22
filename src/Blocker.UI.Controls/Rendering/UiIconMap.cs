using Blocker.UI.Sdk;
using Wpf.Ui.Controls;

namespace Blocker.UI.Controls.Rendering;

/// <summary>Maps the curated <see cref="UiIcon"/> set to Fluent system icons.</summary>
public static class UiIconMap
{
    /// <summary>Gets the WPF-UI symbol for <paramref name="icon"/>.</summary>
    /// <param name="icon">The curated icon.</param>
    /// <returns>The matching symbol.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="icon"/> is not a defined value.</exception>
    public static SymbolRegular ToSymbol(UiIcon icon) => icon switch
    {
        UiIcon.Clock => SymbolRegular.Clock24,
        UiIcon.Timer => SymbolRegular.Timer24,
        UiIcon.Shield => SymbolRegular.Shield24,
        UiIcon.Lock => SymbolRegular.LockClosed24,
        UiIcon.Unlock => SymbolRegular.LockOpen24,
        UiIcon.Play => SymbolRegular.Play24,
        UiIcon.Pause => SymbolRegular.Pause24,
        UiIcon.Stop => SymbolRegular.Stop24,
        UiIcon.Warning => SymbolRegular.Warning24,
        UiIcon.Info => SymbolRegular.Info24,
        UiIcon.Checkmark => SymbolRegular.Checkmark24,
        UiIcon.Dismiss => SymbolRegular.Dismiss24,
        UiIcon.Settings => SymbolRegular.Settings24,
        UiIcon.Chart => SymbolRegular.DataBarVertical24,
        UiIcon.Calendar => SymbolRegular.Calendar24,
        UiIcon.Apps => SymbolRegular.Apps24,
        UiIcon.Globe => SymbolRegular.Globe24,
        UiIcon.Plug => SymbolRegular.PlugConnected24,
        UiIcon.Flame => SymbolRegular.Fire24,
        UiIcon.Leaf => SymbolRegular.LeafTwo24,
        UiIcon.Target => SymbolRegular.Target24,
        UiIcon.Star => SymbolRegular.Star24,
        UiIcon.Heart => SymbolRegular.Heart24,
        UiIcon.Book => SymbolRegular.Book24,
        UiIcon.Code => SymbolRegular.Code24,
        UiIcon.Music => SymbolRegular.MusicNote124,
        UiIcon.Game => SymbolRegular.Games24,
        UiIcon.Moon => SymbolRegular.WeatherMoon24,
        UiIcon.Sun => SymbolRegular.WeatherSunny24,
        UiIcon.Coffee => SymbolRegular.DrinkCoffee24,
        UiIcon.Folder => SymbolRegular.Folder24,
        UiIcon.Trophy => SymbolRegular.Trophy24,
        UiIcon.Edit => SymbolRegular.Edit24,
        UiIcon.Copy => SymbolRegular.Copy24,
        UiIcon.Delete => SymbolRegular.Delete24,
        _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, null),
    };
}
