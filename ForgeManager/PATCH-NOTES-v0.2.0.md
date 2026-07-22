# ForgeManager v0.2.0

## Rebrand

- Renamed the solution, project, assembly, namespaces, executable, UI, scripts, settings, firewall rule, documentation, and package layout to **ForgeManager**.
- Added the product description: **An Arma Reforger server GUI interface for Windows 10/11.**
- Added an in-app feature summary to the main header.

## Optimization

- Batched high-volume console processing through a concurrent queue and UI flush timer.
- Bounded terminal, structured-log, kill-feed, and admin-event memory usage.
- Enabled recycling virtualization on data tables.
- Reused a single UDP listener snapshot per dashboard update.
- Throttled external-process scans and attached-log discovery.
- Added Workshop metadata caching and retained limited parallel requests.
- Serialized settings/config writes and retained only the newest 50 config backups.
- Corrected Workshop dependency traversal structure.
- Improved log severity detection so normal stderr output is not automatically treated as an error.

## Status behavior

- Console `(E)` lines remain visible and filterable.
- The main status only shows `ERROR` when startup fails or the server exits unexpectedly and is no longer running.
- A live process is shown as `STARTING` until its UDP listener becomes active, then `RUNNING`.
