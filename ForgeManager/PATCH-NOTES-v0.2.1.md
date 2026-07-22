# ForgeManager v0.2.1

## SteamCMD automatic server setup
- Added a dedicated SteamCMD Setup tab.
- Downloads and safely extracts SteamCMD automatically.
- Installs stable Arma Reforger Server (App 1874900) or Experimental (App 1890870).
- Supports update, validate/repair, cancellation, live output, download progress, and chosen folders.
- Automatically updates ForgeManager server/config/profile paths after installation.
- Creates profile/config directories and copies the included minimal config when available.
- Persists SteamCMD folder, server install folder, and release channel.
- Includes archive path traversal protection and avoids shell command construction.

## Build and reliability hotfix
- Added explicit `System.IO` and other framework imports to `SteamCmdService.cs`, fixing Visual Studio builds where `Path`, `Directory`, and `File` were not resolved.
- Replaced the constructed SteamCMD command string with `ProcessStartInfo.ArgumentList` so selected paths are passed as literal arguments.
- Fixed cancellation/disposal lifecycle handling so a cancelled SteamCMD process does not leave ForgeManager stuck in a busy state.
- Added archive entry, expansion-size, symbolic-link, and path traversal protections.
- Added stable/experimental App ID allow-list validation.
- Included `configs/config.minimal.json` in build and publish output so a fresh SteamCMD installation can create its initial server config.
- Disabled SteamCMD path/channel controls during an active operation and safely cancels SteamCMD when ForgeManager closes.

- Removed the predictable `AdminPasswordHere` default from new configs and rejects that legacy example value during validation.
- Corrected unexpected-exit status handling so any non-manual server exit becomes `ERROR`, even when the process returns exit code 0.
- Removed a duplicate kill-feed retention call.
- Made existing-server detection safe on first launch when no server executable/profile path has been configured yet.
- Made config backup names collision-resistant during rapid consecutive saves.

## Compiler cleanup hotfix
- Corrected `ObjectDisposedException` / `InvalidOperationException` catch ordering in `SteamCmdService.cs`, resolving CS0160 at both reported locations.
- Removed the nullable delegate mismatch in `AppSettingsService.cs` by filtering nullable path candidates before `Path.GetFullPath`.
- Added an explicit non-null console-log path guard in `ServerProcessService.cs`, resolving the possible-null `FileStream` argument warning.
- Optimized `verify.ps1` to perform one runtime-aware restore and reuse it for Debug, Release, and win-x64 publish checks.
- Fixed the XAML event-handler verifier so properties such as `IsChecked="True"` are not misread as `Checked` event handlers.


## Responsive UI redesign
- Replaced the rigid top-tab layout with a modern ForgeStudio blue application shell.
- Added a wide-window left navigation rail that automatically switches to a scrollable top rail on compact windows.
- Added custom borderless window controls with resize-border support and double-click maximize/restore.
- Rebuilt the Dashboard with adaptive four-, two-, and one-column KPI cards.
- Dashboard configuration and diagnostics cards automatically stack at smaller widths.
- Added dashboard jump actions for Terminal, Logs, and Configuration, plus a copyable local connection target.
- Rebuilt Guided Configuration and Settings as wrapping field-card layouts instead of fixed-width grids.
- Rebuilt SteamCMD setup into a clearer responsive installation workflow with a dedicated console surface.
- Rebuilt configured and available Workshop cards so metadata and actions wrap instead of clipping.
- Added dark custom ComboBox and GroupBox templates to prevent system-white surfaces.
- Added `Ctrl+1` through `Ctrl+8` navigation and `Ctrl+,` for Settings.
- Added `verify.ps1 -StaticOnly` for fast UI/source checks before the full build and publish pass.
