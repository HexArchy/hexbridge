# HexBridge design system

This document describes the interface of two desktop applications that make up one
product: the macOS client (SwiftUI, menu bar) and the Windows 10 22H2 receiver
(Avalonia). It is written so that you can implement from it without making any new
design decisions: every color is given as a value, every animation as a duration and
a curve, every package as a version and a license.

Facts checked on: **10 September 2026**. Anything that could not be confirmed
against a primary source is marked as such.


---

## Contents

| § | Section |
|---|---|
| 0 | [Principles](#0-principles) |
| 1 | [Platform context as of September 2026](#1-platform-context-as-of-september-2026) |
| 2 | [Stack: libraries and versions](#2-stack-libraries-and-versions) |
| 3 | [Icons, assets and licenses](#3-icons-assets-and-licenses) |
| 4 | [Tokens: color, typography, grid](#4-tokens) |
| 5 | [Motion specification](#5-motion-specification) |
| 6 | [Components](#6-components) |
| 7 | [Screens by feature](#7-screens-by-feature) |
| 8 | [Showcase 1 — live DualSense visualization](#8-showcase-moment-1--live-dualsense-visualization) |
| 9 | [Showcase 2 — first run and pairing the machines](#9-showcase-moment-2--first-run-and-pairing-the-machines) |
| 10 | [Empty states, errors and hints](#10-empty-states-errors-and-hints) |
| 11 | [Accessibility and quality](#11-accessibility-and-quality) |
| 12 | [Implementation plan](#12-implementation-plan) |
| 13 | [Unconfirmed](#13-unconfirmed) |

---

## 0. Principles

**Simplicity beats richness.** HexBridge is a utility that sits alongside the stream.
Users open it when something stops working and close it once it works again. An
interface that has to be studied is a failed interface.

Five rules that settle arguments:

1. **One sentence per screen.** In any state, exactly one sentence is visible, and it
   answers the question "is it working or not". Everything else is smaller and
   further down.
2. **One primary button per screen.** If it looks like you need two, one of them
   isn't primary.
3. **An error is a next action.** A message without a button that fixes it is a
   design bug, not a copy bug.
4. **Animation answers the question "what changed".** If the user can already see
   it, there is no animation.
5. **The showcase effect lives in two places, not spread thin.** The live DualSense
   visualization (§8) and the wizard that pairs the two machines (§9). Nowhere else
   is "it looks good" an argument.

Both showcase moments were picked by function, not by taste: the first proves the
controller is being read, the second removes the product's biggest pain point, hand
editing of config files. They pay for themselves even if you look at them as purely
utilitarian features.

### What counts as failure

- The user can't tell whether the microphone is working within a two-second glance.
- The user has to open Settings to work out what to do about an error.
- Something blinks or twitches while everything is fine.
- The app burns CPU while its window is closed.

---

## 1. Platform context as of September 2026

### 1.1 macOS

**Versions.** The current stable release is **macOS 26.6.2 Tahoe (25G83)**,
17 August 2026. **The macOS 27 "Golden Gate" RC (26A428) went out to developers on
9 September 2026**; the public release is expected within the week. The current Xcode
is 26.6 (Swift 6.3), and the Xcode 27 RC with Swift 6.4 is already available.
macOS 27 drops Intel support.

**Three practical conclusions follow:**

1. **The deployment target is macOS 14.** That is the threshold above which the whole
   modern stack is available: `@Observable`, `SettingsLink`, `openSettings`,
   `symbolEffect`, `phaseAnimator`, `keyframeAnimator`, the `.smooth/.snappy/.bouncy`
   spring presets, `ShapeStyle.fill`, `NSColor.systemFill`. macOS 15 adds
   `MeshGradient`, `windowLevel`, `defaultLaunchBehavior`, `materialActiveAppearance`;
   we take those behind `if #available`, but we don't make them the target.
2. **Liquid Glass is a progressive enhancement, not the foundation of the look.** We
   ship right on a major-version changeover; you cannot build the appearance of the
   app on an API that half the users don't have.
3. **`UIDesignRequiresCompatibility` won't save us any more.** The key that let an app
   opt out of the new design is documented like this: *"The system ignores this key
   when you build for … macOS 27 or later"*. There is no escape hatch — the interface
   has to look right in the new design language on its own.

**Liquid Glass, as API (all macOS 26.0+):**

```swift
func glassEffect(_ glass: Glass = .regular, in shape: some Shape = DefaultGlassEffectShape()) -> some View
struct Glass { static var regular; static var clear; static var identity
               func tint(_: Color?) -> Glass; func interactive(_: Bool) -> Glass }
struct GlassEffectContainer<Content: View>          // init(spacing:content:)
func glassEffectID(_ id: some Hashable & Sendable, in namespace: Namespace.ID) -> some View
func glassEffectUnion(id:namespace:) -> some View
struct GlassEffectTransition                        // .identity / .matchedGeometry / .materialize
.buttonStyle(.glass) / .buttonStyle(.glassProminent)
func backgroundExtensionEffect() -> some View
```

The AppKit equivalents are `NSGlassEffectView`, `NSGlassEffectContainerView` and
`NSButton.BezelStyle.glass`, also macOS 26.0+.

**Where Liquid Glass must NOT be used — these are quotes, not opinion.**
HIG "Materials": *"Don't use Liquid Glass in the content layer… Instead, use standard
materials for elements in the content layer, such as app backgrounds"* and
*"Use Liquid Glass effects sparingly… Limit these effects to the most important
functional elements in your app"*. "Adopting Liquid Glass": *"avoid overcrowding
or layering Liquid Glass elements on top of each other"*.

The menu bar popover is already a functional layer, and the system gives it a
background. Putting `.glassEffect()` on cards inside it is exactly the "glass on
glass" and "glass in the content layer" case that is forbidden. **Inside the popover:
standard materials.** Liquid Glass is allowed in exactly one place in HexBridge:
`.buttonStyle(.glass)` or `.glassProminent` on the primary action button, behind
`if #available(macOS 26, *)`.

⚠️ Known macOS 26 bug: `.glass`/`.glassProminent` buttons outside a toolbar don't
show a hover state. Fixed in macOS 27 (release notes). So on 26 the primary button
has to stay a plain `.borderedProminent`, with glass switched on only for 27+.
Simpler still: don't switch it on at all until the next design revision.

**HIG on menu bar extras, the substantive parts:**

- *"The menu bar's height is 24 pt"* is the only number on the entire page.
  **Apple publishes no recommended icon size.** Don't hardcode the 18×18 from blog
  posts: use `MenuBarExtra(_:systemImage:)` and let the system choose.
- *"Both interface icons and symbols use black and clear colors… the system can
  apply other colors"* — the icon has to be a **template image**. Tinting it green
  or red is not an option: in macOS 26 the menu bar is fully transparent and the
  icon sits on top of arbitrary wallpaper.
- *"Display a menu — not a popover — when people click your menu bar extra.
  Unless the app functionality you want to expose is too complex for a menu"*.

  **We choose `.window`, and that is a deliberate departure.** The reasoning: the
  popover hosts a real-time audio level indicator and a mini visualization of the
  controller. A menu (`NSMenu`) cannot draw animated content, and without the level
  indicator the popover loses its main function — answering "is audio flowing or
  not" in one second. This is exactly the "too complex for a menu" case that the
  HIG allows for.
- *"An app that only shows in the menu bar will be automatically terminated if the
  user removes the extra from the menu bar"* — we have to handle this: when
  `isInserted == false`, show the Settings window with an explanation rather than
  dying silently.
- The HIG page "The menu bar" **has not been updated for Tahoe** (last revised
  9 June 2025), so there are no new rules for Liquid Glass in the menu bar.

**⚠️ macOS 27 hides icons in menu items.** Release notes: *"In macOS 27.0, menu
bar and context menus present a reduced set of menu item images… By default,
NSMenu hides all menu item symbol images"*. SwiftUI behaves the same way. Our
`Label("…", systemImage: "…")` items in context menus will vanish. The way back is
`labelStyle(.titleAndIcon)`. Check this before release.

**HIG on Settings:** *"use a noncustomizable toolbar"*, *"Update the window's title
to reflect the currently visible pane"*, *"Restore the most recently viewed pane"*,
*"Minimize the number of settings you offer"*, *"Include a settings item in the App
menu"*. The HIG never mentions a sidebar in the Settings window, only a toolbar with
panes. It gives no window dimensions; the only reference point in the `Settings`
documentation is `.frame(maxWidth: 350, minHeight: 100)` from Apple's example.

### 1.2 Windows 10 22H2

**Support context.** Windows 10 went out of support on 14 October 2025; 22H2 is the
final version. The consumer ESU program keeps security updates coming until
**13 October 2026**, roughly a month from the date of this document. That is not a
reason to drop Win10 (the customer's target machine is exactly that), but it is a
reason not to make a single decision that would break on Windows 11.

**The key limitation is confirmed against a primary source.** Microsoft Learn,
`DWMWINDOWATTRIBUTE`: **`DWMWA_SYSTEMBACKDROP_TYPE` (38) — "Minimum supported
client: Windows 11 Build 22621"**. The Mica page: *"Mica is only available in
Windows 11 and later. If your app uses Mica and is installed on Windows 10, it
will not apply the material"*. Same story for `DWMWA_WINDOW_CORNER_PREFERENCE` (33),
`DWMWA_BORDER/CAPTION/TEXT_COLOR` (34–36) and `DWMWA_USE_HOSTBACKDROPBRUSH` (17):
all of them require build 22000+.

**Bottom line: neither Mica nor system Acrylic is available on Windows 10.**

What is actually there:

| `WindowTransparencyLevel` | Win10 22H2 | Win11 22621+ |
|---|---|---|
| `None`, `Transparent` | ✅ | ✅ |
| `Blur` | ❌ not supported on Windows at all | ❌ |
| **`AcrylicBlur`** | ✅ via `Windows.UI.Composition`, threshold 10.0.15063 | ✅ |
| `Mica` | ❌ (threshold 10.0.22000) | ✅ |

Plus `ExperimentalAcrylicBorder` + `ExperimentalAcrylicMaterial`: acrylic that
Avalonia draws itself, inside the application, without a single undocumented API.

**What not to do: `SetWindowCompositionAttribute` / `ACCENT_ENABLE_ACRYLICBLURBEHIND`.**
A private `user32.dll` export, outside the SDK. It produces documented lag on window
drag and resize that cannot be fixed on the application side (it's a bug in Windows
itself; `framelesshelper#27` was closed won't-fix with the maintainer's words
*"a bug of Windows itself. Not fixable from my side"*; the same symptoms show up in
FluentWPF#43 and DevToys#1258). For MSIX it also risks failing Store certification.

**The decision for HexBridge: opaque themed surfaces.** Blur is an opportunistic
enhancement, switched on according to `ActualTransparencyLevel`, and only on the main
window, never on popup panels:

```xml
<Window TransparencyLevelHint="Mica,AcrylicBlur,None" Background="Transparent">
```

and then styling driven by `ActualTransparencyLevel`: if it comes back `None`, we
paint the solid `bg` from the tokens. For the small tray panel, blur buys nothing:
the surface is small, short-lived, and usually sits on top of an opaque taskbar.

**🔴 Avalonia will not give us a dark title bar on Win10.** `WindowImpl.SetFrameThemeVariant`
gates the call behind `Build >= 22000`, and the Avalonia guide says outright: *"On Windows 10,
the title bar does not darken"*. Two ways out: P/Invoke `DwmSetWindowAttribute(hwnd, 20, …)`
ourselves (community consensus is that attribute 20 works from Win10 2004 onwards, even
though Learn lists Win11 as the minimum — **verify on the target machine**), or
`WindowDecorations="None"` with a title bar of our own. **We take the second option:** it
also disposes of the rounded-corner question (on Win10 they're dead anyway) and frees up
room for the summary status in the header (§7.1).

⚠️ Before that, check the regression in [Avalonia#21082](https://github.com/AvaloniaUI/Avalonia/issues/21082):
`ExtendClientAreaToDecorationsHint` + `WindowDecorations="None"` broke transparency
(black background) starting with 12.0.0-rc2. The issue was closed by PR #21354, but
which release actually carries the fix does not follow from the ticket. **This is the
first thing to verify on 12.1.2.**

**The notification area icon.** Microsoft Learn, "Notifications and the Notification
Area": put **both 16×16 and 32×32** into a single `.ico`, and use `LoadIconMetric`.
Recommended sizes by DPI: 96 dpi → 16, 120 → 20, 144 → 24, 192 → 32.
Avalonia already does the DPI-based selection through `SHAppBarMessage(ABM_GETTASKBARPOS)` →
`GetDpiForMonitor`, and re-selects on `WM_DISPLAYCHANGE` and `TaskbarCreated`.
Our job is to ship a multi-size `.ico`.

## 2. Stack: libraries and versions

Everything below was checked on nuget.org / GitHub / developer.apple.com on
**10 September 2026**. Where the check failed, it says so plainly.

### 2.0 The rule that drives every choice on Avalonia

**Avalonia major releases are not binary compatible.** A library built against
Avalonia 11 throws `TypeLoadException` on Avalonia 12 — confirmed by a maintainer
in [discussion #21091](https://github.com/AvaloniaUI/Avalonia/discussions/21091).
Meanwhile NuGet quietly resolves `Avalonia >= 11.3.x` to 12.1.2 and says nothing.

So the selection criterion is **not "the package is alive" but "it declares a
dependency on `Avalonia >= 12.x`"**. That rules out most of the popular packages.

### 2.1 Windows: Avalonia

**Core, confirmed.**

```xml
<PackageReference Include="Avalonia" Version="12.1.2" />          <!-- MIT, 2026-09-02 -->
<PackageReference Include="Avalonia.Desktop" Version="12.1.2" />
```

Windows 10 22H2 (build 19045) x64 has **Tier 2** status in the
[official platform table](https://docs.avaloniaui.net/docs/supported-platforms) —
supported. The .NET minimum for desktop is 8.0; the project is already on `net10.0`,
which widens the choice of packages further.

**Differences between 12 and 11 that will affect our markup:**
- The Direct2D backend is gone; Skia only.
- Window chrome was rewritten: `TitleBar`/`CaptionButtons`/`ChromeOverlayLayer` were
  removed in favour of `WindowDrawnDecorations`; `SystemDecorations` became
  **`WindowDecorations`**; `ExtendClientAreaChromeHints` is gone;
  `Window.WindowDecorationsTheme` and `Window.IsExtendedIntoWindowDecorations` are new.
- Compiled bindings are on by default; `IBinding` → `BindingBase`.
- `IDataObject` → `IAsyncDataTransfer` (the clipboard).
- **`CubicBezierEasing` was removed** → `SplineEasing`.
- Style animations pause by default on controls that aren't visible (see 2.1.4).

#### 2.1.1 Theme and controls — Semi.Avalonia + Ursa

```xml
<PackageReference Include="Semi.Avalonia" Version="12.1.0.1" />        <!-- MIT, 2026-07-31 -->
<PackageReference Include="Irihi.Ursa" Version="2.2.0" />              <!-- MIT, 2026-07-31 -->
<PackageReference Include="Irihi.Ursa.Themes.Semi" Version="2.2.0" />  <!-- MIT -->
```

**Semi.Avalonia 12.1.0.1** is what the project already has, and it is the right
choice. It requires `Avalonia >= 12.1.0`, TFMs `net8.0`/`net10.0`, MIT, by irihiTech.
⚠️ The GitHub README is out of date (it shows compatibility with 11.3.7) — trust NuGet.

Additional packages from the same author, version 12.1.0.1, MIT: `Semi.Avalonia.DataGrid`,
`Semi.Avalonia.ColorPicker`; `Semi.Avalonia.TreeDataGrid` is at 12.0.0.
⚠️ `Semi.Avalonia.Dock`, `.AvaloniaEdit` and `.ProDataGrid` are **free but not
open source** (README: *"delivered via nuget for free, but not open source"*).
We don't need them. `Semi.Avalonia.Tabalonia` is Avalonia 11 only — **do not take it**.

**Ursa (`Irihi.Ursa` 2.2.0)** is a companion control library from the same authors,
fully MIT. **We need it because Semi is a theme, not a set of controls.**
What we take from it:
- **`NavMenu`** — side navigation with hierarchy, collapse into a rail with tooltips,
  header/footer, and keyboard navigation. Exactly our §7.1.
- **`Loading`, `LoadingContainer`, `LoadingIcon`, `Skeleton`** — Semi has none of these.
- `Banner` — an inline warning (our `InlineAlert`).
- `Dialog`, `Drawer`, `MessageBox` — the pairing wizard.
- `QRCode` — worth trying instead of a control of our own, but check whether it can
  set the error correction level and a white backing.
⚠️ `Irihi.Ursa` 2.2.0 declares `Avalonia >= 12.0.2`, not 12.1.x — it has not been
rebuilt against 12.1. It resolves fine, but this is a place for a smoke test.

**Why not FluentAvalonia.** `FluentAvaloniaUI` 3.1.0 (2026-08-22, MIT) is alive and
declares `Avalonia >= 12.1.0`, so technically it fits. We pass on it for two reasons:
1. It reproduces WinUI/Fluent, and that design language is built on Mica/Acrylic
   backdrops that **physically do not exist** on Windows 10. The app would look like
   an unfinished Windows 11 app rather than a finished application.
2. Its only TFM is `net10.0` (Semi and Ursa also offer `net8.0`), which narrows our
   room to manoeuvre if we ever need to drop down.

**Why not Material.Avalonia.** `Material.Avalonia` 3.20.0 (2026-09-06, MIT,
`Avalonia >= 12.1.1`) is the freshest of the three and perfectly alive. But Material
Design is a foreign language for a tray utility on Windows.

**An alternative without Ursa**, if we want to minimize dependencies: Avalonia 12
added a built-in `DrawerPage` with `DrawerLayoutBehavior="CompactInline"`, which is
rail navigation out of the box (`IsOpen`, `DrawerLength` 320, `CompactDrawerLength` 48,
`DrawerBreakpointLength`, and the `Overlay`/`Split`/`CompactOverlay`/`CompactInline` modes).
⚠️ But in the stock Fluent theme `DrawerPage` has **zero** `Transition`/`Animation`,
so the open animation would have to be written by hand. On top of that there'd be no
`Skeleton`/`Loading`. Conclusion: Ursa pays for itself.

⚠️ **`TabView` does not exist in Avalonia 12** — contrary to the blog post. There is
`TabbedPage` (a plain `TabControl` inside). `TabView` exists only in FluentAvalonia.

#### 2.1.2 Icons on Windows — an important correction to the brief

**Lucide and Phosphor are not available for Avalonia 12.** Every candidate declares
a dependency on Avalonia 11 and, by the rule in 2.0, will not load:

| Package | Version | Declares | Verdict |
|---|---|---|---|
| `Lucide.Avalonia` | 0.2.21 (2026-09-06) | `Avalonia >= 11.3.17` | ❌ |
| `LucideAvalonia` | 1.6.2 (2026-03-22) | `Avalonia >= 11.1.0-beta1` | ❌ |
| `IconPacks.Avalonia.Lucide` | 2.0.0 | `Avalonia >= 11.0.13` | ❌ |
| `IconPacks.Avalonia.PhosphorIcons` | 2.0.0 | Avalonia 11 | ❌ |

⚠️ The `MarwanFr/LucideAvaloniaUI` README claims compatibility with Avalonia 12,
but no version above 1.6.2 **has been published** to NuGet — the main branch has run
ahead of the release. Don't rely on it.

**`Projektanker.Icons.Avalonia` is out as well** (9.6.2, 2025-05-07, Avalonia 11.2.8):
the project is abandoned and Projektanker GmbH has been wound up. The successor is the
same code under a different company, **the XAML namespace is preserved**, and migration
amounts to changing the C# namespace:

```xml
<PackageReference Include="Optris.Icons.Avalonia" Version="12.0.7" />               <!-- MIT -->
<PackageReference Include="Optris.Icons.Avalonia.FontAwesome7" Version="12.0.7" />
<PackageReference Include="Optris.Icons.Avalonia.MaterialDesign" Version="12.0.7" />
```

**The choice for HexBridge is Fluent System Icons:**

```xml
<PackageReference Include="FluentIcons.Avalonia" Version="2.1.339.1" />  <!-- MIT, 2026-09-01 -->
```

`Avalonia >= 12.0.0`, TFM `net10.0` (the project is already on net10.0, so it fits),
MIT. It gives us `<FluentIcon>`/`<SymbolIcon>` and `<FluentIconSource>`. The icon set
itself, `microsoft/fluentui-system-icons`, is MIT; Microsoft publishes no official
NuGet package, so this is a community wrapper.

The reason for the choice: the set is native to Windows in its drawing style, it
covers everything we need (microphone, microphone off, gamepad via `ic_fluent_games_*`,
network, shield, key, link, QR), and it is the only one of the "modern" sets with a
working package for Avalonia 12.

⚠️ **Fluent System Icons has no PlayStation symbols** (△○✕□), only Xbox-style ones.
DualSense buttons are drawn with primitives (§8.2) or taken from Kenney (§3.3).

**If we need an icon Fluent doesn't have:** Lucide is under ISC and Phosphor under
MIT, and both licenses allow us to take the SVG and embed the path data in a
`StreamGeometry` in a `ResourceDictionary`. No package is needed for that, and no
dependency on Avalonia 11 appears.

#### 2.1.3 Lottie — we're not taking it, and here's why

**There is no `Avalonia.Lottie` package on nuget.org** (404). The
`AvaloniaUI/Avalonia.Lottie` repository was **archived on 9 June 2023**. Live options
do exist:

```xml
<PackageReference Include="Avalonia.Labs.Lottie" Version="12.0.2" />  <!-- MIT, owner avaloniaui -->
<!-- or -->
<PackageReference Include="Lottie" Version="12.0.0" />                <!-- MIT, W. Šoltés, richer API -->
```

Both work on Avalonia 12 (`Avalonia >= 12.0.1` / `>= 12.0.0`) and pull in
`SkiaSharp.Skottie` from the **3.119.x** branch.
⚠️ `SkiaSharp.Skottie` 4.x is off limits: `Avalonia.Skia 12.1.2` is built against
`SkiaSharp >= 3.119.4`, and 4.x is a major release with a changed assembly version.

**Decision: Lottie is not used in HexBridge.** The reasoning:
1. Skottie's limitations are severe: no **expressions**, no effects from the Effects
   menu (Drop Shadow, Colour Overlay), no **blending modes**, no luma mattes; text
   layers come with caveats; and gradients degrade into a flat fill when they're set
   up wrong. That means an animation cannot simply be "handed over by the designer" —
   it has to be accepted against a preview in the Skottie player specifically.
2. Everything we need to animate (spinner, skeleton, success checkmark, waiting
   pulse) can be done with the built-in `Transitions`/`Animation` in a few dozen lines.
3. It is an extra dependency and several more MB for the sake of decorative
   illustrations that contradict the principle in §0.

Written down here so the question doesn't come up again.

#### 2.1.4 Animation — what Avalonia 12 actually has

**Property transitions** (`Avalonia.Animation`, in the `Avalonia.Base` assembly);
the full list, confirmed against the sources at tag 12.1.2:
`BoolTransition`, `BoxShadowsTransition`, `BrushTransition`, `ColorTransition`,
`CornerRadiusTransition`, `DoubleTransition`, `EffectTransition`, `FloatTransition`,
`IntegerTransition`, `PointTransition`, `RelativePointTransition`, `SizeTransition`,
`ThicknessTransition`, `TransformOperationsTransition`, `VectorTransition`.
Each of them has `Property`, `Duration`, `Delay`, `Easing`.

⚠️ `TransformTransition` and `RectTransition` **do not exist**. `Rotate3DTransition`
sits in the Transitions folder, but it is a page transition (it derives from `PageSlide`).
No new transition classes appeared in 12.

**Keyframe animations** — `Animation` with `Duration`, `IterationCount`,
`PlaybackDirection` (`Normal`/`Reverse`/`Alternate`/`AlternateReverse`),
`FillMode` (`None`/`Forward`/`Backward`/`Both`), `Easing`, `Delay`,
`DelayBetweenIterations`, `SpeedRatio`, `RunAsync(Animatable, CancellationToken)`.

**🆕 `Animation.PlaybackBehavior` — new in 12** ([PR #20820](https://github.com/AvaloniaUI/Avalonia/pull/20820)):

| Value | Behavior |
|---|---|
| `Auto` (default) | manual animations and ones targeting `IsVisible` always play; **style animations pause when the control is not `IsEffectivelyVisible`** |
| `Always` | as in 11 |
| `OnlyIfVisible` | pauses when not visible |

This works in our favour: the looping waiting pulse stops on its own when its section
isn't selected. ⚠️ The gate is `IsEffectivelyVisible` — any hidden ancestor pauses
the animation; `Opacity="0"` does not.

**Easings — 33 of them**: `LinearEasing` plus In/Out/InOut triplets for Sine, Quadratic,
Cubic, Quartic, Quintic, Exponential, Circular, Back, Elastic and Bounce, plus
**`SplineEasing`** and **`SpringEasing`** (`Mass`, `Stiffness`, …).

**Important: Avalonia does have a spring.** `SpringEasing` is a built-in class; there
is no need to emulate one with keyframes.

**PageTransitions**: `CrossFade` (`Duration`, `FadeInEasing`, `FadeOutEasing`),
`PageSlide` (`Orientation` via `SlideAxis`, `SlideInEasing`, `SlideOutEasing`, `FillMode`),
`CompositePageTransition`, `Rotate3DTransition`. In the Fluent theme, only
`NavigationPage` has a default (`PageSlide` 0:0:0.3). `TabbedPage`, `CarouselPage` and
`DrawerPage` have none.

**Composition API** (`Avalonia.Rendering.Composition[.Animations]`) is **stable**:
there is not a single `[Unstable]` attribute under `src/Avalonia.Base/Rendering/Composition`.
`ElementComposition.GetElementVisual/SetElementChildVisual`, `CompositionVisual`,
`ImplicitAnimationCollection`, `ExpressionAnimation`, `Compositor.CreateAnimationGroup()`,
`CreateCustomVisual()`. Animatable properties of a visual: `Opacity`, `Offset`,
**`Translation` (new in 12)**, `Size`, `Scale`, `RotationAngle`, `Orientation`, `CenterPoint`.
⚠️ The class is called `ElementComposition`, even though the file is still `ElementCompositionPreview.cs`.
⚠️ `Offset`/`Scale` are `Avalonia.Vector3D`, not `System.Numerics.Vector3`.

**Off-the-shelf loaders and skeletons:**

| What | Where |
|---|---|
| `ProgressBar.IsIndeterminate` | Avalonia core ✅ |
| `ProgressRing` as a control | **not in core** |
| `ProgressRing` as a ControlTheme | Semi: `<ProgressBar Theme="{DynamicResource ProgressRing}"/>` ✅ |
| `Loading`, `LoadingContainer`, `LoadingIcon`, **`Skeleton`** | **Ursa** ✅ |
| Skeleton in Semi | none |

**🔴 There is no reduce-motion API in Avalonia.** Checked exhaustively:
`IPlatformSettings` contains only tap/doubletap/hold/hotkey/`GetColorValues`;
`PlatformColorValues` has `ThemeVariant`, `ContrastPreference`, `AccentColor1..3`.
Searching for `ReducedMotion`, `PrefersReducedMotion`, `AnimationsEnabled` and
`SPI_GETCLIENTAREAANIMATION` returns zero hits. The open
[issue #19405](https://github.com/AvaloniaUI/Avalonia/issues/19405), filed 5 August 2025,
has seen no movement.

**So we do it ourselves** — P/Invoke, and listen for `WM_SETTINGCHANGE`:

```csharp
const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
[DllImport("user32.dll", SetLastError = true)]
static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);
```

Gate it centrally: a single `IReducedMotionProvider` that swaps the `Duration`
resources for `0:0:0` or toggles a style class on the window root.
⚠️ `Animation.PlaybackBehavior` **doesn't help** here — it's about visibility, not accessibility.

#### 2.1.5 The tray — what it can and can't do

`Avalonia.Controls.TrayIcon` is declared through the `TrayIcon.Icons` attached property
**on `Application`** (otherwise you get an `InvalidOperationException`).

What works: `Icon` (changeable at runtime ✅), `ToolTipText` ✅, `IsVisible` ✅,
`Menu` (`NativeMenu`) ✅, `Command`/`CommandParameter` (left click), and the `Clicked` event.
DPI-based icon size selection is implemented.

**🔑 A finding that saves work: on Windows the tray menu is not a native Win32 menu.**
`TrayIconImpl.OnRightClicked()` creates a real borderless Avalonia window
(`WindowDecorations.None`, `Topmost`, closed on `Deactivated`) with a
`TrayIconMenuFlyoutPresenter : MenuFlyoutPresenter` inside it. **That means our ordinary
`ControlTheme`/`Style` entries for `MenuFlyoutPresenter`, `MenuItem` and `Separator`
apply to the tray menu.** No third-party library is needed for a custom look.

`NativeMenuItem` supports `Header`, `Icon`, `ToolTip`, `Gesture` (label only),
**`IsChecked` (two-way)**, **`ToggleType`**, `Command`, a nested `Menu`, plus
`NativeMenuItemSeparator`. So the checkmark toggles for features in the tray menu (§7.1)
can be built out of stock parts.

**🔴 What's missing:**
- **Balloon/toast from the tray.** `UpdateIcon` never sets `NIF_INFO`;
  request [#6734](https://github.com/AvaloniaUI/Avalonia/issues/6734) was closed as
  **not planned**. → The one-time "minimized to the tray" hint from §7.1 is done with
  **`WindowNotificationManager`** (an in-app toast) at the moment of minimizing, while
  the window is still visible. Real system toasts (§10.5) would go through
  `DesktopNotifications.Avalonia`, but that has a side effect: it registers an AUMID and
  a Start menu shortcut. **Decision: no system notifications in the first version**;
  we limit ourselves to changing the tray icon and the tooltip.
- **Double click** — `WM_LBUTTONDBLCLK` is not handled ([#10269](https://github.com/AvaloniaUI/Avalonia/issues/10269)).
  → In §7.1, "a second left click collapses it" is implemented with single clicks.
- Middle click, hover, a separate right-click event: none of them exist.

#### 2.1.6 Custom drawing

Three levels:

1. **`Control.Render(DrawingContext)`** — the UI thread, invalidated by `InvalidateVisual()`
   or `AffectsRender<T>()`. Suits `LevelMeter` and `Sparkline` (already done this way).
2. **`DrawingContext.Custom(ICustomDrawOperation)`** — the render thread, with direct
   `SKCanvas` access through `ISkiaSharpApiLeaseFeature`. ⚠️ It bypasses scene graph caching.
3. **`CompositionCustomVisualHandler` + `compositor.CreateCustomVisual(handler)`** —
   per-frame `OnRender`/`OnMessage` callbacks **on the render thread**, with the next
   frame requested via `RequestNextFrameRendering()`. The Avalonia documentation calls
   this the intended approach for *"real-time visualizations, game loops, video rendering"*.

**For the DualSense visualization (§8), option 3.** It is the only one of the three that
gives us our own frame loop without forcing the UI thread to rebuild the scene graph
every 16 ms. A ready-made wrapper, if we'd rather not write our own: `CompositionAnimatedControl`
from [wieslawsoltes/Lottie](https://github.com/wieslawsoltes/Lottie) (MIT, `Avalonia >= 12.0.0`).

**SVG.** ⚠️ `Avalonia.Svg` and `Avalonia.Svg.Skia` 11.3.0 are **deprecated**. The successors:

```xml
<PackageReference Include="Svg.Controls.Avalonia" Version="12.0.0.17" />  <!-- MIT, WITHOUT SkiaSharp -->
```

We take the version without Skia specifically: `Svg.Controls.Skia.Avalonia` pulls in
`SkiaSharp.NativeAssets.Linux >= 4.148.0`, while `Avalonia.Skia 12.1.2` is built against
`SkiaSharp 3.119.4` — NuGet would pick 4.x, and that is a major-version divergence.
**The compatibility of that pair has not been verified.** For our purposes SVG rendering
is not required at all: the icons come as a package, and the controller is drawn with
primitives.

#### 2.1.7 The final csproj for Windows

```xml
<ItemGroup>
  <PackageReference Include="Avalonia" Version="12.1.2" />
  <PackageReference Include="Avalonia.Desktop" Version="12.1.2" />
  <PackageReference Include="Avalonia.Fonts.Inter" Version="12.1.2" />   <!-- fallback, see §4.2 -->
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
  <PackageReference Include="Semi.Avalonia" Version="12.1.0.1" />
  <PackageReference Include="Irihi.Ursa" Version="2.2.0" />
  <PackageReference Include="Irihi.Ursa.Themes.Semi" Version="2.2.0" />
  <PackageReference Include="FluentIcons.Avalonia" Version="2.1.339.1" />
  <PackageReference Include="Net.Codecrete.QrCodeGenerator" Version="3.2.1" />
</ItemGroup>
```

All MIT. Four packages get added to what is already there.

**Overriding Semi's tokens** goes through `ThemeDictionaries`, as already done in
`App.axaml`. Semi's keys are named `Button[State][Variant]Foreground` and so on; our
own tokens (§4.1) live next to them in the same dictionaries. It also works locally,
in the `Resources` of any container.

### 2.2 macOS: SwiftUI

**The system provides nearly everything.** Below is what we take from the system, and
why third-party solutions are barely needed.

| Task | Solution | Why not a library |
|---|---|---|
| Observable model | `@Observable` (macOS 14+) | Already in use. Updates a view only when a specific property is read, unlike `ObservableObject` |
| Menu bar | `MenuBarExtra` (macOS 13+) + `LSUIElement` | The stock scene |
| Settings | `Settings` scene (macOS 11+) + `SettingsLink` (macOS 14+) | — |
| Launch at login | **`SMAppService.mainApp`** (macOS 13+) | See below |
| Icon animation | `symbolEffect` (macOS 14+) | — |
| Live visualization | `Canvas` + `TimelineView` (macOS 12+) | See §8.4 |
| QR | `CIFilter.qrCodeGenerator()` (CoreImage) | — |
| QR scanning | `AVCaptureMetadataOutput` (**macOS 13+**) | — |

**LaunchAtLogin isn't needed.** `sindresorhus/LaunchAtLogin-Modern` is at v1.1.0 from
**21 December 2023**, with its last commit in January 2024; `LaunchAtLogin` was renamed
to `-Legacy` and **archived**. All they ever did was wrap `SMAppService`:

```swift
try SMAppService.mainApp.register()            // macOS 13+
try SMAppService.mainApp.unregister()
SMAppService.mainApp.status                    // .notRegistered / .enabled / .requiresApproval / .notFound
SMAppService.openSystemSettingsLoginItems()
```

Two tricks are worth borrowing from the library: detecting whether we were launched
at login (`NSAppleEventManager` + `keyAELaunchedAsLogInItem`), and handling the
"already registered → unregister → register" case.

**Three libraries genuinely earn their place.**

```
sparkle-project/Sparkle            2.9.6   (17.08.2026, push 09.09.2026)
sindresorhus/KeyboardShortcuts     3.0.1   (17.06.2026)
orchetect/MenuBarExtraAccess       1.3.1   (05.08.2026)
```

**Sparkle 2.9.6** handles updates outside the App Store, and there is no alternative.
Very much a live project: three security releases during 2026 (a symlink vulnerability,
privilege escalation).
⚠️ **The license is not "just MIT"**: it's the MIT text plus an EXTERNAL LICENSES
section covering the vendored `bsdiff` (BSD-2-clause) and `sais-lite`. Ship the LICENSE
in full.
🎯 Release 2.9.4 (3 July 2026) contains an *"activation fix for backgrounded /
dockless applications"* — that is a bug affecting LSUIElement apps precisely.
Stay on 2.9.6 (min macOS 10.13); moving to 2.10 is a deliberate decision, since that
raises the minimum to macOS 12 and drops CocoaPods support.
It requires EdDSA: `generate_keys` → `SUPublicEDKey` in Info.plist, `SUFeedURL`,
an incrementing `CFBundleVersion`, and an appcast via `generate_appcast`.

**KeyboardShortcuts 3.0.1** covers the global mute hotkey (§11.2). The native
`.keyboardShortcut()` only works while the app has focus; global hotkeys mean Carbon's
`RegisterEventHotKey`, and the library does that **without requesting Accessibility
permissions**. It also provides `KeyboardShortcuts.Recorder`, a ready-made control for
recording a shortcut in Settings.
⚠️ `swift-tools-version:6.2` means an Xcode 26+ toolchain is required.

**MenuBarExtraAccess 1.3.1 is needed, and that has been verified.** The question
"can SwiftUI open and close a MenuBarExtra programmatically" has the answer **no**:
in the macOS 27 SDK, `MenuBarExtra` has eight initializers and `isPresented` is not
among them. There is only `isInserted`, documented as *"Whether the item is inserted
in the menu bar"* — that is whether the icon is present, not whether it is open.
`MenuBarExtra` is not mentioned at all in `updates/swiftui` for either June 2025 or
June 2026.
The library provides `.menuBarExtraAccess(isPresented:) { statusItem in }` and
`.introspectMenuBarExtraWindow { window in }` without private API.
⚠️ It only works with the `.window` style — with `.menu`, SwiftUI blocks the runloop.
That suits us: we're on `.window` anyway.
⚠️ **Risk: bus factor of 1.** It depends on SwiftUI internals, and every September
brings a window of downtime (the history: 1.2.1 "not yet compatible with macOS 26 beta"
→ 1.2.2 → 1.3.1 "Support for macOS 27 beta 4"). **Write down plan B right away:** our
own `NSStatusItem` + `NSPanel`, roughly 150 lines, full control. On macOS 27 we'll need
it regardless — that release added `NSStatusItem.expandedInterfaceDelegate`/`expandedInterfaceSession`,
without which keyboard focus breaks on a custom status item.
*("SwiftUI menu bar extras do a lot of this work for you" — WWDC26 session 289;
so as long as we're on SwiftUI, this isn't our concern.)*

**What we are NOT taking:**
- `sindresorhus/Settings` (3.1.1, May 2024) — the `Settings` scene plus `SettingsLink` covers the job.
- `orchetect/SettingsAccess` — not needed with a macOS 14+ target; its own README
  admits that `openSettingsLegacy()` doesn't work in the `.menu` style.
- `lfroms/fluid-menu-bar-extra` — **archived on 20 January 2026**.
- `sindresorhus/Defaults` (9.0.9, alive) — situational. `@AppStorage` handles only
  `Bool/Int/Double/String/URL/Data/RawRepresentable` (plus `Date` from macOS 15)
  and only works inside a View, whereas our config lives in JSON and is read from services.
  We already have a `Config` of our own, and that is enough.
- There are no backport shims for Liquid Glass on macOS, and there cannot be.

**🔴 Budget time for this: opening the Settings window from the menu bar.** `SettingsLink`
and `@Environment(\.openSettings)` (both macOS 14+) solved the act of opening, but not
everything: `openSettings()` requires an existing SwiftUI render tree, so a call from
`AppDelegate` or from a global hotkey handler does nothing; and `SettingsLink` from a
`MenuBarExtra` opens the window but **does not activate the app**, so it slides behind
other applications' windows ([forums thread 731628](https://developer.apple.com/forums/thread/731628);
there is no official answer from Apple). The workaround that works: a hidden window
carrying the SwiftUI context, declared **before** the `Settings` scene; a temporary
`NSApp.setActivationPolicy(.regular)` before the call, returning to `.accessory` afterwards;
and decoupling through `NotificationCenter`. `NSApplication.activate()` (macOS 14+) is
also known to help.
⚠️ Whether this is fixed on released macOS 26.x is **unconfirmed** — test it on the bench.

**Useful scene modifiers for LSUIElement:** `defaultLaunchBehavior(_:)`
(macOS 15+, to keep a window from opening at startup), `restorationBehavior(_:)`
(15+), `windowLevel(_:)` (15+), `windowResizeAnchor(_:)` (26+, directly relevant to
a popover that changes height), `openWindow` (13+) / `dismissWindow` (14+).

**Notarization is mandatory.** Anything built after 1 June 2019 and distributed with a
Developer ID has to be notarized: a Developer ID signature, Hardened Runtime, a secure
timestamp, no `com.apple.security.get-task-allow`, `notarytool` (altool has been rejected
since 1 November 2023), then `xcrun stapler staple`. For Sparkle, sign and notarize
**including Sparkle's nested XPC services**, and separately the DMG/ZIP that the appcast
points at.

## 3. Icons, assets and licenses

### 3.1 Icons — why the two platforms use different sets

**SF Symbols cannot be used on Windows.** The Xcode and Apple SDKs Agreement,
rev EA2002 dated 6 August 2026, **§2.10 System-Provided Images**, verbatim:

> "The system-provided assets (e.g., images, symbols) owned by Apple… are licensed
> to You **solely for the purpose of developing Applications for Apple-branded
> products that run on the system for which the image was provided**. You agree
> that you shall not use or incorporate the System-Provided Images **or any
> substantially or confusingly similar images** into app icons, logos or make any
> other trademark use…"

So neither taking the symbols themselves nor redrawing "similar" ones as a
workaround is allowed.

Hence:

| Platform | Set | License |
|---|---|---|
| macOS | **SF Symbols 7** (system) | Xcode SLA §2.10 — inside the UI on Apple platforms only |
| Windows | **Fluent System Icons** via `FluentIcons.Avalonia` 2.1.339.1 | MIT |
| Filling gaps on Windows | Lucide (ISC) or Phosphor (MIT) — SVG hand-converted into `StreamGeometry` | ISC / MIT |

On SF Symbols: the stable version is **7**; SF Symbols 8 (announced at WWDC26) is
still in beta as of 10 September 2026. The macOS 26.6 system catalog holds ~9,184
names, ~7,988 of them base symbols.

⚠️ **605 symbols carry restrictions** of the form "may only be used to refer to
Apple's iPhone" (the `symbol_restrictions.strings` file). Verified: **not one of
the symbols we need is on that list**, including `personalhotspot` and
`antenna.radiowaves.left.and.right`, which are often wrongly assumed to be
restricted.

The symbols we use (all verified against the macOS 26.6 system catalog):

| Role | SF Symbol | Since macOS |
|---|---|---|
| Microphone / muted | `mic.fill`, `mic.slash.fill` | 10.15 |
| Microphone unavailable | `mic.badge.xmark` | 13.0 |
| Controller | `gamecontroller.fill` | 10.15 |
| Network | `network`, `wifi`, `wifi.exclamationmark` | 11.0 / 10.15 |
| No network | `network.slash` | 14.0 |
| Key | `key.horizontal.fill` | 13.0 |
| Security | `lock.shield.fill` | 10.15 |
| Pairing | `link.circle.fill` | 10.15 |
| QR | `qrcode`, `qrcode.viewfinder` | 10.15 |
| Level meter | `waveform`, `waveform.slash` | 10.15 |

### 3.2 One shared shape vocabulary

The icons are drawn differently on the two platforms — that is normal and correct.
But **the shape that stands for a state has to match**, because it is part of the
status language (§4.1):

| State | Shape | macOS | Windows (Fluent) |
|---|---|---|---|
| All good | circle with a checkmark | `checkmark.circle.fill` | `ic_fluent_checkmark_circle_24_filled` |
| Attention | triangle | `exclamationmark.triangle.fill` | `ic_fluent_warning_24_filled` |
| Error | octagon/circle with a cross | `xmark.octagon.fill` | `ic_fluent_dismiss_circle_24_filled` |
| Off | crossed-out circle | `circle.slash` | `ic_fluent_prohibited_24_regular` |

### 3.3 DualSense assets — what the search turned up

**A freely licensed SVG outline of the DualSense does not exist.** Verified:

- **Wikimedia Commons**: a search on `filemime:image/svg+xml` and a full listing of
  `Category:DualSense` (35 files) — **not a single SVG**. Photos only, under CC BY-SA 4.0.
  SVG diagrams do exist for the DualShock 3/4 (CC BY 3.0, Tokyoship) — a different controller.
- **Visualizer projects**: `nondebug/dualsense` — no license and no graphics
  (it is a WebHID report inspector, useful as a byte-level reference);
  gamepadviewer.com — not open source, no PS5 skin; `e7d/gamepad-viewer` — MIT code,
  but the art is "reworked from assets originally created by mrmcpowned for gamepadviewer.com",
  so the provenance is dirty; DS4Windows is GPL-3.0 and contains no DualSense art;
  DualSenseX has no license; Steam Input glyphs come with no redistribution grant,
  and the public ZIP contains no PlayStation glyphs.

**What is free and usable:**

| Asset | License | What's inside |
|---|---|---|
| [Xelu's FREE Controller Prompts](https://thoseawesomeguys.com/prompts/), vector — [haaldor/Xelu_prompts_SVG](https://github.com/haaldor/Xelu_prompts_SVG) | **CC0-1.0** (LICENSE file) | group `inkscape:label="Playstation_5"`, 136 paths: sticks, D-pad, L1/L2/R1/R2, mute, Create/Options |
| [DJLink/Xelu_Free_Controller-Key_Prompts](https://github.com/DJLink/Xelu_Free_Controller-Key_Prompts) | **CC0-1.0** | `PS5/PS5_Diagram.png`, `PS5_Diagram_Simple.png` — an outline diagram ⚠️ **with the PS wordmark in the middle, which has to be removed** |
| [Kenney Input Prompts](https://kenney.nl) 1.5 | **CC0** (`License.txt` in the archive) | 1504 SVGs, of which `PlayStation Series/Vector/` holds 136 files: `playstation5_button_create/options/mute`, `playstation5_touchpad`, `playstation_button_color_cross/circle/square/triangle`, `playstation_trigger_l1/l2/r1/r2` |
| [PromptFont](https://codeberg.org/shinmera/promptfont) | **SIL OFL 1.1** | glyphs: Square `U+21E0`, Triangle `U+21E1`, Circle `U+21E2`, Cross `U+21E3`, L1–R2 `U+21B0…21B3`, DualSense Touchpad `U+2207`, DualSense Options `U+2208`. Attribution requested |

⚠️ **Simple Icons is not a source for the PlayStation logo.** The repository is
under CC0, but `DISCLAIMER.md` says verbatim: *"Simple Icons is released under CC0 — though that
doesn't mean to imply that all icons within the project are also CC0… We ask that
our users seek the correct permissions"*. The PlayStation entry in
`simple-icons.json` has **no** `license` field.

### 3.4 The legal side and the decision

**Design patents confirmed.** Sony Interactive Entertainment holds a cluster of US
design patents with a priority date of **3 April 2020** (four days before the
DualSense announcement): `USD933750S1`, `USD933751S1`, `USD954710S1`, `USD954838S1`,
`USD958891S1`, `USD958892S1`, `USD977576S1`, `USD984536S1` ("Housing for game
controller" / "Controller for electronic device"), plus `USD990569S1`/`USD990570S1`
on the sticks themselves. Internationally: TWD216292S–216297S, CA199206S, UY4827S.
The `USD933750S1` drawing is a partial design claim: *"The broken lines… depict portions
of the housing… that form no part of the claimed design"*, meaning what is protected
is the specific contour of the housing, not "a gamepad in general".

**Trademarks confirmed.** [Copyright and Trademark Notice](https://www.playstation.com/en-us/legal/copyright-and-trademark-notice/):
*"PlayStation, PS5, … **DualSense**, DUALSHOCK, … are registered trademarks or
trademarks of Sony Interactive Entertainment Inc."* — and the same page lists the
**"PlayStation Shapes Logo"**, meaning the △ ○ ✕ □ combination itself is claimed
as a trademark.

**Decision: we draw our own schematic abstraction (§8.1–8.2).**

Reasoning:
1. A design patent is infringed when an "ordinary observer" would confuse the
   **articles** — that is about manufacture and sale, not about an illustration in
   a UI. The risk from a schematic is low. But a photorealistic render as close as
   possible to the patent drawing is the worst option available, and that is
   precisely the one we are not building.
2. △ ○ ✕ □ used to denote buttons is classic **nominative fair use**. The
   PlayStation logo in the interface or in the app icon is not.
3. There is no ready-made free vector anyway, and pulling someone else's SVG apart
   into named layers costs more than drawing it parametrically (§8.1).

**Hygiene, mandatory in the implementation:**
- In "About" — a line reading: "DualSense and PlayStation are trademarks of Sony
  Interactive Entertainment Inc. HexBridge is not affiliated with or endorsed by
  Sony Interactive Entertainment."
- Do not use "PlayStation", "PS5" or "DualSense" in the application name, in the
  executable name, in the domain, or in the icon.
- The app icon and the tray icon are a brand-neutral controller from Fluent/Lucide,
  not a DualSense silhouette.
- Do not take art from gamepadviewer.com or its derivatives, do not repackage
  Valve's glyphs, do not take the PlayStation logo from Simple Icons.

### 3.5 QR codes

**Windows — generation:**

```xml
<PackageReference Include="Net.Codecrete.QrCodeGenerator" Version="3.2.1" />
```

MIT, 2026-09-07, TFMs `net6.0`/`netstandard2.0`, **zero transitive dependencies**.
It gives exactly what Avalonia needs:

```csharp
public bool   GetModule(int x, int y)
public string ToGraphicsPath(int border = 0)          // SVG-compatible path → Geometry.Parse()
public IReadOnlyList<QrPolygon> ToOutlines()          // single outline, no hairline gaps
public byte[] ToPngBitmap(...)                        // without any imaging library at all
```

`ToGraphicsPath()` is fed into `Geometry.Parse(...)` and drawn as a single `<Path>` —
no need to write a module renderer of our own.
`ToOutlines()` exists specifically to fight antialiasing gaps — use it.

**🔴 Why not QRCoder.** `QRCoder` 1.8.0 (MIT, actively maintained, the repository
moved from `codebude/QRCoder` to `Shane32/QRCoder`) has a public
`QRCodeData.ModuleMatrix`, and the generator itself contains not one mention of
`System.Drawing`. **But** the newest asset in the package is `lib/net6.0`, and its
`.nuspec` declares `<dependency id="System.Drawing.Common" version="6.0.0" />`. A
net10.0 project resolves that very group and pulls System.Drawing into the graph.
⚠️ In versions 1.5.1 and 1.6.0 the `net6.0` group was empty — the dependency
**came back** in 1.7.0, so the widespread claim that "QRCoder is clean on net6+"
is out of date.
`QRCoder.Xaml` is WPF and does not fit Avalonia.

**⚠️ SkiaSharp bindings are unnecessary and dangerous.** `ZXing.Net.Bindings.SkiaSharp`
requires SkiaSharp 4.151.1, `SkiaSharp.QrCode` requires 4.148.0, while Avalonia 12.x
pulls 3.119.4. NuGet will lift Skia to 4.x underneath Avalonia, which was never built
against it.

**Windows — scanning: we don't do it.** Under the §9 scheme, Windows **shows** the
QR and the Mac reads it. So no camera is needed on Windows, and `FlashCap` +
`ZXing.Net` stay out of the dependency list. *(If it is ever needed: FlashCap 1.12.0
Apache-2.0, pure managed, net10.0, with an official Avalonia sample; decoder — ZXing.Net
0.16.11 Apache-2.0 via `BarcodeReaderGeneric.Decode(byte[], w, h, BitmapFormat)`,
no SkiaSharp.)*

**macOS — generation:**

```swift
import CoreImage.CIFilterBuiltins
let g = CIFilter.qrCodeGenerator()          // macOS 10.15+ (the filter itself since 10.9)
g.message = payload.data(using: .ascii)!
g.correctionLevel = "M"                     // L 7% / M 15% (default) / Q 25% / H 30%
let image = g.outputImage!
    .transformed(by: CGAffineTransform(scaleX: 8, y: 8))
    .samplingNearest()                      // macOS 10.13+ — without this the QR goes blurry
```

⚠️ **The output is one point per module**, so scaling is mandatory.
⚠️ **Encoding: Apple contradicts itself.** The legacy Filter Reference says
`NSISOLatin1StringEncoding`, the modern sample code uses `.ascii`.
**UTF-8 is not mentioned anywhere in Apple's documentation.** Our payload (§9.1) —
base64url + IP + port — is entirely ASCII, so we take `.ascii` and let only ASCII
through into the PC name, transliterating the rest.

**macOS — scanning:**

```swift
AVCaptureMetadataOutput()                            // ⚠️ macOS 13.0+ (on iOS since 6.0)
output.metadataObjectTypes = [.qr]                   // .qr available since macOS 10.15
AVCaptureMetadataOutputObjectsDelegate
  → metadataOutput(_:didOutput:from:)
  → AVMetadataMachineReadableCodeObject.stringValue
```

⚠️ `metadataObjectTypes` **throws an `NSException`** if you assign a type that is
not in `availableMetadataObjectTypes`. Always check
`availableMetadataObjectTypes.contains(.qr)` first.
⚠️ `NSCameraUsageDescription` is required in Info.plist (macOS 10.14+) —
**without it `requestAccess` throws**. Request access via
`AVCaptureDevice.requestAccess(for: .video)` and **only at the moment the user taps
"Scan code"**, not at application launch.

The fallback path (and also the way to read a QR off a screenshot):
`VNDetectBarcodesRequest` (macOS 10.13+) with `symbologies = [.qr]` →
`VNBarcodeObservation.payloadStringValue`.
⚠️ The trap: *"Setting the revision on the request resets the symbologies"* —
set the revision first, the symbologies second.
The modern Swift-native option is `DetectBarcodesRequest` (macOS 15+).

## 4. Tokens

Tokens are the single source of color, size and timing. Not one literal of the form
`#RRGGBB`, `12` or `0.25s` may remain in the code outside the tables below.

The names are identical on both platforms so that edits can be carried across by eye:
`bg`, `surface`, `surfaceAlt`, `border`, `borderStrong`, `text`, `textDim`,
`accent`, `ok`, `warn`, `bad`, `off` (plus the `Fg`/`Bg`/`Border` suffixes on states).

### 4.1 Color

#### Light theme

| Token | HEX | Purpose |
|---|---|---|
| `bg` | `#F4F6F9` | window background |
| `surface` | `#FFFFFF` | card, popup, input field |
| `surfaceAlt` | `#F8FAFC` | tile inside a card, list zebra striping |
| `border` | `#E2E6ED` | decorative card border |
| `borderStrong` | `#7E8899` | border of an interactive control, focus ring |
| `text` | `#1B2330` | body text |
| `textDim` | `#5C6675` | caption, units, secondary |
| `accent` | `#1D65C4` | primary button, link, active navigation item |
| `accentSoft` | `#E9F1FD` | backing for the active item, soft badge |
| `accentText` | `#1A5AAC` | text on `accentSoft` |
| `okFg` | `#189055` | "all good" indicator |
| `okText` | `#136B41` | text on `okBg` |
| `okBg` | `#E7F6EE` | status backing |
| `okBorder` | `#B4E0C9` | status border |
| `warnFg` | `#B87D00` | "attention" indicator |
| `warnText` | `#7A4E00` | text on `warnBg` |
| `warnBg` | `#FDF3E0` | |
| `warnBorder` | `#F0DCB0` | |
| `badFg` | `#C62F2F` | "error" indicator |
| `badText` | `#9E2B2B` | text on `badBg` |
| `badBg` | `#FCECEC` | |
| `badBorder` | `#F1C6C6` | |
| `off` | `#7E8899` | "off", neutral gray indicator |
| `offBg` | `#EEF1F5` | backing for the off state |
| `meterTrack` | `#E4E8EF` | level meter track |
| `overlayScrim` | `rgba(17,22,30,0.32)` | dimming behind a modal |

#### Dark theme

| Token | HEX | Purpose |
|---|---|---|
| `bg` | `#15181E` | |
| `surface` | `#1E222A` | |
| `surfaceAlt` | `#242933` | |
| `border` | `#2E343F` | |
| `borderStrong` | `#66738A` | |
| `text` | `#E6EAF0` | |
| `textDim` | `#9AA4B4` | |
| `accent` | `#5A9BFF` | |
| `accentSoft` | `#16233A` | |
| `accentText` | `#8DB9FF` | |
| `accentInk` | `#0F1216` | text **on** an `accent` fill (dark in the dark theme, not white) |
| `okFg` | `#4FD18E` | |
| `okText` | `#5FD39B` | |
| `okBg` | `#132A20` | |
| `okBorder` | `#28503A` | |
| `warnFg` | `#E9B65B` | |
| `warnText` | `#E9B65B` | |
| `warnBg` | `#2B2417` | |
| `warnBorder` | `#55431F` | |
| `badFg` | `#F08A8A` | |
| `badText` | `#F5A0A0` | |
| `badBg` | `#2E1C1C` | |
| `badBorder` | `#5A2F2F` | |
| `off` | `#7A8496` | |
| `offBg` | `#20252E` | |
| `meterTrack` | `#2A303A` | |
| `overlayScrim` | `rgba(0,0,0,0.48)` | |

#### Contrast check (WCAG 2.1)

Computed with the WCAG relative luminance formula. The threshold for text is
**4.5:1** (AA); for non-text interface elements (indicators, control borders,
focus) it is **3:1**.

Light theme:

| Pair | Contrast | Result |
|---|---|---|
| `text` on `surface` | 15.79 | AAA |
| `text` on `bg` | 14.59 | AAA |
| `textDim` on `surface` | 5.81 | AA |
| `textDim` on `bg` | 5.37 | AA |
| `textDim` on `surfaceAlt` | 5.56 | AA |
| `accent` on `surface` | 5.66 | AA |
| white on `accent` | 5.66 | AA |
| `okText` on `okBg` | 5.87 | AA |
| `warnText` on `warnBg` | 6.54 | AA |
| `badText` on `badBg` | 6.47 | AA |
| `accentText` on `accentSoft` | 5.95 | AA |
| `okFg` on `surface` (indicator) | 4.07 | ≥3 ✓ |
| `warnFg` on `surface` (indicator) | 3.52 | ≥3 ✓ |
| `badFg` on `surface` (indicator) | 5.46 | ≥3 ✓ |
| `off` on `surface` (indicator) | 3.58 | ≥3 ✓ |
| `borderStrong` on `surface` | 3.58 | ≥3 ✓ |
| `border` on `surface` | 1.25 | decorative, carries no text |

Dark theme:

| Pair | Contrast | Result |
|---|---|---|
| `text` on `surface` | 13.20 | AAA |
| `textDim` on `surface` | 6.33 | AA |
| `textDim` on `surfaceAlt` | 5.79 | AA |
| `accent` on `surface` | 5.74 | AA |
| `accentInk` on `accent` | 6.77 | AA |
| `okText` on `okBg` | 8.17 | AAA |
| `warnText` on `warnBg` | 8.27 | AAA |
| `badText` on `badBg` | 8.02 | AAA |
| `accentText` on `accentSoft` | 7.87 | AAA |
| `okFg` on `surface` | 8.23 | ≥3 ✓ |
| `warnFg` on `surface` | 8.58 | ≥3 ✓ |
| `badFg` on `surface` | 6.61 | ≥3 ✓ |
| `off` on `surface` | 4.23 | ≥3 ✓ |
| `borderStrong` on `surface` | 3.33 | ≥3 ✓ |
| `borderStrong` on `surfaceAlt` | 3.04 | ≥3 ✓ |

The checking script is not kept in the repository; whenever a color changes, the
contrast is recomputed with the WCAG relative luminance formula and the table is
updated. The rule: **color is never the only carrier of meaning** — next to a
colored indicator there is always status text and/or an icon of a distinct shape
(dot / triangle / cross).

#### State semantics — the same across both features

| State | Tokens | Icon (macOS / Windows) | Meaning |
|---|---|---|---|
| All good | `okFg` / `okBg` / `okBorder` | `checkmark.circle.fill` / `checkmark_circle` | the channel is alive, data is flowing |
| Attention | `warnFg` / `warnBg` / `warnBorder` | `exclamationmark.triangle.fill` / `warning` | working, but not the way it should (host silent, muted, packet loss) |
| Error | `badFg` / `badBg` / `badBorder` | `xmark.octagon.fill` / `dismiss_circle` | not working, the user has to act |
| Off | `off` / `offBg` / `border` | `circle.slash` / `prohibited` | the feature is deliberately off, this is not a problem |
| Waiting | `textDim` + animation | `ellipsis.circle` / spinner | transitional, up to 10 s |

**Mute is `warn`, not `bad`.** The user pressed mute themselves; nothing is broken,
but it is a state worth remembering.

### 4.2 Typography

The system font on each platform, no web fonts.

- **macOS**: SF Pro (`.system`). Telemetry digits use `.monospacedDigit()` so the
  line does not jitter. Logs and keys use `.system(.body, design: .monospaced)` (SF Mono).
- **Windows 10**: `Segoe UI` — the native face for Win10 (Segoe UI Variable exists
  only from Win11). The project already pulls `Avalonia.Fonts.Inter`; **it should
  stay only as a fallback** for non-Windows debugging, while on Windows we use
  Segoe UI. Monospace is `Consolas`.

The scale. One scale for both platforms; on macOS it maps onto the standard roles,
on Windows onto px.

| Role | macOS | Windows (px / weight / leading) | Where |
|---|---|---|---|
| `display` | `.system(size: 28, weight: .semibold)` | 26 / SemiBold / 32 | status card heading ("Audio is flowing") |
| `title` | `.title3` (≈15 pt semibold) | 18 / SemiBold / 24 | screen title, feature name in the list |
| `heading` | `.headline` | 15 / SemiBold / 20 | card heading |
| `body` | `.body` (13 pt) | 14 / Regular / 20 | body text, fields |
| `label` | `.callout` (12 pt) | 13 / Regular / 18 | form labels |
| `caption` | `.caption` (10 pt) | 12 / Regular / 16 | hints, units |
| `section` | `.caption.weight(.semibold)` + `.textCase(.uppercase)` | 12 / SemiBold / 16, `letter-spacing: .04em`, CAPS | "CONNECTION", "CODEC" |
| `metric` | `.system(size: 22, weight: .semibold).monospacedDigit()` | 22 / SemiBold / 28, tabular | the number on a telemetry tile |
| `mono` | `.system(.caption, design: .monospaced)` | Consolas 12 / 18 | key, log, address |

Rules:
1. No more than **four** sizes on screen at once.
2. Numbers that update more often than 1 Hz always use monospaced digits.
3. Headings do not wrap: if it does not fit, shorten the text rather than reducing
   the type size.
4. Maximum paragraph width is **62 characters** (≈ 520 px at `body`).

### 4.3 Grid, spacing, radii

The base is **4 px**. The only permitted values come from this series:

`space`: 2, 4, 6, 8, 12, 16, 20, 24, 32, 40, 48

| Token | Value | Where |
|---|---|---|
| `space.xs` | 4 | between an icon and its label |
| `space.sm` | 8 | between controls in a row |
| `space.md` | 12 | between rows inside a card |
| `space.lg` | 16 | between cards |
| `space.xl` | 24 | page inner padding |
| `space.2xl` | 32 | separation between large blocks |

Radii:

| Token | Value | Where |
|---|---|---|
| `radius.xs` | 4 | badge, chip |
| `radius.sm` | 6 | button, input field |
| `radius.md` | 8 | tile |
| `radius.lg` | 12 | card |
| `radius.xl` | 16 | status card, modal |
| `radius.pill` | 999 | level meter, toggle |

On macOS, instead of plain square radii we use **`RoundedRectangle(cornerRadius:style: .continuous)`**
— that is the shape the rest of the system interface is built around.

Window and panel sizes:

| Surface | Size |
|---|---|
| macOS popover (menu bar) | width **340**, height driven by content, maximum 520 |
| macOS Settings window | 620 × 480, width not resizable |
| Windows main window | 1000 × 700, minimum 880 × 580 |
| Windows side navigation | width 216, collapsed 56 |
| First-run wizard modal | 720 × 520, fixed |

Shadows. On Windows 10 a shadow is the only way to separate a layer (Acrylic is
unavailable).

| Token | Value (light) | Value (dark) |
|---|---|---|
| `shadow.card` | `0 1px 2px rgba(16,24,40,.06), 0 1px 3px rgba(16,24,40,.10)` | `0 1px 2px rgba(0,0,0,.32)` |
| `shadow.pop` | `0 8px 24px rgba(16,24,40,.14)` | `0 8px 24px rgba(0,0,0,.48)` |
| `shadow.modal` | `0 24px 48px rgba(16,24,40,.20)` | `0 24px 48px rgba(0,0,0,.60)` |

On macOS we **do not draw** our own shadows: the popover and the window get the
system one.

## 5. Motion specification

### 5.1 Philosophy

Animation in a utility answers exactly one question: **"what changed?"**
If the user can already see it, there is no animation. The motion budget for the
whole application is about ten named transitions, listed below. Anything not in
the table is not animated.

### 5.2 Curves

| Token | Reference | macOS (SwiftUI) | Windows (Avalonia `Easing`) | When |
|---|---|---|---|---|
| `ease.standard` | ease-out, cubic | `.smooth(duration:)` | `CubicEaseOut` | appearance, disappearance, opacity changes |
| `ease.emphasis` | ease-out, sharper | `.snappy(duration:)` | `QuinticEaseOut` | selection moving, section change |
| `ease.spring` | spring, small bounce | `.spring(duration:bounce: 0.15)` | `SpringEasing` | an entity appearing, the success checkmark |
| `ease.linear` | linear | `.linear` | `LinearEasing` | continuous indicators only: spinner, level meter, sparkline |

**SwiftUI — the exact numbers (verified against Apple's documentation).** `.smooth`,
`.snappy` and `.bouncy` share the same parameter defaults (`duration: 0.5,
extraBounce: 0.0`), but **their base bounce differs, and that is documented in the
description of `extraBounce`**: `.smooth` has a base of 0, `.snappy` has **0.15**,
`.bouncy` has **0.3**. In other words, `extraBounce` is added *on top of* the
preset's base.
Separately: `.default` is `spring(response: 0.55, dampingFraction: 1.0,
blendDuration: 0)`; before macOS 14, `.default` was `easeInOut`.
The whole bounce API and the `Spring` type are **macOS 14.0+**.

Discipline rule: in code we use **only** `.smooth(duration:)`,
`.snappy(duration:)` and `.spring(duration:bounce:)`. We do not use `.bouncy`
anywhere — a bounce of 0.3 is out of place in a utility.

**Avalonia — the spring is there.** `SpringEasing`, with `Mass`, `Stiffness` and
other properties, is a standard class in `Avalonia.Animation.Easings`. There is no
need to emulate it with keyframes.
⚠️ **`CubicBezierEasing` was removed in Avalonia 12** (it was `[Obsolete]` in 11.3).
If an arbitrary curve is needed, use `SplineEasing`.
⚠️ `CustomAnimatorBase<T>` was removed → `InterpolatingAnimator<T>`.

### 5.3 Durations

| Token | Value | Rule |
|---|---|---|
| `dur.instant` | **0 ms** | telemetry value change, controller button press |
| `dur.micro` | **120 ms** | hover, press, focus, state-dot color change |
| `dur.short` | **180 ms** | an element appearing or disappearing within a screen |
| `dur.base` | **240 ms** | card state change, block expanding |
| `dur.long` | **320 ms** | transition between sections, showing a modal, the success checkmark |
| `dur.slow` | **480 ms** | first-run wizard steps only |

Nothing in the application is longer than 480 ms. Nothing is shorter than 120 ms
either, apart from `instant`.

### 5.4 Transition catalog

| # | Event | What moves | Duration | Curve | Note |
|---|---|---|---|---|---|
| 1 | Window/popup opens | opacity 0→1, scale 0.98→1.0 | 180 | `standard` | on macOS the system animates the popover — do not add one of our own |
| 2 | Section change in the side navigation | content: opacity 0→1 + 8 px shift from below | 240 | `emphasis` | the old content fades out over 120, the new one appears after 60 ms |
| 3 | Selection bar in the navigation | translateY | 320 | `emphasis` | it travels continuously, it does not fade out and back in |
| 4 | Status card changes state | background color, border color, heading color | 240 | `standard` | the text changes through `contentTransition`, it does not "travel" |
| 5 | Status text change | crossfade | 180 | `standard` | macOS: `.contentTransition(.opacity)`; Windows: two `TextBlock`s in a `Panel` |
| 6 | Number on a tile changes | per-character digit roll | 240 | `standard` | macOS: `.contentTransition(.numericText(value:))` ⚠️ works **only inside `withAnimation`/`.animation()`**; Windows — **no animation**, there is no equivalent and hand-rolling one does not pay off |
| 7 | State icon change | the symbol is replaced | 240 | `standard` | macOS: `.contentTransition(.symbolEffect(.replace.downUp))`; Windows: crossfade of two `Path`s |
| 8 | Audio level meter | fill width | **0 (rise) / 90 ms (fall)** | `linear` | the rise is instant — otherwise the meter lies about the peak; the fall is smooth |
| 9 | "Waiting" state dot | opacity 1.0 ↔ 0.45 | **1400 ms** cycle | `standard`, autoreverse | the only looping animation in the application. On Windows the default `PlaybackBehavior.Auto` pauses it by itself when the section is not visible |
| 10 | An error appears | opacity 0→1 + height 0→auto | 240 | `standard` | no "shake", no red flash |
| 11 | "Advanced" expands | height + opacity | 240 | `standard` | |
| 12 | Wizard modal | backdrop 0→scrim over 180; window scale 0.96→1.0, opacity 0→1 | 320 | `spring` | |
| 13 | Wizard step forward | old: −24 px + fade out (200); new: +24 px → 0 + fade in (280, delay 80) | 480 total | `emphasis` | backward is mirrored |
| 14 | The "everything works" checkmark | `trim` 0→1 along the path | 320 | `spring` | + a single scale pulse 1.0→1.06→1.0 over 240 |
| 15 | Spinner | rotation | **900 ms** per turn | `linear` | appears after 400 ms of waiting |
| 16 | Skeleton | gradient sheen from left to right | **1200 ms** cycle | `linear` | |
| 17 | Button press | scale 1.0→0.97 | 120 | `standard` | on macOS this is a system effect — do not build your own |
| 18 | Menu bar / tray icon changes state | crossfade | 180 | `standard` | macOS: `symbolEffect(.replace)` |
| 19 | Controller: button press | fill + scale 0.94 | **0 ms** | — | input is not animated at all |
| 20 | Controller: stick/trigger | position | smoothed by a filter, not by animation | — | see §8.4 |

### 5.5 When there must be NO animation

Hard rules; breaking one is a bug:

1. **Anything that reflects the user's physical input in real time** — controller
   buttons, sticks, triggers, the rising edge of the level meter. Latency here
   reads as "it's lagging".
2. **Telemetry numbers that update more than twice a second.** Animating them
   means making them unreadable.
3. **The first frame after application launch.** The window and its contents appear
   already in their final state; there is no intro animation. Otherwise a cold start
   looks slower than it is.
4. **Recovery from an error in the background.** If the connection dropped and came
   back while the user was not looking, on returning they see the finished state,
   not a replay of the history.
5. **The log list.** New lines appear without animation.
6. **Theme change.** Instant. Crossfading the whole window is expensive and looks
   cheap.
7. No more than **two** things move on screen at once. If the table implies more,
   they are queued up with delays.

### 5.6 Reduced motion

The setting is read **every time an animation starts**, not once at launch: the
user can turn it on without restarting the application.

**macOS**
- `@Environment(\.accessibilityReduceMotion)` inside a View (macOS 10.15+);
- outside a View — `NSWorkspace.shared.accessibilityDisplayShouldReduceMotion` (10.12+);
- changes come through `NSWorkspace.accessibilityDisplayOptionsDidChangeNotification`.
  🔴 **You have to subscribe on `NSWorkspace.shared.notificationCenter`, not on
  `NotificationCenter.default`** — Apple calls this out in a dedicated Important block:
  *"If you register using a different notification center, you won't receive the
  notification"*. The notification carries no `userInfo`.
- `accessibilityReduceTransparency` (10.15+) is honored separately: with that
  setting on, every material is replaced with an opaque `surface`.
- 🔴 **There is no automatic handling.** SwiftUI has **no** API at the level of
  `.animation()` or `Transaction` that accounts for Reduce Motion on its own. The
  official pattern is to read the env value and pass `nil`: the documentation for
  `animation(_:value:)` says *"If `animation` is `nil`, the view doesn't animate"*.
  Using `Transaction.disablesAnimations` for this is not recommended.
  Whether `symbolEffect` itself honors Reduce Motion is **unconfirmed**; we assume
  it does not.
- The official statement of what to do is in HIG Accessibility, the Cognitive section:
  *"reducing automatic and repetitive animations… Tightening animation springs to
  reduce bounce effects… **Replacing transitions in x-, y-, and z-axes with fades
  to avoid motion**… Avoiding animating into and out of blurs"*. So the
  "crossfade instead of movement" approach is the letter of the guideline, not
  something we invented.

**Windows**
- 🔴 **Avalonia has no API for it** (see §2.1.4, issue #19405 is open). We do it
  ourselves: `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION = 0x1042, …)` — the
  system flag for "Show animations in Windows" (Settings → Accessibility → Display).
- Changes arrive as a `WM_SETTINGCHANGE` message; listening for it is mandatory,
  because the setting is changed on the fly.
- A single `IReducedMotionProvider` that swaps the `Duration` resources for `0:0:0`
  or switches a style class on the window root. There must be no scattered checks
  throughout the code.

What happens when the setting is on:

| Transition | Behavior |
|---|---|
| 1, 2, 10, 11, 12, 13 | replaced by a 120 ms crossfade |
| 3 (navigation bar) | jumps instantly |
| 6, 7, 14, 18 | no animation, the value changes immediately |
| 9 (waiting pulse) | **disabled**, the dot is simply a static `warn` |
| 15 (spinner) | stays — it is an indicator, not decoration; but 1400 ms instead of 900 |
| 16 (skeleton) | the sheen is disabled, a static gray placeholder remains |
| 8 (level meter), 19, 20 | **unchanged** — this is data, not animation |
| Gyro-driven tilt of the outline (§8.3) | disabled |

The rule is stated as: reduced motion removes **decorative** motion and motion
**across the screen**; it does not remove the display of data.

## 6. Components

The complete list. No further components may be added — if something seems to be
missing, first check whether the job can be done by combining what already exists.

| # | Component | macOS | Windows | Purpose |
|---|---|---|---|---|
| 1 | `StatusCard` | `RoundedRectangle` + `VStack` | `Border.status` (already there) | one large sentence about state + a subline |
| 2 | `StatusDot` | `Circle` 8 pt | `Ellipse.dot` (already there) | state dot in lists and in the header |
| 3 | `MetricTile` | `GroupBox` | `Border.tile` (already there) | caption + large number |
| 4 | `LevelMeter` | present | present | audio level with peak-hold |
| 5 | `Sparkline` | `Canvas` | present | a minute of history in one stroke |
| 6 | `FeatureRow` | `HStack` in the popover | `ListBox` item | icon + feature name + state + toggle |
| 7 | `Toggle` | system `Toggle` | `ToggleSwitch` (Semi) | turning a feature on |
| 8 | `PrimaryButton` | `.buttonStyle(.borderedProminent)` | `Button.Primary` (Semi) | one main action per screen |
| 9 | `SecondaryButton` | `.bordered` | `Button` | everything else |
| 10 | `InlineAlert` | `HStack` with a background | **`Banner` (Ursa)** | error/warning inside a card |
| 11 | `EmptyState` | centered `VStack` | centered `StackPanel` | 32 icon + title + text + one button |
| 12 | `KeyField` | `SecureField` + `Toggle` | `TextBox` + `ToggleButton` | masked key, "Show", "Copy" |
| 13 | `FingerprintLabel` | `Text.monospaced` | `TextBlock.mono` | `A1F2 · 9C40 · 77BE · D103` |
| 14 | `CheckRow` | `Label` + symbol | `Grid` | checklist row: state icon + text + action |
| 15 | `StepDots` | `HStack` of circles | `ItemsControl` | wizard progress |
| 16 | `QRPanel` | `Image` from `CIFilter.qrCodeGenerator()` | `Path` from `QrCode.ToOutlines()` | QR on a white backing (§3.5) |
| 17 | `ControllerView` | `Canvas` + `TimelineView` | `CompositionCustomVisualHandler` | live DualSense visualization (§8) |
| 18 | `Spinner` | `ProgressView()` | **`LoadingIcon` (Ursa)** or `<ProgressBar Theme="{DynamicResource ProgressRing}"/>` (Semi) | waits > 400 ms |
| 19 | `Skeleton` | `Rectangle` + `.shimmer` | **`Skeleton` (Ursa)** | card placeholder on first load |
| 20 | `LogList` | `Table` | `ItemsRepeater` + `ScrollViewer` | log, monospaced, virtualized |

Off-the-shelf library controls (§2.1.1) cover 5 of the 20 rows: `NavMenu` (side
navigation), `Banner` (10), `LoadingIcon`/`Loading` (18), `Skeleton` (19),
`Dialog`/`Drawer`/`MessageBox` (the wizard in §9). The rest is either system-provided
or 15–60 lines of our own code. Not one row needs a library that doesn't exist for
Avalonia 12.

### 6.1 Usage rules

- **Exactly one** `PrimaryButton` per screen. If it feels like you need two, one of
  them isn't the main action.
- An `EmptyState` always carries an action. An empty state without a button is a
  design mistake.
- A `Spinner` never appears before **400 ms** of waiting: a short operation that
  finished in time shouldn't flash an indicator.
- `Skeleton` only where the shape of the coming content is known (tiles, a list).
  Everywhere else, `Spinner`.
- An `InlineAlert` lives inside the card it belongs to. General errors go in the
  header `StatusCard` and are not smeared across the screen.
- The feature toggle is the **only** way to turn a feature on and off. There must be
  no Start/Stop buttons next to it.

## 7. Screens by feature

### 7.0 State model — shared

Both features are described by a single state machine. This matters: the user learns
the rules once.

```
 off ──(on)──▶ starting ──▶ waiting for the other side ──▶ live
  ▲               │                     │                   │
  └─────(off)─────┴─────────────────────┴───────────────────┘
                  │
                  ▼
                error ──(fix)──▶ starting
```

Five states, the same for the microphone and for the DualSense:

| Code | Title in the UI | Color | What the user sees |
|---|---|---|---|
| `off` | "Off" | `off` | feature turned off by the toggle |
| `starting` | "Starting" | `textDim` | up to 10 s, then → `error` |
| `waiting` | "Waiting for Windows" / "Waiting for controller" | `warn` | our side is ready, the other isn't |
| `live` | "Audio is live" / "Controller forwarded" | `ok` | everything works |
| `error` | the specific wording of the error | `bad` | action needed |

Mute is a substate of `live` colored `warn` (see 4.1).

**The one-line rule.** In any state the header shows exactly one sentence, and it
answers the question "is it working or not". Everything else goes below it, smaller.

### 7.1 Shell

#### Windows — main window

```
┌───────────────────────────────────────────────────────────────────────┐
│ [logo] HexBridge              ● All good                [Pause]  [⋯]  │  56 px, surface, bottom border
├─────────────────┬─────────────────────────────────────────────────────┤
│ FEATURES        │                                                     │
│ ● Microphone    │           selected section content                  │
│   Audio is live │           padding 24, max-width 820                 │
│                 │                                                     │
│ ● DualSense     │                                                     │
│   Forwarded     │                                                     │
│                 │                                                     │
│ ──────────      │                                                     │
│   Connection    │                                                     │
│   Log           │                                                     │
│   Settings      │                                                     │
│                 │                                                     │
│ [Windows 10]    │                                                     │
└─────────────────┴─────────────────────────────────────────────────────┘
    216 px
```

The side navigation is **Ursa's `NavMenu`** (§2.1.1), not a `TabControl`: we need a
two-line item, a rail mode and keyboard navigation out of the box.

- A feature item is **two lines**: the name (`title`) plus the current state
  (`caption`, in the status color), with an 8 px indicator dot on the left. Item
  height 52, padding `12/16`.
- The Connection, Log and Settings items are single-line, height 36.
- Active item: `accentSoft` backing, a 3 px vertical `accent` bar on the left,
  `radius.sm` corners. The bar **slides** between items (see 5.4).
- The divider between groups is a 1 px `border` with `space.md` around it.
- At the bottom, the version line and "Windows 10 22H2" in small `caption` `textDim`.

The window header:
- On the left, the 24 px logo and "HexBridge".
- In the center, the **summary status**: dot plus one sentence. The summary is the
  worst state among the enabled features (`error` > `waiting` > `live` > `off`).
- On the right, one main button: "Pause" when something is running, "Start" when
  nothing is. The button uses the `accent` fill. And `⋯` — a menu (About, Open log,
  Quit).

#### Windows — tray

- Icon at 16/20/24/32 px in a single `.ico`. Avalonia picks the size itself based on
  the DPI of the monitor holding the taskbar, and re-picks on `WM_DISPLAYCHANGE` and
  on an explorer restart. Four variants: `idle` (outline), `live` (`okFg` fill),
  `warn`, `error`. The icons are distinguishable by **shape**, not by color alone:
  `live` is solid, `warn` has a dot in the corner, `error` has a diagonal cross.
  `TrayIcon.Icon` can be changed at runtime, so no special trick is needed.
- The tooltip (`ToolTipText`) is one line: `HexBridge — audio live, controller forwarded`.
- Context menu (right-click), items in this order:
  `Open` · `—` · `Microphone ✓` · `DualSense ✓` · `Mute microphone` · `—` · `Log` · `Quit`.
  The checkmarks are `NativeMenuItem.ToggleType` + `IsChecked` (two-way); these are the
  feature toggles, so a feature can be turned off without opening the window.
  🔑 **On Windows, Avalonia's tray menu is not a native Win32 menu but an ordinary
  window with a `MenuFlyoutPresenter` inside** (§2.1.5). That means our `ControlTheme`
  for `MenuFlyoutPresenter`/`MenuItem`/`Separator` applies to it, and the tray menu
  looks like the rest of the application without a single third-party library.
- Left click shows the window and brings it to the front.
  ⚠️ **Avalonia does not support double-click** (`WM_LBUTTONDBLCLK` isn't handled), so
  we hang nothing on it. "Click again to minimize" is implemented on the single click:
  if the window is already active, minimize it.
- Closing the window with the X **minimizes it to the tray**.
  ⚠️ **Avalonia has no tray balloon notifications** (`NIF_INFO` is never set; the
  request was closed as not planned). So the "it went to the tray" hint is an in-app
  toast via **`WindowNotificationManager`**, shown at the moment of minimizing while
  the window is still on screen, once per installation:
  "HexBridge keeps running in the notification area. To quit, right-click the tray icon."

#### macOS — menu bar

- `MenuBarExtra` with `.menuBarExtraStyle(.window)` (already the case) — a popover,
  not a menu. The HIG asks for the opposite (*"Display a menu — not a popover"*), but
  it makes an exception for functionality that is "too complex for a menu". Ours is
  exactly that case: the popover holds a live level meter and a mini controller
  visualization, and `NSMenu` cannot draw animated content. The reasoning is recorded
  in §1.1 so that nobody has to revisit it.
  ⚠️ `MenuBarExtra` has no API for opening it programmatically — only `isInserted`
  (whether the icon is present). Programmatic display requires `MenuBarExtraAccess` (§2.2).
  ⚠️ If the user removes the icon from the menu bar, **the system terminates** an
  LSUIElement application. Handle it: when `isInserted == false`, show the settings
  window with an explanation.
- The menu bar icon is a **template image**, monochrome. We do **not hardcode** its
  size: Apple publishes no recommended size (the only number in the HIG is "the menu
  bar's height is 24 pt"), so we use `MenuBarExtra(_:systemImage:)` and let the system
  pick. The symbol changes with the summary state: `waveform` (running) /
  `waveform.slash` (muted) / `exclamationmark.triangle` (error) / an outline
  `waveform` at reduced opacity (off).
  The menu bar icon is **never tinted** — that violates the HIG and would make it
  unreadable on colorful wallpaper; state is carried by the shape of the symbol.
- Popover width 340. Structure top to bottom:
  1. Summary line: symbol + state + host address in small type.
  2. Microphone card: level meter, mute button, input selection.
  3. DualSense card: mini controller outline (see 8), pad name, battery.
  4. Divider.
  5. `Settings…` (`SettingsLink`) · `Quit`.
- The card of a disabled feature collapses into a single row with a toggle.

#### macOS — settings window

The standard `Settings` scene with **a toolbar and panes** — the HIG requires exactly
this (*"use a noncustomizable toolbar"*) and never mentions a sidebar in a settings
window. The panes: **General · Microphone · DualSense · Connection · Diagnostics**.
Inside, `Form { Section { … } }` with `.formStyle(.grouped)`. Nothing custom: a
utility's settings window should look like every other utility's settings window.

Three more HIG requirements that have to be implemented explicitly:
- **The window title changes to the name of the active pane.**
- **The last opened pane is restored.**
- The minimize/zoom buttons are dimmed.

🔴 Opening this window from the menu bar is a known unsolved SwiftUI problem; time
for it is budgeted separately (§2.2).

### 7.2 Microphone

The screen (Windows) / card (macOS) is made of these blocks, top to bottom:

1. **Status card** (`radius.xl`, padding 22/20, colored by state).
2. **Level meter** — always, even when muted (muted it's gray, but still alive).
3. **Telemetry tiles** — four of them: packets/s, RTT, uptime, buffer.
4. **Sparkline** — a minute of level or packet-loss history.
5. **Connection** — address, input device, codec, key (masked).

#### States

| State | Title | Subtitle | What is shown | Action |
|---|---|---|---|---|
| **Not set up** | "Microphone not set up" | "Enter the receiver address and the shared key. You only do this once." | an empty state instead of telemetry, with a Mac→PC schematic illustration | **Set up** (launches the wizard from §9) |
| **Waiting for host** | "Waiting for Windows" | "Audio is going to `192.168.1.10:47702`, but the receiver isn't answering." | level meter live; RTT tile shows "—"; checklist of likely causes | **Check connection**, "Open log" |
| **Audio flowing** | "Audio is live" | "Games see it as `Steam Streaming Microphone`." | everything; tiles green | **Mute** |
| **Muted** | "Microphone muted" | "The receiver knows about the mute and keeps the connection open." | meter gray; packet counter not increasing | **Unmute** |
| **Error** | something specific, e.g. "No microphone access" | something specific, see §10 | an error block with a single button instead of telemetry | the specific action |

Level meter:
- scale −60…0 dBFS, linear in dB;
- fill is a gradient `okFg` → `warnFg` (from −12 dBFS) → `badFg` (from −3 dBFS);
- **peak-hold**: a separate 2 px tick that falls at 20 dB/s;
- when muted, the fill is `off` and peak-hold is hidden;
- height 14 (Windows) / 8 (macOS popover), `pill` radius;
- label on the right: `peak −12.9 dBFS`, in monospaced digits.

### 7.3 DualSense

1. **Status card**.
2. **Live controller visualization** (see §8) — the main element of the screen,
   taking the top ~340 px of height.
3. **Tiles**: reports/s, latency, battery, reports forwarded.
4. **Driver** — a card with the `usbip-win2` status, version, and an install button.

#### States

| State | Title | Subtitle | Visualization | Action |
|---|---|---|---|---|
| **Not connected** | "Controller not connected" | "Connect the DualSense to the Mac with a USB cable. Forwarding over Bluetooth does not work." | controller outline filled with `off`, opacity 0.35, unresponsive | — |
| **Connected, not forwarded** | "Controller found, forwarding off" | "`DualSense Wireless Controller`, battery 62 %. Windows doesn't see it yet." | outline is **live** — it reacts to input, but is tinted `textDim` rather than accent | **Turn on forwarding** |
| **Forwarded and working** | "Controller forwarded" | "Windows sees it as `054C:0CE6`. Triggers and gyro work." | outline live and colored; the lightbar glows in its real color | **Turn off forwarding** |
| **Driver not installed** | "usbip-win2 driver not installed" | "Without it, Windows can't create the virtual controller." | outline in `off`, with a dimming overlay and an install block on top | **Install driver** (see §10) |
| **Triggers not applied** (a special case of `warn`) | "Adaptive triggers not applied" | "macOS 26 won't pass output reports to this application. Buttons, sticks and gyro work; trigger effects don't." | outline live, trigger area carries a `warn` badge | "Learn more" → docs |

This is deliberate: the "connected but not forwarded" state shows the live
visualization. That is the "my pad is being read" check, available before anything has
been turned on, and it is the first showcase moment — one that happens by itself.

### 7.4 Shared settings

One place, reachable from both platforms, with identical contents:

- **Connection**: receiver/relay address, port, shared key (masked + "Show" +
  "Copy" + "Generate"), and a "Check connection" button.
- **Startup**: launch at login (macOS — LaunchAgent, Windows — Task Scheduler),
  "Minimize to tray on close" (Windows only).
- **Appearance**: theme — System / Light / Dark. **Three options, no more.**
- **Advanced** (collapsed by default): Opus bitrate, expected packet loss, jitter
  buffer, WASAPI latency, output device.
- **Diagnostics**: "Open log", "Test microphone" (3 s), "Test controller",
  "Copy report" (gathers versions, the config without the key, and the last 200 log lines).

The rule: the first level of settings holds only what the user actually touches.
Anything that has a sensible default lives under Advanced.

## 8. Showcase moment 1 — live DualSense visualization

This is the one place in the application where it is acceptable to spend effort on
"pretty". The justification isn't aesthetic but functional: **it is a self-test**. The
user presses a trigger and sees that it arrived. No amount of text is that convincing.

### 8.1 What to draw it with

**Decision: our own schematic rendering from vector primitives, with no external SVG.**

The reasons:
1. **Legal.** The DualSense shape is a Sony industrial design, and "PlayStation", the
   △○✕□ glyphs and the controller silhouette are trademarks. A freely licensed
   photorealistic outline can be found (see 8.6), but using a recognizable silhouette
   of somebody else's product inside our own application is a risk this feature isn't
   worth. A schematic geometric abstraction (rounded body + two grips + circles and
   capsules) reads as "gamepad", imitates nothing, and is drawn with a couple of dozen
   primitives.
2. **Technical.** Buttons, sticks and triggers have to move and change color
   independently. A ready-made SVG would still have to be broken up into named layers,
   and that costs more than drawing it parametrically in the first place.
3. **Theming.** Our own outline recolors easily for light and dark themes. A ready-made
   asset does not.

The outline is built in a **normalized 400 × 260 coordinate system** and scaled as a
whole (`uniform`, `Viewbox` / `scaleEffect`), so every number below is in that grid.

### 8.2 Geometry (normalized 400 × 260 grid)

| Element | Shape | Coordinates (x, y, w, h), radius |
|---|---|---|
| Body | rounded rectangle | `(60, 20, 280, 120)`, r 40 |
| Left grip | capsule, tilted −12° | center `(118, 178)`, 56 × 130, r 28 |
| Right grip | capsule, tilted +12° | center `(282, 178)`, 56 × 130, r 28 |
| Touchpad | rounded rectangle | `(148, 38, 104, 60)`, r 8 |
| Left stick | circle | center `(150, 122)`, R 26 (rim), R 17 (cap) |
| Right stick | circle | center `(250, 122)`, R 26 / R 17 |
| D-pad | cross of 4 capsules | center `(96, 74)`, arm 13 × 22, r 5 |
| △○✕□ | 4 circles R 11 | center `(304, 74)`, spacing 26 |
| L1 / R1 | capsule | `(78, 6, 52, 12)` / `(270, 6, 52, 12)`, r 6 |
| L2 / R2 | a "petal" — a capsule that pivots around its top edge | `(78, −16, 52, 22)` / `(270, −16, 52, 22)`, r 8 |
| Create / Options | capsule | `(126, 46, 10, 18)` / `(264, 46, 10, 18)`, r 5 |
| PS | circle R 9 | center `(200, 152)` |
| Mute | capsule | `(190, 126, 20, 10)`, r 5 |
| Lightbar | 2 arcs along the sides of the touchpad, thickness 4 | along `x = 142` and `x = 258`, `y` 44…92 |

Draw order (bottom to top): body and grips → lightbar → touchpad → passive buttons →
sticks → triggers → active highlights → touch points.

### 8.3 Mapping data to pixels

| Data (from `GamepadState`) | Range | What it does on screen |
|---|---|---|
| `left.x/y`, `right.x/y` | `UInt8` 0…255 | the stick cap moves `(v−127.5)/127.5 × 14` px from the center; the rim stays put |
| `l2`, `r2` | `UInt8` 0…255 | the trigger petal rotates `−18° × v/255` around its top edge **and** fills vertically with `accent` over `v/255` of its height |
| `buttons` (bitmask) | 15 bits | a pressed button: `accent` fill, `accent` stroke, scale 0.94 |
| `dpad` (hat 0…8) | 9 values | 1 or 2 arms of the cross light up |
| `touch[0..1]` | `active`, `x` 0…1919, `y` 0…1079 | an R 7 point in `accent`, with a "tail" of the last 8 positions, alpha 1.0 → 0.0 |
| lightbar (from output report `0x02`) | RGB 0…255 | the color of the arcs; with the lightbar off, `border` |
| `gyro` (Int16 ×3) | ±32767 | **the whole outline** tilts: `rotation.z = clamp(gyro.z/32767 × 12°)`, `rotation.x/y` via pseudo-3D through `.rotation3DEffect` (macOS) / `RotateTransform` plus a slight skew (Windows) |
| `accel` | ±32767 | not drawn — redundant; numbers only, in diagnostics |
| `batteryPercent` | 0…100 | a separate badge outside the outline, not in the visualization |
| `sequence` | `UInt8` | a gap in the sequence → a "dropped frame" badge blinks for 300 ms (diagnostics mode only) |

**The gyro is the cheapest showcase effect there is.** The outline follows the tilt of
the controller in the user's hands, only just perceptibly. The amplitude is
deliberately understated (12° max): it should read as "it's alive", not as a fairground
ride.

### 8.4 Refresh rate and CPU

The controller emits **250 reports/s** (`bInterval 6` at High Speed). Drawing that many
frames is neither possible nor necessary.

**A three-level scheme:**

```
HID thread 250 Hz ──▶ atomic snapshot (lock-free, last one wins)
                              │
       UI timer ──────────────┴──▶ reads the snapshot, interpolates, requests a redraw
```

| Condition | Redraw rate | Reasoning |
|---|---|---|
| DualSense screen open and visible, input arriving | **60 Hz** | sticks and triggers are analog; below 60 the stepping is visible |
| Open, no input for 2 s | **8 Hz** | only battery and status change, and the eye is fine with that |
| Screen not selected / window minimized / macOS popover closed | **0 Hz** — no frames requested (`paused: true` / no `RequestNextFrameRendering`) | the main source of savings |
| Window in the background but visible | 20 Hz | |
| "Reduce motion" enabled | 30 Hz, no inertia and no gyro tilt | |

The rules without which 60 Hz will burn the battery:

0. **The right rendering primitive.**
   - **Windows: `CompositionCustomVisualHandler` + `compositor.CreateCustomVisual(handler)`.**
     Of the three ways to do custom drawing in Avalonia, this is the only one that
     gives you your own frame loop **on the render thread**: `OnRender` is called
     per-frame, and the next frame is requested with `RequestNextFrameRendering()`.
     The Avalonia documentation names it outright as the one for "real-time
     visualizations, game loops". `Control.Render` + `InvalidateVisual()` at 60 Hz
     would force the UI thread to rebuild the scene graph every 16 ms, which is not an
     option. A ready-made wrapper, if we'd rather not write our own:
     `CompositionAnimatedControl` from wieslawsoltes/Lottie (MIT, Avalonia ≥ 12.0.0).
   - **macOS: `Canvas` inside `TimelineView(.animation(minimumInterval:paused:))`.**
     This is the only combination in SwiftUI with explicit control over **both rate and
     pause**: `minimumInterval` caps the rate, and `paused: true` stops the timeline
     entirely. `phaseAnimator`/`keyframeAnimator` give no such control.
     ⚠️ `Canvas` offers neither interactivity nor per-element accessibility. For us
     that doesn't matter (the visualization is decorative), but it does mean the data
     must be duplicated as text in diagnostics.
1. **No layout per frame.** There are no child elements inside the visualization. Not a
   single `Border`, not a single binding on a button.
2. **Invalidate only on change.** The snapshot is compared with the previous one; if
   the bytes match, `InvalidateVisual()` is not called.
3. **Brushes and geometries are cached** in fields on the control rather than created
   in `Render`. The static part of the body is one pre-built `StreamGeometry`, rebuilt
   only on a size or theme change.
4. **The timer is tied to visibility**: subscribe to `IsEffectivelyVisible` (Avalonia) /
   `.onDisappear` and `NSWindow.occlusionState` (macOS). On Windows the built-in
   `Animation.PlaybackBehavior.Auto` (§2.1.4) also does its part, pausing style
   animations on invisible controls by itself — but it has no effect on our own frame
   loop, which we stop by hand.
5. Target budget: **< 3 % of one core** at 60 Hz on a typical gaming PC and **< 4 %** on
   a MacBook Air. Measure it and record it in the measurement log.

Smoothing: analog values pass through an exponential filter,
`v = v + (target − v) × 0.35` per frame (60 Hz). It removes stick jitter at rest and
adds about 15 ms of softness, which reads as "smooth" rather than "laggy". Buttons do
**not** pass through the filter: a press must be instantaneous.

### 8.5 Both themes

| Layer | Light | Dark |
|---|---|---|
| Body, fill | `#FFFFFF` | `#262B34` |
| Body, stroke | `borderStrong` `#7E8899`, 2 px | `borderStrong` `#66738A`, 2 px |
| Passive buttons | fill `#EEF1F5`, stroke `#C7CEDA` | fill `#2E3440`, stroke `#3E4757` |
| Pressed button | fill `accent` `#1D65C4`, glyph white | fill `accent` `#5A9BFF`, glyph `accentInk` |
| Stick rim | `#C7CEDA` | `#3E4757` |
| Stick cap | `#FFFFFF` + `shadow.card` | `#39414F` |
| Trigger fill | `accent`, alpha 0.85 | `accent`, alpha 0.9 |
| Touchpad | fill `#F4F6F9`, stroke `#DCE2EA` | fill `#1A1E25`, stroke `#333A46` |
| Touch point | `accent`, no shadow | `accent`, glow `blur 6` alpha .5 |
| Lightbar off | `#DCE2EA` | `#333A46` |
| Lightbar on | real RGB, **saturation ×0.9** | real RGB, plus an outer glow `blur 10` alpha .45 |
| Inactive outline (`off`) | everything in `off` `#7E8899`, alpha 0.35 | everything in `off` `#7A8496`, alpha 0.35 |

The lightbar cannot be drawn as-is in the light theme: a pure `#FFFFFF` lightbar merges
with the body. So above 90 % brightness a `borderStrong` stroke is added.

### 8.6 Ready-made assets — what turned up

The full search report is in §3.3–3.4. In short: **a freely licensed SVG outline of the
DualSense does not exist** (Wikimedia Commons has zero SVGs in the category; every
visualizer project is either unlicensed, or GPL, or has art of dubious origin). There
are CC0 sets of **button glyphs** — Kenney Input Prompts (`PlayStation Series/Vector/`)
and Xelu (the `Playstation_5` group) — but the four shapes △ ○ ✕ □ are easier to draw
from primitives: that's ~15 lines and zero licensing questions. Sony's design patents on
the body and the trademark on the "PlayStation Shapes Logo" are confirmed (§3.4), which
is exactly why we draw an abstraction rather than a silhouette.

### 8.7 What to show on macOS

The menu bar popover holds **a scaled-down version of the same control**, 300 × 100,
with no gyro and no touchpad: just the sticks, the triggers and the press highlights.
The full visualization lives on the DualSense tab of the settings window.
The reason: the popover must not turn into an application.

## 9. Showcase moment 2 — first run and pairing the machines

Today: the user runs `keygen` in a terminal, copies base64 into two config files,
and types in the IP and port by hand. All of that has to go.

### 9.1 Principle

**The key is generated on Windows, not on the Mac.** Windows is the side that
listens: it has the address and the port, and it is Windows' data that needs to be
carried across. So the pairing code has to contain *both* the key *and* the
address — and only Windows knows the address.

The payload to be carried is a single URI:

```
hexbridge://pair?v=1&h=192.168.1.10&p=47702&k=<base64url of key>&n=<PC name>
```

Three ways to get it onto the Mac, in order of preference. All three always work;
the user picks:

| Method | When | What it looks like |
|---|---|---|
| **1. Discovery on the network** | Mac and PC on the same LAN | The Mac finds the PC by itself, a 6-digit confirmation code appears on the Windows screen, the user types it on the Mac |
| **2. QR code** | Always, if the Mac is nearby and has a camera | Windows draws a QR code, the Mac opens the camera and points at it |
| **3. Short code** | No camera, discovery didn't work | Windows shows **12 characters** formatted as `ABCD-EFGH-JKLM`, the Mac accepts them |

Method 3 cannot carry a full 32-byte key. So it carries a **one-time exchange
code** instead: Windows listens on a port for a short while (3 minutes) and hands
the full URI to whoever presents the correct code — the exchange is protected by
SPAKE2/PAKE or, more simply, HKDF over the code plus fingerprint confirmation on
both sides. That is a separate security task; only the UX is fixed here.

### 9.2 Windows flow: "Pair a Mac"

A 720 × 520 modal, four steps, progress shown as dots along the top (not a bar:
there are too few steps).

**Step 1. Readiness.**
> **Pair this PC with a Mac**
> HexBridge will create a shared key and show it as a code. You'll enter that
> code on the Mac once.
>
> A status checklist, each line with its own check mark:
> - usbip-win2 driver — `installed` / `not installed` `[Install]`
> - Virtual audio cable — `Steam Streaming Microphone` / `not found` `[Choose]`
> - UDP port 47702 — `open` / `blocked by the firewall` `[Allow]`
> - Address on the network — `192.168.1.10`
>
> `[Next]`

Every unresolved line is fixed **right here**, without leaving the wizard.
The wizard does not block "Next" — you can pair now and install the driver later.

**Step 2. Code.**
On the left, a 240 × 240 QR code on a white backing with `radius.lg` (the white
backing is mandatory in dark theme too, otherwise the camera won't read it; the
quiet zone must be ≥ 4 modules).
It's drawn as a single `<Path>` from `QrCode.ToOutlines()` / `ToGraphicsPath()`
(`Net.Codecrete.QrCodeGenerator`, §3.5) — no raster needed.
Correction level **M**: the payload is short, and a higher correction level would
only make the modules smaller for no benefit. Below the code, the short code in
large monospace with a "Copy" button.
On the right, three lines of instructions for the Mac.
At the bottom, a timer: "Code valid for 2:47". Once it expires, a "Refresh code"
button.

**Step 3. Waiting.**
The QR code stays where it is but dims; on top of it, a waiting indicator and the
line "Waiting for the Mac". As soon as the Mac connects, step 4 comes up
automatically.

**Step 4. Connection check** — see 9.4.

### 9.3 macOS flow: "Connect to a PC"

Opens in a **separate window** (not in the popover: scanning with the camera from
the menu bar is a bad idea, since the window closes on any click outside it).

**Step 1.** Three large buttons to pick the method:
> `Find a PC on the network` (recommended) · `Scan a code` · `Enter a code`

**Step 1a — discovery.** Bonjour/`NWBrowser` on the `_hexbridge._udp` type, with a
list of the PCs found, by name. Usually there's exactly one row — in which case it
is selected immediately and `Next` is enabled.

**Step 1b — camera.** Live preview with a 240 × 240 viewfinder frame in the
center. On recognition, the frame collapses into a check mark and the flow moves
on by itself.
`AVCaptureMetadataOutput` + `[.qr]` (⚠️ macOS **13.0+**, see §3.5), with a
mandatory `availableMetadataObjectTypes.contains(.qr)` check before assignment.
Camera permission is requested **at the moment of the tap**, not at app launch,
and with an explanation: "The camera is only used to read the code off the PC
screen."
⚠️ `NSCameraUsageDescription` in Info.plist is mandatory — without it
`requestAccess` throws.

If there's no camera, or access wasn't granted, the method simply isn't offered in
step 1, with no error message.

**Step 1c — manual entry.** A single 12-character field with auto-formatting
(`ABCD-EFGH-JKLM`), automatic `uppercase`, and an alphabet with no `I`, `O`, `0`,
or `1`.

**Step 2. Connection check.**

### 9.4 Connection check — a result you can see

This is the main screen of the wizard, and it's also available at any time
afterwards from Settings via the "Check connection" button.

Six checks, each one a row with a status icon, run one after another with a delay
of ≥250 ms between rows appearing (otherwise the whole thing flickers past
unreadably):

| # | Row | Success | Failure |
|---|---|---|---|
| 1 | Address resolves | `192.168.1.10` | "Name doesn't resolve to an address" |
| 2 | Packets arrive | "12 ms" | "No reply within 3 s" |
| 3 | Keys match | "Fingerprint `A1F2 · 9C40`" | "Keys don't match" |
| 4 | Receiver found the audio device | "Steam Streaming Microphone" | "Device not found" |
| 5 | Audio makes it through | "peak −18 dBFS" | "Silence on the receiver" |
| 6 | Controller (if enabled) | "DualSense, 4 ms" | "Driver not installed" |

Item 5 is the important one: the wizard **asks you to say something out loud** and
shows a live level meter *from Windows*, not from the Mac. It's the only check
that proves the path works end to end. Five seconds, with a countdown.

The finish:

> **Everything works**
> Audio is going to `GAMING-PC`. In games, pick the microphone
> `Steam Streaming Microphone (Steam Streaming Microphone)`.
>
> `[Done]`

Here — and only here — one short celebratory moment is allowed: the check mark is
drawn stroke by stroke (`trim` / `StrokeDashOffset`) over 320 ms, with a `spring`,
and pulses once. No confetti.

If something didn't pass, the heading isn't "Error" but something specific:
"Audio isn't reaching Windows", with only the item that broke expanded below it,
carrying its own text from §10.

### 9.5 After pairing

Config is written on both machines, features are switched on, the wizard closes,
and the app settles on the status screen. The wizard is never shown again, but it
stays available in Settings as "Pair again".

## 10. Empty states, errors and hints

### 10.1 Tone rules

1. Calm and to the point. No "Oh no", "Oops", or "Something went wrong".
2. **Not a single exclamation mark** anywhere in the interface.
3. Message structure: **what happened → why → what to do**. Three sentences maximum.
4. An error always carries exactly one primary button with an imperative verb
   ("Install driver", "Check connection", "Open Settings").
5. Don't blame the user: not "you entered the wrong key" but "the keys don't match".
6. Technical detail (codes, stacks, addresses) goes on the second line in
   monospace, selectable with the mouse, but never in the heading.
7. Don't promise what we don't know: "try again later" is banned.
8. Units take a non-breaking space: `47 kbit/s`, `12 ms`, `62 %`.

### 10.2 Empty states

#### Nothing set up yet (first run)

> **HexBridge is ready to set up**
> All that's left is pairing the Mac with the gaming PC: generate a key and set
> the address. It takes about a minute.
>
> `[Start setup]` `[Set up manually]`

#### Microphone off

> **Microphone off**
> Audio isn't being sent to the gaming PC. Turn the feature on when you need it.
>
> `[Turn on microphone]`

#### DualSense: controller not connected

> **Controller not connected**
> Connect the DualSense to the Mac with a USB cable. Forwarding doesn't work over
> Bluetooth: it needs access to HID reports at full rate.

#### Log is empty

> **No entries yet**
> The log fills up as features start or change state.

### 10.3 Errors

#### No connection to Windows

> **Windows isn't responding**
> Audio is being sent to `192.168.1.10:47702`, but nothing is acknowledging it.
> Check that HexBridge is running on the gaming PC and that UDP port 47702 is open.
>
> `[Check connection]` `[Open Settings]`

An expandable "What to check" block — a checklist, each line with its own status
(checked automatically, see §9.4):

- HexBridge running on Windows — `not checked / yes / no`
- UDP port 47702 allowed through the firewall — `…`
- Mac and PC on the same network — `…`
- Address in Settings matches the PC's address — `…`

#### Keys don't match

> **Keys don't match**
> Packets are reaching Windows, but they can't be decrypted — the Mac and the PC
> have different keys stored. Copy the key from one machine to the other in full,
> including the trailing `=`.
>
> `[Show key]` `[Pair again]`

We also show the key's **fingerprint** from both sides — 4 groups of 4 characters,
in monospace, for example `A1F2 · 9C40 · 77BE · D103`. The user compares them by
eye without ever revealing the key itself:

> On this Mac: `A1F2 · 9C40 · 77BE · D103`
> On Windows: `5E71 · 20AC · 8B32 · 1FF9`

#### usbip-win2 driver not installed

> **usbip-win2 driver isn't installed**
> Without it Windows can't create a virtual controller, and the DualSense won't
> show up in games. Installation takes under a minute, no reboot, and no need to
> turn off Secure Boot.
>
> `[Install driver]` `[What is this]`

During installation, a warning — a calm one:

> USB hubs are reinitialized once, and connected devices will drop out for about a
> second. This is expected behavior from the installer.

After installation:

> **Driver installed**
> Version `0.9.8.0`. Controller forwarding can be turned on.

#### No virtual audio cable

> **No audio output device found**
> HexBridge writes audio into a virtual cable, and games read its paired half.
> None of the known cables were found on this PC.
>
> `[Pick a device manually]` `[How this works]`

With a list of options as cards, in order of preference:

| Option | Caption |
|---|---|
| **Steam Streaming Microphone** | Installed along with Steam. You most likely already have it — install Steam and restart HexBridge. |
| **VB-Audio Virtual Cable** | Free, donation optional. Installs in a minute, requires a reboot. |
| **VoiceMeeter** | Fine if you already have it. No need to install it just for HexBridge. |

#### No microphone access (macOS)

> **No microphone access**
> macOS won't let HexBridge read the input. Permission is granted once, in System
> Settings.
>
> `[Open Privacy settings]`

The button opens exactly the right pane:
`x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone`.

#### Port in use

> **Port 47702 is in use by another program**
> The receiver couldn't open it. Pick a different port outside the 47984–48010
> range — Sunshine holds that one — and set the same port on the Mac.
>
> `[Pick another port]`

#### Adaptive triggers aren't applied

> **Adaptive triggers aren't applied**
> macOS 26 blocks output reports from apps outside the App Store, and the
> controller answers `0xE00002C1`. Buttons, sticks, gyro and touchpad work;
> trigger resistance and the lightbar don't.
>
> `[Details]`

#### A second Mac with the same key

> **Connection already taken**
> Another Mac is already connected with this key — `Nikita's MacBook`, connected
> 14 minutes ago. Disconnect it, or use a separate key for the second machine.
>
> `[Take over the connection]`

#### Packet loss

Not an error — a `warn` in the status card:

> **Packet loss — 2.4 %**
> Audio is being reconstructed, but artifacts are audible in places. A wired
> connection or a larger buffer in advanced settings helps.

### 10.4 Hints (tooltips and captions)

A tooltip appears after 600 ms, stays as long as the cursor doesn't move, and runs
two lines at most.

| Element | Text |
|---|---|
| RTT | Estimated from timestamps in the control packets. Requires synchronized clocks on both machines. |
| Buffer | How many 20 ms frames are queued. The target is the jitter buffer divided by 20. |
| Concealed | Frames the codec reconstructed in place of lost ones. A non-zero value on a stable network means the buffer should be larger. |
| Shared key | 32 bytes in base64. Must match character for character on the Mac and the PC. |
| Expected loss | Controls Opus FEC redundancy: a higher value means more robust audio and more traffic. |
| DualSense forwarding | The controller stays connected to the Mac. HexBridge reads reports without taking the device away from the system or from Steam. |
| Launch at login | The app will start when you log in and run in the background. |
| Mute | The receiver is told about the mute and holds the connection — no reconnect needed. |

### 10.5 Notifications

Notifications fire only on a transition into `error` from a working state, and only
when the window isn't in the foreground. No more than one every 5 minutes. No
success notifications.

| Event | Title | Text |
|---|---|---|
| Connection lost | HexBridge | Windows stopped responding. Audio isn't getting through. |
| Controller disconnected | HexBridge | The DualSense was disconnected from the Mac. Forwarding is paused. |
| Connection restored | — | No notification. Only the tray / menu bar icon changes. |

## 11. Accessibility and quality

### 11.1 The mandatory minimum

| Requirement | How it's checked |
|---|---|
| Text contrast ≥ 4.5:1, indicators and control borders ≥ 3:1 | tables in §4.1 |
| Color is never the only carrier of meaning | every colored indicator has text and its own icon shape |
| Full keyboard navigation | Tab reaches every interactive element in visual order |
| Visible focus | 2 px `accent` ring + 2 px offset, contrast against the background ≥ 3:1 |
| Respect for system settings | theme, reduce motion, reduce transparency, text size |
| Screen reader | every status has a text label; the live visualization is marked decorative and duplicated as a table of values in Diagnostics |

### 11.2 Keyboard

| Shortcut | Action | Platform |
|---|---|---|
| `⌘,` / `Ctrl+,` | Settings | both |
| `⌘M` | Mute microphone | macOS (already there) |
| `Ctrl+M` | Mute microphone | Windows |
| `Ctrl+1…4` | Sidebar sections | Windows |
| `Esc` | Close modal/popover | both |
| `⌘W` / `Alt+F4` | Hide the window (not quit) | both |
| `⌘Q` | Quit | macOS |
| Global mute hotkey | user-configurable, unassigned by default | both |

The global mute hotkey is the single most valuable function for a streamer,
because their hands are busy. On macOS it's `KeyboardShortcuts` 3.0.1 (§2.2): it
registers a Carbon hotkey **without asking for Accessibility permissions** and
gives you a ready-made shortcut-recording control, `KeyboardShortcuts.Recorder`,
to drop straight into Settings. On Windows it's `RegisterHotKey` on the main
window's HWND.

### 11.3 Localization

The interface is in Russian. Strings live in resource files, not in markup, even
though a second language isn't planned: it's a discipline that forces you to
proofread the wording as a list rather than one string at a time.

Rules for Russian strings:
- Units take a non-breaking space (`U+00A0`).
- The dash is `—` (U+2014) with spaces around it; the hyphen inside compound words
  is a plain hyphen.
- Quotation marks are guillemets, «like this».
- Numbers: a comma for the decimal separator (`2,4 %`), a thin non-breaking space
  for the thousands separator.
- Plurals are computed by a function (Russian picks a different form for 1, 2 and
  5), not by gluing a string onto an abbreviated unit.

### 11.4 What to check before a release

1. Both apps in light and dark theme, at 100 %, 125 %, 150 % and 200 % scale.
2. Both apps with "reduce motion" turned on.
3. The Windows window at 880 wide (the minimum) — nothing clipped, nothing overlapping.
4. The macOS popover in all five states of each feature — the height must not jump
   by more than 40 px between adjacent states.
5. CPU load: DualSense visualization open for 10 minutes, take the average.
6. Every text from §10 shown live and proofread on screen, not in an editor.
7. A screenshot of every state from §7 saved in `docs/screens/` — that's the
   regression baseline for the next change.

## 12. Implementation plan

The order is chosen so that each step produces something visible and doesn't block
the ones after it.

### Stage −1. Reconnaissance (done first, before design)

Three things could force rework, so they get checked before anything else.

| # | Task |
|---|---|
| −1.1 | **Check [Avalonia#21082](https://github.com/AvaloniaUI/Avalonia/issues/21082) on 12.1.2**: `WindowDecorations="None"` + transparency. If the background comes out black, the custom title bar plan is off and we go through `DwmSetWindowAttribute(hwnd, 20, …)` |
| −1.2 | **Smoke-test `Irihi.Ursa` 2.2.0 on Avalonia 12.1.2** — the package was built against 12.0.2 and hasn't been rebuilt for 12.1. Check `NavMenu`, `Skeleton`, `Banner`, `Dialog` |
| −1.3 | **Check whether the Settings window opens from `MenuBarExtra` on release macOS 26.x** and whether the app activates. If not, budget for the workaround from §2.2 |
| −1.4 | Check `MenuBarExtraAccess` 1.3.1 on macOS 26.6 and on RC 27 |
| −1.5 | Check that icons in menu items haven't disappeared on macOS 27 (`labelStyle(.titleAndIcon)`) |

### Stage 0. Foundations (both platforms, in parallel)

| # | Task | Where |
|---|---|---|
| 0.1 | Create a tokens file: colors for both themes, spacing, radii, shadows, durations, curves. Not a single literal outside it | `win/src/MicBridge.App/Styles/Tokens.axaml`, `mac/Sources/…/UI/Tokens.swift` |
| 0.2 | Update the existing palette to the values in §4.1. `textDim` (`#6B7687` → `#5C6675`) and `accent` (`#387ADF` → `#1D65C4`) get fixed — neither old value passes WCAG AA. `borderStrong`, `accentInk`, `off`/`offBg`, `okText`/`warnText`/`badText` get added | both |
| 0.3 | Typographic styles per §4.2; on Windows the primary font is **Segoe UI**, with `Avalonia.Fonts.Inter` kept as a fallback for debugging off Windows | both |
| 0.4 | A "reduced motion" helper per §5.6: on Windows, P/Invoke `SPI_GETCLIENTAREAANIMATION` + `WM_SETTINGCHANGE` (Avalonia has no API for it); on macOS, env value + `NSWorkspace.shared.notificationCenter` | both |
| 0.5 | System/Light/Dark theme switch in Settings | both |
| 0.6 | Add to csproj: `Irihi.Ursa` 2.2.0, `Irihi.Ursa.Themes.Semi` 2.2.0, `FluentIcons.Avalonia` 2.1.339.1, `Net.Codecrete.QrCodeGenerator` 3.2.1 | Windows |
| 0.7 | Add SPM dependencies: Sparkle 2.9.6, KeyboardShortcuts 3.0.1, MenuBarExtraAccess 1.3.1. Move launch at login to `SMAppService`, no library | macOS |
| 0.8 | A multi-size `.ico` (16/20/24/32) for the four tray states, distinguishable by shape | Windows |
| 0.9 | A line about Sony trademarks in About (§3.4) | both |

### Stage 1. Shell

| # | Task |
|---|---|
| 1.1 | Windows: replace `TabControl` with `NavMenu` (Ursa), two-line feature items and a sliding selection bar (§7.1, transition 3) |
| 1.2 | Windows: `WindowDecorations="None"` + a custom header with the summary status and one primary button (on Win10 the system title bar doesn't go dark, §1.2) |
| 1.3 | Windows: tray — four icons; the menu via `NativeMenu` with `ToggleType`/`IsChecked`; menu theming via a `ControlTheme` for `MenuFlyoutPresenter`; the minimize hint via `WindowNotificationManager`, **not a balloon** (§2.1.5) |
| 1.4 | macOS: rebuild the popover as two feature cards plus a summary row; the menu bar icon is strictly template, no color |
| 1.5 | macOS: Settings window — General / Microphone / DualSense / Connection / Diagnostics tabs |
| 1.6 | A shared feature state machine (§7.0) — one type on both sides, with identical state names |

### Stage 2. Components

| # | Task |
|---|---|
| 2.1 | Build the 20 components from §6; on Windows as `Styles`/`UserControl`, on macOS as `View` |
| 2.2 | `LevelMeter`: add peak-hold and the asymmetric animation (§5.4, transition 8) |
| 2.3 | `EmptyState`, `InlineAlert`, `CheckRow`, `FingerprintLabel` — none of these exist on either platform today |
| 2.4 | The transition catalog from §5.4 — one at a time, each reviewed for whether it's needed at all |

### Stage 3. Screens by feature

| # | Task |
|---|---|
| 3.1 | Microphone screen: the five states in §7.2, all texts from §10 |
| 3.2 | DualSense screen: the five states in §7.3 |
| 3.3 | Settings per §7.4, with "Advanced" collapsed |
| 3.4 | Diagnostics: "Copy report" |

### Stage 4. Showcase 1 — DualSense visualization

| # | Task |
|---|---|
| 4.1 | Transport: an atomic `GamepadState` snapshot out of the HID stream, lock-free |
| 4.2 | Rendering scaffold: `CompositionCustomVisualHandler` on Windows, `Canvas` + `TimelineView(.animation(minimumInterval:paused:))` on macOS (§8.4, item 0) |
| 4.2a | The geometry from §8.2 as a cacheable static layer (`StreamGeometry`, rebuilt only on a size or theme change) |
| 4.3 | The dynamic layer from §8.3 |
| 4.4 | Frame-rate control per §8.4: 60/20/8/0 Hz by visibility and activity; smoothing filter |
| 4.5 | Both themes, §8.5 |
| 4.6 | The mini version for the macOS popover, §8.7 |
| 4.7 | Measure CPU, record the result in the document |

### Stage 5. Showcase 2 — pairing

| # | Task |
|---|---|
| 5.1 | The `hexbridge://pair?…` format and the key fingerprint (4 groups of 4 characters) |
| 5.2 | Windows: the "Pair a Mac" wizard, 4 steps, QR + short code + code expiry timer |
| 5.3 | Windows: the step 1 readiness checklist with fixes in place (driver, cable, firewall) |
| 5.4 | macOS: the "Connect to a PC" window, three methods |
| 5.5 | Discovery on the network (Bonjour/`_hexbridge._udp`) with 6-digit confirmation |
| 5.6 | QR scanning with the camera on macOS (`AVCaptureMetadataOutput`, macOS 13+), `NSCameraUsageDescription`, permission requested on tap |
| 5.7 | Connection check — the six items in §9.4, including the end-to-end audio test with the level **from Windows** |
| 5.8 | One-time exchange over the short code — a separate security task, the UX is settled |

### Stage 6. Polish

| # | Task |
|---|---|
| 6.1 | Proofread all the texts from §10 on live screens |
| 6.2 | Notifications from §10.5 with rate limiting. **On Windows, in the first version, only the tray icon and tooltip change**: Avalonia can't do system toasts, and `DesktopNotifications.Avalonia` registers an AUMID and a Start menu shortcut (§2.1.5) |
| 6.3 | Keyboard per §11.2, including the global mute hotkey |
| 6.4 | Run the §11.4 checklist, screenshots into `docs/screens/` |

### Deliberately out of scope

- **Faking Acrylic/Mica on Win10 via `SetWindowCompositionAttribute`.**
  Private API, unfixable drag lag, risk for the Store (§1.2).
  `AcrylicBlur` through the supported `TransparencyLevelHint` is fine, but only as
  an opportunistic improvement to the main window.
- **Lottie.** Skottie doesn't support expressions, effects, or blending modes;
  everything we need is a few dozen lines of stock transitions (§2.1.3).
- **Liquid Glass as the basis of the visual design.** At most
  `.buttonStyle(.glass)` on a single button, guarded by `if #available(macOS 26, *)`,
  and not even in the first version: on macOS 26 its hover is broken outside the
  toolbar (§1.1).
- **`MeshGradient` and decorative gradient backgrounds.** They raise the target to
  macOS 15, live in the content layer against the guidelines, and burn idle CPU in
  a utility app.
- **QR scanning with a camera on Windows.** Windows shows the code and the Mac
  reads it — no camera is needed on the PC, and no FlashCap/ZXing dependency gets
  added.
- **A custom icon set.** System icons on macOS, Fluent on Windows.
- **Windows system toasts** in the first version (§2.1.5).
- **Double-click on the tray icon** — Avalonia doesn't support it.
- **A custom `TabView`** — Avalonia 12 has no such control.
- A dark/light theme that doesn't follow the system by default. Sound effects.

## 13. Unconfirmed

This list is kept honestly: for these items the decision was made on indirect
evidence, and it needs to be re-verified on real hardware before release.

**Platforms**
1. The public release date of macOS 27 (14.09.2026) — from the press, not from
   Apple's site. The fact from developer.apple.com is only the RC build 26A428
   dated 09.09.2026.
2. `DWMWA_USE_IMMERSIVE_DARK_MODE` (value 20) on Windows 10 19045:
   Microsoft Learn gives Windows 11 build 22000 as the minimum, community practice
   says it works from Win10 2004 onward. There's no primary confirmation.
   We work around it with our own window title bar, but if the workaround doesn't
   hold, this item becomes critical.
3. Whether the menu bar height changed in Tahoe. The HIG still says 24 pt, and
   `NSStatusBar.thickness` is still documented as 22 px (the page hasn't been
   updated in years). Read `NSStatusBar.system.thickness` at runtime.
4. Whether a `MenuBarExtra(.window)` window has a vibrancy background of its own.
   The documentation says nothing. For a real `NSPopover` it's confirmed that it
   does (*"AppKit creates visual effect views automatically for… popovers.
   You don't need to add visual effect views"*).

**Libraries**
5. Which Avalonia 12.x release exactly the transparency regression #21082 was
   closed in. The issue was closed via PR #21354, and the release number doesn't
   follow from the ticket. → task −1.1.
6. Whether `Irihi.Ursa` 2.2.0 (built against 12.0.2) works on 12.1.2. → task −1.2.
7. Compatibility of `Svg.Controls.Skia.Avalonia` (SkiaSharp native assets 4.x)
   with `Avalonia.Skia 12.1.2` (SkiaSharp 3.119.4). We work around it by taking
   `Svg.Controls.Avalonia` without Skia — but if the Skia version turns out to be
   needed, it needs a smoke test.
8. Whether the problem of opening the Settings window from the menu bar was fixed
   on release macOS 26.x. The source (Steinberger) is dated to the beta period.
   → task −1.3.
9. `FAProgressRing` in FluentAvalonia was checked against `master`, not against the
   3.1.0 tag — the repository has no tags. Doesn't affect us, we aren't taking
   FluentAvalonia.

**APIs and behavior**
10. Whether `symbolEffect` respects the Reduce Motion setting automatically.
    The SF Symbols HIG contains not one mention of Reduce Motion.
    **We assume it doesn't**, and disable the effects explicitly.
11. Whether Liquid Glass reduces motion under Reduce Motion. Only its reaction to
    Reduce Transparency and Increase Contrast is confirmed.
12. The encoding of `inputMessage` in `CIQRCodeGenerator`: the legacy documentation
    says `NSISOLatin1StringEncoding`, the modern code sample says `.ascii`.
    **UTF-8 isn't mentioned by Apple at all.** We keep the payload in ASCII so the
    question never comes up.
13. macOS availability for `CIRoundedQRCodeGenerator` and for
    `CIQRCodeGenerator.correctionLevel` — it isn't stated on the pages.
14. Stale tray icon scaling when the taskbar is moved between monitors with
    different DPI (Avalonia handles `WM_DISPLAYCHANGE` but not `WM_DPICHANGED`).
    This is an inference from reading the source, not a filed bug.
15. Performance characteristics of `MeshGradient` — Apple documents nothing.
    Doesn't affect us, we don't use it.

**Legal**
16. Whether there's an EUIPO Registered Community Design on the DualSense — the
    EUIPO endpoints weren't responding. Given the registrations in TW/CA/UY it's
    very likely. It doesn't change our decision (§3.4).
17. We couldn't find a public standalone text of the SF Symbols license; the
    conclusion is drawn from the Xcode and Apple SDKs Agreement §2.10, which
    covers it.

**To be measured, not researched**
18. The real CPU load of the DualSense visualization at 60 Hz on the target PC and
    on a MacBook Air (budget in §8.4). Record the result in this document.
19. Popover height in every state of every feature — it must not jump by more than
    40 px between adjacent states (§11.4).
