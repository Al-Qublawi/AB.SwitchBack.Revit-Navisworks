# AB SwitchBack

**by [Abdullah Lotfy](https://www.linkedin.com/in/abdullahalqublawi/)**

Ctrl+Left-click an element in Navisworks; the paired Revit session finds that element in
the **active** project, activates a 3D view, wraps a section box around it, zooms in and
comes to the foreground.

> **Why Ctrl+Click and not Ctrl+Shift+Click?** Navisworks reserves Ctrl+Shift+Click: it
> expands the pick to the whole model file, so the plugin only ever sees the file node and
> can never find an element id. Ctrl+Click is Navisworks' normal "add to selection", which
> resolves to a single element at your configured Selection Resolution. The gesture is
> fully configurable from the ribbon **Settings** button if you want something else.

---

## Version support

| Host | Releases | Runtime | Notes |
|---|---|---|---|
| Revit | 2020 – 2024 | .NET Framework 4.8 | `ElementId(int)` / `IntegerValue` |
| Revit | 2025 – 2027 | .NET 8 | `ElementId(long)` / `.Value` — the `int` overloads were **removed** in 2025 |
| Navisworks | 2024 – 2027 | .NET Framework 4.8 | Navisworks has not moved to .NET 8 as of the 2027 release |

Projects exist for the whole range. `build.ps1` detects what is installed and builds only
that, so installing a newer Autodesk release later needs no code change — just re-run it.

## Architecture

```
ABSwitchBack.Core            net48 + net8.0-windows   shared, Autodesk-free
  Protocol/                  one-line text messages
  Ipc/                       named pipe server + client
  Discovery/                 per-process advert files
  UI/InstancePickerForm      the destination picker, used by both hosts

ABSwitchBack.Revit           built once per Revit release   -p:RevitVersion=2024
  App                        IExternalApplication: ribbon + listener
  SwitchBackEventHandler     IExternalEventHandler: all Revit API work
  RevitCompat                the ONLY file that differs across Revit versions

ABSwitchBack.Navisworks      built once per Navisworks release  -p:NavisVersion=2027
  SwitchBackWatcher          EventWatcherPlugin, auto-loads at startup
  TriggerGesture             reads the modifier keys, once per selection change
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

Detection order for both products: the product's own registry key, then the Windows
uninstall registry, then the conventional Program Files layout — each candidate validated
by checking the API assembly actually exists. No path is hard-coded.

Build one target only:

```bash
powershell -ExecutionPolicy Bypass -File build\build.ps1 -RevitYears 2024 -NavisYears 2027
```

## Installer (share this with the team)

```bash
powershell -ExecutionPolicy Bypass -File build\make-msi.ps1
```

Produces `dist\ABSwitchBack-1.1.1.msi` — one per-machine package covering every release that
was compiled into `artifacts\`. Requires the WiX 5 CLI once:
`dotnet tool install --global wix`.

The WiX source is generated from the contents of `artifacts\`, so adding an Autodesk release
means re-running `build.ps1` then `make-msi.ps1` — never editing XML.

| Host | Destination | Behaviour |
|---|---|---|
| Revit | `%ProgramData%\Autodesk\Revit\Addins\<year>\` | Installed for every built year. Revit reads only its own year folder, so one MSI serves a team on mixed versions. |
| Navisworks | `<install>\Plugins\ABSwitchBack\` | Install path read from the registry at install time; files are skipped entirely when that release is absent. |

The team member just double-clicks the MSI and accepts the UAC prompt. Uninstall from
Add/Remove Programs.

> **Coverage:** the MSI can only contain versions that were compiled, which means versions
> installed on the build machine. Check what it will deploy before sharing:
>
> ```bash
> powershell -ExecutionPolicy Bypass -File build\verify-msi.ps1
> ```
>
> It prints every file, its exact destination, and the condition gating it.

The Restart Manager is deliberately disabled in the package: left on, Windows Installer
offers to shut Revit down to finish the install, which risks losing unsaved work. Instead
the classic "files in use" prompt asks the user to close the applications and retry.

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

Both applications get an **AB SwitchBack** ribbon tab carrying the product logo, a **Settings**
button (trigger gesture, on/off, section box options), a **Status and Log** button (listener and
trigger state, running instance counts, log folder), an **About** button and a **LinkedIn** link.

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

## Configuration

Use the **Settings** button on the ribbon in either host — there is no need to edit a file.

The trigger is any combination of **Ctrl**, **Shift** and **Alt** plus a left click, ticked
from checkboxes, and the whole thing can be switched off. The dialog previews the gesture as
you build it and warns you about the two combinations worth knowing:

- **Ctrl+Shift** is reserved by Navisworks and expands the pick to the whole model file.
- **No modifier at all** sends *every* element you select — useful for a dedicated
  coordination session, noisy the rest of the time.

Changes apply immediately; nothing needs restarting. Settings are shared between Revit and
Navisworks, so it does not matter which side you open the dialog from.

### The underlying file

`%LOCALAPPDATA%\ABSwitchBack\config.txt` — plain `key=value`, re-read on each switch-back.

| Key | Default | Meaning |
|---|---|---|
| `SectionBoxMarginMm` | `1000` | Padding around the element, in millimetres |
| `CreateSectionBox` | `true` | `false` = select and zoom only |
| `CreateViewIfMissing` | `true` | `false` = never create a 3D view (see *What it changes in your model*) |
| `EnableClickHook` | `true` | `false` disables the trigger entirely |
| `Trigger` | `Ctrl` | Any combination of `Ctrl`, `Shift`, `Alt` (e.g. `Ctrl+Alt`), or `None`. Anything unrecognised falls back to `Ctrl` |
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

43 checks over the real compiled assemblies: protocol round-tripping (including payloads
containing `|`, newlines and backslashes, and 64-bit ids), pipe request/response, four
simultaneous instances with routing verification, closed-destination timeout, handler
exceptions, malformed input, 25 concurrent senders, discovery with dead-PID pruning and
PID-reuse rejection, and element-id text parsing.

The Autodesk-side behaviour (section box, zoom, ribbon) needs both applications running and
has to be exercised by hand.

## Troubleshooting

| Symptom | Cause |
|---|---|
| "The click selected the whole model file" | `Trigger=CtrlShift` (Navisworks reserves it), or Selection Resolution is set to File in Options → Interface → Selection |
| Nothing happens on Ctrl+Click | Check `SwitchBack Status` — the trigger may be off, or `EnableClickHook=false` |
| "No Revit instance found" | Revit is not running, or the add-in did not load — check the Revit log |
| "Element not found in the active project" | The Navisworks model came from a different project, or the id belongs to a link |
| Navisworks plugin missing | It must sit at `Plugins\ABSwitchBack\ABSwitchBack.dll` — the DLL name must match the folder name |
| Revit ribbon tab missing | Check `%LOCALAPPDATA%\ABSwitchBack\logs\Revit-*.log` |
