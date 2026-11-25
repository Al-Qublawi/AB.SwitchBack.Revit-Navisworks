# AB SwitchBack 1.4.0

Ctrl+click an element in Navisworks and the paired Revit session selects it, section boxes it and
zooms to it. Revit 2020 – 2027, Navisworks 2024 – 2027.

## What's new

- **Revit 2027.** Built for Revit 2027, which runs add-ins on .NET 10.
- **Shift is no longer offered in the trigger.** Navisworks reserves Ctrl+Shift+Click (it selects the
  whole model file), so Shift was only ever a way to break the gesture. Settings now offer Ctrl, Alt or
  Ctrl+Alt. A click with Shift held never triggers a switch back. A `config.txt` that still names Shift
  loads with it dropped: `Ctrl+Shift` becomes `Ctrl`, `Shift+Alt` becomes `Alt`.
- **Help.** A Help button on the SwitchBack panel in both Revit and Navisworks. It opens the user guide
  when one is installed with the add-in (the Autodesk Marketplace version), and the project page
  otherwise. F1 over a SwitchBack button in Revit opens the guide too.
- **On the Autodesk Marketplace.** SwitchBack is being submitted to the Marketplace as a free app. That
  version has no GitHub update check and no Check for Updates button, as the store requires. This GitHub
  release still has both. Install one or the other, not both.

---

# AB SwitchBack 1.3.1

Ctrl+click an element in Navisworks and the paired Revit session selects it, section boxes it and
zooms to it. Revit 2020 – 2026, Navisworks 2024 – 2027.

## The installer is an .msi again: `AB.SwitchBack-1.3.1.msi`

1.3.0's `AB.SwitchBack.Setup.exe` was blocked on company PCs by Microsoft Defender's attack surface
reduction rule *"Block executable files from running unless they meet a prevalence, age, or trusted
list criteria"*. That rule stops unknown programs; it does not stop Windows Installer packages, and
the new package runs no program of its own. Nothing in SwitchBack itself changed.

Everything 1.3.0's installer offered is still there:

- **Install for** Everyone (the default) or Only me. Navisworks has no per-user plugin folder, so
  its plugin installs with Everyone.
- **Choose releases** — every Revit year is ticked, installed or not, so the add-in is ready when
  that Revit is; the Navisworks plugin is offered for each Navisworks edition installed. The ticks
  are remembered for the next upgrade.
- **Earlier versions** — a copy installed by 1.3.0's `Setup.exe` is listed and removed first unless
  you untick it; the 1.0 – 1.2 packages are upgraded in place.
- Revit and Navisworks are never closed for you: if they hold SwitchBack's files, Windows asks you
  to close them.
- Silent deployment with standard `msiexec` options — see the README.

If Setup.exe 1.3.0 is installed, just run the .msi: it replaces it.

---

# AB SwitchBack 1.3.0

## What's new

### One ribbon tab for every AB add-in

In both Revit and Navisworks, SwitchBack's buttons now sit on the **AB Adv Tools** tab, shared with
the other AB add-ins, instead of a tab of their own. At the end of the tab is one **AB Adv Tools**
panel for the whole suite:

- **About** lists every AB tool loaded, with its version.
- **Check for Updates** checks them all.
- **LinkedIn**.

That panel replaces SwitchBack's own About and LinkedIn buttons; selecting SwitchBack in About shows
what its own About did, with **Open the settings and log folder**. Revit Target, Settings and Status
and Log are unchanged, and so is everything SwitchBack does.

### Release notifications, now shared

The once-a-day, once-per-version check is now the suite's. It works the same way, adds **Check for
Updates** on the ribbon, and can be switched off for every AB add-in from **AB Adv Tools › About**.
The **Check for updates** option in SwitchBack's Settings still works as before.

### A new installer: `AB.SwitchBack.Setup.exe`

It replaces the MSI and:

- **finds earlier versions**: the 1.x MSI, files copied by `build\install.ps1`, or an earlier install
  from this setup. It lists them, and removes them first unless you untick them.
- installs Revit for **all users** (the default, as before) or **just you**, for every supported Revit
  release whether or not it is installed yet (as the MSI did). Navisworks always goes
  in its program folder.
- asks for administrator rights only when your choices need them
- never closes Revit or Navisworks for you: it asks you to close them, so no unsaved work is lost
- supports `/silent`, `/uninstall /silent`, and `/scan` to report what is installed without
  changing anything

### Fixed

- The version shown in dialogs came from a hand-kept constant. It now comes from the build.
