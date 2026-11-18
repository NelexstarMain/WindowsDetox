# WindowsDetox — UI Redesign & Icon Integration Guide (`CHANGES.md`)

> **READ THIS FIRST (CRITICAL CONTEXT FOR IMPLEMENTING AI):**
> - You are starting from the **original, untouched `WindowsDetox` repository** (`WindowsDetox.slnx`). None of the redesign changes exist in the codebase yet.
> - Everything in this folder (`icons/`, `xaml/`, and this `CHANGES.md`) is a **visual style & layout template** paired with ready-to-use `#BFD0B4` icon assets and WPF resource dictionaries.
> - **ADAPT TO REAL FEATURES & CONTENT:** Treat `xaml/ViewTemplates.xaml` as a structural & stylistic template—**never** hardcode preview placeholder strings or drop real features. Preserve every existing `WindowsDetox` ViewModel binding, validation message, command, IPC behavior, lock banner, plugin `UiNodeView` slot (`Widgets`, `Sections`, `PluginPages`, `BlockWindow.Contributions`), and real domain copy.

---

## 1. Contents of This Package

```text
windowsdetox-ui-redesign/
├── CHANGES.md                         # Complete file-by-file change map (from original repo)
├── icons/
│   ├── windowsdetox-icon.ico          # Multi-res Windows app icon (256, 64, 48, 32, 16) in #BFD0B4
│   ├── windowsdetox-icon-256.png      # Transparent 256x256 #BFD0B4 PNG
│   ├── windowsdetox-logo.svg          # 4-wedge vector SVG (#BFD0B4)
│   └── windowsdetox-logo.xaml         # Standalone WPF ResourceDictionary (Blocker.Logo.Brush + Geometry)
└── xaml/
    ├── Blocker.xaml                   # Drop-in replacement for src/Blocker.UI.Controls/Themes/Blocker.xaml
    ├── Shared.xaml                    # Drop-in replacement for src/Blocker.UI/Resources/Shared.xaml
    └── ViewTemplates.xaml             # Visual XAML templates for all Windows/Pages (adapt to ViewModels)
```

---

## 2. How the New Design Works

1. **Deep Black Monochrome Surface System:**
   - Sidebar background: `#07080A` (`Blocker.Bg.Base`)
   - Workspace / Window canvas: `#0B0C0F` (`Blocker.Bg.Canvas`)
   - Dialog / card / tooltip surface: `#111215` / `#08090C` (`Blocker.Bg.Card`, `Blocker.Bg.Raised` `#1A1D25`)
   - Hairline borders: `#181A21` (`Blocker.Border.Subtle`), stronger borders `#323746` (`Blocker.Border.Strong`)
   - **No outer `CardBorder` wrappers around page content.** Native pages (`DashboardPage`, `SetupPage`, `AllowlistPage`, `PluginsPage`, `SettingsPage`) use whitespace and `1px` `Blocker.SectionDivider` hairlines.
2. **Signature Accents (`#BFD0B4` & `#5F6F2E`):**
   - **Sage `#BFD0B4` (`Blocker.Logo.Brush` / `Blocker.Success`):** Used for the 4-wedge brand logo in the sidebar, the `0.025`-opacity background watermark on the workspace & block screen, active sidebar icons, keyboard focus rings, the system tray icon, and the `.exe` Windows application icon (`windowsdetox-icon.ico`).
   - **Olive Moss `#5F6F2E` (`Blocker.Accent`, hover `#6E8036`):** Used for primary CTA buttons (`Start session`, `Add`, `Save allowlist`, `Install (.zip)`), selected duration pills (`#252C15` background + `#5F6F2E` border + `#DCE6D3` text), hover border on `?` info badges, and the `4px` session progress bar (`#7C913D`).
3. **Mixed Button & Control Geometry (Intentionally Varied, Not Uniform):**
   - `Blocker.Button.Primary`: Crisp `4px` radius, borderless `#5F6F2E` fill.
   - `Blocker.Button.Secondary` / `Blocker.Button.Warning`: Crisp `4px` radius monochrome (`#161820` fill, `#2B303C` border).
   - `Blocker.Button.SecondaryPill` / `App.PillItem`: Smooth `"Badania"`-style rounded pill (`CornerRadius="20"` / `18`).
   - `Blocker.Button.GhostPill`: Borderless `"Badania"` pill (`#121419` fill, `BorderThickness="0"`), used for `Break-glass code` and secondary dialog/template actions.
   - `ui:ToggleSwitch`: Custom square-box track (`42×22`, `CornerRadius="5"`) and square thumb (`14×14`, `CornerRadius="3"`).
