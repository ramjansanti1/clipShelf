# ClipShelf

ClipShelf is a lightweight Windows clipboard history app that lives in the system tray. It captures text, image, and file clipboard entries, lets you restore previous items, and includes a configurable global shortcut.

## Features

- Runs quietly from the Windows system tray
- Saves clipboard history for text, images, and copied files
- Restores selected history items back to the clipboard
- Searchable history window
- Configurable maximum history size
- Configurable global open shortcut
- Optional startup with Windows
- Pause, clear history, settings, and exit controls from the tray menu

## Requirements

- Windows
- .NET SDK 10.0 or newer

Check your installed SDK:

```powershell
dotnet --version
```

## Installation

1. Clone or download this repository.

2. Open PowerShell in the project folder:

```powershell
cd c:\Ayan\clipShelf
```

3. Restore and build the app:

```powershell
dotnet restore
dotnet build -c Release
```

4. Run ClipShelf:

```powershell
.\bin\Release\net10.0-windows\ClipShelf.exe
```

ClipShelf starts in the system tray. Double-click the tray icon, or use the configured shortcut, to open clipboard history.

## Publish A Standalone Build

Create a release folder with the executable and required runtime files:

```powershell
dotnet publish -c Release
```

The published app is created at:

```text
bin\Release\net10.0-windows\publish\
```

Run:

```powershell
.\bin\Release\net10.0-windows\publish\ClipShelf.exe
```

## Usage

- Double-click the tray icon to open clipboard history.
- Use the default shortcut `Ctrl + Alt + V` to open clipboard history.
- Select an item and restore it to place it back on the clipboard.
- Open the tray menu to pause capture, clear history, open settings, or exit.
- In settings, enable **Start with Windows** if you want ClipShelf to launch automatically.

## Data Storage

ClipShelf stores history and settings in:

```text
%APPDATA%\ClipShelf
```

Images copied to the clipboard are stored under:

```text
%APPDATA%\ClipShelf\images
```

Clearing history from the app also removes saved clipboard image files.

## Development

Run a debug build:

```powershell
dotnet run
```

Build a release executable:

```powershell
dotnet build -c Release
```

The executable icon is configured in `ClipShelf.csproj` and uses `Assets\ClipShelf.ico`, which matches the system tray icon drawn by the app.
