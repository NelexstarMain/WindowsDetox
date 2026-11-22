using System.Globalization;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;

namespace Blocker.UI.Render;

/// <summary>A bundled plugin running in-process on a <see cref="FakePluginContext"/>, seeded with data.</summary>
internal sealed class LoadedPlugin
{
    public required string Folder { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string Author { get; init; }

    public required string? Icon { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required FakePluginContext Context { get; init; }

    public required IBlockerPlugin Instance { get; init; }
}

/// <summary>
/// Stands in for the plugin host process: runs the bundled plugins in this process and answers the UI's contribution and action
/// requests the way <c>PluginHostRuntime</c> does (same titles, ids and JSON round trip).
/// A new world is created for every screenshot so one shot cannot leak plugin state into the next.
/// </summary>
internal sealed class PluginWorld
{
    private static readonly string[] Folders = ["Calendar", "GoalsTasks", "FocusInsights"];

    private PluginWorld(IReadOnlyList<LoadedPlugin> plugins) => Plugins = plugins;

    public IReadOnlyList<LoadedPlugin> Plugins { get; }

    /// <summary>Creates, seeds and starts every bundled plugin with its clock at <paramref name="now"/> (UTC is the local zone).</summary>
    /// <param name="pluginsDirectory">Directory with one folder of <c>plugin.json</c> per plugin.</param>
    /// <param name="now">The fixed current time.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The world.</returns>
    public static async Task<PluginWorld> CreateAsync(string pluginsDirectory, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<LoadedPlugin> plugins = [];
        foreach (string folder in Folders)
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginsDirectory, folder, "plugin.json")));
            JsonElement root = manifest.RootElement;
            string id = root.GetProperty("id").GetString()!;
            Type entry = typeof(PluginWorld).Assembly.GetType(root.GetProperty("entryType").GetString()!, throwOnError: true)!;
            var context = new FakePluginContext(id);
            var instance = (IBlockerPlugin)Activator.CreateInstance(entry)!;

            await PluginSeeds.SeedAsync(folder, context, now, cancellationToken);
            await instance.InitializeAsync(context, cancellationToken);
            if (folder == "FocusInsights")
            {
                await PluginSeeds.ReplayInsightsAsync(context, now, cancellationToken);
            }

            context.FakeTime.SetUtcNow(now);
            plugins.Add(new LoadedPlugin
            {
                Folder = folder,
                Id = id,
                Name = root.GetProperty("name").GetString()!,
                Version = root.GetProperty("version").GetString()!,
                Author = root.GetProperty("author").GetString()!,
                Icon = root.TryGetProperty("icon", out JsonElement icon) ? icon.GetString() : null,
                Capabilities = [.. root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)],
                Context = context,
                Instance = instance,
            });
        }

        return new PluginWorld(plugins);
    }

    /// <summary>Tells every plugin which session is running.</summary>
    /// <param name="snapshot">The session.</param>
    public void SetSession(SessionSnapshot snapshot)
    {
        foreach (LoadedPlugin plugin in Plugins)
        {
            plugin.Context.FakeSession.Current = snapshot;
        }
    }

    /// <summary>Renders every contribution of a slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The contributions in plugin and registration order.</returns>
    public async Task<GetContributionsResponse> RenderSlotAsync(UiSlot slot, CancellationToken cancellationToken)
    {
        List<UiContributionDto> items = [];
        foreach (LoadedPlugin plugin in Plugins)
        {
            IReadOnlyList<IPluginExtension> extensions = plugin.Context.RegisteredExtensions;
            for (int index = 0; index < extensions.Count; index++)
            {
                if (extensions[index] is not IUiContribution contribution)
                {
                    continue;
                }

                (string Title, bool WithIcon)? shown = slot switch
                {
                    UiSlot.Page when contribution is IUiPagePlugin page => (page.Navigation.Title, true),
                    UiSlot.DashboardWidget when contribution is IDashboardWidgetPlugin => (plugin.Name, true),
                    UiSlot.SettingsSection when contribution is ISettingsSectionPlugin section => (section.Title, false),
                    _ => null,
                };
                if (shown is not { } show)
                {
                    continue;
                }

                UiNode node = await plugin.Context.RenderAsync(contribution, cancellationToken);
                items.Add(new UiContributionDto(plugin.Id, ContributionId(slot, index), show.Title, UiJson.Serialize(node), show.WithIcon ? plugin.Icon : null));
            }
        }

        return new GetContributionsResponse(slot, items);
    }

    /// <summary>Delivers a user interaction to the contribution it was meant for.</summary>
    /// <param name="request">The request as the UI sent it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the plugin handled it.</returns>
    public ValueTask SendActionAsync(SendActionRequest request, CancellationToken cancellationToken)
    {
        LoadedPlugin plugin = Plugins.First(p => p.Id == request.PluginId);
        int index = int.Parse(request.ContributionId[(request.ContributionId.IndexOf('-', StringComparison.Ordinal) + 1)..], CultureInfo.InvariantCulture);
        return plugin.Context.SendActionAsync((IUiContribution)plugin.Context.RegisteredExtensions[index], request.Action, cancellationToken);
    }

    private static string ContributionId(UiSlot slot, int index) => slot switch
    {
        UiSlot.Page => $"page-{index}",
        UiSlot.DashboardWidget => $"widget-{index}",
        UiSlot.SettingsSection => $"settings-{index}",
        _ => $"block-{index}",
    };
}
