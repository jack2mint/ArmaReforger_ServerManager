# ForgeManager v0.1.1

## Compiler fix

Added an explicit `using System.IO;` directive to:

- `MainWindow.xaml.cs`
- `Services/AppSettingsService.cs`
- `Services/ConfigService.cs`
- `Services/NetworkService.cs`
- `Services/ServerProcessService.cs`

This resolves unresolved references to `File`, `Path`, `Directory`, `InvalidDataException`, and `FileNotFoundException`, even when SDK implicit usings are unavailable or overridden.

The project version metadata was updated to `0.1.1`.
