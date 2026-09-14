# AB SwitchBack 1.3.0

Ctrl+click an element in Navisworks and the paired Revit session selects it, section boxes it and
zooms to it. Revit 2020 – 2026, Navisworks 2024 – 2027.

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
