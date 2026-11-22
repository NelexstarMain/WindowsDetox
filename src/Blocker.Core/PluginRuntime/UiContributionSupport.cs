using System.Text.RegularExpressions;
using Blocker.Contracts;
using Blocker.Sdk.Extensions;
using Blocker.UI.Sdk;

namespace Blocker.Core.PluginRuntime;

/// <summary>A plugin returned something the host refuses to render (invalid tree, oversized tree, bad title).</summary>
internal sealed class InvalidContributionException(string message) : Exception(message);

/// <summary>Contribution ids: <c>{slot}-{registration number}</c>, for example <c>widget-0</c>.</summary>
internal static partial class ContributionIds
{
    public static string For(UiSlot slot, int index) => $"{Prefix(slot)}-{index}";

    public static bool TryParse(string? id, out UiSlot slot, out int index)
    {
        slot = default;
        index = 0;
        if (id is null || !IdPattern().IsMatch(id))
        {
            return false;
        }

        int dash = id.IndexOf('-', StringComparison.Ordinal);
        string prefix = id[..dash];
        foreach (UiSlot candidate in Enum.GetValues<UiSlot>())
        {
            if (Prefix(candidate) == prefix && int.TryParse(id.AsSpan(dash + 1), out index))
            {
                slot = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether <paramref name="extension"/> contributes to <paramref name="slot"/>.</summary>
    public static bool Contributes(UiSlot slot, IPluginExtension extension) => slot switch
    {
        UiSlot.Page => extension is IUiPagePlugin,
        UiSlot.DashboardWidget => extension is IDashboardWidgetPlugin,
        UiSlot.SettingsSection => extension is ISettingsSectionPlugin,
        UiSlot.BlockScreen => extension is IBlockScreenPlugin,
        _ => false,
    };

    private static string Prefix(UiSlot slot) => slot switch
    {
        UiSlot.Page => "page",
        UiSlot.DashboardWidget => "widget",
        UiSlot.SettingsSection => "settings",
        _ => "block",
    };

    [GeneratedRegex("^(page|widget|settings|block)-[0-9]{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}

/// <summary>Validation of user interactions before they reach plugin code (the IPC peer is untrusted).</summary>
internal static partial class UiActionValidator
{
    private const int MaxFormValues = 50;
    private const int MaxTextLength = 2000;

    public static bool IsValid(UiAction? action)
    {
        if (action is null || !Enum.IsDefined(action.Kind) || action.ElementId is null || !ElementIdPattern().IsMatch(action.ElementId))
        {
            return false;
        }

        return action.Kind switch
        {
            UiActionKind.Toggle => action.Value is "true" or "false" && FormValuesEmpty(action),
            UiActionKind.Submit => action.Value is null && ValidForm(action.FormValues),
            UiActionKind.Schedule => action.Value is null && ValidSchedule(action.FormValues),
            UiActionKind.Click => action.Value is null && (FormValuesEmpty(action) || IsExtendFlag(action.FormValues)),
            _ => action.Value is null && FormValuesEmpty(action),
        };
    }

    private static bool IsExtendFlag(IReadOnlyDictionary<string, string>? values) =>
        values is { Count: 1 } && values.TryGetValue("extend", out string? extend) && extend == "true";

    private static bool ValidSchedule(IReadOnlyDictionary<string, string>? values)
    {
        const int minutesPerDay = 1440;
        const int maxDays = 7;
        return values is { Count: 3 }
            && TryMinute(values, "day", out int day) && day < maxDays
            && TryMinute(values, "start", out int start)
            && TryMinute(values, "end", out int end)
            && start < end && end <= minutesPerDay;
    }

    private static bool TryMinute(IReadOnlyDictionary<string, string> values, string key, out int value)
    {
        value = 0;
        return values.TryGetValue(key, out string? text)
            && text is { Length: > 0 and <= 4 }
            && text.All(char.IsAsciiDigit)
            && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool FormValuesEmpty(UiAction action) => action.FormValues is null or { Count: 0 };

    private static bool ValidForm(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count > MaxFormValues)
        {
            return false;
        }

        foreach ((string key, string value) in values)
        {
            if (!ElementIdPattern().IsMatch(key) || value is null || value.Length > MaxTextLength || value.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ElementIdPattern();
}
