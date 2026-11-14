# CleanSlate

<p align="center">
  <strong>A sleek, lightweight, and modern desktop dock for Windows 10 & 11.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?logo=windows" alt="Platform: Windows" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License: MIT" />
</p>

---

## Overview

**CleanSlate** is a high-performance Windows desktop application designed to declutter your workspace. It gathers your scattered desktop shortcuts into an elegant, responsive dock or a customizable floating mini-desktop, keeping your desktop clean while keeping all your apps, games, and tools one click away.

---

## Key Features

- **Universal Icon Extraction:**
  High-fidelity shell icon extraction supporting Win32 executables, `.lnk` shortcuts, `.url` bookmarks, Steam games, PWAs, and Windows Store apps. Includes auto-crop margin detection for crisp icon rendering.

- **Dual Display Modes:**
  - **Floating Dock:** Classic dock layout (horizontal or vertical) with smooth hover scaling, auto-hide, and multi-monitor alignment.
  - **Mini Desktop:** A floating, resizeable window layout with customizable column/row sizing, smart grid packing, and zero micro-scrolling.

- **Recycle Bin Integration:**
  Full Windows shell integration for the Recycle Bin with live icon status (empty/full), context menus, drag-and-drop deletion, and registry-safe toggling.

- **Keyboard-First Navigation:**
  Customizable global hotkeys and instant search to filter and launch apps in milliseconds.

- **Non-Destructive Restoration:**
  CleanSlate preserves your exact desktop icon positions. If you ever uninstall or toggle the dock off, every shortcut is restored to its precise coordinates on your desktop.

- **Lightweight & Native:**
  Built with C# and WPF on .NET 8. Consumes virtually zero idle CPU cycles with minimal memory usage.

---

## Installation

### Recommended: Setup Installer
1. Head over to the [Releases](https://github.com/Satsugingg/CleanSlate/releases) page.
2. Download the latest `Setup.exe`.
3. Run `Setup.exe` and follow the on-screen prompts.
4. CleanSlate will launch automatically and can be configured to start with Windows.

### Manual / Portable
Download `CleanSlate-Portable.zip` from Releases, extract to any folder, and run `CleanSlate.exe`.

---

## Building from Source

### Prerequisites
- Windows 10 (version 1809+) or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 (with .NET desktop development workload) or Visual Studio Code

### Clone & Build
```powershell
# Clone the repository
git clone https://github.com/Satsugingg/CleanSlate.git
cd CleanSlate

# Restore and build the solution
dotnet build CleanSlate.sln -c Release
```

The compiled binaries will be output to:
- `CleanSlate/bin/Release/net8.0-windows/`
- `CleanSlate.Settings/bin/Release/net8.0-windows/`
- `installer/src/CleanSlate.Installer/bin/Release/net8.0-windows/win-x64/`
- `installer/src/CleanSlate.Uninstaller/bin/Release/net8.0-windows/win-x64/`

---

## Project Structure

```
CleanSlate/
├── CleanSlate/                   # Main dock window, background watchers, icon extraction & UI
│   ├── Core/                     # Shell hooks, desktop watcher, position helpers, config service
│   ├── Models/                   # Data models (DockItem, AppConfig)
│   ├── ViewModels/               # Dock view model and interaction logic
│   └── Views/                    # WPF XAML views, dock bar, mini desktop, search overlay
├── CleanSlate.Settings/          # Dedicated Settings UI & configuration editor
├── installer/
│   ├── src/
│   │   ├── CleanSlate.Installer/   # Setup wizard GUI application
│   │   └── CleanSlate.Uninstaller/ # Uninstallation wizard & position restoration
│   ├── Install-CleanSlate.ps1    # PowerShell silent installation helper
│   └── Uninstall-CleanSlate.ps1  # PowerShell uninstallation helper
├── .github/workflows/           # GitHub Actions CI/CD workflows
└── CleanSlate.sln                # Unified Visual Studio Solution
```

---

## Configuration

Settings are saved in `%LocalAppData%\CleanSlate\config.json`.
You can customize:
- Dock position (Top, Bottom, Left, Right)
- Dock display style (Classic Dock vs. Mini Desktop)
- Mini desktop dimensions and column counts
- Auto-hide sensitivity and animation timings
- Hotkeys and search triggers
- Startup behavior

---

## License

This project is licensed under the [MIT License](LICENSE).
