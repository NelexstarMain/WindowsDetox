# Focus Counter (Blocker starter plugin)

A tiny working plugin that counts the distractions Blocker stopped during a detox session. Use it as the base for your own plugin.

## Quickstart

1. Open this folder in your editor or AI agent. Give the agent `AGENT_INSTRUCTIONS.md` and `STYLE_GUIDE.md` first.
2. Change `id`, `name`, `author`, `description` and `entryType` in `plugin.json`, then rename the namespace in the `.cs` files.
3. Edit `PluginMain.cs`; keep `capabilities` in `plugin.json` limited to what the code uses.
4. Update `PluginTests.cs` so every feature, including failure cases, is tested.
5. Zip the **contents** of the folder (`plugin.json` must be at the ZIP root) and upload it in Blocker, Plugins.
   Blocker validates, compiles and tests the plugin, and installs it only if everything passes.
   Installing or removing plugins is not possible during an active detox session.

Files: `plugin.json` (manifest), `PluginMain.cs` (plugin), `PluginTests.cs` (tests), `AGENT_INSTRUCTIONS.md`, `STYLE_GUIDE.md`.
Allowed in the ZIP: `.cs`, `.json`, `.md`; at most 50 entries, 2 MB unpacked, 5 MB ZIP. `description` in `plugin.json` is optional.
