# HexBridge — дизайн-система

Документ описывает интерфейс двух desktop-приложений одного продукта: клиента на
macOS (SwiftUI, menu bar) и приёмника на Windows 10 22H2 (Avalonia). Он написан
так, чтобы по нему можно было реализовывать, не принимая новых дизайн-решений:
все цвета даны значениями, все анимации — длительностями и кривыми, все пакеты —
версиями и лицензиями.

Дата проверки фактов: **10 сентября 2026**. Всё, что не удалось подтвердить
первоисточником, помечено явно.


---

## Содержание

| § | Раздел |
|---|---|
| 0 | [Принципы](#0-принципы) |
| 1 | [Платформенный контекст на сентябрь 2026](#1-платформенный-контекст-на-сентябрь-2026) |
| 2 | [Стек: библиотеки и версии](#2-стек-библиотеки-и-версии) |
| 3 | [Иконки, ассеты и лицензии](#3-иконки-ассеты-и-лицензии) |
| 4 | [Токены: цвет, типографика, сетка](#4-токены) |
| 5 | [Спецификация движения](#5-спецификация-движения) |
| 6 | [Компоненты](#6-компоненты) |
| 7 | [Экраны по фичам](#7-экраны-по-фичам) |
| 8 | [Вау №1 — живая визуализация DualSense](#8-вау-момент-1--живая-визуализация-dualsense) |
| 9 | [Вау №2 — первый запуск и связывание машин](#9-вау-момент-2--первый-запуск-и-связывание-машин) |
| 10 | [Пустые состояния, ошибки и подсказки](#10-пустые-состояния-ошибки-и-подсказки) |
| 11 | [Доступность и качество](#11-доступность-и-качество) |
| 12 | [План реализации](#12-план-реализации) |
| 13 | [Что не подтверждено](#13-что-не-подтверждено) |

---

## 0. Принципы

**Простота важнее богатства.** HexBridge — утилита рядом со стримом. Пользователь
открывает её, когда что-то не работает, и закрывает, когда заработало. Интерфейс,
который требует изучения, — провалившийся интерфейс.

Пять правил, которые разрешают споры:

1. **Одна фраза на экран.** В любом состоянии видно ровно одно предложение,
   отвечающее на вопрос «работает или нет». Всё остальное — мельче и ниже.
2. **Одна главная кнопка на экран.** Если кажется, что нужны две, — одна из них
   не главная.
3. **Ошибка = следующее действие.** Сообщение без кнопки, которая его чинит, —
   баг проектирования, а не текста.
4. **Анимация отвечает на вопрос «что изменилось».** Если пользователь и так это
   видит — анимации нет.
5. **Вау-эффект — в двух местах, не размазан.** Живая визуализация DualSense (§8)
   и мастер связывания двух машин (§9). Больше нигде «красиво» не является
   аргументом.

Оба вау-момента выбраны не по вкусу, а по функции: первый доказывает, что
контроллер читается, второй убирает главную боль продукта — ручную правку
конфигов. Они окупаются, даже если на них смотреть как на утилитарные фичи.

### Что считается провалом

- Пользователь не понял, работает микрофон или нет, за 2 секунды взгляда.
- Пользователь открыл настройки, чтобы понять, что делать при ошибке.
- Что-то мигает или дёргается, когда всё хорошо.
- Приложение жжёт CPU, когда окно закрыто.

---

## 1. Платформенный контекст на сентябрь 2026

### 1.1 macOS

**Версии.** Текущая стабильная — **macOS 26.6.2 Tahoe (25G83)**, 17.08.2026.
**macOS 27 «Golden Gate» RC (26A428) выложен разработчикам 09.09.2026**, публичный
релиз ожидается в течение недели. Актуальный Xcode — 26.6 (Swift 6.3), Xcode 27 RC
со Swift 6.4 уже доступен. macOS 27 не поддерживает Intel.

**Из этого следуют три практических вывода:**

1. **Минимальная цель — macOS 14.** Это порог, за которым доступен весь
   современный стек: `@Observable`, `SettingsLink`, `openSettings`, `symbolEffect`,
   `phaseAnimator`, `keyframeAnimator`, spring-пресеты `.smooth/.snappy/.bouncy`,
   `ShapeStyle.fill`, `NSColor.systemFill`. macOS 15 добавляет `MeshGradient`,
   `windowLevel`, `defaultLaunchBehavior`, `materialActiveAppearance` — берём
   их через `if #available`, но целью не делаем.
2. **Liquid Glass — прогрессивное улучшение, а не основа визуала.** Мы выпускаемся
   ровно на смене мажорной версии; строить внешний вид на API, которого нет
   у половины пользователей, нельзя.
3. **`UIDesignRequiresCompatibility` больше не спасёт.** Ключ, позволявший
   отказаться от нового дизайна, документирован так: *«The system ignores this key
   when you build for … macOS 27 or later»*. Аварийного выхода нет — интерфейс
   должен нормально выглядеть в новом языке дизайна сам по себе.

**Liquid Glass — что это в API (всё macOS 26.0+):**

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

AppKit-эквиваленты — `NSGlassEffectView`, `NSGlassEffectContainerView`,
`NSButton.BezelStyle.glass`, тоже macOS 26.0+.

**Где Liquid Glass применять НЕЛЬЗЯ — это цитаты, а не мнение.**
HIG «Materials»: *«Don't use Liquid Glass in the content layer… Instead, use standard
materials for elements in the content layer, such as app backgrounds»* и
*«Use Liquid Glass effects sparingly… Limit these effects to the most important
functional elements in your app»*. «Adopting Liquid Glass»: *«avoid overcrowding
or layering Liquid Glass elements on top of each other»*.

Popover меню-бара — это уже функциональный слой, система даёт ему фон.
Класть внутрь него `.glassEffect()` на карточки — ровно тот случай «glass on glass»
и «glass в content layer», который запрещён. **Внутри popover — стандартные
материалы.** Liquid Glass в HexBridge допустим ровно в одном месте: `.buttonStyle(.glass)`
или `.glassProminent` на главной кнопке действия, под `if #available(macOS 26, *)`.

⚠️ Известный баг macOS 26: кнопки `.glass`/`.glassProminent` вне тулбара не
показывают hover-состояние. Исправлено в macOS 27 (release notes). Значит на 26
главная кнопка должна оставаться обычной `.borderedProminent`, а glass включаться
только на 27+. Проще: не включать вовсе до следующей ревизии дизайна.

**HIG про menu bar extras — существенное:**

- *«The menu bar's height is 24 pt»* — единственное число на всей странице.
  **Рекомендованного размера иконки Apple не публикует.** Не хардкодить 18×18 из
  блогов: использовать `MenuBarExtra(_:systemImage:)` и дать системе выбрать.
- *«Both interface icons and symbols use black and clear colors… the system can
  apply other colors»* — иконка обязана быть **template image**. Красить её
  в зелёный/красный нельзя: в macOS 26 меню-бар полностью прозрачный, иконка
  лежит поверх произвольных обоев.
- *«Display a menu — not a popover — when people click your menu bar extra.
  Unless the app functionality you want to expose is too complex for a menu»*.

  **Мы выбираем `.window`, и это осознанное отступление.** Обоснование: в попапе
  живёт индикатор уровня звука в реальном времени и мини-визуализация контроллера.
  Меню (`NSMenu`) не умеет рисовать анимированный контент, а без индикатора
  уровня попап теряет главную функцию — за секунду ответить «звук идёт или нет».
  Это ровно тот случай «too complex for a menu», который HIG допускает.
- *«An app that only shows in the menu bar will be automatically terminated if the
  user removes the extra from the menu bar»* — надо предусмотреть: при
  `isInserted == false` показать окно настроек с объяснением, а не молча умереть.
- Страница HIG «The menu bar» **не обновлялась под Tahoe** (последняя правка
  09.06.2025) — новых правил для Liquid Glass в меню-баре нет.

**⚠️ macOS 27 прячет иконки в пунктах меню.** Release notes: *«In macOS 27.0, menu
bar and context menus present a reduced set of menu item images… By default,
NSMenu hides all menu item symbol images»*. SwiftUI ведёт себя так же. Наши
`Label("…", systemImage: "…")` в контекстных меню исчезнут. Возврат — `labelStyle(.titleAndIcon)`.
Проверить до релиза.

**HIG про Settings:** *«use a noncustomizable toolbar»*, *«Update the window's title
to reflect the currently visible pane»*, *«Restore the most recently viewed pane»*,
*«Minimize the number of settings you offer»*, *«Include a settings item in the App
menu»*. Sidebar в окне настроек HIG не упоминает — только toolbar с панелями.
Численных размеров окна нет; единственный ориентир из документации `Settings` —
`.frame(maxWidth: 350, minHeight: 100)` в примере Apple.

### 1.2 Windows 10 22H2

**Контекст поддержки.** Windows 10 вышла из поддержки 14.10.2025; 22H2 —
последняя версия. Потребительская программа ESU держит обновления безопасности
до **13.10.2026** — то есть примерно месяц от даты этого документа. Это не повод
отказываться от Win10 (целевая машина у заказчика именно такая), но повод не
принимать ни одного решения, которое сломается на Windows 11.

**Ключевое ограничение подтверждено первоисточником.** Microsoft Learn,
`DWMWINDOWATTRIBUTE`: **`DWMWA_SYSTEMBACKDROP_TYPE` (38) — «Minimum supported
client: Windows 11 Build 22621»**. Страница Mica: *«Mica is only available in
Windows 11 and later. If your app uses Mica and is installed on Windows 10, it
will not apply the material»*. Туда же — `DWMWA_WINDOW_CORNER_PREFERENCE` (33),
`DWMWA_BORDER/CAPTION/TEXT_COLOR` (34–36), `DWMWA_USE_HOSTBACKDROPBRUSH` (17):
все требуют build 22000+.

**Итог: ни Mica, ни системный Acrylic на Windows 10 недоступны.**

Что реально есть:

| `WindowTransparencyLevel` | Win10 22H2 | Win11 22621+ |
|---|---|---|
| `None`, `Transparent` | ✅ | ✅ |
| `Blur` | ❌ на Windows не поддерживается вообще | ❌ |
| **`AcrylicBlur`** | ✅ через `Windows.UI.Composition`, порог 10.0.15063 | ✅ |
| `Mica` | ❌ (порог 10.0.22000) | ✅ |

Плюс `ExperimentalAcrylicBorder` + `ExperimentalAcrylicMaterial` — акрил, который
Avalonia рисует сама, внутри приложения, без единого недокументированного API.

**Чего не делать: `SetWindowCompositionAttribute` / `ACCENT_ENABLE_ACRYLICBLURBEHIND`.**
Приватный экспорт `user32.dll`, вне SDK. Даёт документированный, неисправимый на
стороне приложения лаг при перетаскивании и ресайзе окна (баг самой Windows;
`framelesshelper#27` закрыт как won't-fix со словами мейнтейнера *«a bug of Windows
itself. Not fixable from my side»*; те же симптомы в FluentWPF#43, DevToys#1258).
Для MSIX — риск провала сертификации Store.

**Решение для HexBridge: непрозрачные тематические поверхности.** Блюр —
оппортунистическое улучшение, включаемое по `ActualTransparencyLevel`, и только
на главном окне, не на всплывающих панелях:

```xml
<Window TransparencyLevelHint="Mica,AcrylicBlur,None" Background="Transparent">
```

и стилизация от `ActualTransparencyLevel`: если пришло `None` — рисуем сплошной
`bg` из токенов. Для маленькой трей-панели блюр не даёт ничего: поверхность
маленькая, короткоживущая, обычно поверх сплошного таскбара.

**🔴 Тёмный заголовок окна Avalonia на Win10 не сделает.** `WindowImpl.SetFrameThemeVariant`
гейтит вызов условием `Build >= 22000`; гайд Avalonia прямо пишет: *«On Windows 10,
the title bar does not darken»*. Два выхода: P/Invoke `DwmSetWindowAttribute(hwnd, 20, …)`
самому (по консенсусу сообщества значение 20 работает с Win10 2004+, хотя Learn
указывает минимумом Win11 — **проверить на целевой машине**), либо
`WindowDecorations="None"` и собственный заголовок. **Берём второе:** заодно
снимается вопрос скруглений (на Win10 они мертвы) и появляется место под сводный
статус в шапке (§7.1).

⚠️ Перед этим проверить регрессию [Avalonia#21082](https://github.com/AvaloniaUI/Avalonia/issues/21082):
`ExtendClientAreaToDecorationsHint` + `WindowDecorations="None"` ломал прозрачность
(чёрный фон) начиная с 12.0.0-rc2. Issue закрыт через PR #21354, но в каком именно
релизе фикс — из тикета не следует. **Это первое, что надо проверить на 12.1.2.**

**Иконка в области уведомлений.** Microsoft Learn «Notifications and the Notification
Area»: класть **и 16×16, и 32×32** в один `.ico`, использовать `LoadIconMetric`.
Рекомендованные размеры по DPI: 96 dpi → 16, 120 → 20, 144 → 24, 192 → 32.
Avalonia уже делает DPI-подбор через `SHAppBarMessage(ABM_GETTASKBARPOS)` →
`GetDpiForMonitor`, и переподбирает на `WM_DISPLAYCHANGE` и `TaskbarCreated`.
Наша задача — положить многоразмерный `.ico`.
## 2. Стек: библиотеки и версии

Всё ниже проверено на nuget.org / GitHub / developer.apple.com **10.09.2026**.
Где проверить не удалось — сказано прямо.

### 2.0 Правило, которое определяет весь выбор под Avalonia

**Мажорные релизы Avalonia не бинарно совместимы.** Библиотека, собранная под
Avalonia 11, на Avalonia 12 даёт `TypeLoadException` — подтверждено мейнтейнером
в [discussion #21091](https://github.com/AvaloniaUI/Avalonia/discussions/21091).
NuGet при этом молча резолвит `Avalonia >= 11.3.x` на 12.1.2 и ничего не скажет.

Поэтому критерий отбора — **не «пакет жив», а «объявляет зависимость `Avalonia >= 12.x`»**.
Это отсекает большую часть популярных пакетов.

### 2.1 Windows: Avalonia

**Ядро — подтверждено.**

```xml
<PackageReference Include="Avalonia" Version="12.1.2" />          <!-- MIT, 2026-09-02 -->
<PackageReference Include="Avalonia.Desktop" Version="12.1.2" />
```

Windows 10 22H2 (build 19045) x64 в [официальной таблице платформ](https://docs.avaloniaui.net/docs/supported-platforms)
имеет статус **Tier 2** — поддерживается. Минимум .NET для desktop — 8.0; проект
уже на `net10.0`, что дополнительно расширяет выбор пакетов.

**Важные отличия 12 от 11, которые затронут разметку:**
- Direct2D-бэкенд удалён, только Skia.
- Оконный хром переписан: `TitleBar`/`CaptionButtons`/`ChromeOverlayLayer` удалены →
  `WindowDrawnDecorations`; `SystemDecorations` → **`WindowDecorations`**;
  `ExtendClientAreaChromeHints` удалён; появились `Window.WindowDecorationsTheme`
  и `Window.IsExtendedIntoWindowDecorations`.
- Compiled bindings включены по умолчанию; `IBinding` → `BindingBase`.
- `IDataObject` → `IAsyncDataTransfer` (буфер обмена).
- **`CubicBezierEasing` удалён** → `SplineEasing`.
- Style-анимации по умолчанию встают на паузу у невидимых контролов (см. 2.1.4).

#### 2.1.1 Тема и контролы — Semi.Avalonia + Ursa

```xml
<PackageReference Include="Semi.Avalonia" Version="12.1.0.1" />        <!-- MIT, 2026-07-31 -->
<PackageReference Include="Irihi.Ursa" Version="2.2.0" />              <!-- MIT, 2026-07-31 -->
<PackageReference Include="Irihi.Ursa.Themes.Semi" Version="2.2.0" />  <!-- MIT -->
```

**Semi.Avalonia 12.1.0.1** — то, что уже стоит в проекте, и это правильный выбор.
Требует `Avalonia >= 12.1.0`, TFM `net8.0`/`net10.0`, MIT, автор irihiTech.
⚠️ README на GitHub устарел (показывает совместимость с 11.3.7) — верить NuGet.

Доп. пакеты того же автора, версия 12.1.0.1, MIT: `Semi.Avalonia.DataGrid`,
`Semi.Avalonia.ColorPicker`; `Semi.Avalonia.TreeDataGrid` — 12.0.0.
⚠️ `Semi.Avalonia.Dock`, `.AvaloniaEdit`, `.ProDataGrid` — **бесплатны, но не
open source** (README: *«delivered via nuget for free, but not open source»*).
Нам они не нужны. `Semi.Avalonia.Tabalonia` — только Avalonia 11, **не брать**.

**Ursa (`Irihi.Ursa` 2.2.0)** — компаньон-библиотека контролов от тех же авторов,
полностью MIT. **Она нужна, потому что Semi — это тема, а не набор контролов.**
Из неё берём:
- **`NavMenu`** — боковая навигация с иерархией, сворачиванием в rail с тултипами,
  header/footer и клавиатурной навигацией. Ровно наш §7.1.
- **`Loading`, `LoadingContainer`, `LoadingIcon`, `Skeleton`** — в Semi их нет.
- `Banner` — инлайновое предупреждение (наш `InlineAlert`).
- `Dialog`, `Drawer`, `MessageBox` — мастер связывания.
- `QRCode` — можно попробовать вместо своего контрола, но проверить, умеет ли он
  задавать уровень коррекции и белую подложку.
⚠️ `Irihi.Ursa` 2.2.0 объявляет `Avalonia >= 12.0.2`, а не 12.1.x — пересобран под
12.1 он не был. Резолвится нормально, но это место для smoke-теста.

**Почему не FluentAvalonia.** `FluentAvaloniaUI` 3.1.0 (2026-08-22, MIT) жив и
объявляет `Avalonia >= 12.1.0` — то есть технически подходит. Отказ по двум причинам:
1. Он воспроизводит WinUI/Fluent, а этот язык дизайна построен на Mica/Acrylic-подложках,
   которых на Windows 10 **физически нет**. Приложение будет выглядеть недоделанным
   Windows 11, а не законченным приложением.
2. TFM только `net10.0` (у Semi и Ursa есть `net8.0`) — сужает пространство манёвра,
   если однажды понадобится опуститься.

**Почему не Material.Avalonia.** `Material.Avalonia` 3.20.0 (2026-09-06, MIT,
`Avalonia >= 12.1.1`) — самый свежий из трёх и вполне живой. Но Material Design —
чужеродный язык для трей-утилиты под Windows.

**Альтернатива без Ursa**, если захочется минимизировать зависимости: в Avalonia 12
появился встроенный `DrawerPage` с `DrawerLayoutBehavior="CompactInline"` — это и
есть rail-навигация из коробки (`IsOpen`, `DrawerLength` 320, `CompactDrawerLength` 48,
`DrawerBreakpointLength`, режимы `Overlay`/`Split`/`CompactOverlay`/`CompactInline`).
⚠️ Но в штатной теме Fluent у `DrawerPage` **ноль** `Transition`/`Animation` —
анимацию открытия придётся писать самому. Плюс не будет `Skeleton`/`Loading`.
Вывод: Ursa окупается.

⚠️ **`TabView` в Avalonia 12 не существует** — вопреки блог-посту. Есть `TabbedPage`
(внутри обычный `TabControl`). `TabView` есть только в FluentAvalonia.

#### 2.1.2 Иконки под Windows — важная поправка к брифу

**Lucide и Phosphor под Avalonia 12 недоступны.** Все кандидаты объявляют
зависимость от Avalonia 11 и, по правилу 2.0, не загрузятся:

| Пакет | Версия | Объявляет | Вердикт |
|---|---|---|---|
| `Lucide.Avalonia` | 0.2.21 (2026-09-06) | `Avalonia >= 11.3.17` | ❌ |
| `LucideAvalonia` | 1.6.2 (2026-03-22) | `Avalonia >= 11.1.0-beta1` | ❌ |
| `IconPacks.Avalonia.Lucide` | 2.0.0 | `Avalonia >= 11.0.13` | ❌ |
| `IconPacks.Avalonia.PhosphorIcons` | 2.0.0 | Avalonia 11 | ❌ |

⚠️ README `MarwanFr/LucideAvaloniaUI` утверждает совместимость с Avalonia 12,
но версии выше 1.6.2 на NuGet **не опубликовано** — main-ветка ушла вперёд релиза.
Не полагаться.

**Также отпал `Projektanker.Icons.Avalonia`** (9.6.2, 2025-05-07, Avalonia 11.2.8):
проект заброшен, Projektanker GmbH ликвидирована. Преемник — тот же код у другой
компании, **XAML-namespace сохранён**, миграция сводится к смене C#-namespace:

```xml
<PackageReference Include="Optris.Icons.Avalonia" Version="12.0.7" />               <!-- MIT -->
<PackageReference Include="Optris.Icons.Avalonia.FontAwesome7" Version="12.0.7" />
<PackageReference Include="Optris.Icons.Avalonia.MaterialDesign" Version="12.0.7" />
```

**Выбор для HexBridge — Fluent System Icons:**

```xml
<PackageReference Include="FluentIcons.Avalonia" Version="2.1.339.1" />  <!-- MIT, 2026-09-01 -->
```

`Avalonia >= 12.0.0`, TFM `net10.0` (проект уже на net10.0 — подходит), MIT.
Даёт `<FluentIcon>`/`<SymbolIcon>` и `<FluentIconSource>`. Сам набор
`microsoft/fluentui-system-icons` — MIT, официального NuGet у Microsoft нет,
это community-обёртка.

Причина выбора: набор родной для Windows по рисунку, покрывает всё нужное
(микрофон, микрофон-выкл, геймпад — `ic_fluent_games_*`, сеть, щит, ключ, ссылка,
QR), и это единственный из «современных» наборов с рабочим пакетом под Avalonia 12.

⚠️ **В Fluent System Icons нет PlayStation-символов** (△○✕□) — только Xbox-стилистика.
Кнопки DualSense рисуются примитивами (§8.2) или берутся из Kenney (§3.3).

**Если понадобится иконка, которой нет в Fluent:** Lucide под ISC, а Phosphor под
MIT — обе лицензии разрешают просто взять SVG и вшить path-данные в
`StreamGeometry` в `ResourceDictionary`. Пакет для этого не нужен, а зависимость
на Avalonia 11 — не появится.

#### 2.1.3 Lottie — не берём, и вот почему

**Пакета `Avalonia.Lottie` на nuget.org не существует** (404). Репозиторий
`AvaloniaUI/Avalonia.Lottie` **заархивирован 09.06.2023**. Живые варианты есть:

```xml
<PackageReference Include="Avalonia.Labs.Lottie" Version="12.0.2" />  <!-- MIT, owner avaloniaui -->
<!-- либо -->
<PackageReference Include="Lottie" Version="12.0.0" />                <!-- MIT, W. Šoltés, богаче API -->
```

Оба работают на Avalonia 12 (`Avalonia >= 12.0.1` / `>= 12.0.0`), тянут
`SkiaSharp.Skottie` ветки **3.119.x**.
⚠️ `SkiaSharp.Skottie` 4.x брать нельзя: `Avalonia.Skia 12.1.2` собрана под
`SkiaSharp >= 3.119.4`, а 4.x — мажор со сменой assembly version.

**Решение: Lottie в HexBridge не используется.** Обоснование:
1. Ограничения Skottie жёсткие: не поддерживаются **expressions**, эффекты из меню
   Effects (Drop Shadow, Colour Overlay), **blending modes**, luma mattes; текстовые
   слои — с оговорками; градиенты при неверной настройке деградируют в заливку.
   Это значит, что анимацию нельзя просто «принести от дизайнера» — её надо
   принимать по превью именно в Skottie-плеере.
2. Всё, что нам нужно анимировать (спиннер, скелетон, галочка успеха, пульс
   ожидания), делается штатными `Transitions`/`Animation` за десятки строк.
3. Это лишняя зависимость и +несколько МБ ради декоративных иллюстраций,
   которые противоречат принципу §0.

Записано сюда, чтобы к вопросу не возвращались.

#### 2.1.4 Анимации — что реально есть в Avalonia 12

**Property transitions** (`Avalonia.Animation`, сборка `Avalonia.Base`), полный
список подтверждён по исходникам тега 12.1.2:
`BoolTransition`, `BoxShadowsTransition`, `BrushTransition`, `ColorTransition`,
`CornerRadiusTransition`, `DoubleTransition`, `EffectTransition`, `FloatTransition`,
`IntegerTransition`, `PointTransition`, `RelativePointTransition`, `SizeTransition`,
`ThicknessTransition`, `TransformOperationsTransition`, `VectorTransition`.
У каждого — `Property`, `Duration`, `Delay`, `Easing`.

⚠️ `TransformTransition` и `RectTransition` **не существуют**. `Rotate3DTransition`
лежит в папке Transitions, но это page-transition (наследник `PageSlide`).
Новых transition-классов в 12 не появилось.

**Keyframe-анимации** — `Animation` с `Duration`, `IterationCount`,
`PlaybackDirection` (`Normal`/`Reverse`/`Alternate`/`AlternateReverse`),
`FillMode` (`None`/`Forward`/`Backward`/`Both`), `Easing`, `Delay`,
`DelayBetweenIterations`, `SpeedRatio`, `RunAsync(Animatable, CancellationToken)`.

**🆕 `Animation.PlaybackBehavior` — новое в 12** ([PR #20820](https://github.com/AvaloniaUI/Avalonia/pull/20820)):

| Значение | Поведение |
|---|---|
| `Auto` (по умолчанию) | ручные и таргетящие `IsVisible` играют всегда; **style-анимации ставятся на паузу, когда контрол не `IsEffectivelyVisible`** |
| `Always` | как в 11 |
| `OnlyIfVisible` | пауза, когда не виден |

Это работает нам на руку: зацикленный пульс ожидания сам встанет, когда раздел
не выбран. ⚠️ Гейт по `IsEffectivelyVisible` — любой скрытый предок ставит на
паузу; `Opacity="0"` — не ставит.

**Easings — 33 штуки**: `LinearEasing` + триплеты In/Out/InOut для Sine, Quadratic,
Cubic, Quartic, Quintic, Exponential, Circular, Back, Elastic, Bounce, плюс
**`SplineEasing`** и **`SpringEasing`** (`Mass`, `Stiffness`, …).

**Важно: пружина в Avalonia есть.** `SpringEasing` — штатный класс, эмулировать
её keyframes не нужно.

**PageTransitions**: `CrossFade` (`Duration`, `FadeInEasing`, `FadeOutEasing`),
`PageSlide` (`Orientation` через `SlideAxis`, `SlideInEasing`, `SlideOutEasing`, `FillMode`),
`CompositePageTransition`, `Rotate3DTransition`. Дефолт в теме Fluent есть только
у `NavigationPage` (`PageSlide` 0:0:0.3). У `TabbedPage`, `CarouselPage`, `DrawerPage` — нет.

**Composition API** (`Avalonia.Rendering.Composition[.Animations]`) — **стабилен**:
атрибута `[Unstable]` под `src/Avalonia.Base/Rendering/Composition` нет ни одного.
`ElementComposition.GetElementVisual/SetElementChildVisual`, `CompositionVisual`,
`ImplicitAnimationCollection`, `ExpressionAnimation`, `Compositor.CreateAnimationGroup()`,
`CreateCustomVisual()`. Анимируемые свойства визуала: `Opacity`, `Offset`,
**`Translation` (новое в 12)**, `Size`, `Scale`, `RotationAngle`, `Orientation`, `CenterPoint`.
⚠️ Класс называется `ElementComposition`, хотя файл до сих пор `ElementCompositionPreview.cs`.
⚠️ `Offset`/`Scale` — `Avalonia.Vector3D`, не `System.Numerics.Vector3`.

**Готовые лоадеры и скелетоны:**

| Что | Где |
|---|---|
| `ProgressBar.IsIndeterminate` | Avalonia core ✅ |
| `ProgressRing` как контрол | **в core нет** |
| `ProgressRing` как ControlTheme | Semi: `<ProgressBar Theme="{DynamicResource ProgressRing}"/>` ✅ |
| `Loading`, `LoadingContainer`, `LoadingIcon`, **`Skeleton`** | **Ursa** ✅ |
| Skeleton в Semi | нет |

**🔴 «Уменьшить анимацию» — API в Avalonia нет.** Проверено исчерпывающе:
`IPlatformSettings` содержит только tap/doubletap/hold/hotkey/`GetColorValues`;
`PlatformColorValues` — `ThemeVariant`, `ContrastPreference`, `AccentColor1..3`.
Поиск по `ReducedMotion`, `PrefersReducedMotion`, `AnimationsEnabled`,
`SPI_GETCLIENTAREAANIMATION` даёт ноль совпадений. Открытый
[issue #19405](https://github.com/AvaloniaUI/Avalonia/issues/19405) от 05.08.2025
без движения.

**Значит, делаем сами** — P/Invoke, и слушаем `WM_SETTINGCHANGE`:

```csharp
const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
[DllImport("user32.dll", SetLastError = true)]
static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);
```

Гейтить централизованно: один `IReducedMotionProvider`, который подменяет ресурсы
`Duration` на `0:0:0` или переключает style-класс на корне окна.
⚠️ `Animation.PlaybackBehavior` здесь **не помогает** — он про видимость, не про доступность.

#### 2.1.5 Трей — что умеет и чего не умеет

`Avalonia.Controls.TrayIcon`, объявляется attached-свойством `TrayIcon.Icons`
**на `Application`** (иначе `InvalidOperationException`).

Работает: `Icon` (меняется в рантайме ✅), `ToolTipText` ✅, `IsVisible` ✅,
`Menu` (`NativeMenu`) ✅, `Command`/`CommandParameter` (левый клик), событие `Clicked`.
DPI-подбор размера иконки реализован.

**🔑 Находка, которая экономит работу: на Windows меню трея — не нативное Win32-меню.**
`TrayIconImpl.OnRightClicked()` создаёт настоящее безрамочное Avalonia-окно
(`WindowDecorations.None`, `Topmost`, закрывается по `Deactivated`) с
`TrayIconMenuFlyoutPresenter : MenuFlyoutPresenter` внутри. **Значит наши обычные
`ControlTheme`/`Style` для `MenuFlyoutPresenter`, `MenuItem` и `Separator`
применяются к меню трея.** Сторонние библиотеки для кастомного вида не нужны.

`NativeMenuItem` поддерживает `Header`, `Icon`, `ToolTip`, `Gesture` (только подпись),
**`IsChecked` (two-way)**, **`ToggleType`**, `Command`, вложенное `Menu`, плюс
`NativeMenuItemSeparator`. То есть галочки-тумблеры фич в меню трея (§7.1) реализуемы штатно.

**🔴 Чего нет:**
- **Balloon/toast из трея.** `UpdateIcon` никогда не выставляет `NIF_INFO`;
  запрос [#6734](https://github.com/AvaloniaUI/Avalonia/issues/6734) закрыт как
  **not planned**. → Однократная подсказка «свернулось в трей» из §7.1 делается
  **`WindowNotificationManager`** (внутриприложенческий тост) в момент сворачивания,
  пока окно ещё видно. Настоящие системные тосты (§10.5) — через
  `DesktopNotifications.Avalonia`, но у него побочка: регистрирует AUMID и ярлык
  в меню «Пуск». **Решение: системные уведомления в первой версии не делаем**,
  ограничиваемся сменой иконки в трее и тултипом.
- **Двойной клик** — `WM_LBUTTONDBLCLK` не обрабатывается ([#10269](https://github.com/AvaloniaUI/Avalonia/issues/10269)).
  → В §7.1 «повторный левый клик сворачивает» реализуем через одиночные клики.
- Средний клик, hover, отдельное событие правого клика — нет.

#### 2.1.6 Кастомное рисование

Три уровня:

1. **`Control.Render(DrawingContext)`** — UI-поток, инвалидация `InvalidateVisual()`
   или `AffectsRender<T>()`. Подходит для `LevelMeter`, `Sparkline` (уже так сделано).
2. **`DrawingContext.Custom(ICustomDrawOperation)`** — рендер-поток, прямой `SKCanvas`
   через `ISkiaSharpApiLeaseFeature`. ⚠️ Обходит кэширование scene graph.
3. **`CompositionCustomVisualHandler` + `compositor.CreateCustomVisual(handler)`** —
   per-frame коллбэки `OnRender`/`OnMessage` **на рендер-потоке**, следующий кадр
   запрашивается `RequestNextFrameRendering()`. Документация Avalonia прямо
   называет это целевым для *«real-time visualizations, game loops, video rendering»*.

**Для визуализации DualSense (§8) — вариант 3.** Это единственный из трёх, который
даёт собственный кадровый цикл, не заставляя UI-поток пересобирать scene graph
каждые 16 мс. Готовая обёртка, если не хочется писать самим: `CompositionAnimatedControl`
из [wieslawsoltes/Lottie](https://github.com/wieslawsoltes/Lottie) (MIT, `Avalonia >= 12.0.0`).

**SVG.** ⚠️ `Avalonia.Svg` и `Avalonia.Svg.Skia` 11.3.0 — **deprecated**. Преемники:

```xml
<PackageReference Include="Svg.Controls.Avalonia" Version="12.0.0.17" />  <!-- MIT, БЕЗ SkiaSharp -->
```

Берём именно версию без Skia: `Svg.Controls.Skia.Avalonia` тянет
`SkiaSharp.NativeAssets.Linux >= 4.148.0`, а `Avalonia.Skia 12.1.2` собрана под
`SkiaSharp 3.119.4` — NuGet выберет 4.x, и это мажорное расхождение.
**Совместимость этой пары не проверена.** Для наших задач SVG-рендер вообще не
обязателен: иконки идут пакетом, контроллер рисуется примитивами.

#### 2.1.7 Итоговый csproj для Windows

```xml
<ItemGroup>
  <PackageReference Include="Avalonia" Version="12.1.2" />
  <PackageReference Include="Avalonia.Desktop" Version="12.1.2" />
  <PackageReference Include="Avalonia.Fonts.Inter" Version="12.1.2" />   <!-- fallback, см. §4.2 -->
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
  <PackageReference Include="Semi.Avalonia" Version="12.1.0.1" />
  <PackageReference Include="Irihi.Ursa" Version="2.2.0" />
  <PackageReference Include="Irihi.Ursa.Themes.Semi" Version="2.2.0" />
  <PackageReference Include="FluentIcons.Avalonia" Version="2.1.339.1" />
  <PackageReference Include="Net.Codecrete.QrCodeGenerator" Version="3.2.1" />
</ItemGroup>
```

Все — MIT. Добавляется четыре пакета к тому, что уже есть.

**Переопределение токенов Semi** — через `ThemeDictionaries`, как уже сделано
в `App.axaml`. Ключи Semi именуются `Button[State][Variant]Foreground` и т.п.;
наши собственные токены (§4.1) живут рядом в тех же словарях. Работает и
локально, в `Resources` любого контейнера.

### 2.2 macOS: SwiftUI

**Система даёт почти всё.** Ниже — что берём системным и почему сторонних
решений почти не нужно.

| Задача | Решение | Почему не библиотека |
|---|---|---|
| Наблюдаемая модель | `@Observable` (macOS 14+) | Уже используется. Обновляет вью только при чтении конкретного свойства, в отличие от `ObservableObject` |
| Menu bar | `MenuBarExtra` (macOS 13+) + `LSUIElement` | Штатная сцена |
| Настройки | `Settings` scene (macOS 11+) + `SettingsLink` (macOS 14+) | — |
| Автозапуск | **`SMAppService.mainApp`** (macOS 13+) | См. ниже |
| Анимация иконки | `symbolEffect` (macOS 14+) | — |
| Живая визуализация | `Canvas` + `TimelineView` (macOS 12+) | См. §8.4 |
| QR | `CIFilter.qrCodeGenerator()` (CoreImage) | — |
| Сканирование QR | `AVCaptureMetadataOutput` (**macOS 13+**) | — |

**LaunchAtLogin не нужен.** `sindresorhus/LaunchAtLogin-Modern` — v1.1.0 от
**21.12.2023**, последний коммит январь 2024; `LaunchAtLogin` переименован в
`-Legacy` и **архивирован**. Всё, что они делали, — обёртка над `SMAppService`:

```swift
try SMAppService.mainApp.register()            // macOS 13+
try SMAppService.mainApp.unregister()
SMAppService.mainApp.status                    // .notRegistered / .enabled / .requiresApproval / .notFound
SMAppService.openSystemSettingsLoginItems()
```

Стоит позаимствовать из библиотеки два приёма: определение «запущены ли мы при
входе» (`NSAppleEventManager` + `keyAELaunchedAsLogInItem`) и обработку
«уже зарегистрирован → unregister → register».

**Библиотеки, которые действительно оправданы — три.**

```
sparkle-project/Sparkle            2.9.6   (17.08.2026, push 09.09.2026)
sindresorhus/KeyboardShortcuts     3.0.1   (17.06.2026)
orchetect/MenuBarExtraAccess       1.3.1   (05.08.2026)
```

**Sparkle 2.9.6** — обновления вне App Store, альтернатив нет. Очень живой проект:
три security-релиза за 2026 (symlink-уязвимость, privilege escalation).
⚠️ **Лицензия не «просто MIT»**: MIT-текст плюс раздел EXTERNAL LICENSES для
вендоренных `bsdiff` (BSD-2-clause) и `sais-lite`. Прикладывать LICENSE целиком.
🎯 Релиз 2.9.4 (03.07.2026) содержит починку *«activation fix for backgrounded /
dockless applications»* — это баг ровно LSUIElement-приложений.
Сидеть на 2.9.6 (min macOS 10.13); переход на 2.10 — осознанно: там min macOS 12
и удалена поддержка CocoaPods.
Требует EdDSA: `generate_keys` → `SUPublicEDKey` в Info.plist, `SUFeedURL`,
инкрементирующийся `CFBundleVersion`, appcast через `generate_appcast`.

**KeyboardShortcuts 3.0.1** — глобальный хоткей мьюта (§11.2). Нативный
`.keyboardShortcut()` работает только когда приложение в фокусе; глобальные хоткеи —
это Carbon `RegisterEventHotKey`, и библиотека делает это **без запроса
Accessibility-разрешений**. Даёт `KeyboardShortcuts.Recorder` — готовый контрол
записи сочетания в настройках.
⚠️ `swift-tools-version:6.2` → нужен тулчейн Xcode 26+.

**MenuBarExtraAccess 1.3.1 — нужен, и это проверено.** Вопрос «умеет ли SwiftUI
программно открывать/закрывать MenuBarExtra» имеет ответ **нет**: в SDK macOS 27
у `MenuBarExtra` восемь инициализаторов, и `isPresented` среди них нет. Есть только
`isInserted`, документированный как *«Whether the item is inserted in the menu bar»* —
это присутствие иконки, не открытое состояние. В `updates/swiftui` за июнь 2025
и июнь 2026 `MenuBarExtra` не упоминается вовсе.
Библиотека даёт `.menuBarExtraAccess(isPresented:) { statusItem in }` и
`.introspectMenuBarExtraWindow { window in }` без private API.
⚠️ Работает только со стилем `.window` — при `.menu` SwiftUI блокирует runloop.
Нам подходит: мы и так на `.window`.
⚠️ **Риск: bus factor = 1**, зависит от внутренностей SwiftUI, каждый сентябрь —
окно простоя (история: 1.2.1 «not yet compatible with macOS 26 beta» → 1.2.2 →
1.3.1 «Support for macOS 27 beta 4»). **План Б записать сразу:** свой `NSStatusItem`
+ `NSPanel`, ~150 строк, полный контроль. На macOS 27 он всё равно понадобится —
там появился `NSStatusItem.expandedInterfaceDelegate`/`expandedInterfaceSession`,
без которого у кастомного статус-айтема ломается клавиатурный фокус.
*(«SwiftUI menu bar extras do a lot of this work for you» — WWDC26 session 289;
то есть пока мы на SwiftUI, это не наша забота.)*

**Что НЕ берём:**
- `sindresorhus/Settings` (3.1.1, май 2024) — `Settings` scene + `SettingsLink` закрывают задачу.
- `orchetect/SettingsAccess` — при таргете macOS 14+ не нужен; его README сам
  признаёт, что `openSettingsLegacy()` не работает в `.menu`-стиле.
- `lfroms/fluid-menu-bar-extra` — **архивирован 20.01.2026**.
- `sindresorhus/Defaults` (9.0.9, живой) — по ситуации. `@AppStorage` умеет
  только `Bool/Int/Double/String/URL/Data/RawRepresentable` (+`Date` с macOS 15)
  и работает только внутри View, а у нас конфиг живёт в JSON и читается из сервисов.
  У нас уже есть свой `Config` — этого достаточно.
- Backport-шимов Liquid Glass под macOS нет и быть не может.

**🔴 Заложить время: открытие окна настроек из меню-бара.** `SettingsLink` и
`@Environment(\.openSettings)` (оба macOS 14+) решили факт открытия, но не всё:
`openSettings()` требует существующего SwiftUI render tree — вызов из `AppDelegate`
или из handler'а глобального хоткея не делает ничего; `SettingsLink` из
`MenuBarExtra` открывает окно, но **не активирует приложение**, и оно уезжает
за чужие окна ([forums thread 731628](https://developer.apple.com/forums/thread/731628),
официального ответа Apple нет). Рабочий обход: скрытое окно-носитель SwiftUI-контекста,
объявленное **до** сцены `Settings`; временное `NSApp.setActivationPolicy(.regular)`
перед вызовом с возвратом в `.accessory` после; развязка через `NotificationCenter`.
Известен и `NSApplication.activate()` (macOS 14+).
⚠️ Исправлено ли это на релизной macOS 26.x — **не подтверждено**, проверять на стенде.

**Полезные scene-модификаторы для LSUIElement:** `defaultLaunchBehavior(_:)`
(macOS 15+, чтобы окно не открывалось при старте), `restorationBehavior(_:)`
(15+), `windowLevel(_:)` (15+), `windowResizeAnchor(_:)` (26+ — прямо релевантно
попапу, который меняет высоту), `openWindow` (13+) / `dismissWindow` (14+).

**Нотаризация обязательна.** Всё, собранное после 01.06.2019 и распространяемое
с Developer ID, должно быть нотаризовано: подпись Developer ID, Hardened Runtime,
secure timestamp, без `com.apple.security.get-task-allow`, `notarytool` (altool
не принимается с 01.11.2023), затем `xcrun stapler staple`. Для Sparkle —
подписать и нотаризовать **включая вложенные XPC-сервисы Sparkle**, и отдельно
DMG/ZIP, на который указывает appcast.
## 3. Иконки, ассеты и лицензии

### 3.1 Иконки — почему наборы разные на двух платформах

**SF Symbols нельзя использовать на Windows.** Xcode and Apple SDKs Agreement,
rev EA2002 от 06.08.2026, **§2.10 System-Provided Images**, дословно:

> «The system-provided assets (e.g., images, symbols) owned by Apple… are licensed
> to You **solely for the purpose of developing Applications for Apple-branded
> products that run on the system for which the image was provided**. You agree
> that you shall not use or incorporate the System-Provided Images **or any
> substantially or confusingly similar images** into app icons, logos or make any
> other trademark use…»

То есть нельзя ни взять сами символы, ни перерисовать «похожие», чтобы обойти.

Поэтому:

| Платформа | Набор | Лицензия |
|---|---|---|
| macOS | **SF Symbols 7** (системный) | Xcode SLA §2.10 — только внутри UI на платформах Apple |
| Windows | **Fluent System Icons** через `FluentIcons.Avalonia` 2.1.339.1 | MIT |
| Добор недостающего на Windows | Lucide (ISC) или Phosphor (MIT) — SVG вручную в `StreamGeometry` | ISC / MIT |

Про SF Symbols: стабильная версия — **7**; SF Symbols 8 (анонс WWDC26) на
10.09.2026 всё ещё в бете. Системный каталог macOS 26.6 содержит ~9 184 имени,
из них ~7 988 базовых.

⚠️ **605 символов имеют ограничения** вида «may only be used to refer to Apple's
iPhone» (файл `symbol_restrictions.strings`). Проверено: **ни один нужный нам
символ в этот список не входит**, включая `personalhotspot` и
`antenna.radiowaves.left.and.right`, которые часто ошибочно считают ограниченными.

Символы, которые используем (все проверены по системному каталогу macOS 26.6):

| Роль | SF Symbol | macOS с |
|---|---|---|
| Микрофон / выключен | `mic.fill`, `mic.slash.fill` | 10.15 |
| Микрофон недоступен | `mic.badge.xmark` | 13.0 |
| Геймпад | `gamecontroller.fill` | 10.15 |
| Сеть | `network`, `wifi`, `wifi.exclamationmark` | 11.0 / 10.15 |
| Сети нет | `network.slash` | 14.0 |
| Ключ | `key.horizontal.fill` | 13.0 |
| Защита | `lock.shield.fill` | 10.15 |
| Связывание | `link.circle.fill` | 10.15 |
| QR | `qrcode`, `qrcode.viewfinder` | 10.15 |
| Индикатор уровня | `waveform`, `waveform.slash` | 10.15 |

### 3.2 Единая семантика форм

Иконки на двух платформах разные по рисунку — это нормально и правильно. Но
**форма, обозначающая состояние, должна совпадать**, потому что это часть языка
статусов (§4.1):

| Состояние | Форма | macOS | Windows (Fluent) |
|---|---|---|---|
| Всё хорошо | круг с галочкой | `checkmark.circle.fill` | `ic_fluent_checkmark_circle_24_filled` |
| Внимание | треугольник | `exclamationmark.triangle.fill` | `ic_fluent_warning_24_filled` |
| Ошибка | восьмиугольник/круг с крестом | `xmark.octagon.fill` | `ic_fluent_dismiss_circle_24_filled` |
| Выключено | перечёркнутый круг | `circle.slash` | `ic_fluent_prohibited_24_regular` |

### 3.3 Ассеты DualSense — итог поиска

**Свободно лицензированного SVG-контура DualSense не существует.** Проверено:

- **Wikimedia Commons**: поиск по `filemime:image/svg+xml` и полный листинг
  `Category:DualSense` (35 файлов) — **ни одного SVG**. Только фото под CC BY-SA 4.0.
  SVG-схемы есть для DualShock 3/4 (CC BY 3.0, Tokyoship) — это другой контроллер.
- **Проекты-визуализаторы**: `nondebug/dualsense` — лицензии нет и графики нет
  (это WebHID-инспектор репортов, полезен как справочник по байтам);
  gamepadviewer.com — не open source, PS5-скина нет; `e7d/gamepad-viewer` — код MIT,
  но арт «reworked from assets originally created by mrmcpowned for gamepadviewer.com»,
  происхождение грязное; DS4Windows — GPL-3.0 и DualSense-арта не содержит;
  DualSenseX — лицензии нет; Steam Input glyphs — гранта на редистрибуцию нет,
  и PlayStation-глифов в публичном ZIP нет.

**Что есть свободного и пригодного:**

| Ассет | Лицензия | Что внутри |
|---|---|---|
| [Xelu's FREE Controller Prompts](https://thoseawesomeguys.com/prompts/), вектор — [haaldor/Xelu_prompts_SVG](https://github.com/haaldor/Xelu_prompts_SVG) | **CC0-1.0** (файл LICENSE) | группа `inkscape:label="Playstation_5"`, 136 путей: стики, D-pad, L1/L2/R1/R2, mute, Create/Options |
| [DJLink/Xelu_Free_Controller-Key_Prompts](https://github.com/DJLink/Xelu_Free_Controller-Key_Prompts) | **CC0-1.0** | `PS5/PS5_Diagram.png`, `PS5_Diagram_Simple.png` — контурная схема ⚠️ **с PS-вордмарком в центре, его надо удалить** |
| [Kenney Input Prompts](https://kenney.nl) 1.5 | **CC0** (`License.txt` в архиве) | 1504 SVG, из них `PlayStation Series/Vector/` — 136 файлов: `playstation5_button_create/options/mute`, `playstation5_touchpad`, `playstation_button_color_cross/circle/square/triangle`, `playstation_trigger_l1/l2/r1/r2` |
| [PromptFont](https://codeberg.org/shinmera/promptfont) | **SIL OFL 1.1** | глифы: Square `U+21E0`, Triangle `U+21E1`, Circle `U+21E2`, Cross `U+21E3`, L1–R2 `U+21B0…21B3`, DualSense Touchpad `U+2207`, DualSense Options `U+2208`. Просьба об атрибуции |

⚠️ **Simple Icons — не источник для логотипа PlayStation.** Репозиторий под CC0,
но `DISCLAIMER.md` дословно: *«Simple Icons is released under CC0 — though that
doesn't mean to imply that all icons within the project are also CC0… We ask that
our users seek the correct permissions»*. У записи PlayStation в
`simple-icons.json` поля `license` **нет**.

### 3.4 Юридическая сторона и итоговое решение

**Промышленные образцы подтверждены.** У Sony Interactive Entertainment есть
кластер US design patents с приоритетом **03.04.2020** (за четыре дня до анонса
DualSense): `USD933750S1`, `USD933751S1`, `USD954710S1`, `USD954838S1`,
`USD958891S1`, `USD958892S1`, `USD977576S1`, `USD984536S1` («Housing for game
controller» / «Controller for electronic device»), плюс `USD990569S1`/`USD990570S1`
на сами стики. Международно — TWD216292S–216297S, CA199206S, UY4827S.
Чертёж `USD933750S1` — partial design claim: *«The broken lines… depict portions
of the housing… that form no part of the claimed design»*, то есть защищены
конкретные обводы корпуса, а не «геймпад вообще».

**Товарные знаки подтверждены.** [Copyright and Trademark Notice](https://www.playstation.com/en-us/legal/copyright-and-trademark-notice/):
*«PlayStation, PS5, … **DualSense**, DUALSHOCK, … are registered trademarks or
trademarks of Sony Interactive Entertainment Inc.»* — и там же **«PlayStation
Shapes Logo»**, то есть сама комбинация △ ○ ✕ □ заявлена как товарный знак.

**Решение: рисуем свою схематичную абстракцию (§8.1–8.2).**

Обоснование:
1. Дизайн-патент нарушается, когда «ordinary observer» спутает **изделия** — это
   про изготовление и продажу, а не про иллюстрацию в UI. Риск от схемы низкий.
   Но фотореалистичный рендер, максимально близкий к патентному чертежу, — худший
   из возможных вариантов, и именно его мы не делаем.
2. △ ○ ✕ □ как обозначение кнопок — классический **nominative fair use**.
   Логотип PlayStation в интерфейсе или в иконке приложения — уже нет.
3. Готового свободного вектора всё равно нет, а разбирать чужой SVG на именованные
   слои дороже, чем нарисовать параметрически (§8.1).

**Гигиена, обязательная к реализации:**
- В «О программе» — строка: «DualSense и PlayStation — товарные знаки Sony
  Interactive Entertainment Inc. HexBridge не связан с Sony Interactive
  Entertainment и не одобрен ею.»
- Не использовать «PlayStation», «PS5» или «DualSense» в названии приложения,
  в имени исполняемого файла, в домене и в иконке.
- Иконка приложения и иконка трея — брендо-нейтральный геймпад из Fluent/Lucide,
  не силуэт DualSense.
- Не брать арт из gamepadviewer.com и производных, не переупаковывать глифы Valve,
  не брать логотип PlayStation из Simple Icons.

### 3.5 QR-коды

**Windows — генерация:**

```xml
<PackageReference Include="Net.Codecrete.QrCodeGenerator" Version="3.2.1" />
```

MIT, 2026-09-07, TFM `net6.0`/`netstandard2.0`, **ноль транзитивных зависимостей**.
Даёт ровно то, что нужно Avalonia:

```csharp
public bool   GetModule(int x, int y)
public string ToGraphicsPath(int border = 0)          // SVG-совместимый path → Geometry.Parse()
public IReadOnlyList<QrPolygon> ToOutlines()          // единый контур без hairline-щелей
public byte[] ToPngBitmap(...)                        // без единой имиджинговой библиотеки
```

`ToGraphicsPath()` скармливается в `Geometry.Parse(...)` и рисуется одним `<Path>` —
собственный рендер модулей писать не нужно.
`ToOutlines()` специально сделан против щелей при антиалиасинге — брать его.

**🔴 Почему не QRCoder.** `QRCoder` 1.8.0 (MIT, живой, репозиторий переехал
`codebude/QRCoder` → `Shane32/QRCoder`) имеет публичный `QRCodeData.ModuleMatrix`,
и сам генератор не содержит ни одного упоминания `System.Drawing`. **Но** самый
старший ассет в пакете — `lib/net6.0`, а его `.nuspec` объявляет
`<dependency id="System.Drawing.Common" version="6.0.0" />`. Проект на net10.0
резолвит именно эту группу и получает System.Drawing в граф.
⚠️ В версиях 1.5.1 и 1.6.0 группа `net6.0` была пустая — зависимость **вернули**
в 1.7.0, так что распространённое «на net6+ QRCoder чистый» устарело.
`QRCoder.Xaml` — WPF, для Avalonia не подходит.

**⚠️ SkiaSharp-биндинги не нужны и опасны.** `ZXing.Net.Bindings.SkiaSharp` требует
SkiaSharp 4.151.1, `SkiaSharp.QrCode` — 4.148.0, а Avalonia 12.x тянет 3.119.4.
NuGet поднимет Skia до 4.x под Avalonia, которая под неё не собиралась.

**Windows — сканирование: не делаем.** По схеме §9 Windows **показывает** QR,
а Mac его читает. Значит камера на Windows не нужна, и `FlashCap` + `ZXing.Net`
в зависимости не попадают. *(Если однажды понадобится: FlashCap 1.12.0 Apache-2.0,
чистый managed, net10.0, есть официальный Avalonia-сэмпл; декодер — ZXing.Net
0.16.11 Apache-2.0 через `BarcodeReaderGeneric.Decode(byte[], w, h, BitmapFormat)`,
без SkiaSharp.)*

**macOS — генерация:**

```swift
import CoreImage.CIFilterBuiltins
let g = CIFilter.qrCodeGenerator()          // macOS 10.15+ (сам фильтр — с 10.9)
g.message = payload.data(using: .ascii)!
g.correctionLevel = "M"                     // L 7% / M 15% (default) / Q 25% / H 30%
let image = g.outputImage!
    .transformed(by: CGAffineTransform(scaleX: 8, y: 8))
    .samplingNearest()                      // macOS 10.13+ — без этого QR размоется
```

⚠️ **Размер вывода — один пункт на модуль**, масштабировать обязательно.
⚠️ **Кодировка: у Apple расхождение.** Legacy Filter Reference говорит
`NSISOLatin1StringEncoding`, современный пример кода — `.ascii`.
**UTF-8 в документации Apple не упоминается вообще.** Наш payload (§9.1) —
base64url + IP + порт — целиком ASCII, поэтому берём `.ascii` и в имя ПК
пропускаем только ASCII, транслитерируя остальное.

**macOS — сканирование:**

```swift
AVCaptureMetadataOutput()                            // ⚠️ macOS 13.0+ (на iOS с 6.0)
output.metadataObjectTypes = [.qr]                   // .qr доступен с macOS 10.15
AVCaptureMetadataOutputObjectsDelegate
  → metadataOutput(_:didOutput:from:)
  → AVMetadataMachineReadableCodeObject.stringValue
```

⚠️ `metadataObjectTypes` **выбрасывает `NSException`**, если присвоить тип,
отсутствующий в `availableMetadataObjectTypes`. Всегда проверять
`availableMetadataObjectTypes.contains(.qr)` сначала.
⚠️ Требуется `NSCameraUsageDescription` в Info.plist (macOS 10.14+) —
**без него `requestAccess` бросает исключение**. Запрашивать доступ через
`AVCaptureDevice.requestAccess(for: .video)` и **только в момент нажатия
«Отсканировать код»**, не при запуске приложения.

Запасной путь (и он же для чтения QR со скриншота): `VNDetectBarcodesRequest`
(macOS 10.13+) с `symbologies = [.qr]` → `VNBarcodeObservation.payloadStringValue`.
⚠️ Ловушка: *«Setting the revision on the request resets the symbologies»* —
сначала revision, потом symbologies.
Современный Swift-native вариант — `DetectBarcodesRequest` (macOS 15+).
## 4. Токены

Токены — единственный источник цвета, размера и времени. В коде не должно остаться
ни одного литерала вида `#RRGGBB`, `12`, `0.25s` вне таблиц ниже.

Именование одинаковое на обеих платформах, чтобы правки переносились глазами:
`bg`, `surface`, `surfaceAlt`, `border`, `borderStrong`, `text`, `textDim`,
`accent`, `ok`, `warn`, `bad`, `off` (+ суффиксы `Fg`/`Bg`/`Border` у состояний).

### 4.1 Цвет

#### Светлая тема

| Токен | HEX | Назначение |
|---|---|---|
| `bg` | `#F4F6F9` | фон окна |
| `surface` | `#FFFFFF` | карточка, попап, поле ввода |
| `surfaceAlt` | `#F8FAFC` | плитка внутри карточки, зебра в списке |
| `border` | `#E2E6ED` | декоративная граница карточки |
| `borderStrong` | `#7E8899` | граница интерактивного контрола, фокус-кольцо |
| `text` | `#1B2330` | основной текст |
| `textDim` | `#5C6675` | подпись, единицы, вторичное |
| `accent` | `#1D65C4` | основная кнопка, ссылка, активный пункт навигации |
| `accentSoft` | `#E9F1FD` | подложка активного пункта, мягкий бейдж |
| `accentText` | `#1A5AAC` | текст на `accentSoft` |
| `okFg` | `#189055` | индикатор «всё хорошо» |
| `okText` | `#136B41` | текст на `okBg` |
| `okBg` | `#E7F6EE` | подложка статуса |
| `okBorder` | `#B4E0C9` | граница статуса |
| `warnFg` | `#B87D00` | индикатор «внимание» |
| `warnText` | `#7A4E00` | текст на `warnBg` |
| `warnBg` | `#FDF3E0` | |
| `warnBorder` | `#F0DCB0` | |
| `badFg` | `#C62F2F` | индикатор «ошибка» |
| `badText` | `#9E2B2B` | текст на `badBg` |
| `badBg` | `#FCECEC` | |
| `badBorder` | `#F1C6C6` | |
| `off` | `#7E8899` | «выключено», нейтральный серый индикатор |
| `offBg` | `#EEF1F5` | подложка выключенного |
| `meterTrack` | `#E4E8EF` | дорожка индикатора уровня |
| `overlayScrim` | `rgba(17,22,30,0.32)` | затемнение под модалкой |

#### Тёмная тема

| Токен | HEX | Назначение |
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
| `accentInk` | `#0F1216` | текст **на** заливке `accent` (в тёмной теме тёмный, не белый) |
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

#### Проверка контраста (WCAG 2.1)

Считано по формуле относительной яркости WCAG. Порог для текста — **4.5:1** (AA),
для нетекстовых элементов интерфейса (индикаторы, границы контролов, фокус) — **3:1**.

Светлая тема:

| Пара | Контраст | Итог |
|---|---|---|
| `text` на `surface` | 15.79 | AAA |
| `text` на `bg` | 14.59 | AAA |
| `textDim` на `surface` | 5.81 | AA |
| `textDim` на `bg` | 5.37 | AA |
| `textDim` на `surfaceAlt` | 5.56 | AA |
| `accent` на `surface` | 5.66 | AA |
| белый на `accent` | 5.66 | AA |
| `okText` на `okBg` | 5.87 | AA |
| `warnText` на `warnBg` | 6.54 | AA |
| `badText` на `badBg` | 6.47 | AA |
| `accentText` на `accentSoft` | 5.95 | AA |
| `okFg` на `surface` (индикатор) | 4.07 | ≥3 ✓ |
| `warnFg` на `surface` (индикатор) | 3.52 | ≥3 ✓ |
| `badFg` на `surface` (индикатор) | 5.46 | ≥3 ✓ |
| `off` на `surface` (индикатор) | 3.58 | ≥3 ✓ |
| `borderStrong` на `surface` | 3.58 | ≥3 ✓ |
| `border` на `surface` | 1.25 | декоративная, текста не несёт |

Тёмная тема:

| Пара | Контраст | Итог |
|---|---|---|
| `text` на `surface` | 13.20 | AAA |
| `textDim` на `surface` | 6.33 | AA |
| `textDim` на `surfaceAlt` | 5.79 | AA |
| `accent` на `surface` | 5.74 | AA |
| `accentInk` на `accent` | 6.77 | AA |
| `okText` на `okBg` | 8.17 | AAA |
| `warnText` на `warnBg` | 8.27 | AAA |
| `badText` на `badBg` | 8.02 | AAA |
| `accentText` на `accentSoft` | 7.87 | AAA |
| `okFg` на `surface` | 8.23 | ≥3 ✓ |
| `warnFg` на `surface` | 8.58 | ≥3 ✓ |
| `badFg` на `surface` | 6.61 | ≥3 ✓ |
| `off` на `surface` | 4.23 | ≥3 ✓ |
| `borderStrong` на `surface` | 3.33 | ≥3 ✓ |
| `borderStrong` на `surfaceAlt` | 3.04 | ≥3 ✓ |

Скрипт проверки в репозитории не хранится; при изменении любого цвета контраст
пересчитывается по формуле относительной яркости WCAG и таблица правится. Правило: **цвет никогда
не единственный носитель смысла** — рядом со цветным индикатором всегда есть текст
статуса и/или иконка разной формы (точка / треугольник / крест).

#### Семантика состояний — единая на обе фичи

| Состояние | Токены | Иконка (macOS / Windows) | Смысл |
|---|---|---|---|
| Всё хорошо | `okFg` / `okBg` / `okBorder` | `checkmark.circle.fill` / `checkmark_circle` | канал живой, данные идут |
| Внимание | `warnFg` / `warnBg` / `warnBorder` | `exclamationmark.triangle.fill` / `warning` | работает, но не так, как надо (хост молчит, мьют, потери) |
| Ошибка | `badFg` / `badBg` / `badBorder` | `xmark.octagon.fill` / `dismiss_circle` | не работает, нужно действие пользователя |
| Выключено | `off` / `offBg` / `border` | `circle.slash` / `prohibited` | фича выключена сознательно, это не проблема |
| Ожидание | `textDim` + анимация | `ellipsis.circle` / spinner | переходное, до 10 с |

**Мьют — это `warn`, а не `bad`.** Пользователь сам нажал мьют, это не поломка,
но это состояние, о котором надо помнить.

### 4.2 Типографика

Системный шрифт на каждой платформе, без веб-шрифтов.

- **macOS**: SF Pro (`.system`). Цифры телеметрии — `.monospacedDigit()`, чтобы
  строка не дёргалась. Логи и ключи — `.system(.body, design: .monospaced)` (SF Mono).
- **Windows 10**: `Segoe UI` — родной для Win10 (Segoe UI Variable есть только с Win11).
  Проект уже тянет `Avalonia.Fonts.Inter`; **её надо оставить только как fallback**
  для не-Windows отладки, а на Windows использовать Segoe UI. Моноширинный — `Consolas`.

Шкала. Одна шкала на обе платформы; на macOS маппится на стандартные роли, на Windows — на px.

| Роль | macOS | Windows (px / вес / интерлиньяж) | Где |
|---|---|---|---|
| `display` | `.system(size: 28, weight: .semibold)` | 26 / SemiBold / 32 | заголовок статус-карточки («Звук идёт») |
| `title` | `.title3` (≈15 pt semibold) | 18 / SemiBold / 24 | заголовок экрана, имя фичи в списке |
| `heading` | `.headline` | 15 / SemiBold / 20 | заголовок карточки |
| `body` | `.body` (13 pt) | 14 / Regular / 20 | основной текст, поля |
| `label` | `.callout` (12 pt) | 13 / Regular / 18 | подписи в формах |
| `caption` | `.caption` (10 pt) | 12 / Regular / 16 | подсказки, единицы |
| `section` | `.caption.weight(.semibold)` + `.textCase(.uppercase)` | 12 / SemiBold / 16, `letter-spacing: .04em`, CAPS | «СОЕДИНЕНИЕ», «КОДЕК» |
| `metric` | `.system(size: 22, weight: .semibold).monospacedDigit()` | 22 / SemiBold / 28, tabular | число на плитке телеметрии |
| `mono` | `.system(.caption, design: .monospaced)` | Consolas 12 / 18 | ключ, лог, адрес |

Правила:
1. Одновременно на экране не больше **четырёх** размеров.
2. Числа, которые обновляются чаще 1 Гц, всегда моноширинные цифры.
3. Заголовки не переносятся: если не влезло — сокращать текст, а не уменьшать кегль.
4. Максимальная ширина абзаца — **62 символа** (≈ 520 px в `body`).

### 4.3 Сетка, отступы, радиусы

База **4 px**. Разрешённые значения — только из ряда:

`space`: 2, 4, 6, 8, 12, 16, 20, 24, 32, 40, 48

| Токен | Значение | Где |
|---|---|---|
| `space.xs` | 4 | между иконкой и её подписью |
| `space.sm` | 8 | между контролами в строке |
| `space.md` | 12 | между строками внутри карточки |
| `space.lg` | 16 | между карточками |
| `space.xl` | 24 | внутренний паддинг страницы |
| `space.2xl` | 32 | отбивка крупных блоков |

Радиусы:

| Токен | Значение | Где |
|---|---|---|
| `radius.xs` | 4 | бейдж, чип |
| `radius.sm` | 6 | кнопка, поле ввода |
| `radius.md` | 8 | плитка |
| `radius.lg` | 12 | карточка |
| `radius.xl` | 16 | статус-карточка, модалка |
| `radius.pill` | 999 | индикатор уровня, тумблер |

На macOS вместо квадратных радиусов используются **`RoundedRectangle(cornerRadius:style: .continuous)`**
— это форма, к которой привязан весь остальной системный интерфейс.

Размеры окон и панелей:

| Поверхность | Размер |
|---|---|
| macOS popover (menu bar) | ширина **340**, высота по контенту, максимум 520 |
| macOS окно настроек | 620 × 480, не ресайзится по ширине |
| Windows главное окно | 1000 × 700, минимум 880 × 580 |
| Windows боковая навигация | ширина 216, свёрнутая 56 |
| Модалка мастера первого запуска | 720 × 520, фиксированная |

Тени. На Windows 10 тень — единственный способ отделить слой (Acrylic недоступен).

| Токен | Значение (light) | Значение (dark) |
|---|---|---|
| `shadow.card` | `0 1px 2px rgba(16,24,40,.06), 0 1px 3px rgba(16,24,40,.10)` | `0 1px 2px rgba(0,0,0,.32)` |
| `shadow.pop` | `0 8px 24px rgba(16,24,40,.14)` | `0 8px 24px rgba(0,0,0,.48)` |
| `shadow.modal` | `0 24px 48px rgba(16,24,40,.20)` | `0 24px 48px rgba(0,0,0,.60)` |

На macOS собственные тени **не рисуются**: popover и окно получают системную.
## 5. Спецификация движения

### 5.1 Философия

Анимация в утилите отвечает на один вопрос: **«что именно изменилось?»**
Если пользователь и так это видит — анимации нет. Бюджет движения на всё
приложение: около десяти именованных переходов, перечисленных ниже. Всё,
что не в таблице, не анимируется.

### 5.2 Кривые

| Токен | Эталон | macOS (SwiftUI) | Windows (Avalonia `Easing`) | Когда |
|---|---|---|---|---|
| `ease.standard` | ease-out, cubic | `.smooth(duration:)` | `CubicEaseOut` | появление, исчезновение, смена непрозрачности |
| `ease.emphasis` | ease-out, резче | `.snappy(duration:)` | `QuinticEaseOut` | переезд выделения, смена раздела |
| `ease.spring` | пружина, малый отскок | `.spring(duration:bounce: 0.15)` | `SpringEasing` | появление сущности, галочка успеха |
| `ease.linear` | линейная | `.linear` | `LinearEasing` | только непрерывные индикаторы: спиннер, уровень, спарклайн |

**SwiftUI — точные числа (проверено по документации Apple).** У `.smooth`,
`.snappy` и `.bouncy` одинаковые дефолты параметров (`duration: 0.5,
extraBounce: 0.0`), но **разный базовый отскок, и он задокументирован в описании
`extraBounce`**: у `.smooth` база 0, у `.snappy` — **0.15**, у `.bouncy` — **0.3**.
То есть `extraBounce` добавляется *сверх* базы пресета.
Отдельно: `.default` — это `spring(response: 0.55, dampingFraction: 1.0,
blendDuration: 0)`; до macOS 14 `.default` был `easeInOut`.
Весь bounce-API и тип `Spring` — **macOS 14.0+**.

Правило дисциплины: в коде используем **только** `.smooth(duration:)`,
`.snappy(duration:)` и `.spring(duration:bounce:)`. `.bouncy` не используем нигде —
отскок 0.3 в утилите неуместен.

**Avalonia — пружина есть.** `SpringEasing` со свойствами `Mass`, `Stiffness`
и др. — штатный класс `Avalonia.Animation.Easings`. Эмулировать её keyframes
не нужно.
⚠️ **`CubicBezierEasing` удалён в Avalonia 12** (был `[Obsolete]` в 11.3).
Если нужна произвольная кривая — `SplineEasing`.
⚠️ `CustomAnimatorBase<T>` удалён → `InterpolatingAnimator<T>`.

### 5.3 Длительности

| Токен | Значение | Правило |
|---|---|---|
| `dur.instant` | **0 мс** | смена значения телеметрии, нажатие кнопки геймпада |
| `dur.micro` | **120 мс** | hover, press, фокус, смена цвета точки состояния |
| `dur.short` | **180 мс** | появление и исчезновение элемента внутри экрана |
| `dur.base` | **240 мс** | смена состояния карточки, раскрытие блока |
| `dur.long` | **320 мс** | переход между разделами, показ модалки, галочка успеха |
| `dur.slow` | **480 мс** | только шаги мастера первого запуска |

Ничего длиннее 480 мс в приложении нет. Ничего короче 120 мс, кроме `instant`, тоже.

### 5.4 Каталог переходов

| # | Событие | Что двигается | Длительность | Кривая | Примечание |
|---|---|---|---|---|---|
| 1 | Окно/попап открывается | opacity 0→1, scale 0.98→1.0 | 180 | `standard` | на macOS попап анимирует система — свою анимацию не добавлять |
| 2 | Смена раздела в боковой навигации | контент: opacity 0→1 + сдвиг 8 px снизу | 240 | `emphasis` | старый контент исчезает за 120, новый появляется через 60 мс |
| 3 | Полоса выделения в навигации | translateY | 320 | `emphasis` | едет непрерывно, не гаснет и не появляется |
| 4 | Статус-карточка меняет состояние | цвет фона, цвет границы, цвет заголовка | 240 | `standard` | текст меняется через `contentTransition`, не «переезжает» |
| 5 | Смена текста статуса | crossfade | 180 | `standard` | macOS: `.contentTransition(.opacity)`; Windows: два `TextBlock` в `Panel` |
| 6 | Смена числа на плитке | посимвольный ролл цифр | 240 | `standard` | macOS: `.contentTransition(.numericText(value:))` ⚠️ работает **только внутри `withAnimation`/`.animation()`**; Windows — **без анимации**, аналога нет, ролл вручную не окупается |
| 7 | Смена иконки состояния | символ заменяется | 240 | `standard` | macOS: `.contentTransition(.symbolEffect(.replace.downUp))`; Windows: crossfade двух `Path` |
| 8 | Индикатор уровня звука | ширина заливки | **0 (нарастание) / 90 мс (спад)** | `linear` | нарастание мгновенное — иначе метр врёт про пик; спад плавный |
| 9 | Точка состояния «ждёт» | opacity 1.0 ↔ 0.45 | **1400 мс** цикл | `standard`, autoreverse | единственная зацикленная анимация в приложении. На Windows дефолтный `PlaybackBehavior.Auto` сам ставит её на паузу, когда раздел не виден |
| 10 | Появление ошибки | opacity 0→1 + высота 0→auto | 240 | `standard` | без «тряски», без красной вспышки |
| 11 | Раскрытие «Дополнительно» | высота + opacity | 240 | `standard` | |
| 12 | Модалка мастера | подложка 0→scrim за 180; окно scale 0.96→1.0, opacity 0→1 | 320 | `spring` | |
| 13 | Шаг мастера вперёд | старый: −24 px + fade out (200); новый: +24 px → 0 + fade in (280, задержка 80) | 480 общая | `emphasis` | назад — зеркально |
| 14 | Галочка «всё работает» | `trim` 0→1 по контуру | 320 | `spring` | + одна пульсация scale 1.0→1.06→1.0 за 240 |
| 15 | Спиннер | вращение | **900 мс** оборот | `linear` | появляется после 400 мс ожидания |
| 16 | Скелетон | градиентный блик слева направо | **1200 мс** цикл | `linear` | |
| 17 | Нажатие кнопки | scale 1.0→0.97 | 120 | `standard` | на macOS системная — своей не делать |
| 18 | Иконка меню-бара / трея меняет состояние | crossfade | 180 | `standard` | macOS: `symbolEffect(.replace)` |
| 19 | Контроллер: нажатие кнопки | заливка + scale 0.94 | **0 мс** | — | ввод не анимируется вообще |
| 20 | Контроллер: стик/триггер | положение | сглаживание фильтром, не анимацией | — | см. §8.4 |

### 5.5 Когда анимации быть НЕ должно

Жёсткие правила, нарушение — баг:

1. **Всё, что отражает физический ввод пользователя в реальном времени** — кнопки
   геймпада, стики, триггеры, индикатор уровня на нарастание. Задержка здесь
   читается как «лагает».
2. **Числа телеметрии, обновляющиеся чаще 2 раз в секунду.** Анимировать их —
   значит сделать нечитаемыми.
3. **Первый кадр после запуска приложения.** Окно и его содержимое появляются
   уже в конечном состоянии; вступительной анимации нет. Иначе холодный старт
   выглядит медленнее, чем он есть.
4. **Восстановление после ошибки в фоне.** Если связь пропала и вернулась, пока
   пользователь не смотрел, при возврате он видит готовое состояние, а не
   проигрывание истории.
5. **Список журнала.** Новые строки появляются без анимации.
6. **Смена темы.** Мгновенно. Кроссфейд всего окна — дорого и выглядит дёшево.
7. Одновременно на экране движется **не более двух** вещей. Если по таблице
   выходит больше — они выстраиваются в очередь через задержки.

### 5.6 Уменьшенное движение

Настройка читается **при каждом старте анимации**, а не один раз при запуске:
пользователь может включить её, не перезапуская приложение.

**macOS**
- `@Environment(\.accessibilityReduceMotion)` внутри View (macOS 10.15+);
- вне View — `NSWorkspace.shared.accessibilityDisplayShouldReduceMotion` (10.12+);
- изменение — `NSWorkspace.accessibilityDisplayOptionsDidChangeNotification`.
  🔴 **Подписываться нужно на `NSWorkspace.shared.notificationCenter`, а не на
  `NotificationCenter.default`** — Apple выделяет это отдельным Important-блоком:
  *«If you register using a different notification center, you won't receive the
  notification»*. `userInfo` у нотификации нет.
- Отдельно уважается `accessibilityReduceTransparency` (10.15+): при включённой
  настройке все материалы заменяются на непрозрачный `surface`.
- 🔴 **Автоматики нет.** В SwiftUI **не существует** API уровня `.animation()`
  или `Transaction`, который сам учитывал бы Reduce Motion. Официальный паттерн —
  читать env value и передавать `nil`: документация `animation(_:value:)` —
  *«If `animation` is `nil`, the view doesn't animate»*. `Transaction.disablesAnimations`
  для этого использовать не рекомендовано.
  Учитывает ли Reduce Motion сам `symbolEffect` — **не подтверждено**, считаем что нет.
- Официальная формулировка того, что делать, — в HIG Accessibility, раздел Cognitive:
  *«reducing automatic and repetitive animations… Tightening animation springs to
  reduce bounce effects… **Replacing transitions in x-, y-, and z-axes with fades
  to avoid motion**… Avoiding animating into and out of blurs»*. То есть подход
  «кроссфейд вместо перемещения» — буква гайдлайна, а не наша выдумка.

**Windows**
- 🔴 **API в Avalonia нет** (см. §2.1.4, issue #19405 открыт). Делаем сами:
  `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION = 0x1042, …)` — системный флаг
  «Показывать анимацию в Windows» (Параметры → Специальные возможности → Дисплей).
- Изменение — сообщение `WM_SETTINGCHANGE`, слушать обязательно: настройку
  меняют на лету.
- Один `IReducedMotionProvider`, который подменяет ресурсы `Duration` на `0:0:0`
  либо переключает style-класс на корне окна. Точечных проверок по коду быть не должно.

Что происходит при включённой настройке:

| Переход | Поведение |
|---|---|
| 1, 2, 10, 11, 12, 13 | заменяются на crossfade 120 мс |
| 3 (полоса навигации) | перескакивает мгновенно |
| 6, 7, 14, 18 | без анимации, значение меняется сразу |
| 9 (пульс ожидания) | **отключается**, точка просто статичная `warn` |
| 15 (спиннер) | остаётся — это индикатор, а не украшение; но 1400 мс вместо 900 |
| 16 (скелетон) | блик отключается, остаётся статичная серая заглушка |
| 8 (уровень), 19, 20 | **не меняются** — это данные, а не анимация |
| Наклон контура по гироскопу (§8.3) | отключается |

Правило формулируется так: уменьшенное движение убирает **декоративное**
движение и движение **по экрану**; оно не убирает отображение данных.
## 6. Компоненты

Полный список. Больше компонентов заводить нельзя — если чего-то не хватает,
сначала проверяется, не решается ли задача комбинацией существующих.

| # | Компонент | macOS | Windows | Назначение |
|---|---|---|---|---|
| 1 | `StatusCard` | `RoundedRectangle` + `VStack` | `Border.status` (уже есть) | одна крупная фраза о состоянии + подстрока |
| 2 | `StatusDot` | `Circle` 8 pt | `Ellipse.dot` (уже есть) | точка состояния в списках и шапке |
| 3 | `MetricTile` | `GroupBox` | `Border.tile` (уже есть) | подпись + крупное число |
| 4 | `LevelMeter` | есть | есть | уровень звука с peak-hold |
| 5 | `Sparkline` | `Canvas` | есть | минута истории одним росчерком |
| 6 | `FeatureRow` | `HStack` в popover | пункт `ListBox` | иконка + имя фичи + состояние + тумблер |
| 7 | `Toggle` | системный `Toggle` | `ToggleSwitch` (Semi) | включение фичи |
| 8 | `PrimaryButton` | `.buttonStyle(.borderedProminent)` | `Button.Primary` (Semi) | одно главное действие на экран |
| 9 | `SecondaryButton` | `.bordered` | `Button` | остальные |
| 10 | `InlineAlert` | `HStack` с фоном | **`Banner` (Ursa)** | ошибка/предупреждение внутри карточки |
| 11 | `EmptyState` | `VStack` по центру | `StackPanel` по центру | иконка 32 + заголовок + текст + одна кнопка |
| 12 | `KeyField` | `SecureField` + `Toggle` | `TextBox` + `ToggleButton` | ключ с маской, «Показать», «Скопировать» |
| 13 | `FingerprintLabel` | `Text.monospaced` | `TextBlock.mono` | `A1F2 · 9C40 · 77BE · D103` |
| 14 | `CheckRow` | `Label` + символ | `Grid` | строка чек-листа: иконка состояния + текст + действие |
| 15 | `StepDots` | `HStack` кругов | `ItemsControl` | прогресс мастера |
| 16 | `QRPanel` | `Image` из `CIFilter.qrCodeGenerator()` | `Path` из `QrCode.ToOutlines()` | QR на белой подложке (§3.5) |
| 17 | `ControllerView` | `Canvas` + `TimelineView` | `CompositionCustomVisualHandler` | живая визуализация DualSense (§8) |
| 18 | `Spinner` | `ProgressView()` | **`LoadingIcon` (Ursa)** или `<ProgressBar Theme="{DynamicResource ProgressRing}"/>` (Semi) | ожидание > 400 мс |
| 19 | `Skeleton` | `Rectangle` + `.shimmer` | **`Skeleton` (Ursa)** | заглушка карточки при первой загрузке |
| 20 | `LogList` | `Table` | `ItemsRepeater` + `ScrollViewer` | журнал, моноширинный, виртуализованный |

Готовое из библиотек (§2.1.1) закрывает 5 позиций из 20: `NavMenu` (боковая
навигация), `Banner` (10), `LoadingIcon`/`Loading` (18), `Skeleton` (19),
`Dialog`/`Drawer`/`MessageBox` (мастер §9). Остальное — либо системное, либо
15–60 строк собственного кода. Ни одна из позиций не требует библиотеки,
которой нет под Avalonia 12.

### 6.1 Правила использования

- На экране **ровно одна** `PrimaryButton`. Если кажется, что нужны две — одна из
  них не главная.
- `EmptyState` всегда несёт действие. Пустое состояние без кнопки — это ошибка проектирования.
- `Spinner` не показывается раньше **400 мс** ожидания: короткая операция, успевшая
  завершиться, не должна мигать индикатором.
- `Skeleton` — только там, где известна форма будущего контента (плитки, список).
  В остальных местах — `Spinner`.
- `InlineAlert` живёт внутри карточки, к которой относится. Общие ошибки —
  в `StatusCard` шапки, не размазываются по экрану.
- Тумблер фичи — **единственный** способ включить и выключить фичу. Кнопок
  «Старт»/«Стоп» рядом с ним быть не должно.
## 7. Экраны по фичам

### 7.0 Модель состояний — общая

Обе фичи описываются одним автоматом. Это важно: пользователь учит правила один раз.

```
выключено ──(вкл)──▶ запускается ──▶ ждёт вторую сторону ──▶ работает
    ▲                     │                  │                   │
    └──────(выкл)─────────┴──────────────────┴───────────────────┘
                          │
                          ▼
                       ошибка ──(исправить)──▶ запускается
```

Пять состояний, одинаковых для микрофона и для DualSense:

| Код | Заголовок в UI | Цвет | Что видит пользователь |
|---|---|---|---|
| `off` | «Выключено» | `off` | фича выключена тумблером |
| `starting` | «Запускается» | `textDim` | до 10 с, потом → `error` |
| `waiting` | «Ждёт Windows» / «Ждёт контроллер» | `warn` | наша сторона готова, вторая нет |
| `live` | «Звук идёт» / «Контроллер проброшен» | `ok` | всё работает |
| `error` | конкретная формулировка ошибки | `bad` | нужно действие |

Мьют — подсостояние `live` с цветом `warn` (см. 4.1).

**Правило одной строки.** В любом состоянии в шапке видно ровно одно предложение,
которое отвечает на вопрос «работает или нет». Всё остальное — ниже и мельче.

### 7.1 Оболочка

#### Windows — главное окно

```
┌────────────────────────────────────────────────────────────────────┐
│ [лого] HexBridge          ● Всё работает            [Пауза]  [⋯]   │  56 px, surface, border снизу
├──────────────┬─────────────────────────────────────────────────────┤
│ ФИЧИ         │                                                     │
│ ● Микрофон   │           контент выбранного раздела                │
│   Звук идёт  │           padding 24, max-width 820                 │
│              │                                                     │
│ ● DualSense  │                                                     │
│   Проброшен  │                                                     │
│              │                                                     │
│ ──────────   │                                                     │
│   Соединение │                                                     │
│   Журнал     │                                                     │
│   Настройки  │                                                     │
│              │                                                     │
│ [Windows 10] │                                                     │
└──────────────┴─────────────────────────────────────────────────────┘
   216 px
```

Боковая навигация — **`NavMenu` из Ursa** (§2.1.1), а не `TabControl`: нужен
двухстрочный пункт, rail-режим и клавиатурная навигация из коробки.

- Пункт фичи — **две строки**: имя (`title`) + текущее состояние (`caption`, цвет статуса),
  слева точка-индикатор 8 px. Высота пункта 52, паддинг `12/16`.
- Пункты «Соединение», «Журнал», «Настройки» — однострочные, высота 36.
- Активный пункт: подложка `accentSoft`, слева вертикальная полоса 3 px `accent`,
  радиус `radius.sm`. Полоса **едет** между пунктами (см. 5.4).
- Разделитель между группами — `border` 1 px + `space.md` вокруг.
- Внизу — строка версии и «Windows 10 22H2» мелким `caption` `textDim`.

Шапка окна:
- Слева лого 24 px и «HexBridge».
- По центру — **сводный статус**: точка + одно предложение. Сводка = худшее из
  состояний включённых фич (`error` > `waiting` > `live` > `off`).
- Справа — одна главная кнопка: «Пауза» когда что-то работает, «Запустить» когда нет.
  Кнопка `accent`-заливкой. И `⋯` — меню (О программе, Открыть журнал, Выход).

#### Windows — трей

- Иконка 16/20/24/32 px в одном `.ico`. Avalonia сама подбирает размер по DPI
  монитора с таскбаром и переподбирает на `WM_DISPLAYCHANGE` и перезапуск explorer.
  Четыре варианта — `idle` (контур), `live` (заливка `okFg`), `warn`, `error`.
  Иконки различимы **формой**, не только цветом: `live` — сплошная,
  `warn` — с точкой в углу, `error` — с косым крестом.
  `TrayIcon.Icon` меняется в рантайме — отдельного трюка не нужно.
- Тултип (`ToolTipText`) — одна строка: `HexBridge — звук идёт, контроллер проброшен`.
- Контекстное меню (правая кнопка), пункты в этом порядке:
  `Открыть` · `—` · `Микрофон ✓` · `DualSense ✓` · `Заглушить микрофон` · `—` · `Журнал` · `Выход`.
  Галочки — `NativeMenuItem.ToggleType` + `IsChecked` (two-way), это тумблеры фич:
  выключить фичу можно, не открывая окно.
  🔑 **На Windows меню трея у Avalonia — не нативное Win32-меню, а обычное окно
  с `MenuFlyoutPresenter` внутри** (§2.1.5). Значит наши `ControlTheme` для
  `MenuFlyoutPresenter`/`MenuItem`/`Separator` к нему применяются, и меню трея
  выглядит как остальное приложение без единой сторонней библиотеки.
- Левый клик — показать окно и вывести на передний план.
  ⚠️ **Двойной клик Avalonia не поддерживает** (`WM_LBUTTONDBLCLK` не обрабатывается) —
  на него ничего не вешаем. «Свернуть повторным кликом» реализуется на одиночном
  клике: если окно уже активно — свернуть.
- Закрытие окна по «крестику» **сворачивает в трей**.
  ⚠️ **Balloon-подсказок из трея в Avalonia нет** (`NIF_INFO` не выставляется,
  запрос закрыт как not planned). Поэтому подсказка «свернулось в трей» —
  внутриприложенческий тост через **`WindowNotificationManager`**, показанный
  в момент сворачивания, пока окно ещё на экране, один раз за установку:
  «HexBridge продолжит работать в области уведомлений. Выход — правой кнопкой по иконке».

#### macOS — menu bar

- `MenuBarExtra` со `.menuBarExtraStyle(.window)` (уже так) — попап, а не меню.
  HIG требует обратного (*«Display a menu — not a popover»*), но делает исключение
  для функциональности, которая «too complex for a menu». Наш случай — ровно он:
  в попапе живой индикатор уровня и мини-визуализация контроллера, а `NSMenu`
  анимированный контент рисовать не умеет. Обоснование зафиксировано в §1.1,
  чтобы к нему не возвращались.
  ⚠️ `MenuBarExtra` не имеет API для программного открытия — только `isInserted`
  (присутствие иконки). Для программного показа нужен `MenuBarExtraAccess` (§2.2).
  ⚠️ Если пользователь уберёт иконку из меню-бара, LSUIElement-приложение
  **система завершит**. Обработать: при `isInserted == false` показать окно
  настроек с объяснением.
- Иконка меню-бара — **template image**, монохром. Размер **не хардкодим**:
  Apple рекомендованного размера не публикует (единственное число в HIG — «the
  menu bar's height is 24 pt»), поэтому используем `MenuBarExtra(_:systemImage:)`
  и даём системе подобрать. Символ меняется по сводному состоянию:
  `waveform` (работает) / `waveform.slash` (мьют) / `exclamationmark.triangle` (ошибка) /
  контурный `waveform` с пониженной непрозрачностью (выключено).
  Цветом иконка меню-бара **не красится** — это нарушение HIG и она станет нечитаемой
  на цветных обоях; состояние передаётся формой символа.
- Ширина попапа 340. Структура сверху вниз:
  1. Сводная строка: символ + состояние + адрес хоста мелким.
  2. Карточка «Микрофон»: индикатор уровня, кнопка мьюта, выбор входа.
  3. Карточка «DualSense»: мини-контур контроллера (см. 8), имя пада, батарея.
  4. Разделитель.
  5. `Настройки…` (`SettingsLink`) · `Выход`.
- Карточка выключенной фичи схлопывается в одну строку с тумблером.

#### macOS — окно настроек

Стандартная сцена `Settings` с **тулбаром и панелями** — HIG прямо это требует
(*«use a noncustomizable toolbar»*) и sidebar в окне настроек не упоминает вовсе.
Панели: **Общие · Микрофон · DualSense · Соединение · Диагностика**.
Внутри — `Form { Section { … } }` со `.formStyle(.grouped)`. Ничего кастомного:
окно настроек утилиты должно выглядеть как окно настроек любой другой утилиты.

Ещё три требования HIG, которые надо реализовать явно:
- **Заголовок окна меняется на имя активной панели.**
- **Восстанавливается последняя открытая панель.**
- Кнопки свернуть/развернуть — приглушены.

🔴 Открытие этого окна из меню-бара — известная нерешённая проблема SwiftUI,
время на неё заложено отдельно (§2.2).

### 7.2 Микрофон

Экран (Windows) / карточка (macOS) состоит из блоков сверху вниз:

1. **Статус-карточка** (`radius.xl`, паддинг 22/20, цвет по состоянию).
2. **Индикатор уровня** — всегда, даже в мьюте (в мьюте — серый, но живой).
3. **Плитки телеметрии** — 4 штуки: пакетов/с, RTT, аптайм, буфер.
4. **Спарклайн** — минута истории уровня или потерь.
5. **Соединение** — адрес, устройство ввода, кодек, ключ (маска).

#### Состояния

| Состояние | Заголовок | Подзаголовок | Что показано | Действие |
|---|---|---|---|---|
| **Не настроен** | «Микрофон не настроен» | «Укажите адрес приёмника и общий ключ — это делается один раз.» | пустое состояние вместо телеметрии, иллюстрация-схема Mac→ПК | **Настроить** (запускает мастер из §9) |
| **Ждёт хост** | «Ждёт Windows» | «Звук отправляется на `192.168.1.10:47702`, но приёмник не отвечает.» | индикатор уровня живой; плитка RTT — «—»; чек-лист причин | **Проверить связь**, «Открыть журнал» |
| **Идёт звук** | «Звук идёт» | «Игры видят его как `Steam Streaming Microphone`.» | всё; плитки зелёные | **Заглушить** |
| **Мьют** | «Микрофон заглушен» | «Приёмник знает про мьют и держит соединение.» | индикатор серый; счётчик пакетов не растёт | **Включить микрофон** |
| **Ошибка** | конкретно, напр. «Нет доступа к микрофону» | конкретно, см. §10 | вместо телеметрии — блок ошибки с одной кнопкой | конкретное действие |

Индикатор уровня:
- шкала −60…0 dBFS, линейно по dB;
- заливка — градиент `okFg` → `warnFg` (с −12 dBFS) → `badFg` (с −3 dBFS);
- **peak-hold**: отдельная риска 2 px, падает на 20 dB/с;
- в мьюте — заливка `off`, peak-hold скрыт;
- высота 14 (Windows) / 8 (macOS popover), радиус `pill`;
- подпись справа: `пик −12.9 dBFS`, моноширинные цифры.

### 7.3 DualSense

1. **Статус-карточка**.
2. **Живая визуализация контроллера** (см. §8) — главный элемент экрана, занимает
   верхние ~340 px по высоте.
3. **Плитки**: репортов/с, задержка, батарея, проброшено репортов.
4. **Драйвер** — карточка со статусом `usbip-win2`, версией и кнопкой установки.

#### Состояния

| Состояние | Заголовок | Подзаголовок | Визуализация | Действие |
|---|---|---|---|---|
| **Не подключён** | «Контроллер не подключён» | «Подключите DualSense к Mac кабелем USB. По Bluetooth проброс не работает.» | контур контроллера, залитый `off`, непрозрачность 0.35, без реакции | — |
| **Подключён, не проброшен** | «Контроллер найден, проброс выключен» | «`DualSense Wireless Controller`, батарея 62 %. Windows его пока не видит.» | контур **живой** — реагирует на ввод, но окрашен в `textDim`, а не в акцент | **Включить проброс** |
| **Проброшен и работает** | «Контроллер проброшен» | «Windows видит его как `054C:0CE6`. Триггеры и гироскоп работают.» | контур живой и цветной; лайтбар светится реальным цветом | **Выключить проброс** |
| **Драйвер не установлен** | «Не установлен драйвер usbip-win2» | «Без него Windows не сможет создать виртуальный контроллер.» | контур `off`, поверх — затемнение и блок установки | **Установить драйвер** (см. §10) |
| **Триггеры не применяются** (частный случай `warn`) | «Адаптивные триггеры не применяются» | «macOS 26 не пропускает output-репорты этому приложению. Кнопки, стики и гироскоп работают, эффекты триггеров — нет.» | контур живой, зона триггеров с бейджем `warn` | «Подробнее» → док |

Специально предусмотрено: состояние «подключён, но не проброшен» показывает
живую визуализацию. Это и есть проверка «мой пад читается» до того, как что-то
включено, и первый вау-момент, который случается сам собой.

### 7.4 Общие настройки

Одно место, доступное с обеих платформ, одинаковый состав:

- **Соединение**: адрес приёмника/релея, порт, общий ключ (маска + «Показать» +
  «Скопировать» + «Сгенерировать»), кнопка «Проверить связь».
- **Запуск**: автозапуск при входе (macOS — LaunchAgent, Windows — Task Scheduler),
  «Сворачивать в трей при закрытии» (только Windows).
- **Оформление**: тема — Системная / Светлая / Тёмная. **Три пункта, не больше.**
- **Дополнительно** (свёрнуто по умолчанию): битрейт Opus, ожидаемые потери,
  джиттер-буфер, задержка WASAPI, устройство вывода.
- **Диагностика**: «Открыть журнал», «Проверить микрофон» (3 с), «Проверить контроллер»,
  «Скопировать отчёт» (собирает версии, конфиг без ключа, последние 200 строк лога).

Правило: на первом уровне настроек — только то, что пользователь трогает.
Всё, что имеет разумное значение по умолчанию, живёт в «Дополнительно».
## 8. Вау-момент №1 — живая визуализация DualSense

Это единственное место в приложении, где допустимо потратить силы на «красиво».
Оправдание не эстетическое, а функциональное: **это самопроверка**. Пользователь
жмёт триггер и видит, что он дошёл. Ни одна строка текста так не убеждает.

### 8.1 Чем рисовать

**Решение: собственная схематичная отрисовка векторными примитивами, без внешнего SVG.**

Причины:
1. **Юридическая.** Форма DualSense — промышленный образец Sony, а «PlayStation»,
   значки △○✕□ и силуэт контроллера — товарные знаки. Свободно лицензированный
   фотореалистичный контур найти можно (см. 8.6), но использование узнаваемого
   силуэта чужого продукта в собственном приложении — риск, который эта фича
   не стоит. Схематичная геометрическая абстракция (скруглённый корпус + две ручки
   + круги и капсулы) читается как «геймпад», ничего не имитирует и рисуется
   двумя десятками примитивов.
2. **Техническая.** Кнопки, стики и триггеры должны двигаться и менять цвет
   независимо. Готовый SVG всё равно пришлось бы разбирать на именованные слои,
   а это дороже, чем нарисовать сразу параметрически.
3. **Тематическая.** Свой контур легко перекрашивается под светлую и тёмную тему.
   Готовый ассет — нет.

Контур строится в **нормированной системе координат 400 × 260** и масштабируется
целиком (`uniform`, `Viewbox` / `scaleEffect`), поэтому все числа ниже — в этой сетке.

### 8.2 Геометрия (нормированная сетка 400 × 260)

| Элемент | Форма | Координаты (x, y, w, h), радиус |
|---|---|---|
| Корпус | скруглённый прямоугольник | `(60, 20, 280, 120)`, r 40 |
| Левая ручка | капсула, наклон −12° | центр `(118, 178)`, 56 × 130, r 28 |
| Правая ручка | капсула, наклон +12° | центр `(282, 178)`, 56 × 130, r 28 |
| Тачпад | скруглённый прямоугольник | `(148, 38, 104, 60)`, r 8 |
| Левый стик | окружность | центр `(150, 122)`, R 26 (обод), R 17 (шляпка) |
| Правый стик | окружность | центр `(250, 122)`, R 26 / R 17 |
| D-pad | крест из 4 капсул | центр `(96, 74)`, плечо 13 × 22, r 5 |
| △○✕□ | 4 окружности R 11 | центр `(304, 74)`, разнос 26 |
| L1 / R1 | капсула | `(78, 6, 52, 12)` / `(270, 6, 52, 12)`, r 6 |
| L2 / R2 | «лепесток» — капсула, поворачивается вокруг верхнего края | `(78, −16, 52, 22)` / `(270, −16, 52, 22)`, r 8 |
| Create / Options | капсула | `(126, 46, 10, 18)` / `(264, 46, 10, 18)`, r 5 |
| PS | окружность R 9 | центр `(200, 152)` |
| Mute | капсула | `(190, 126, 20, 10)`, r 5 |
| Лайтбар | 2 дуги по бокам тачпада, толщина 4 | вдоль `x = 142` и `x = 258`, `y` 44…92 |

Порядок отрисовки (снизу вверх): корпус и ручки → лайтбар → тачпад → пассивные
кнопки → стики → триггеры → активные подсветки → точки касания.

### 8.3 Отображение данных на пиксели

| Данные (из `GamepadState`) | Диапазон | Что делает на экране |
|---|---|---|
| `left.x/y`, `right.x/y` | `UInt8` 0…255 | шляпка стика смещается на `(v−127.5)/127.5 × 14` px от центра; ободок остаётся на месте |
| `l2`, `r2` | `UInt8` 0…255 | лепесток триггера поворачивается на `−18° × v/255` вокруг верхней кромки **и** заполняется вертикальной заливкой `accent` на `v/255` высоты |
| `buttons` (bitmask) | 15 бит | нажатая кнопка: заливка `accent`, обводка `accent`, масштаб 0.94 |
| `dpad` (hat 0…8) | 9 значений | подсвечиваются 1 или 2 плеча креста |
| `touch[0..1]` | `active`, `x` 0…1919, `y` 0…1079 | точка R 7, `accent`, с «хвостом» из 8 последних позиций, альфа 1.0 → 0.0 |
| лайтбар (из output-репорта `0x02`) | RGB 0…255 | цвет дуг; при выключенном лайтбаре — `border` |
| `gyro` (Int16 ×3) | ±32767 | **весь контур** наклоняется: `rotation.z = clamp(gyro.z/32767 × 12°)`, `rotation.x/y` — псевдо-3D через `.rotation3DEffect` (macOS) / `RotateTransform` + лёгкий скос (Windows) |
| `accel` | ±32767 | не рисуется — избыточно, только в диагностике числами |
| `batteryPercent` | 0…100 | отдельный бейдж вне контура, не в визуализации |
| `sequence` | `UInt8` | разрыв последовательности → на 300 мс мигает бейдж «пропуск кадра» (только в режиме диагностики) |

**Гироскоп — самый дешёвый источник вау.** Контур едва заметно повторяет наклон
контроллера в руках. Амплитуда сознательно занижена (макс. 12°): это должно
читаться как «оно живое», а не как аттракцион.

### 8.4 Частота обновления и CPU

Контроллер отдаёт **250 репортов/с** (`bInterval 6` на High Speed). Рисовать
столько кадров нельзя и не нужно.

**Трёхуровневая схема:**

```
HID-поток 250 Гц ──▶ атомарный снимок (lock-free, последний побеждает)
                          │
       UI-таймер ─────────┴──▶ читает снимок, интерполирует, вызывает перерисовку
```

| Условие | Частота перерисовки | Обоснование |
|---|---|---|
| Экран DualSense открыт и виден, есть ввод | **60 Гц** | стики и триггеры аналоговые, ниже 60 видно ступеньки |
| Открыт, ввода нет 2 с | **8 Гц** | только батарея и статус, глазу этого хватает |
| Экран не выбран / окно свёрнуто / macOS popover закрыт | **0 Гц** — кадры не запрашиваются (`paused: true` / нет `RequestNextFrameRendering`) | главный источник экономии |
| Окно на фоне, но видимо | 20 Гц | |
| Включён «уменьшить движение» | 30 Гц, без инерции и без наклона по гироскопу | |

Правила, без которых 60 Гц сожжёт батарею:

0. **Правильный примитив рендеринга.**
   - **Windows: `CompositionCustomVisualHandler` + `compositor.CreateCustomVisual(handler)`.**
     Это единственный из трёх способов кастомного рисования в Avalonia, который
     даёт собственный кадровый цикл **на рендер-потоке**: `OnRender` вызывается
     per-frame, следующий кадр запрашивается `RequestNextFrameRendering()`.
     Документация Avalonia прямо называет его целевым для «real-time visualizations,
     game loops». `Control.Render` + `InvalidateVisual()` на 60 Гц заставил бы
     UI-поток пересобирать scene graph каждые 16 мс — так делать нельзя.
     Готовая обёртка, если не писать самим: `CompositionAnimatedControl`
     из wieslawsoltes/Lottie (MIT, Avalonia ≥ 12.0.0).
   - **macOS: `Canvas` внутри `TimelineView(.animation(minimumInterval:paused:))`.**
     Это единственная связка в SwiftUI с явным контролем **и частоты, и паузы**:
     `minimumInterval` — потолок частоты, `paused: true` полностью останавливает
     таймлайн. `phaseAnimator`/`keyframeAnimator` такого контроля не дают.
     ⚠️ `Canvas` не даёт ни интерактивности, ни accessibility отдельным элементам —
     для нас это неважно (визуализация декоративная), но значит, что данные
     обязаны дублироваться текстом в диагностике.
1. **Никакого layout на кадр.** Внутри визуализации нет дочерних элементов.
   Ни одного `Border`, ни одной привязки на кнопку.
2. **Инвалидация только при изменении.** Снимок сравнивается с предыдущим;
   если байты совпали — `InvalidateVisual()` не вызывается.
3. **Кисти и геометрии кэшируются** в полях контрола, а не создаются в `Render`.
   Статичная часть корпуса — один заранее собранный `StreamGeometry`,
   пересобирается только при смене размера или темы.
4. **Таймер привязан к видимости**: подписка на `IsEffectivelyVisible` (Avalonia) /
   `.onDisappear` и `NSWindow.occlusionState` (macOS). На Windows дополнительно
   работает штатный `Animation.PlaybackBehavior.Auto` (§2.1.4), который сам
   ставит style-анимации на паузу у невидимых контролов — но на собственный
   кадровый цикл он не влияет, его останавливаем руками.
5. Целевой бюджет: **< 3 % одного ядра** при 60 Гц на типичном игровом ПК
   и **< 4 %** на MacBook Air. Замерить и записать в журнал измерений.

Сглаживание: аналоговые значения проходят через экспоненциальный фильтр
`v = v + (target − v) × 0.35` на кадр (60 Гц) — убирает дрожание стика в покое,
добавляет ~15 мс мягкости, которая читается как «плавно», а не как «тормозит».
Кнопки фильтр **не проходят**: нажатие должно быть мгновенным.

### 8.5 Обе темы

| Слой | Светлая | Тёмная |
|---|---|---|
| Корпус, заливка | `#FFFFFF` | `#262B34` |
| Корпус, обводка | `borderStrong` `#7E8899`, 2 px | `borderStrong` `#66738A`, 2 px |
| Пассивные кнопки | заливка `#EEF1F5`, обводка `#C7CEDA` | заливка `#2E3440`, обводка `#3E4757` |
| Нажатая кнопка | заливка `accent` `#1D65C4`, текст-глиф белый | заливка `accent` `#5A9BFF`, глиф `accentInk` |
| Ободок стика | `#C7CEDA` | `#3E4757` |
| Шляпка стика | `#FFFFFF` + тень `shadow.card` | `#39414F` |
| Заливка триггера | `accent`, альфа 0.85 | `accent`, альфа 0.9 |
| Тачпад | заливка `#F4F6F9`, обводка `#DCE2EA` | заливка `#1A1E25`, обводка `#333A46` |
| Точка касания | `accent`, тень нет | `accent`, свечение `blur 6` альфа .5 |
| Лайтбар выключен | `#DCE2EA` | `#333A46` |
| Лайтбар включён | реальный RGB, **насыщенность ×0.9** | реальный RGB, + внешнее свечение `blur 10` альфа .45 |
| Неактивный контур (`off`) | всё в `off` `#7E8899`, альфа 0.35 | всё в `off` `#7A8496`, альфа 0.35 |

Лайтбар в светлой теме нельзя рисовать «как есть»: чистый `#FFFFFF` лайтбар
сливается с корпусом. Поэтому при яркости выше 90 % добавляется обводка `borderStrong`.

### 8.6 Готовые ассеты — что нашлось

Полный отчёт по поиску — §3.3–3.4. Кратко: **свободно лицензированного SVG-контура
DualSense не существует** (Wikimedia Commons — ноль SVG в категории; все проекты-
визуализаторы либо без лицензии, либо GPL, либо с грязным происхождением арта).
Существуют CC0-наборы **глифов кнопок** — Kenney Input Prompts
(`PlayStation Series/Vector/`) и Xelu (группа `Playstation_5`), — но четыре фигуры
△ ○ ✕ □ проще нарисовать примитивами: это ~15 строк и ноль лицензионных вопросов.
Дизайн-патенты Sony на корпус и товарный знак на «PlayStation Shapes Logo»
подтверждены (§3.4) — именно поэтому мы рисуем абстракцию, а не силуэт.

### 8.7 Что показывать на macOS

В popover меню-бара живёт **уменьшенная версия того же контрола**, 300 × 100,
без гироскопа и без тачпада: только стики, триггеры и подсветка нажатий.
Полная визуализация — на вкладке «DualSense» окна настроек.
Причина: popover не должен становиться приложением.
## 9. Вау-момент №2 — первый запуск и связывание машин

Сейчас: пользователь запускает `keygen` в терминале, копирует base64 в два конфига,
руками пишет IP и порт. Это надо убрать целиком.

### 9.1 Принцип

**Ключ генерируется на Windows, а не на Mac.** Windows — та сторона, которая
слушает: у неё есть адрес и порт, и именно её данные нужно перенести. Значит
код связывания должен содержать *и* ключ, *и* адрес — а адрес знает только Windows.

Переносимая полезная нагрузка — один URI:

```
hexbridge://pair?v=1&h=192.168.1.10&p=47702&k=<base64url ключа>&n=<имя ПК>
```

Три способа перенести его на Mac, в порядке предпочтения. Все три работают
всегда, пользователь выбирает сам:

| Способ | Когда | Как выглядит |
|---|---|---|
| **1. Автопоиск в сети** | Mac и ПК в одной локальной сети | Mac сам находит ПК, на экране Windows появляется 6-значный код подтверждения, пользователь набирает его на Mac |
| **2. QR-код** | Всегда, если Mac рядом и есть камера | Windows рисует QR, Mac открывает камеру и наводит |
| **3. Короткий код** | Камеры нет, автопоиск не сработал | Windows показывает **12 символов** в формате `ABCD-EFGH-JKLM`, Mac принимает их |

Способ 3 не может нести 32-байтный ключ целиком. Поэтому он несёт **одноразовый
код обмена**: Windows временно (на 3 минуты) слушает на порту и отдаёт полный
URI тому, кто предъявит правильный код — обмен закрыт SPAKE2/PAKE или, проще,
HKDF от кода + подтверждение отпечатка на обеих сторонах. Отдельная задача
безопасности, здесь фиксируется только UX.

### 9.2 Поток на Windows: «Связать Mac»

Модалка 720 × 520, четыре шага, прогресс — точки сверху (не полоса: шагов мало).

**Шаг 1. Готовность.**
> **Связать этот ПК с Mac**
> HexBridge создаст общий ключ и покажет его в виде кода. На Mac этот код
> нужно будет ввести один раз.
>
> Чек-лист состояния, каждая строка со своей галочкой:
> - Драйвер usbip-win2 — `установлен` / `не установлен` `[Установить]`
> - Виртуальный аудиокабель — `Steam Streaming Microphone` / `не найден` `[Выбрать]`
> - Порт UDP 47702 — `открыт` / `закрыт брандмауэром` `[Разрешить]`
> - Адрес в сети — `192.168.1.10`
>
> `[Далее]`

Каждая незакрытая строка чинится **прямо здесь**, не выходя из мастера.
Мастер не блокирует «Далее» — можно связать сейчас, а драйвер поставить потом.

**Шаг 2. Код.**
Слева — QR 240 × 240 на белой подложке с `radius.lg` (белая подложка обязательна
и в тёмной теме, иначе камера не прочитает; отступ-«quiet zone» ≥ 4 модуля).
Рисуется одним `<Path>` из `QrCode.ToOutlines()` / `ToGraphicsPath()`
(`Net.Codecrete.QrCodeGenerator`, §3.5) — растр не нужен.
Уровень коррекции **M**: payload короткий, а высокая коррекция сделала бы модули
мельче без пользы. Под кодом — короткий код крупно, моноширинным,
с кнопкой «Скопировать».
Справа — три строки инструкции для Mac.
Внизу — таймер: «Код действует ещё 2:47». По истечении — кнопка «Обновить код».

**Шаг 3. Ожидание.**
QR остаётся на месте, но приглушается; поверх — индикатор ожидания и строка
«Ждём Mac». Как только Mac подключился — переход на шаг 4 автоматически.

**Шаг 4. Проверка связи** — см. 9.4.

### 9.3 Поток на macOS: «Подключиться к ПК»

Открывается **отдельным окном** (не в popover: сканирование камеры в меню-баре —
плохая идея, окно закроется от клика мимо).

**Шаг 1.** Три большие кнопки выбора способа:
> `Найти ПК в сети` (рекомендуется) · `Отсканировать код` · `Ввести код`

**Шаг 1а — автопоиск.** Bonjour/`NWBrowser` по типу `_hexbridge._udp`, список
найденных ПК с именами. Обычно там ровно одна строка — тогда она сразу выделена
и `Далее` активна.

**Шаг 1б — камера.** Живой превью с рамкой-визиром 240 × 240 по центру.
При распознавании — рамка схлопывается в галочку, переход дальше сам.
`AVCaptureMetadataOutput` + `[.qr]` (⚠️ macOS **13.0+**, см. §3.5), с обязательной
проверкой `availableMetadataObjectTypes.contains(.qr)` до присваивания.
Разрешение на камеру запрашивается **в момент нажатия**, а не при старте
приложения, и с объяснением: «Камера нужна только чтобы прочитать код с экрана ПК».
⚠️ `NSCameraUsageDescription` в Info.plist обязателен — без него `requestAccess`
бросает исключение.

Если камеры нет или доступ не дан — способ просто не предлагается на шаге 1,
без сообщений об ошибке.

**Шаг 1в — ручной ввод.** Одно поле на 12 символов с автоформатированием
(`ABCD-EFGH-JKLM`), автоматическим `uppercase`, и алфавитом без `I`, `O`, `0`, `1`.

**Шаг 2. Проверка связи.**

### 9.4 Проверка связи — наглядный результат

Это главный экран мастера, и он же доступен потом в любой момент из настроек
кнопкой «Проверить связь».

Шесть проверок, каждая — строка с иконкой состояния, выполняются
последовательно с задержкой ≥250 мс между появлением строк (иначе всё
мелькает и не читается):

| # | Строка | Успех | Провал |
|---|---|---|---|
| 1 | Адрес разрешается | `192.168.1.10` | «Имя не разрешается в адрес» |
| 2 | Пакеты доходят | «12 мс» | «Ответа нет за 3 с» |
| 3 | Ключи совпадают | «Отпечаток `A1F2 · 9C40`» | «Ключи не совпадают» |
| 4 | Приёмник нашёл аудиоустройство | «Steam Streaming Microphone» | «Устройство не найдено» |
| 5 | Звук проходит насквозь | «пик −18 dBFS» | «Тишина на приёмнике» |
| 6 | Контроллер (если включён) | «DualSense, 4 мс» | «Драйвер не установлен» |

Пункт 5 — важный: мастер **просит сказать что-нибудь вслух** и показывает
живой индикатор уровня *с Windows*, а не с Mac. Это единственная проверка,
которая доказывает сквозную работу тракта. Пять секунд, с обратным отсчётом.

Финал:

> **Всё работает**
> Звук идёт на `GAMING-PC`. В играх выбирайте микрофон
> `Steam Streaming Microphone (Steam Streaming Microphone)`.
>
> `[Готово]`

Здесь — и только здесь — разрешён один короткий праздничный момент: галочка
рисуется штрихом (`trim` / `StrokeDashOffset`) за 320 мс, `spring`, и один раз
пульсирует. Никакого конфетти.

Если что-то не прошло — заголовок не «Ошибка», а конкретика:
«Звук не доходит до Windows», и ниже раскрыт только тот пункт, который сломался,
со своим текстом из §10.

### 9.5 После связывания

Конфиг записывается на обеих машинах, фичи включаются, мастер закрывается,
приложение остаётся на экране статуса. Мастер больше не показывается никогда,
но доступен в настройках как «Связать заново».
## 10. Пустые состояния, ошибки и подсказки

### 10.1 Правила тона

1. Спокойно и по делу. Никаких «Ой!», «Упс», «Что-то пошло не так».
2. **Ни одного восклицательного знака** во всём интерфейсе.
3. Структура сообщения: **что случилось → почему → что сделать**. Три предложения максимум.
4. Ошибка всегда несёт ровно одну главную кнопку с глаголом в инфинитиве
   («Установить драйвер», «Проверить связь», «Открыть настройки»).
5. Не обвинять пользователя: не «вы ввели неверный ключ», а «ключи не совпадают».
6. Технические подробности (коды, стеки, адреса) — во второй строке моноширинным,
   выделяемые мышью, но не в заголовке.
7. Не обещать того, чего не знаем: «попробуйте позже» — запрещено.
8. Единицы через неразрывный пробел: `47 кбит/с`, `12 мс`, `62 %`.

### 10.2 Пустые состояния

#### Ничего ещё не настроено (первый запуск)

> **HexBridge готов к настройке**
> Осталось связать Mac и игровой ПК: сгенерировать ключ и указать адрес.
> Это занимает около минуты.
>
> `[Начать настройку]` `[Настроить вручную]`

#### Микрофон выключен

> **Микрофон выключен**
> Звук на игровой ПК не отправляется. Включите фичу, когда она понадобится.
>
> `[Включить микрофон]`

#### DualSense: контроллер не подключён

> **Контроллер не подключён**
> Подключите DualSense к Mac кабелем USB. По Bluetooth проброс не работает:
> нужен доступ к HID-репортам на полной частоте.

#### Журнал пуст

> **Записей пока нет**
> Журнал заполняется, когда фичи запускаются или меняют состояние.

### 10.3 Ошибки

#### Нет связи с Windows

> **Windows не отвечает**
> Звук отправляется на `192.168.1.10:47702`, но подтверждений оттуда нет.
> Проверьте, что на игровом ПК запущен HexBridge и что порт UDP 47702 открыт.
>
> `[Проверить связь]` `[Открыть настройки]`

Раскрывающийся блок «Что проверить» — чек-лист, каждая строка со своим статусом
(проверяется автоматически, см. §9.4):

- HexBridge запущен на Windows — `не проверено / да / нет`
- Порт UDP 47702 разрешён в брандмауэре — `…`
- Mac и ПК в одной сети — `…`
- Адрес в настройках совпадает с адресом ПК — `…`

#### Ключи не совпадают

> **Ключи не совпадают**
> Пакеты доходят до Windows, но расшифровать их не получается — на Mac и на ПК
> записаны разные ключи. Скопируйте ключ с одной машины на другую целиком,
> вместе с символом `=` в конце.
>
> `[Показать ключ]` `[Связать заново]`

Дополнительно показываем **отпечаток** ключа с обеих сторон — 4 группы по 4 символа,
моноширинным, например `A1F2 · 9C40 · 77BE · D103`. Пользователь сравнивает
глазами, не раскрывая сам ключ:

> На этом Mac: `A1F2 · 9C40 · 77BE · D103`
> На Windows: `5E71 · 20AC · 8B32 · 1FF9`

#### Драйвер usbip-win2 не установлен

> **Не установлен драйвер usbip-win2**
> Без него Windows не может создать виртуальный контроллер, и DualSense не появится в играх.
> Установка занимает меньше минуты, перезагрузка не нужна, Secure Boot отключать не требуется.
>
> `[Установить драйвер]` `[Что это такое]`

В момент установки — предупреждение, спокойное:

> USB-концентраторы переинициализируются один раз, подключённые устройства
> отключатся примерно на секунду. Это ожидаемое поведение установщика.

После установки:

> **Драйвер установлен**
> Версия `0.9.8.0`. Можно включать проброс контроллера.

#### Нет виртуального аудиокабеля

> **Не найдено устройство для вывода звука**
> HexBridge пишет звук в виртуальный кабель, а игры читают его парную половину.
> Ни один из известных кабелей на этом ПК не найден.
>
> `[Выбрать устройство вручную]` `[Как это устроено]`

Со списком вариантов — карточками, в порядке предпочтения:

| Вариант | Подпись |
|---|---|
| **Steam Streaming Microphone** | Ставится вместе со Steam. Скорее всего уже есть — установите Steam и перезапустите HexBridge. |
| **VB-Audio Virtual Cable** | Бесплатный, донат по желанию. Ставится за минуту, требует перезагрузки. |
| **VoiceMeeter** | Подойдёт, если он уже стоит. Отдельно ради HexBridge ставить не нужно. |

#### Нет доступа к микрофону (macOS)

> **Нет доступа к микрофону**
> macOS не разрешает HexBridge читать вход. Разрешение выдаётся один раз в
> «Системных настройках».
>
> `[Открыть настройки конфиденциальности]`

Кнопка открывает ровно нужную панель:
`x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone`.

#### Порт занят

> **Порт 47702 занят другой программой**
> Приёмник не смог его открыть. Выберите другой порт вне диапазона 47984–48010 —
> его держит Sunshine — и укажите тот же порт на Mac.
>
> `[Выбрать другой порт]`

#### Адаптивные триггеры не применяются

> **Адаптивные триггеры не применяются**
> macOS 26 не пропускает output-репорты от приложений вне App Store,
> контроллер отвечает `0xE00002C1`. Кнопки, стики, гироскоп и тачпад работают,
> сопротивление триггеров и лайтбар — нет.
>
> `[Подробнее]`

#### Второй Mac с тем же ключом

> **Соединение уже занято**
> С этим ключом уже подключён другой Mac — `Nikita's MacBook`, подключился 14 минут назад.
> Отключите его или используйте отдельный ключ для второй машины.
>
> `[Занять соединение]`

#### Потери в сети

Это не ошибка, а `warn` в статус-карточке:

> **Потери в сети — 2,4 %**
> Звук восстанавливается, но местами слышны артефакты. Помогает проводное
> подключение или увеличение буфера в дополнительных настройках.

### 10.4 Подсказки (tooltip и подписи)

Подсказка появляется через 600 мс, живёт пока курсор на месте, максимум 2 строки.

| Элемент | Текст |
|---|---|
| RTT | Оценка по меткам времени в служебных пакетах. Требует синхронных часов на обеих машинах. |
| Буфер | Сколько 20-миллисекундных кадров ждут очереди. Цель — джиттер-буфер, делённый на 20. |
| Скрыто | Кадры, восстановленные кодеком вместо потерянных. Ненулевое значение при стабильной сети — повод увеличить буфер. |
| Общий ключ | 32 байта в base64. Должен совпадать на Mac и на ПК посимвольно. |
| Ожидаемые потери | Управляют избыточностью Opus FEC: выше значение — устойчивее звук и больше трафика. |
| Проброс DualSense | Контроллер остаётся подключённым к Mac. HexBridge читает репорты, не забирая устройство у системы и у Steam. |
| Автозапуск | Приложение будет запускаться при входе в систему и работать в фоне. |
| Мьют | Приёмник узнаёт о мьюте и удерживает соединение — переподключение не потребуется. |

### 10.5 Уведомления

Уведомления — только при переходе в `error` из рабочего состояния, и только
когда окно не на переднем плане. Не чаще одного в 5 минут. Никаких уведомлений
об успехе.

| Событие | Заголовок | Текст |
|---|---|---|
| Связь пропала | HexBridge | Windows перестал отвечать. Звук не доходит. |
| Контроллер отключён | HexBridge | DualSense отключён от Mac. Проброс приостановлен. |
| Связь восстановлена | — | Уведомления нет. Меняется только иконка в трее/меню-баре. |
## 11. Доступность и качество

### 11.1 Обязательный минимум

| Требование | Проверка |
|---|---|
| Контраст текста ≥ 4.5:1, индикаторов и границ контролов ≥ 3:1 | таблицы §4.1 |
| Цвет не единственный носитель смысла | у каждого цветного индикатора есть текст и своя форма иконки |
| Полная клавиатурная навигация | Tab обходит все интерактивные элементы в визуальном порядке |
| Видимый фокус | кольцо 2 px `accent` + отступ 2 px, контраст к фону ≥ 3:1 |
| Уважение системных настроек | тема, уменьшение движения, уменьшение прозрачности, размер текста |
| Скринридер | у каждого статуса есть текстовая метка; живая визуализация помечена как декоративная и продублирована таблицей значений в диагностике |

### 11.2 Клавиатура

| Сочетание | Действие | Платформа |
|---|---|---|
| `⌘,` / `Ctrl+,` | Настройки | обе |
| `⌘M` | Мьют микрофона | macOS (уже есть) |
| `Ctrl+M` | Мьют микрофона | Windows |
| `Ctrl+1…4` | Разделы боковой навигации | Windows |
| `Esc` | Закрыть модалку/popover | обе |
| `⌘W` / `Alt+F4` | Скрыть окно (не выйти) | обе |
| `⌘Q` | Выход | macOS |
| Глобальный хоткей мьюта | настраивается пользователем, по умолчанию не назначен | обе |

Глобальный хоткей мьюта — самая ценная функция для стримера, потому что руки
заняты. На macOS — `KeyboardShortcuts` 3.0.1 (§2.2): она регистрирует Carbon-хоткей
**без запроса Accessibility-разрешений** и даёт готовый контрол записи сочетания
`KeyboardShortcuts.Recorder` прямо в настройки. На Windows — `RegisterHotKey`
на HWND главного окна.

### 11.3 Локализация

Интерфейс русский. Тексты живут в ресурсах, а не в разметке, даже если
второй язык не планируется: это дисциплина, которая заставляет вычитывать
формулировки списком, а не по одной.

Правила русских строк:
- Единицы измерения через неразрывный пробел (`U+00A0`).
- Тире — `—` (U+2014) с пробелами; дефис в составных словах — обычный.
- Кавычки — «ёлочки».
- Числа: разделитель дробной части — запятая (`2,4 %`), разделитель разрядов —
  тонкий неразрывный пробел.
- Множественное число считается функцией (`1 пакет / 2 пакета / 5 пакетов`),
  а не склеиванием строки с «шт.».

### 11.4 Что проверять перед релизом

1. Оба приложения в светлой и тёмной теме, при 100 %, 125 %, 150 % и 200 % масштаба.
2. Оба приложения с включённым «уменьшить движение».
3. Windows-окно при ширине 880 (минимум) — ничего не обрезано, ничего не наехало.
4. macOS popover при всех пяти состояниях каждой фичи — высота не прыгает больше
   чем на 40 px между соседними состояниями.
5. Загрузка CPU: визуализация DualSense открыта 10 минут — снять среднее.
6. Все тексты из §10 показаны вживую и вычитаны на экране, а не в редакторе.
7. Скриншот каждого состояния из §7 сохранён в `docs/screens/` — это регрессионная
   база для следующего изменения.
## 12. План реализации

Порядок выбран так, чтобы каждый шаг давал видимый результат и не блокировал следующие.

### Этап −1. Разведка (делается первым, до дизайна)

Три вещи могут заставить переделывать, поэтому проверяются до всего остального.

| # | Задача |
|---|---|
| −1.1 | **Проверить [Avalonia#21082](https://github.com/AvaloniaUI/Avalonia/issues/21082) на 12.1.2**: `WindowDecorations="None"` + прозрачность. Если чёрный фон — план с собственным заголовком отменяется, идём через `DwmSetWindowAttribute(hwnd, 20, …)` |
| −1.2 | **Smoke-тест `Irihi.Ursa` 2.2.0 на Avalonia 12.1.2** — пакет собран под 12.0.2, под 12.1 не пересобирался. Проверить `NavMenu`, `Skeleton`, `Banner`, `Dialog` |
| −1.3 | **Проверить, открывается ли окно настроек из `MenuBarExtra` на релизной macOS 26.x** и активируется ли приложение. Если нет — заложить обход из §2.2 |
| −1.4 | Проверить `MenuBarExtraAccess` 1.3.1 на macOS 26.6 и на RC 27 |
| −1.5 | Проверить, что иконки в пунктах меню не исчезли на macOS 27 (`labelStyle(.titleAndIcon)`) |

### Этап 0. Фундамент (обе платформы, параллельно)

| # | Задача | Где |
|---|---|---|
| 0.1 | Завести файл токенов: цвета обеих тем, отступы, радиусы, тени, длительности, кривые. Ни одного литерала вне него | `win/src/MicBridge.App/Styles/Tokens.axaml`, `mac/Sources/…/UI/Tokens.swift` |
| 0.2 | Обновить существующую палитру на значения §4.1. Правятся `textDim` (`#6B7687` → `#5C6675`) и `accent` (`#387ADF` → `#1D65C4`) — обе старые не проходят WCAG AA. Добавляются `borderStrong`, `accentInk`, `off`/`offBg`, `okText`/`warnText`/`badText` | обе |
| 0.3 | Типографические стили по §4.2; на Windows основной шрифт — **Segoe UI**, `Avalonia.Fonts.Inter` остаётся fallback для отладки не на Windows | обе |
| 0.4 | Хелпер «уменьшенное движение» по §5.6: на Windows — P/Invoke `SPI_GETCLIENTAREAANIMATION` + `WM_SETTINGCHANGE` (API в Avalonia нет); на macOS — env value + `NSWorkspace.shared.notificationCenter` | обе |
| 0.5 | Переключатель темы Системная/Светлая/Тёмная в настройках | обе |
| 0.6 | Добавить в csproj: `Irihi.Ursa` 2.2.0, `Irihi.Ursa.Themes.Semi` 2.2.0, `FluentIcons.Avalonia` 2.1.339.1, `Net.Codecrete.QrCodeGenerator` 3.2.1 | Windows |
| 0.7 | Добавить SPM-зависимости: Sparkle 2.9.6, KeyboardShortcuts 3.0.1, MenuBarExtraAccess 1.3.1. Автозапуск перевести на `SMAppService` без библиотеки | macOS |
| 0.8 | Многоразмерный `.ico` (16/20/24/32) на четыре состояния трея, различимые формой | Windows |
| 0.9 | Строка о товарных знаках Sony в «О программе» (§3.4) | обе |

### Этап 1. Оболочка

| # | Задача |
|---|---|
| 1.1 | Windows: заменить `TabControl` на `NavMenu` (Ursa) с двухстрочными пунктами фич и едущей полосой выделения (§7.1, переход 3) |
| 1.2 | Windows: `WindowDecorations="None"` + собственная шапка со сводным статусом и одной главной кнопкой (на Win10 системный заголовок не темнеет, §1.2) |
| 1.3 | Windows: трей — четыре иконки; меню через `NativeMenu` с `ToggleType`/`IsChecked`; тема меню — через `ControlTheme` для `MenuFlyoutPresenter`; подсказка о сворачивании — `WindowNotificationManager`, **не balloon** (§2.1.5) |
| 1.4 | macOS: пересобрать popover в две карточки фич + сводную строку; иконка меню-бара строго template, без цвета |
| 1.5 | macOS: окно настроек — вкладки Общие / Микрофон / DualSense / Соединение / Диагностика |
| 1.6 | Общий автомат состояний фичи (§7.0) — один тип на обеих сторонах, одинаковые имена состояний |

### Этап 2. Компоненты

| # | Задача |
|---|---|
| 2.1 | Реализовать 20 компонентов из §6; на Windows — как `Styles`/`UserControl`, на macOS — как `View` |
| 2.2 | `LevelMeter`: добавить peak-hold и асимметричную анимацию (§5.4, переход 8) |
| 2.3 | `EmptyState`, `InlineAlert`, `CheckRow`, `FingerprintLabel` — их сейчас нет ни на одной платформе |
| 2.4 | Каталог переходов §5.4 — по одному, с ревью каждого на «а нужен ли он» |

### Этап 3. Экраны по фичам

| # | Задача |
|---|---|
| 3.1 | Экран микрофона: пять состояний §7.2, все тексты из §10 |
| 3.2 | Экран DualSense: пять состояний §7.3 |
| 3.3 | Настройки по §7.4, «Дополнительно» свёрнуто |
| 3.4 | Диагностика: «Скопировать отчёт» |

### Этап 4. Вау №1 — визуализация DualSense

| # | Задача |
|---|---|
| 4.1 | Транспорт: атомарный снимок `GamepadState` из HID-потока, без блокировок |
| 4.2 | Каркас рендеринга: `CompositionCustomVisualHandler` на Windows, `Canvas` + `TimelineView(.animation(minimumInterval:paused:))` на macOS (§8.4, п. 0) |
| 4.2а | Геометрия §8.2 как кэшируемая статическая часть (`StreamGeometry`, пересборка только при смене размера или темы) |
| 4.3 | Динамический слой §8.3 |
| 4.4 | Управление частотой §8.4: 60/20/8/0 Гц по видимости и активности; фильтр сглаживания |
| 4.5 | Обе темы §8.5 |
| 4.6 | Мини-версия для macOS popover §8.7 |
| 4.7 | Замер CPU, запись результата в документ |

### Этап 5. Вау №2 — связывание

| # | Задача |
|---|---|
| 5.1 | Формат `hexbridge://pair?…` и отпечаток ключа (4 группы по 4 символа) |
| 5.2 | Windows: мастер «Связать Mac», 4 шага, QR + короткий код + таймер жизни кода |
| 5.3 | Windows: чек-лист готовности шага 1 с починкой на месте (драйвер, кабель, брандмауэр) |
| 5.4 | macOS: окно «Подключиться к ПК», три способа |
| 5.5 | Автопоиск в сети (Bonjour/`_hexbridge._udp`) с 6-значным подтверждением |
| 5.6 | Сканирование QR камерой на macOS (`AVCaptureMetadataOutput`, macOS 13+), `NSCameraUsageDescription`, запрос доступа по нажатию |
| 5.7 | Проверка связи — шесть пунктов §9.4, включая сквозной тест звука с уровнем **с Windows** |
| 5.8 | Одноразовый обмен по короткому коду — отдельная задача безопасности, UX зафиксирован |

### Этап 6. Отделка

| # | Задача |
|---|---|
| 6.1 | Вычитать все тексты §10 на живых экранах |
| 6.2 | Уведомления §10.5 с ограничением частоты. **На Windows в первой версии — только смена иконки трея и тултипа**: системных тостов Avalonia не умеет, а `DesktopNotifications.Avalonia` регистрирует AUMID и ярлык в «Пуске» (§2.1.5) |
| 6.3 | Клавиатура §11.2, включая глобальный хоткей мьюта |
| 6.4 | Прогон чек-листа §11.4, скриншоты в `docs/screens/` |

### Что сознательно не делаем

- **Имитация Acrylic/Mica на Win10 через `SetWindowCompositionAttribute`.**
  Приватный API, неисправимый лаг перетаскивания, риск для Store (§1.2).
  `AcrylicBlur` через штатный `TransparencyLevelHint` — можно, но только как
  оппортунистическое улучшение главного окна.
- **Lottie.** Skottie не поддерживает expressions, эффекты, blending modes;
  всё нужное делается штатными transitions за десятки строк (§2.1.3).
- **Liquid Glass как основа визуала.** Максимум `.buttonStyle(.glass)` на одной
  кнопке, под `if #available(macOS 26, *)`, и то не в первой версии: на macOS 26
  у неё сломан hover вне тулбара (§1.1).
- **`MeshGradient` и декоративные градиентные фоны.** Поднимают цель до macOS 15,
  живут в content layer вопреки гайдлайнам, и жгут idle CPU в утилите.
- **Сканирование QR камерой на Windows.** Windows показывает код, Mac читает —
  камера на ПК не нужна, зависимости FlashCap/ZXing не добавляются.
- **Собственный набор иконок.** Системный на macOS, Fluent на Windows.
- **Системные тосты Windows** в первой версии (§2.1.5).
- **Двойной клик по иконке трея** — Avalonia его не поддерживает.
- **Кастомный `TabView`** — в Avalonia 12 такого контрола нет.
- Тёмная/светлая тема, не совпадающая с системной по умолчанию. Звуковые сигналы.
## 13. Что не подтверждено

Список ведётся честно: по этим пунктам решение принято на основании косвенных
данных, и его надо перепроверить на стенде до релиза.

**Платформы**
1. Публичная дата релиза macOS 27 (14.09.2026) — из прессы, не с сайта Apple.
   Факт с developer.apple.com — только RC-сборка 26A428 от 09.09.2026.
2. `DWMWA_USE_IMMERSIVE_DARK_MODE` (значение 20) на Windows 10 19045:
   Microsoft Learn указывает минимумом Windows 11 build 22000, практика
   сообщества — работает с Win10 2004+. Первичного подтверждения нет.
   Мы это обходим собственным заголовком окна, но если обход не сработает —
   пункт становится критическим.
3. Изменилась ли высота меню-бара в Tahoe. HIG по-прежнему говорит 24 pt,
   `NSStatusBar.thickness` по-прежнему документирован как 22 px (страница
   не обновлялась годами). Читать `NSStatusBar.system.thickness` в рантайме.
4. Есть ли у окна `MenuBarExtra(.window)` собственный vibrancy-фон.
   Документация молчит. Для настоящего `NSPopover` — подтверждено, что есть
   (*«AppKit creates visual effect views automatically for… popovers.
   You don't need to add visual effect views»*).

**Библиотеки**
5. В каком именно релизе Avalonia 12.x закрыта регрессия прозрачности #21082.
   Issue закрыт через PR #21354, номер релиза из тикета не следует. → задача −1.1.
6. Работает ли `Irihi.Ursa` 2.2.0 (собран под 12.0.2) на 12.1.2. → задача −1.2.
7. Совместимость `Svg.Controls.Skia.Avalonia` (SkiaSharp native assets 4.x)
   с `Avalonia.Skia 12.1.2` (SkiaSharp 3.119.4). Мы это обходим, беря
   `Svg.Controls.Avalonia` без Skia — но если понадобится Skia-версия,
   нужен smoke-тест.
8. Исправлена ли проблема открытия окна настроек из меню-бара на релизной
   macOS 26.x. Источник (Steinberger) датирован beta-периодом. → задача −1.3.
9. `FAProgressRing` в FluentAvalonia проверен по `master`, а не по тегу 3.1.0 —
   у репозитория нет тегов. Нас не касается, FluentAvalonia мы не берём.

**API и поведение**
10. Учитывает ли `symbolEffect` настройку Reduce Motion автоматически.
    HIG SF Symbols не содержит ни одного упоминания Reduce Motion.
    **Считаем, что нет**, и гасим эффекты явно.
11. Уменьшает ли Liquid Glass движение при Reduce Motion. Подтверждена только
    реакция на Reduce Transparency и Increase Contrast.
12. Кодировка `inputMessage` в `CIQRCodeGenerator`: legacy-документация говорит
    `NSISOLatin1StringEncoding`, современный пример кода — `.ascii`.
    **UTF-8 у Apple не упоминается вообще.** Мы держим payload в ASCII, чтобы
    вопрос не возникал.
13. macOS-availability для `CIRoundedQRCodeGenerator` и для
    `CIQRCodeGenerator.correctionLevel` — на страницах не проставлена.
14. Устаревание масштаба иконки в трее при переносе таскбара между мониторами
    с разным DPI (Avalonia обрабатывает `WM_DISPLAYCHANGE`, но не `WM_DPICHANGED`).
    Вывод из чтения исходника, не заведённый баг.
15. Performance-характеристики `MeshGradient` — Apple не документирует ничего.
    Нас не касается, мы его не используем.

**Юридическое**
16. Наличие EUIPO Registered Community Design на DualSense — эндпоинты EUIPO
    не отвечали. С учётом регистраций в TW/CA/UY это очень вероятно.
    На наше решение (§3.4) не влияет.
17. Публичный standalone-текст лицензии SF Symbols найти не удалось; вывод
    сделан по Xcode and Apple SDKs Agreement §2.10, который его покрывает.

**Что надо измерить, а не выяснить**
18. Реальная загрузка CPU визуализацией DualSense на 60 Гц на целевом ПК
    и на MacBook Air (бюджет — §8.4). Записать результат в этот документ.
19. Высота popover при всех состояниях каждой фичи — не должна прыгать
    больше чем на 40 px между соседними (§11.4).