4. **Typography & Countdown Digits:**
   - Hero countdown (`Blocker.Text.Clock`): `Segoe UI Variable Display` (`Light`, `74px`, tabular) so zeros render **open and unslashed** (`0`, not `Ø`).
   - UI body/titles: `Segoe UI Variable Text`.
   - Technical fields only (domain patterns, break-glass code, version): `Cascadia Mono, Consolas`.
5. **Zero AI Slop & Lowered `?` Micro-Badges:**
   - Remove filler subtitles, `"Service connected"` status cards, `"IDLE"` / `"REMAINING"` telemetry dots, `"elapsed %"` labels, and multi-sentence helper paragraphs.
   - Move essential explanations into **`Blocker.InfoBadge`** (`14×14` sharp `CornerRadius="2"` square `?` badge, slightly lowered via `Margin="..., -1, 0, 0"`) with sharp `Blocker.InfoTip.ToolTip` (`CornerRadius="2"`).
6. **Unified Full-Height Sidebar (No 4-Section Crosshair):**
   - The `#07080A` left column runs uninterrupted from top to bottom with the `#BFD0B4` logo + `Windows Detox` at the top.
   - `ui:TitleBar` must NOT draw a horizontal hairline cutting across above the sidebar logo.
   - Active navigation item uses `#14171E` background + `1px` `#222632` border (`CornerRadius="4"`) with `#BFD0B4` icon and NO protruding left accent bar.
7. **Complete Metaball & BlueprintGrid Removal:**
   - Remove `MetaballHost`, `Metaball.IsBody`, `BlueprintGrid`, `UiPreferences`, and the `"Effects quality"` setting across XAML, C#, and tests.

---

## 3. Application Icon Setup (`#BFD0B4` Across `.exe`, Window, Tray & UI)

**Mandatory requirement:** Install the `#BFD0B4` 4-wedge logo as the actual Windows application icon, window icon, tray icon, and in-app brand logo:

1. **Copy icon files from `icons/` into the projects:**
   - Copy `icons/windowsdetox-icon.ico` -> `src/Blocker.UI/Assets/app.ico`
   - Copy `icons/windowsdetox-icon.ico` -> `src/Blocker.Stub/Assets/app.ico`
2. **Register `<ApplicationIcon>` in both `.csproj` executables:**
   - In `src/Blocker.UI/Blocker.UI.csproj` and `src/Blocker.Stub/Blocker.Stub.csproj`:
     ```xml
     <PropertyGroup>
       <!-- existing properties -->
       <ApplicationIcon>Assets\app.ico</ApplicationIcon>
     </PropertyGroup>
     <ItemGroup>
       <Resource Include="Assets\app.ico" />
     </ItemGroup>
     ```
   - Set `Icon="pack://application:,,,/Assets/app.ico"` on `MainWindow.xaml` and `BlockWindow.xaml` so Alt+Tab and window corners match the `.exe` icon.
3. **Update Notification-Area Tray Icon (`src/Blocker.UI/Agent/TrayService.cs`):**
   - Original `RenderIcon()` draws a `#3DD9C1` ring + dot.
   - Replace it so it renders `Blocker.Logo.Geometry` (from `icons/windowsdetox-logo.xaml` / `Blocker.xaml`) scaled to `32×32` in `#BFD0B4` (`Color.FromRgb(0xBF, 0xD0, 0xB4)`), returning `32 * 32 * 4` premultiplied BGRA bytes via `RenderTargetBitmap`.
4. **In-App Sidebar & Watermark (`MainWindow.xaml`, `BlockWindow.xaml`):**
   - Render `<Path Data="{StaticResource Blocker.Logo.Geometry}" Fill="{DynamicResource Blocker.Logo.Brush}" Stretch="Uniform" />` in the sidebar header (`19×19`) and subtle workspace watermark (`Opacity="0.025"`).

---

## 4. Exact File-by-File Change Map (Starting From Original Repo)

Summary count starting from original `WindowsDetox`:
- **Add:** 2 `.ico` asset copies (`src/Blocker.UI/Assets/app.ico`, `src/Blocker.Stub/Assets/app.ico`)
- **Modify:** 22 source files (+ optional docs `STYLE_GUIDE.md`)
- **Delete:** 13 Metaball / Effects files

