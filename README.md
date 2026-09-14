# AB SwitchBack

**by [Abdullah Lotfy](https://www.linkedin.com/in/abdullahalqublawi/)**

Ctrl+Left-click an element in Navisworks; the paired Revit session finds that element in
the **active** project, activates a 3D view, wraps a section box around it, zooms in and
comes to the foreground.

> **Why Ctrl+Click and not Ctrl+Shift+Click?** Navisworks reserves Ctrl+Shift+Click: it
> expands the pick to the whole model file, so the plugin only ever sees the file node and
> can never find an element id. Ctrl+Click is Navisworks' normal "add to selection", which
> resolves to a single element at your configured Selection Resolution. The gesture is
> fully configurable from the **Settings** button on the Navisworks ribbon.

---

## Version support

**Revit 2020–2026** and **Navisworks 2024–2027**. Every one is built and shipped in the
installer; there is one binary per release.

| Host | Releases | Runtime | API difference handled |
|---|---|---|---|
| Revit | 2020 – 2024 | .NET Framework 4.8 | `ElementId(int)` / `IntegerValue` |
| Revit | 2025 – 2026 | .NET 8 | `ElementId(long)` / `.Value` — the `int` overloads were **removed** in 2025 |
| Navisworks | 2024 – 2025 | .NET Framework 4.8 | `VariantData` has `IsInt32` only |
| Navisworks | 2026 – 2027 | .NET Framework 4.8 | `VariantData` **gained** `IsInt64`, `IsNat32`, `IsNat64` in 2026 |

Each split is isolated to one file — `RevitCompat.cs` and `NavisCompat.cs`. Everything else
is version-neutral.

### Building does not require Autodesk products

The projects reference **NuGet API packages** by default
(`Nice3point.Revit.Api.*`, `Speckle.Navisworks.API`), so the full matrix builds on any
machine. Autodesk's assemblies are not redistributable and none are committed here.

This is deliberately not "build against whatever is installed". A Revit 2026 update in
September 2026 moved that release from .NET 8 to .NET 10 *in place*, so the same source
built a week apart produced add-ins with different runtime requirements — and one built
against the updated SDK will not load for anyone who has not taken the update. The
reference packages pin the baseline API for each year, which loads on both.

To build against local SDKs anyway:

```bash
powershell -ExecutionPolicy Bypass -File build\build.ps1 -UseInstalledSdk
```

## Architecture

```
ABSwitchBack.Core            net48 + net8.0-windows   shared, Autodesk-free
  Protocol/                  one-line text messages
  Ipc/                       named pipe server + client
  Discovery/                 per-process advert files
  SelectionDiff              which element a selection change was about
  TriggerGesture             parses, formats and tests the modifier gesture
  UI/                        the destination picker and the settings dialog

ABSwitchBack.Revit           built once per Revit release   -p:RevitVersion=2024
  App                        IExternalApplication: ribbon + listener
  SwitchBackEventHandler     IExternalEventHandler: all Revit API work
  RevitCompat                the ONLY file that differs across Revit versions

ABSwitchBack.Navisworks      built once per Navisworks release  -p:NavisVersion=2027
  SwitchBackWatcher          EventWatcherPlugin, auto-loads at startup
  SwitchBackContext          lifecycle, pipe messaging, sending to Revit
  SelectionWatcher           turns selection changes into "you clicked this element"
  ElementIdExtractor         finds the Revit id in the item's properties
```

**How Core is consumed differs by host, deliberately.** Revit references it as a second
assembly, because Revit probes the add-in folder for dependencies. The Navisworks plugin
**compiles the same Core sources in** and ships as one self-contained DLL, because
Navisworks scans a plugin assembly for `[Plugin]` types *before any of our code has run* —
so an `AssemblyResolve` handler registered from a static constructor is always too late. If
a scanned type needs Core resolved at that moment (a value-type field such as an enum forces
it, since the CLR must compute the type's layout), the scan throws
`ReflectionTypeLoadException` and Navisworks silently loads **no plugins at all**: no ribbon,
no listener, nothing. Test 9 in the self-test suite guards this.

**Threading contract.** The pipe listener runs entirely on the thread pool and never calls
the Autodesk API. In Revit it only enqueues an id and calls `ExternalEvent.Raise()`; all
model work happens later on the UI thread inside `IExternalEventHandler.Execute`. Neither
host can be blocked by the transport.

**Protocol.** One UTF-8 line per message:

```
ABSB1|<Type>|<SourcePid>|<ElementId>|<Payload>
```

`Type` is `Ping | Pong | Select | Ack | Error`. The payload is escaped so a document title
containing `|` or a newline cannot corrupt the frame.

**Discovery.** Each process writes a small advert to
`%LOCALAPPDATA%\ABSwitchBack\instances\<Role>.<PID>.inst` carrying role, PID, process name,
application, version, open document and pipe name. Liveness comes from the OS process table,
not from a timestamp; the process *name* is re-checked too, so a recycled PID can never be
mistaken for a live host. Adverts are withdrawn on shutdown and pruned when read.

**Pipes.** One endpoint per process: `ABSwitchBack.<Role>.<PID>`. Any number of Revit and
Navisworks instances coexist.

## Build

```bash
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

Builds all 11 targets: Revit 2020-2026 and Navisworks 2024-2027. No Autodesk product
needs to be installed.

Build one target only:

```bash
powershell -ExecutionPolicy Bypass -File build\build.ps1 -RevitYears 2024 -NavisYears 2027
```

## Installer (share this with the team)

```bash
powershell -ExecutionPolicy Bypass -File build\build.ps1
powershell -ExecutionPolicy Bypass -File build\make-installer.ps1
```

Produces `dist\AB.SwitchBack.Setup.exe` — one installer covering every Revit and Navisworks
release compiled into `artifacts\`, built on the AB Adv Tools installer engine
(`shared\ABAdvTools`). It replaces the 1.x MSI; no WiX is needed. Adding an Autodesk release
means re-running both scripts — nothing else.

| Host | Destination | Behaviour |
|---|---|---|
| Revit | `Addins\<year>\` under `%ProgramData%` (all users, default) or `%APPDATA%` | Installed for every built release, whether or not that Revit is installed yet - Revit reads only its own year folder, so one installer serves a team on mixed versions, as the 1.x MSI did. |
| Navisworks | `<install>\Plugins\ABSwitchBack\` | Install path read from the registry; only releases actually installed. Always needs administrator rights. |

The team member double-clicks it. Setup **finds earlier copies** — the 1.x MSI (by its
UpgradeCode), files copied by `build\install.ps1`, or an earlier install from this setup —
lists them, and removes them first unless they are unticked. It asks for administrator
rights only when the choices need them, restarting itself elevated with the same choices.
Uninstall from Apps and Features.

Setup never closes Revit or Navisworks for you: it refuses to start while either is running
and asks you to close them, so nobody loses unsaved work.

```bash
AB.SwitchBack.Setup.exe /silent                 :: elevated prompt; all users
AB.SwitchBack.Setup.exe /uninstall /silent
AB.SwitchBack.Setup.exe /scan                   :: what is installed and where, changes nothing
AB.SwitchBack.Setup.exe /silent /sandbox:C:\Temp\sb   :: full install into a folder, for testing
```

## Install from source (developer machine)

Close the host you are installing into first — a running host locks its own DLLs.

```bash
powershell -ExecutionPolicy Bypass -File build\install.ps1
```

| Host | Destination | Rights |
|---|---|---|
| Revit | `%APPDATA%\Autodesk\Revit\Addins\<year>\` | current user, no admin |
| Navisworks | `<install>\Plugins\ABSwitchBack\` | **administrator** |

Navisworks has no per-user plugin folder, so that half needs an elevated shell. Install one
side at a time when only one host is closed:

```bash
powershell -ExecutionPolicy Bypass -File build\install.ps1 -RevitOnly
```

Uninstall with `build\uninstall.ps1` (add `-PurgeSettings` to also drop logs and config).

## Use

1. Open the Revit project the Navisworks model was exported from.
2. Open the model in Navisworks. Both sides start listening automatically.
3. In Navisworks: **AB SwitchBack → Revit Target**, pick the Revit instance.
   Skip this when only one Revit is running — it pairs automatically.
4. Hold **Ctrl** and left-click an element.

Revit selects the element, applies a section box with a 1 m margin, zooms to it and comes
forward.

In both applications SwitchBack's buttons are on the **AB Adv Tools** ribbon tab, which every
AB add-in shares. The tab ends with the suite's **AB Adv Tools** panel: **About** (every AB
tool loaded, with versions), **Check for Updates** and **LinkedIn**.

Navisworks drives; Revit only receives, so its SwitchBack panel is one diagnostic button:

| Button | Navisworks | Revit |
|---|---|---|
| **Revit Target** | choose the destination Revit | — |
| **Settings** | trigger gesture, on/off, section box options | — |
| **Status and Log** | listener and trigger state, log folder | listener state, the settings in force, running Navisworks instances with a connection **Test**, log folder |

### How the clicked element is identified

Navisworks reports only *that* the selection changed, never *what* changed, and Ctrl+click
**adds** an unselected element but **removes** an already-selected one. So the plugin
snapshots the selection in the `Changing` event and diffs it in `Changed`: a one-item
difference in either direction identifies the clicked element exactly. Clicking an
already-selected element therefore works correctly instead of sending the wrong one.

The snapshot is taken **only while the trigger modifiers are actually held**, so ordinary
picking and navigation cost nothing at all. Bulk changes such as Select All differ by far
more than one item and are ignored; above 10 000 selected items the diff is skipped and the
gesture reports that it cannot identify the element rather than guessing.

The comparison itself is `SelectionDiff.FindSingleChange` in Core — generic, free of any
Autodesk type, and covered by 13 tests using plain strings. It is the one piece of logic
where a bug would send the *wrong* element silently, so it is deliberately kept pure and
testable rather than tangled up with the Navisworks event plumbing in `SelectionWatcher`.

> **No mouse hook.** Version 1.0.0 detected the gesture with a `WH_MOUSE_LL` global mouse
> hook installed on the Navisworks UI thread. That forced Windows to route *every* mouse
> event in the system — including the flood of moves during an orbit — through that thread's
> message queue before the input could proceed, so whenever Navisworks was busy rendering,
> all mouse input serialised behind it and the application felt sluggish. The hook is gone
> as of 1.0.1: nothing in SwitchBack now touches the system input path.

### One behaviour worth knowing

**Only the active Revit document is searched.** Linked models are deliberately ignored: an
id taken from a link would resolve to a different, wrong element in the host model.

## What it changes in your model

Two writes, both view-level, both wrapped in named transactions so Ctrl+Z reverses them:

| Write | When | Transaction |
|---|---|---|
| Section box on the 3D view | Every switch-back | `SwitchBack: section box` |
| Creates a 3D view | Only if the project has **no** usable non-template 3D view | `SwitchBack: create 3D view` |

Nothing else is touched: **no geometry, parameters, families, types, worksets, levels or
deletions.** Selection, active view, zoom and refresh are all UI-only and change nothing.

Both writes mark the document as having unsaved changes. On a workshared model, if another
user owns the 3D view the section box transaction fails safely — it is caught and logged,
and the element is still selected and zoomed.

To make the add-in **strictly read-only**, set both of these in `config.txt`:

```
CreateSectionBox=false
CreateViewIfMissing=false
```

## Staying up to date

Both hosts check once a day whether a newer release exists, and tell you **once per
version** — never again for the same one. Clicking through opens the release page; the new
`AB.SwitchBack.Setup.exe` removes the version you have and installs the new one.
**Check for Updates** on the AB Adv Tools tab checks immediately, for every AB add-in.

Since 1.3.0 this is the AB Adv Tools kit's release checker (`shared\ABAdvTools`), shared by
every AB add-in; `ABSwitchBack.Core.UpdateCheck` is gone.

Exactly what happens: a single anonymous `GET` to GitHub's public releases API, on a
background thread. **Nothing is uploaded** — no telemetry, no identifiers, no usage data.
Offline, behind a proxy, firewalled or rate-limited all fail silently. The notice never
appears during startup: Navisworks shows it once the UI is up, Revit waits until a view is
open and it is idle.

Turn it off with the **Tell me when a newer version is released** checkbox in Settings, or
`CheckForUpdates=false` in `config.txt` — useful if your clients' IT restricts outbound
traffic from Revit or Navisworks. The checkbox in **AB Adv Tools › About** turns it off for
every AB add-in at once.

## Configuration

Use the **Settings** button on the **Navisworks** ribbon — there is no need to edit a file.

Everything is configured from Navisworks, because that is where the workflow starts. Revit
only receives, so it carries no Settings button; its **Status and Log** dialog shows the
settings currently in force, read-only.

The trigger is any combination of **Ctrl**, **Shift** and **Alt** plus a left click, ticked
from checkboxes, and the whole thing can be switched off. The dialog previews the gesture as
you build it and warns you about the two combinations worth knowing:

- **Ctrl+Shift** is reserved by Navisworks and expands the pick to the whole model file.
- **No modifier at all** sends *every* element you select — useful for a dedicated
  coordination session, noisy the rest of the time.

Changes apply immediately; nothing needs restarting. Revit re-reads the config on every
switch-back, so the Revit-side options take effect on the very next click.

### The underlying file

`%LOCALAPPDATA%\ABSwitchBack\config.txt` — plain `key=value`, re-read on each switch-back.

| Key | Default | Meaning |
|---|---|---|
| `SectionBoxMarginMm` | `1000` | Padding around the element, in millimetres |
| `CreateSectionBox` | `true` | `false` = select and zoom only |
| `CreateViewIfMissing` | `true` | `false` = never create a 3D view (see *What it changes in your model*) |
| `TriggerEnabled` | `true` | `false` disables the trigger entirely |
| `Trigger` | `Ctrl` | Any combination of `Ctrl`, `Shift`, `Alt` (e.g. `Ctrl+Alt`), or `None`. Anything unrecognised falls back to `Ctrl` |
| `CheckForUpdates` | `true` | `false` stops the daily GitHub version check entirely |
| `PipeTimeoutMs` | `3000` | Connect/response timeout |

Logs are per process: `%LOCALAPPDATA%\ABSwitchBack\logs\<Role>-<PID>.log`.

## Element ID extraction

Exporters disagree about where the Revit id lives, so the extractor searches the clicked
item and then walks up its ancestors (the id often sits on a parent, not the geometry leaf):

1. A property category named `Element ID` / `Revit Element ID` / `Revit ID` — the value is
   usually the property called `Value`.
2. Failing that, any property named `Element ID`, `ElementId`, `Revit Element ID`,
   `Revit ID` or `Id` in any category.

Integer, 64-bit, unsigned and whole-number-double property types are all handled, plus text
such as `"123456"` or `"Element ID: 123456"`. GUIDs, zero, negatives and ambiguous
multi-number strings are rejected. When nothing is found, the error lists the property tabs
that *were* present on the item so you can see why.

## Testing

```bash
dotnet build tests\ABSwitchBack.SelfTest -c Release
artifacts\SelfTest\ABSwitchBack.SelfTest.exe
```

78 checks over the real compiled assemblies. It needs no Autodesk product installed — the
sections that do (7 and 9) skip themselves when nothing has been built.

| | Covers |
|---|---|
| 1 | Protocol round-trip: payloads containing `\|`, newlines and backslashes; 64-bit ids; malformed input rejected |
| 2–5 | Pipes: request/response, four simultaneous instances with routing verified, closed-destination timeout, handler exceptions, 25 concurrent senders |
| 6 | Discovery: dead-PID pruning, PID-reuse rejection, advert withdrawal |
| 7 | Element-id text parsing, including GUID and ambiguous-value rejection |
| 8 | Trigger parsing, and all eight modifier combinations surviving a save/reload |
| 9 | The Navisworks plugin loading with no folder probing — guards the 1.1.0 regression |
| 10 | **Selection diff**: which element a change was about, including every ambiguous case |
| 11 | Config key matching and the legacy-key migration |
| 12 | Update check: version comparison and release-tag parsing |

The Autodesk-side behaviour (section box, zoom, ribbon) needs both applications running and
has to be exercised by hand.

## Troubleshooting

| Symptom | Cause |
|---|---|
| "The click selected the whole model file" | `Trigger=CtrlShift` (Navisworks reserves it), or Selection Resolution is set to File in Options → Interface → Selection |
| Nothing happens on Ctrl+Click | Check `SwitchBack Status` — the trigger may be off, or `TriggerEnabled=false` |
| "No Revit instance found" | Revit is not running, or the add-in did not load — check the Revit log |
| "Element not found in the active project" | The Navisworks model came from a different project, or the id belongs to a link |
| Navisworks plugin missing | It must sit at `Plugins\ABSwitchBack\ABSwitchBack.dll` — the DLL name must match the folder name |
| Revit ribbon tab missing | Check `%LOCALAPPDATA%\ABSwitchBack\logs\Revit-*.log` |
