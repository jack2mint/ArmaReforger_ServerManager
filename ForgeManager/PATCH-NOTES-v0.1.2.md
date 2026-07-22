# ForgeManager v0.1.2 UI Theme Patch

## Fixed

- Removed the white application background around all cards and panels.
- Replaced WPF's default light TabControl and TabItem surfaces with fully dark custom templates.
- Added an explicit dark background directly to MainWindow and its root layout so the theme does not depend on implicit Window-style inheritance.
- Added a dark Windows title-bar request through DWM on supported Windows 10/11 systems.
- Replaced the system-white checkbox with a custom mint/dark checkbox template.
- Hardened dark styling for text boxes, password boxes, rich text, combo items, data grids, group boxes, tooltips, and selection states.
- Fixed button templates so border brushes and border thicknesses are actually rendered.
- Preserved the existing responsive WrapPanel dashboard and toolbar behavior.

## Validation performed

- XAML and project XML parsed successfully.
- Event-handler references were checked against code-behind.
- JSON files parsed successfully.
- Source ZIP integrity was tested after packaging.

A full WPF compiler run still needs Visual Studio/.NET 8 on Windows because this packaging environment does not include the .NET SDK.