### A. Files to DELETE (13 files)

1–11. **Delete entire `src/Blocker.UI.Controls/Metaballs/` directory** (11 files):
   - `BlueprintGridPainter.cs`
   - `BlurThresholdMetaballRenderer.cs`
   - `Metaball.cs`
   - `MetaballFrame.cs`
   - `MetaballHost.cs`
   - `MetaballMath.cs`
   - `MetaballScene.cs`
   - `MetaballSurface.cs`
   - `SdfMetaballRenderer.cs`
   - `Spring.cs`
   - `ThemeColors.cs`
12. **Delete `src/Blocker.UI/Services/UiPreferences.cs`** (`IUiPreferences` / `UiPreferences` only persisted `MetaballQuality`).
13. **Delete `tests/Blocker.UI.Controls.Tests/MetaballMathTests.cs`** (tests removed metaball math).

---

### B. Files to MODIFY (22 files)

| # | Original File | Exact Changes Required |
|---|---|---|
| 1 | `src/Blocker.UI/Blocker.UI.csproj` | Add `<ApplicationIcon>Assets\app.ico</ApplicationIcon>` in `<PropertyGroup>` and `<Resource Include="Assets\app.ico" />`. |
| 2 | `src/Blocker.Stub/Blocker.Stub.csproj` | Add `<ApplicationIcon>Assets\app.ico</ApplicationIcon>` in `<PropertyGroup>` and `<Resource Include="Assets\app.ico" />`. |
| 3 | `src/Blocker.UI.Controls/Blocker.UI.Controls.csproj` | Remove `<PackageReference Include="SkiaSharp.Views.WPF" />` (was only used by Metaballs/BlueprintGrid). Update `<Description>` to remove blueprint grid & metaball mentions. |
| 4 | `src/Blocker.UI.Controls/Themes/Blocker.xaml` | **Replace directly with `xaml/Blocker.xaml`** from this package. Preserves all `ToneMap` & `RenderingMathTests` keys (`Blocker.Accent`, `Blocker.Warning`, `Blocker.Danger`, `Blocker.Success`, `Blocker.Bg.Raised`, `Blocker.Chart.Series1..6`, `Blocker.Button.*`, `Blocker.Text.*`), adds `#BFD0B4` logo geometry/brush, `#5F6F2E` accent fills, WPF-UI dark overrides, mixed pill/square button styles, custom square `ToggleSwitch`, lowered `Blocker.InfoBadge`, square `Blocker.InfoTip.ToolTip`, and removes `Blocker.Brush.BlueprintGrid`. |
| 5 | `src/Blocker.UI/Resources/Shared.xaml` | **Replace directly with `xaml/Shared.xaml`** from this package. Preserves all 5 converters (`BoolToVisible`, `BoolToCollapsed`, `HasTextToVisible`, `HasTextToCollapsed`, `PngToImage`) and updates `App.PillItem` / `App.Chip` to the borderless `"Badania"`-style pills with `#252C15`/`#5F6F2E` selected state. |
| 6 | `src/Blocker.UI.Controls/Rendering/ChartElements.cs` | Update fallback RGB colors in `ThemeBrush(...)` from old teal `#3DD9C1` / light gray to monochrome + `#5F6F2E` / `#BFD0B4` (`#0B0C0F` card, `#5C6270` muted, `#9AA0AE` secondary, `#EEF0F4` primary, `#5F6F2E` accent). |
| 7 | `src/Blocker.UI.Controls/Rendering/UiVisualBuilder.cs` | Remove `using Blocker.UI.Controls.Metaballs;` and both `Metaball.SetIsBody(border, true);` calls (lines 182 and 270). Optionally refine progress bar height from `6` to `4` with `CornerRadius(2)` and use `Blocker.Accent.Bar` / `Blocker.Logo.Brush` on icons where appropriate. Keep `UiNode` validation/rendering contracts intact. |
| 8 | `src/Blocker.UI/AppHost.cs` | Remove `services.AddSingleton<IUiPreferences, UiPreferences>();` from `AddWindowMode(...)` (line 61). |
| 9 | `src/Blocker.UI/ViewModels/ShellViewModel.cs` | Remove `using Blocker.UI.Controls.Metaballs;`, remove `_effects` / `Effects` observable property, remove `IUiPreferences` constructor parameter and event wiring. Keep `IsDisconnected`, `DisconnectedMessage`, `PluginPages`, `RefreshPagesAsync`, `SendPageActionAsync`, and snackbar notifications intact. |
| 10 | `src/Blocker.UI/ViewModels/SettingsViewModel.cs` | Remove `using Blocker.UI.Controls.Metaballs;`, remove `IUiPreferences`, `Effects`, `EffectsOptions`, `EffectsDescription`, and `OnEffectsChanged`. Keep `Sections`, `HasSections`, `VersionText`, `RefreshSectionsAsync`, and `SendSectionActionAsync` intact. *(Note: Leave `ServiceSettingKeys.EffectsQuality` inside `src/Blocker.Core/ServiceHost/ServiceStores.cs` untouched so `Blocker.Core.Tests` / `ServiceStoreTests.cs` remain passing unless you intentionally remove that IPC key and update its core tests.)* |
| 11 | `src/Blocker.UI/Agent/TrayService.cs` | Update `RenderIcon()` (lines 178–195) to draw `Blocker.Logo.Geometry` (or fallback 4-wedge `Geometry` matching `icons/windowsdetox-logo.xaml`) in `#BFD0B4` (`Color.FromRgb(0xBF, 0xD0, 0xB4)`) instead of the teal ring. Keep tooltip, popup menu, and Win32 lifetime logic unchanged. |
| 12 | `src/Blocker.UI/Views/MainWindow.xaml` | Remove `mb:MetaballHost`. Implement unified `#07080A` full-height sidebar with `#BFD0B4` `Blocker.Logo.Geometry` + `"Windows Detox"` at top, clean nav item styling (`#14171E` bg + `#222632` border, `#BFD0B4` active icon, no clipped left bar), **no horizontal titlebar border above the logo**, and `#0B0C0F` main workspace with faint `#BFD0B4` watermark (`Opacity="0.025"`). Keep `RootNavigation`, `RootContentDialog`, `RootSnackbar`, and `IsDisconnected` banner wired so `MainWindow.xaml.cs` and `NavigationBinder.cs` work with zero breakage (dynamic `PluginPages` must still appear in `RootNavigation.MenuItems`). Set `Icon="pack://application:,,,/Assets/app.ico"`. See `xaml/ViewTemplates.xaml`. |
| 13 | `src/Blocker.UI/Views/DashboardPage.xaml` | Remove `mb:Metaball.IsBody` and all `Blocker.CardBorder` wrappers. Apply the box-free centered focal stage from `xaml/ViewTemplates.xaml`: `Session` header with lowered `Blocker.InfoBadge`, `74px` Light `Blocker.Text.Clock` (open unslashed `0`), `#5F6F2E` primary Start button + `"Badania"` `SecondaryPill` button to Allowlist/Setup when `IsIdle`; `ClockText` + `4px` `#7C913D` progress + `IsPassActive` pass box + `Emergency exit` (`Blocker.Button.Warning`) + `PassesLeftText` with lowered `?` badge + `GhostPill` `Break-glass code` when `IsSessionRunning`; preserve `Widgets` `ItemsControl` below. **Adapt:** If quick duration presets are desired on Dashboard Idle, wire them cleanly in `DashboardViewModel` (or route `StartSessionCommand` / `AllowlistCommand` to real `INavigator` commands). |
| 14 | `src/Blocker.UI/Views/SetupPage.xaml` | Remove `mb:Metaball.IsBody` and all `CardBorder` wrappers. Use hairline `Blocker.SectionDivider` sections + vertical hairline column divider (`1.4*` / `1*`) matching `xaml/ViewTemplates.xaml`. Preserve real bindings: `DurationOptions` (`App.PillList`), `CustomHours`/`CustomMinutes`, `DurationError`, `PassesPerDay`, `PassMinutes`, `CarryOverUnusedPasses`, `PassError`, `DurationText`, `EndsAtText`, `AppsText`, `DomainsText`, `PassesText`, `StartSessionCommand`, `StartBlockedReason`, `StartErrors`. Add lowered `Blocker.InfoBadge` beside `EMERGENCY PASSES`. |
| 15 | `src/Blocker.UI/Views/AllowlistPage.xaml` | Remove `mb:Metaball.IsBody` and both `CardBorder` boxes. Flatten into symmetric hairline-divided `Apps` (`?` badge, `ScanCommand`, `SearchText`, `SelectAllFilteredCommand`, `FilteredApps` with `PngToImage` & `Badges`) and `Domains` (`NewDomain` in mono, `#5F6F2E` `AddDomainCommand`, `DomainHint`, `Domains`, `RemoveDomainCommand`), plus `#5F6F2E` `SaveCommand`. Preserve `IsLocked` banner. |
| 16 | `src/Blocker.UI/Views/PluginsPage.xaml` | Remove `mb:Metaball.IsBody` and `CardBorder` wrappers. Header with `Plugins` + lowered `Blocker.InfoBadge` (`?`), `GhostPill`/`SecondaryPill` `DownloadTemplateCommand`, and `#5F6F2E` `InstallCommand`. Render `Plugins` list with `Blocker.SectionDivider` hairlines while preserving `IsLocked`, `IsBusy`, `HasNoPlugins`, `Capabilities` (`IsPrivacySensitive` warning indicator), `ToggleEnabledCommand`, and `RemoveCommand`. |
| 17 | `src/Blocker.UI/Views/SettingsPage.xaml` | Remove `mb:Metaball.IsBody`, all `CardBorder` boxes, and the entire `"Effects quality"` section. Keep clean `VersionText` row and `Sections` (`ItemsControl` + `rd:UiNodeView`) separated by `Blocker.SectionDivider`. |
| 18 | `src/Blocker.UI/Views/Dialogs/BreakGlassCodeContent.xaml` | Replace corner radius `8` with `4`, set raised container `#07080A` / `#323746`, tighten copy (zero filler prose), keep `Code` mono textbox & `CopyCommand`. |
| 19 | `src/Blocker.UI/Views/Dialogs/BreakGlassPromptContent.xaml` | Match minimalist dark styling & terse prompt text; keep `Code` binding (`UpdateSourceTrigger=PropertyChanged`). |
| 20 | `src/Blocker.UI/Views/Dialogs/InstallReportContent.xaml` | Update stage status glyphs to match monochrome + `#BFD0B4` (`Blocker.Success`) for `Passed` / `Blocker.Danger` for `Failed` / `#5C6270` for `NotRun`, keep all test/error `ItemsControl` bindings. |
| 21 | `src/Blocker.UI/Agent/ToastWindow.xaml` | Remove `mb:MetaballHost` and `mb:Metaball.IsBody`. Keep dark `#13151B` border (`CornerRadius="4"`, `BorderBrush="#323746"` / `Blocker.Warning` when warning) and monospace countdown. |
| 22 | `src/Blocker.Stub/BlockWindow.xaml` | Remove `mb:MetaballHost` and outer margin `CardBorder` box. Apply `#0B0C0F` window with `1px` `#323746` border, `#BFD0B4` logo watermark (`Opacity="0.025"`), `Icon="pack://application:,,,/Assets/app.ico"`, app icon, `ClockText`, `Contributions` (`UiNodeView`), inline `IsConfirming` controls, `UseEmergencyExitCommand`, and `SecondaryPill` `CloseCommand`. |

