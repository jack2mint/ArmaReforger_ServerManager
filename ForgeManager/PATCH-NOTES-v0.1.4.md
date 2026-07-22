# ForgeManager v0.1.4

## External server attachment

- Detects `ArmaReforgerServer.exe` instances started by BAT files, CMD, PowerShell, Steam, or another launcher.
- Matches the running process to the server executable beside/configured for the manager.
- Attaches to the existing PID instead of starting a duplicate server instance.
- Rechecks every second, so a server started after the manager opens is attached automatically.
- Shows `PID <id> · Attached` for external instances and `PID <id> · Managed` for manager-created instances.
- Tails the newest `profile\logs\...\console.log` file to populate the built-in terminal and filterable log table.
- Closing the manager detaches from an externally started server without stopping it.
- Stop/restart actions require confirmation before terminating an externally started process.
- Terminal stdin is disabled for attached instances because Windows cannot reclaim the standard-input pipe of an already-running console process.

## Status behavior

- Console `(E)` entries no longer change the main server status to Error.
- Main status is based on process and UDP-listener state:
  - `STARTING`: process exists but the configured UDP listener is not ready yet.
  - `RUNNING`: process exists and the configured UDP listener is active.
  - `ERROR`: startup fails or the server exits unexpectedly with a non-zero code.
  - `OFFLINE`: no server process is running or it was stopped normally.
