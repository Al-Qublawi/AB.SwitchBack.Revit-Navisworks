# AB SwitchBack - what its .msi installs and where. Built by build\make-installer.ps1 through the
# AB Adv Tools kit (shared\ABAdvTools\msi\New-AdvToolsMsi.ps1); the kit README documents every key.
@{
    # Keys the installer's registry state and finds copies left by the retired Setup.exe. Never rename.
    Id          = 'SwitchBack'
    Name        = 'AB SwitchBack'
    FileName    = 'AB.SwitchBack'
    Repository  = 'AB.SwitchBack.Revit-Navisworks'

    # The 1.0-1.2 WiX packages used this code, so they are upgraded in place. Never change it.
    UpgradeCode = 'EC8C6EF4-D80D-43D8-AAFA-81653D9BA6FD'

    Description = 'Ctrl+click an element in Navisworks and the paired Revit session selects it, section boxes it and zooms to it. Install it on both sides.'
    NextSteps   = 'Start Revit and Navisworks, then Ctrl+click an element in Navisworks. SwitchBack''s buttons are on the AB Adv Tools tab in both.'
    Icon        = '..\assets\logo.ico'

    # Everyone by default, as the 1.x packages and Setup.exe did; Only me for the Revit half.
    Scope        = 'UserOrMachine'
    DefaultScope = 'Machine'
    ScopeNote    = 'Everyone installs the Revit add-in into ProgramData and the Navisworks plugin into the Navisworks program folder; it needs administrator rights. Only me installs the Revit add-in into your own profile and leaves Navisworks out, because Navisworks has no per-user plugin folder.'

    RevitAddin = @{
        ManifestFileName  = 'ABSwitchBack.addin'
        FolderName        = 'ABSwitchBack'
        AssemblyFileName  = 'ABSwitchBack.Revit.dll'
        AddInName         = 'AB SwitchBack'
        FullClassName     = 'ABSwitchBack.Revit.App'
        # Revit keys the registration off this id. Never change it.
        AddInId           = 'b6342c28-4b89-4417-bb34-17d8f02664e3'
        VendorId          = 'ABSB'
        VendorDescription = 'AB SwitchBack - Navisworks to Revit switch back'
        # Revit reads only its own year folder, so every build is ticked, installed or not -
        # one package serves a team on mixed versions, including a Revit installed later.
        Releases          = 'AllBuilt'
    }

    NavisworksPlugin = @{
        # <Navisworks install folder>\Plugins\ABSwitchBack - the folder must carry the DLL's name.
        FolderName = 'ABSwitchBack'
        Editions   = @('Manage', 'Simulate')
    }
}