*(Optional Doc Update: Update `templates/PluginStarter/STYLE_GUIDE.md` if it references the old cyan `#3DD9C1` or metaballs, and check `tests/Blocker.UI.Tests/Fakes.cs` if any test instantiates `ShellViewModel` or `SettingsViewModel` directly with `IUiPreferences` — remove that fake dependency if present.)*

---

## 5. Verification Checklist After Applying Changes

1. Run a repo-wide search confirming **zero** remaining references to `Metaball`, `BlueprintGrid`, or `IUiPreferences` across `src/` and `tests/Blocker.UI.*`.
2. Verify `src/Blocker.UI/Assets/app.ico` and `src/Blocker.Stub/Assets/app.ico` are populated from `icons/windowsdetox-icon.ico` and linked via `<ApplicationIcon>`.
3. Confirm `TrayService.cs` renders the `#BFD0B4` 4-wedge icon and `MainWindow` renders `#BFD0B4` brand logo + `0.025` watermark.
4. Confirm all `Blocker.*` resource keys checked by `tests/Blocker.UI.Controls.Tests/RenderingMathTests.cs` resolve cleanly in `Blocker.xaml`.
5. On Windows / .NET 10 SDK:
   ```powershell
   dotnet build WindowsDetox.slnx
   dotnet test WindowsDetox.slnx
   ```
