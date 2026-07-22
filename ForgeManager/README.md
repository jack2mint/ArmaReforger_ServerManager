# ForgeManager

**An Arma Reforger server GUI interface for Windows 10/11.**

ForgeManager is a modern C#/.NET 8 WPF control center for running and maintaining an Arma Reforger dedicated server without relying on a permanent command-prompt window.

## Features

- Start, stop, restart, force-stop, automatic restart, and `-addonsRepair`
- Detect and attach to a server started by a BAT file or another launcher
- Drop-in mode beside `ArmaReforgerServer.exe`
- Built-in SteamCMD download plus stable/experimental server install, update, validation, and cancellation
- Automatic loading of `configs\config.json` and `profile`
- Live terminal and bounded, filterable structured logs
- Filters for `BACKEND`, `NETWORK`, `ENGINE`, `PLATFORM`, warnings, errors, and search text
- Status based on the actual server process and UDP listener—not routine console `(E)` messages
- Guided server configuration editor and raw JSON editor
- JSON validation, atomic saves, and timestamped backup retention
- Configurable server name, scenario, ports, player limit, crossplay, visibility, BattlEye, view distances, grass distance, and third-person setting
- Official Arma Reforger Workshop browser with search, paging, cards, metadata, thumbnails, and dependency discovery
- Add and remove root Workshop mods directly from `config.json`
- Kill Feed tab for supported console kill events
- Admin Events tab for joins, leaves, disconnects, and detected Game Master activity
- Active-player table with session duration and IP/endpoint when the server log exposes it
- UDP listener diagnostics, LAN target detection, firewall-rule helper, and local-client launcher
- Responsive ForgeStudio blue UI with an adaptive navigation rail, wrapping cards, custom window controls, and dark-only control surfaces
- Dashboard quick actions, copyable local connection target, and keyboard navigation shortcuts


## Responsive interface

The main navigation automatically changes between a left sidebar and a horizontally scrollable top rail based on the window width. Dashboard metrics adapt between four, two, and one columns, while configuration, settings, SteamCMD, and Workshop cards wrap to the available space.

Keyboard shortcuts:

| Shortcut | Destination |
|---|---|
| `Ctrl+1` through `Ctrl+8` | Dashboard through SteamCMD |
| `Ctrl+,` | Settings |
| Double-click title bar | Maximize or restore |

## Performance and stability work in v0.2.0

- Server output is queued and applied to the UI in batches instead of dispatching every line independently.
- The terminal view is bounded so long-running servers do not consume unlimited memory.
- Log, kill-feed, and admin-event collections have retention limits.
- DataGrid row and column virtualization uses recycling mode.
- UDP listener state is collected once per status update and reused throughout the dashboard.
- External-process detection is throttled rather than scanning every second.
- Attached-server log discovery is throttled while live log reads remain responsive.
- Workshop metadata uses a short-lived cache and limited parallel requests.
- Config and settings writes are serialized, atomic, and protected against overlapping saves.
- Config backups are automatically limited to the newest 50 files.
- Reforger stderr lines are no longer automatically classified as errors unless an explicit marker or error text supports it.

## Project layout

```text
ForgeManager\
├── ForgeManager.sln
├── build.ps1
├── verify.ps1
├── publish-self-contained.ps1
├── publish-drop-in.ps1
├── diagnose-network.ps1
├── configs\
│   └── config.minimal.json
└── src\
    └── ForgeManager\
        ├── ForgeManager.csproj
        ├── App.xaml
        ├── MainWindow.xaml
        ├── Models\
        └── Services\
```

## Build in Visual Studio

1. Install Visual Studio 2022 with **.NET desktop development**.
2. Open `ForgeManager.sln`.
3. Select `Release` and `Any CPU` or `x64`.
4. Choose **Build → Rebuild Solution**.

You can also run:

```powershell
.\build.ps1
```

For a fast source, XAML, JSON, event-handler, and responsive-UI verification pass:

```powershell
powershell -ExecutionPolicy Bypass -File .\verify.ps1 -StaticOnly
```

For the full static, Debug, Release, and self-contained publish verification pass:

```powershell
powershell -ExecutionPolicy Bypass -File .\verify.ps1
```

## Install or update the server with SteamCMD

Open the **SteamCMD Setup** tab, choose separate SteamCMD and server installation folders, select **Stable** or **Experimental**, and run **Install SteamCMD + Server**. ForgeManager downloads and safely extracts SteamCMD, installs or repairs the selected Arma Reforger Server branch, creates the profile/config folders, and updates the manager paths.

The included `configs\config.minimal.json` is copied into build and publish output so a new server installation can create an initial `configs\config.json`.

## Publish a self-contained Windows executable

```powershell
.\publish-self-contained.ps1
```

Output:

```text
publish\win-x64\ForgeManager.exe
```

## Install directly into an Arma Reforger server directory

```powershell
.\publish-drop-in.ps1 -ServerRoot "C:\Path\To\Arma Reforger Server"
```

Expected runtime layout:

```text
Arma Reforger Server\
├── ArmaReforgerServer.exe
├── ForgeManager.exe
├── forgemanager.settings.json
├── configs\
│   └── config.json
└── profile\
```

When `ForgeManager.exe` is beside `ArmaReforgerServer.exe`, drop-in mode is authoritative. ForgeManager automatically uses:

```text
.\ArmaReforgerServer.exe
.\configs\config.json
.\profile
```

For non-portable development runs, settings are stored at:

```text
%LOCALAPPDATA%\ForgeManager\forgemanager.settings.json
```

## Server status meaning

| Status | Meaning |
|---|---|
| `OFFLINE` | No matching server process is running. |
| `STARTING` | The process exists, but the configured UDP listener is not active yet. |
| `RUNNING` | The server process exists and the UDP listener is active. |
| `ERROR` | Starting the process failed or it exited unexpectedly and is no longer running. |

Routine Reforger console errors remain visible in the Terminal and Log Table, but they do not change a running server's main status to `ERROR`.

## BAT-started server attachment

ForgeManager detects an existing `ArmaReforgerServer.exe` matching the selected installation and attaches to its PID, uptime, listener, and newest `profile\logs\...\console.log`.

Windows does not allow another application to reclaim the original stdin/stdout handles of an already-running console process. For attached instances, ForgeManager tails `console.log`; command input is disabled, while status, logs, stop, restart, configuration, mods, kill feed, admin events, and network tools remain available.

## Notes

- Kill, player, IP, and Game Master visibility depends on what the active scenario and installed mods write to `console.log`.
- Workshop metadata is read from public official Arma Reforger Workshop pages. Website layout changes may require parser updates.
- Protect `config.json`; it can contain the server admin password and administrator identifiers. New sample configs leave the admin password blank instead of shipping a predictable default.
- Executables are launched with structured argument lists rather than shell-built commands.
