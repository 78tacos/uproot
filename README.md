# Uproot

Uproot uninstalls Windows programs, then finds and removes what their uninstallers leave behind.

It shows the same list Apps & Features does, Microsoft Store apps included, but it examines each
program's uninstaller before running it, and searches for what the program left behind afterwards.
Adobe Acrobat is the canonical case: its own uninstaller leaves settings in every profile's
AppData, keys under `HKCU` and `HKLM\SOFTWARE\Adobe`, an updater service and a scheduled task.

Uproot began as the uninstaller inside [WGUpdater](https://github.com/LCMS-Foundation/wgupdater)
and is now a program of its own. Removals WGUpdater made are listed and can be undone from Uproot
(see [Coming from WGUpdater](#coming-from-wgupdater)).

## Getting it

Uproot runs from wherever it is unzipped, or it can be installed for everyone on the PC.

| Download | What it is | Needs |
|---|---|---|
| `Uproot-<version>-self-contained-win-x64.zip` | Portable | Nothing: runs on any Windows 10 or 11 x64 machine |
| `Uproot-<version>-framework-dependent-win-x64.zip` | Portable, much smaller | The .NET 10 Desktop Runtime (`winget install Microsoft.DotNet.DesktopRuntime.10`) |
| `Uproot-Setup-<version>-win-x64.exe` | Installs the framework-dependent build for everyone | The .NET 10 Desktop Runtime, and administrator rights |

Unzipped, Uproot writes nothing to disk until something is removed. See
[Installing](#installing) for what an install adds and why you might want one.

Each zip holds two files:

| File | What it is |
|---|---|
| `Uproot.exe` | The window |
| `uproot.com` | The command line (see [Command line](#command-line)) |

Windows file names ignore case, so the command line cannot be called `uproot.exe` beside
`Uproot.exe`. It is `uproot.com` instead. cmd and PowerShell both try `.com` before `.exe`, so typing
`uproot` in a shell opened in that folder (or with it on PATH) runs the command line, and
double-clicking `Uproot.exe` opens the window. Visual Studio's `devenv.com` and `devenv.exe` work the
same way.

## Installing

Installing is optional, and there is only one kind: for everyone, in `C:\Program Files\Uproot`.

The reason to install is that Uproot often restarts itself with administrator rights, to clean up
Program Files, `HKLM`, services and other people's profiles. A portable copy in Downloads or on the
Desktop is a file any program running as you could replace before you answer the administrator
prompt, and an unsigned program's prompt cannot show the difference. Program Files can only be
changed by administrators, so the installed copy is safe to run elevated. A per-user install would
sit in a folder you can change and solve nothing, so there isn't one.

An install adds four things, and removing it takes away exactly those four:

| What | Where |
|---|---|
| `Uproot.exe` and `uproot.com` | `C:\Program Files\Uproot` |
| A Start menu shortcut | All users' Start menu |
| An Apps & Features entry | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uproot` |
| The folder on the machine PATH | So `uproot` works in any new shell |

Three ways to install, all running the same code:

- **Install for everyone…** in the header of a portable copy. It explains what it adds, asks for
  administrator rights once, and offers to open the installed copy. From a copy newer than the
  installed one, the button reads **Update the installed copy…**.
- **`uproot self-install`** from a portable copy's folder. Run it from an administrator prompt to
  skip the prompt.
- **`Uproot-Setup.exe`**, which asks for administrator rights as it starts and unpacks straight into
  Program Files: nothing it carries is written anywhere a standard account can change first.
  `Uproot-Setup.exe --quiet` installs with no window, for Intune, ConfigMgr or a script, and reports
  through its exit code (0 installed; 1 installed without the shortcut or PATH entry; 64 an
  incomplete setup; 70 failed) and `setup.log` in the install folder.

An update replaces the files in place. A copy that is open keeps running from the old files, which
were renamed aside and go at the next install or restart.

To remove it, use Apps & Features, or run `uproot self-uninstall` (`--quiet` for no dialogs, as
Apps & Features' quiet uninstall does). It asks you to close Uproot first, and its own files go as
soon as it exits. Backups of past removals are not part of the install and are kept.

**Restart as admin** uses the installed copy whenever there is one, from any window. A portable
copy with no install behind it, in a folder you can change, says so and offers three choices:
install first and restart from Program Files (one administrator prompt for both), restart this copy
anyway, or neither. A portable copy in a folder only administrators can change restarts as it is.

Uproot does not offer to uninstall itself from its own list: that is Apps & Features' job, and the
installed folder is never offered as anyone's leftover.

## The program list

The list sorts by name, publisher, install date or size, exports to CSV, and has a right-click menu
for the install folder, the registry entry and copying the name. Ctrl-click or Shift-click selects
several programs: their uninstallers run one after another, and one review covers what they all
left, with shared folders removed once all of their contents have gone.

### Examining the uninstaller first

Selecting a program shows what its uninstaller is and what it will do, before anything runs:

| Framework | Recognised by | What Uproot gets from it |
|---|---|---|
| Windows Installer (MSI) | `WindowsInstaller` in the entry, or an msiexec command | The package's own record — every file, registry value, service and shortcut it installed — read from the cached database |
| Inno Setup | `_is1` key, or the uninstaller's signature | The paths and registry keys in its `unins000.dat` log |
| NSIS | The uninstaller's embedded signature | That it relaunches from Temp, so the wait follows the copy |
| WiX bundle, Squirrel, Chromium, Click-to-Run, InstallShield | Entry values and command shape | The right unattended switches, where one exists |
| Microsoft Store (MSIX) | Listed by `Get-AppxPackage`; frameworks, system apps and sparse identity packages left out | Nothing to follow: Windows removes the package and its data folder itself, through `Remove-AppxPackage` |

It also works out the right command. Many MSI packages — Acrobat among them — register `msiexec /I`,
which opens a Modify / Repair / Remove wizard; Uproot runs `/X`, which goes straight to removal.
Where a framework has a known unattended mode it is offered, and the vendor's own
`QuietUninstallString` is preferred when there is one.

The MSI record is the strongest evidence there is, but it is not taken at face value. Components
Windows Installer marks permanent, or that another installed product also uses, are **shared** and
never offered: Acrobat's package includes the Visual C++ runtime in System32, and deleting that
because Acrobat "left it behind" would break half the machine.

### Waiting for it properly

Waiting for the process that was started is not enough. NSIS and Inno Setup uninstallers copy
themselves to Temp, relaunch from there so they can delete their own folder, and exit at once. The
runner follows the whole tree by parent process id — recognising children even after their parent
has gone — and then waits for Windows Installer's transaction lock to clear. Browsers are not
followed, because plenty of uninstallers open a "sorry to see you go" page on the way out.

Uninstallers start in the system folder, never their own: a relaunched copy inherits the working
directory, and Windows will not delete a folder that is some process's current directory.

## Finding leftovers

### Three modes

| Mode | Looks for | Pre-selected |
|---|---|---|
| **Safe** | What is provably the program's: its install folder, what its installer recorded, its own `Publisher\Product` folders and keys, and anything pointing into its folder — shortcuts, startup entries, services, tasks, PATH entries and Windows Firewall rules | Everything found |
| **Moderate** (default) | Also name matches in Program Files, ProgramData, AppData, the Start menu, services, scheduled tasks, and COM classes, ProgIDs, context-menu entries, shell-extension approvals, icon overlays and file types that point into its folder; event log sources; its Default Apps entry; PATH entries and firewall rules for a folder named after it that no longer exists; and the records Windows keeps about programs it has run (below) | Certain and likely items |
| **Expert** | Also partial matches, publisher folders, Temp and Documents, and a search for the install path across `HKLM\SOFTWARE` and `HKCU\Software` (bounded to a minute) | Never the uncertain ones |

Every item carries its confidence — **Certain**, **Likely** or **Possible** — and the reason, and
the mode never changes how an item is judged: the same folder gets the same confidence whichever
mode found it. **Every user profile on this PC** extends the search to other accounts, loading each
one's registry hive for the scan and unloading it straight after. Without it, the search covers the
profile the program is installed in: yours, or for another account's per-user program, that
account's — never yours as well, in case you have a copy of your own. The option names whose.

Windows itself keeps records about programs, keyed by their path, that no uninstaller cleans up: the
name Explorer caches for each executable (`MuiCache`), compatibility settings and the Program
Compatibility Assistant's records (`AppCompatFlags`), Windows Installer's folder list, and
`SharedDLLs` counts for files that no longer exist. These go value by value — the keys themselves are
Windows' and stay — and only for paths inside the program's folder.

**Find leftovers of a removed program** does the same for a program that is no longer listed,
working from its name and publisher, and from the folder it was installed in when you know it. It
finds, for instance, the scheduled task Creative Cloud leaves pointing at an executable it has
already deleted. Given a folder, the folder itself and everything pointing into it are found for
certain — so the folder is checked against the whole safety policy first, and refused if it is a
standard or known folder, holds one, belongs to Windows, or belongs to or holds another installed
program. `C:\Users`, a profile, or Documents redirected into OneDrive cannot be named as one
program's folder.

### What it will not touch

The safety policy is consulted when the list is built and again immediately before each deletion,
so an edited or stale list still cannot reach anything protected:

- Anything under Windows, the package caches, WindowsApps and Windows' own ProgramData folders.
- Standard folders themselves — Program Files, AppData, a profile, the Start menu — though a
  program's own folder inside them may go. That includes each account's own folders wherever they
  have been moved: Documents, Desktop or Pictures redirected into OneDrive, and the OneDrive folders
  themselves. Nothing holding one of these is ever offered, and an install folder that is one of
  them — whoever recorded it — makes nothing the program's.
- Uproot's own data and backups, in every profile, and the backups WGUpdater made, so a scan can
  never offer the backup of a program's last removal as one of its leftovers. The folder Uproot runs
  from is never removed either.
- Shared registry keys: `SOFTWARE`, `Classes`, `CLSID`, a file extension's key, `Directory\shell`,
  `Run`, `Uninstall`, all of `SYSTEM` and Windows' own configuration. A program's own ProgID, CLSID,
  verb or value inside them may go.
- Anything another installed program claims: its folder, anything containing it, and names that
  match it better than they match the program being removed. A name that is only the start of
  another installed program's ("Acrobat" when Acrobat Reader is installed) is flagged, not selected.
- Services and scheduled tasks that run something in another installed program's folder — Acrobat's
  updater service belongs to Adobe Refresh Manager, a separate product — and anything a
  `SharedDLLs` count says another program still uses.
- A key the installer merely added values to — Acrobat writes into `Office\14.0\Word` — goes value
  by value, never whole. A verb added to someone else's file type goes without the file type:
  "Edit with Notepad++" leaves `VBSFile` itself alone.
- A publisher folder such as `ProgramData\Adobe` is removed only if it is empty by the time its turn
  comes, and no folder or key is offered whole while something inside it is protected.
- Network paths, and other spellings of a protected place — `\\?\C:\Windows`, `C:\PROGRA~1` —
  which are resolved before any rule is applied. A folder that is a junction is removed as the link,
  never followed.
- PATH is taken apart one entry at a time and the variable itself always stays, with its other
  entries, their order and its type unchanged. Entries are matched by their text, so removing
  `C:\Java\bin` leaves a `%JAVA_HOME%\bin` beside it alone. An entry for a Windows folder is never
  removed.
- A context-menu handler, shell-extension approval or file type naming a COM class goes only when
  every registration of that class, in every hive and both registry views, is the program's: 7-Zip's
  32-bit and 64-bit builds share one, and removing one build leaves the other's menu working.
- A registry key is never deleted, nor a value changed, through a registry symbolic link — on the
  way to it or anywhere inside it — since the link could lead anywhere.
- Firewall rules are removed by name, so a name is offered only when every rule carrying it lets the
  program through. Rules in Windows' own groups, or for Windows' own programs, stay.
- An event log source goes only when its messages come from the program's own files, or, for the
  shared .NET message file, when it is named exactly after the program.

A program's short name — "Acrobat" from "Adobe Acrobat", "Notes" from "Fabrikam Notes" — is trusted
inside the publisher's own folder, but not on its own in a shared one, where it is often just a word.

### Turning protection off

Protected items are listed, greyed out, with the reason. Most of those reasons are judgements — this
folder belongs to another installed program, that name matches another program better, this
service's program could not be identified — and **Allow removing protected items** above the list
lets a person overrule them. It warns first, with No as the default; nothing protected is ever
selected for you, and "Select all" still leaves protected items alone. The removal prompt then lists
every protected item chosen, in capitals.

Some protection cannot be turned off at all. Those items are marked **Always kept**: anything part of
Windows, the standard folders and registry keys every program shares, drive roots and network paths,
a folder or key holding something like that, Windows' own services, tasks and firewall rules, PATH
entries for Windows folders, and Uproot's own backups. The remover checks again at the moment of
removal: an item it now finds protected for a reason nobody was shown — because another program was
installed since the scan, say, or a folder turns out to hold a second program the reason did not
name — is refused even with protection off. Protection comes back on with every rescan. Of several
programs removed together, one whose uninstaller was cancelled keeps its things protected against
the others', and "Select all" leaves its items out.

The command line never removes protected items. The window can, because someone is there to read the
warning.

## Removing, and undoing it

Registry keys and values are exported with `reg export` before they are deleted, scheduled tasks
are saved as XML, firewall rules as JSON, and files and folders go to the Recycle Bin. Backups land in
`%LOCALAPPDATA%\Uproot\Backups\<date> <program>\`, or, for a removal made as administrator, in
`%ProgramData%\Uproot Backups\<account SID>\`, a tree only administrators and SYSTEM can change at
any level. A `manifest.json` records every item, what happened to it and which backup puts it back,
written even if removal is stopped part-way. These folders are only created when something is
actually removed. Anything the Recycle Bin will not take — in use, too large, or on a drive without
one — is reported and left in place rather than deleted permanently instead.

**Undo this removal** on the finished page puts everything back at once; **Restore removed items**
in the main window lists every past removal and puts back any part of one:

| Item | Comes back from |
|---|---|
| Registry key, service | Its `.reg` export, read once, checked to hold nothing outside that key, and written back from what was checked — never handed to `reg.exe`, which would read the file again |
| Registry value | Its key's export, one value at a time, so the rest of a shared key such as `Run` is untouched |
| PATH entry | Added back to the end of PATH, unless it is already there |
| Scheduled task | Its saved definition, registered again under the account it named |
| Firewall rule | Its saved properties |
| File, folder | The Recycle Bin, found by its original path and when it was deleted |

Nothing is overwritten: an item already back is skipped. A service is registered again at once but
Windows loads it at the next restart. A task that stored a password has to be imported by hand, and
the page says so.

Undoing is also guarded, because a backup in the account's profile could have been changed by a
program running without administrator rights. While elevated, the restore puts machine-wide items
back — `HKLM` keys, services, firewall rules, tasks, machine PATH — only from a backup in the
ProgramData tree whose every folder, from the root down, is still locked. A locked folder inside the
profile would not do: the account controls the folder above it, so it could rename the backup away
and put another in its place. Files are not moved back at all while elevated, since the Recycle Bin
and the folder a file returns to are both the account's to change; restore those from the Recycle
Bin in Explorer, or undo without administrator rights. And every item must pass the checks a removal
would have made — a restored service is judged by the program its ImagePath runs, a task by its
commands, an event source by its message files — so a backup is never a way to write what a removal
could not have touched. Backups made by the first version of WGUpdater's uninstaller did not record
which file belongs to which item, and are restored by hand from their folder.

## Administrator rights

Uproot starts without them, and uninstallers ask for rights themselves. Leftovers in Program Files,
ProgramData, `HKLM` and other people's profiles can only be removed as administrator, so the window
says so before a machine-wide uninstall and offers **Restart as admin**, which asks once and opens
Uproot again with them, from the installed copy when there is one (see [Installing](#installing)).
A System Restore point can be created first when elevated; Windows allows
one a day and the result says when it declined.

When a standard user's administrator prompt is answered with another account's credentials, the
elevated Uproot runs as that account: its `HKCU`, its AppData, its Store apps and its Recycle Bin.
A banner names both accounts. That account's per-user programs are labelled with its name and can be
uninstalled there. The signed-in person's per-user programs are listed but refused, because their
uninstallers would run as the wrong account, and their Store apps are not listed at all. Run Uproot
without elevating for those.

## Coming from WGUpdater

The uninstaller used to be part of WGUpdater (the **Uninstall programs** button, and `wgu --programs`,
`--analyze`, `--leftovers`, `--uninstall-program`, `--backups` and `--restore`). It works the same
way here, with these differences:

| | WGUpdater | Uproot |
|---|---|---|
| Backups, without admin | `%LOCALAPPDATA%\WGUpdater\Uninstall backups` | `%LOCALAPPDATA%\Uproot\Backups` |
| Backups, as administrator | `%ProgramData%\WGUpdater Uninstall Backups` | `%ProgramData%\Uproot Backups` |
| Other accounts' hives, while loaded | `HKU\WGU_<sid>` | `HKU\Uproot_<sid>` |
| Command line | `wgu --leftovers "X"` | `uproot leftovers "X"` (the `--leftovers` form still works) |

Removals WGUpdater made are read from its folders and listed beside Uproot's own, and restore the
same way: one made as administrator is trusted for machine-wide items if WGUpdater's ProgramData
tree is still locked, exactly as Uproot's own would be. Nothing new is written to WGUpdater's
folders, and both are protected from being offered as leftovers. Uninstalling WGUpdater with its own
uninstaller deletes `%LOCALAPPDATA%\WGUpdater`, including those old backups, so undo anything you
still want back first.

## Command line

```text
uproot programs [--json]
uproot analyze <name> [--json]
uproot leftovers <name> [--publisher X] [--folder F] [--mode safe|moderate|expert] [--all-users] [--clean [--force]] [--json]
uproot uninstall <name> [--unattended] [--restore-point] [--mode …] [--all-users] [--clean] [--json]
uproot backups [--json]
uproot restore <name>|latest [--json]
uproot self-install [--quiet]
uproot self-uninstall [--quiet]
uproot help
uproot version
```

| Command | Does |
|---|---|
| `programs` | Lists installed programs, as Apps & Features does, including Microsoft Store apps |
| `analyze <name>` | Shows what the uninstaller is and what it will do, without running it |
| `leftovers <name>` | Scans for what a program left behind. Changes nothing unless `--clean` is given. Works for a program that is no longer listed; `--publisher` and `--folder` help it |
| `uninstall <name>` | Runs the program's own uninstaller, waits for it to finish, then scans for leftovers |
| `backups` | Lists past removals, Uproot's and WGUpdater's, newest first |
| `restore <name>\|latest` | Puts back what one removal took |
| `self-install` | Installs this copy for everyone in Program Files, or repairs the install when run from it |
| `self-uninstall` | Removes that install; what Apps & Features runs |

`--clean` removes the Recommended items only, never protected ones. Running `leftovers --clean` on a
program that is still installed is refused unless `--force` is added, for when its own uninstaller is
broken. `--all-users` and `--restore-point` need an elevated prompt.

Progress goes to stderr and results to stdout, so `--json` can be redirected into a file. Exit codes:
0 success (for `leftovers`, nothing removable found), 1 leftovers found and not removed, 64 bad
usage, 70 failed, 130 stopped with Ctrl+C.

`Uproot.exe` takes the same commands, borrowing the console of the shell that started it, but a
shell does not wait for a windowed program, so `uproot.com` is the one to script with.

## Build and run

Requires the .NET 10 SDK and Windows.

```powershell
dotnet build Uproot.slnx
dotnet test Uproot.slnx
dotnet run --project src\Uproot.App
dotnet run --project src\Uproot.Cli -- programs
.\publish.ps1 -Zip
```

`publish.ps1` writes both builds to `dist\`, renames the command line to `uproot.com`, checks both
files are the same build and that `uproot.com` starts, and with `-Zip` packages each flavour.

Every build carries a version, a build number (the number of commits in its history) and the
commit, for example `0.9.0 (build 3, abc1234)`: in the file's Properties, in Task Manager, and from
`uproot version`. A build from uncommitted changes says "modified".

## Layout

```text
src/
  Uproot.Core/            the engine: no UI
    Cli/                  command-line parsing and commands, shared by both executables
    Elevation/            administrator status, restarting elevated, which account is running
    InstalledProgram.cs   ProgramInventory.cs  StoreApps.cs       the program list
    UninstallerAnalyzer.cs  MsiFootprint.cs  InnoSetupLog.cs      examining uninstallers
    UninstallerRunner.cs  UninstallSession.cs                     running them
    LeftoverScanner.cs    LeftoverScanContext.cs  ProgramNames.cs finding leftovers
    LeftoverSafety.cs     PathRules.cs  RegistryLinks.cs          what may never be removed
    LeftoverRemover.cs    RemovalBackup.cs  RecycleBin.cs         removing, with backups
    RemovalRestorer.cs    RegFile.cs                              putting things back
    Installation.cs                                               installing Uproot itself for everyone
  Uproot.App/             the window (WPF)
  Uproot.Cli/             the console front end, shipped as uproot.com
  Uproot.Setup/           Uproot-Setup.exe: the framework-dependent build, installed for everyone
tests/
  Uproot.Core.Tests/      xunit; scanner and remover tests build a fake program in temp folders
                          and a test key under HKCU\Software\Uproot.Tests
```
