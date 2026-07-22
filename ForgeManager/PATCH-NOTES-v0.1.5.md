# ForgeManager v0.1.5

## Kill Feed

- Adds a dedicated Kill Feed tab backed by live server-console parsing.
- Displays time, result type, killer, victim, weapon, and distance when those values are present in the log line.
- Marks friendly-fire and suicide events separately.
- Resolves numeric player references against the active-player table when possible.
- Keeps raw console errors independent from the main server status.
- Important: the manager can only display kills emitted to the dedicated-server console/log by the active scenario or a logging mod.

## Admin Events and Active Players

- Adds a dedicated Admin Events tab.
- Tracks player join, leave, disconnect, and observed-session records from console output.
- Maintains an active-player table with display name, player ID, endpoint/IP when exposed, connection time, and live connected duration.
- Records detected Game Master/editor events such as enter/leave, spawn, placement, deletion, teleport, possession, and related actions.
- Displays `Not exposed by log` instead of inventing an address when Reforger does not print one.
- Clears the active-player list when the server process stops while preserving event history until manually cleared.

## Configurable Mod List

- Configured root addons can now be removed directly from the Mods tab.
- Adds addons by 16-character Workshop ID or by pasting an official Workshop URL.
- Prevents duplicate explicit entries and avoids adding an addon already resolved as a dependency.
- Saves through the existing atomic config writer and immediately refreshes raw JSON and the Dashboard summary.

## Official Workshop Browser

- Adds an Available Workshop section using the official Arma Reforger Workshop pages.
- Supports Workshop search, pagination, metadata cards, thumbnails, author, version, game version, size, popularity, availability, and direct Workshop links.
- Adds an addon from a Workshop card directly into `configs\config.json`.
- Marks addons that are already configured and disables duplicate-add actions.

## Existing fixes retained

- Attaches to BAT/CMD/PowerShell-started server instances.
- Console `(E)` lines remain visible and filterable but do not change the main status to Error.
- Main status continues to represent process/listener state only.
