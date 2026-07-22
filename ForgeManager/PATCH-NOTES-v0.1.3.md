# ForgeManager v0.1.3

## Fixed

- Added true drop-in mode for placement beside `ArmaReforgerServer.exe`.
- Automatically loads `configs\config.json` and `profile` from the current server directory on every launch.
- Portable settings now live beside the manager instead of inheriting stale LocalAppData paths.
- Local join target follows `bindAddress` and active UDP listeners.
- Launch Local Client waits for an active UDP listener and uses the resolved local target.
- Added local socket compatibility status to the dashboard.
- Added bind/public address fields to guided configuration editing and stopped silently clearing them.
- Added Steam client executable discovery across default and additional Steam libraries.
- Added `publish-drop-in.ps1` for one-command deployment into the server directory.
