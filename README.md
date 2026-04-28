# Win Gemini Wrapper (Windows 11 Native)

A native Windows desktop app (`WinForms + WebView2`) that wraps multiple Google apps and includes an Evernote export workspace with optional Google Drive sync.

## Wrapped apps

The top switcher lets you move between:

- `Gemini`
- `NotebookLM`
- `Google Drive`
- `Evernote Export` (local DB reading + markdown export + Drive sync options)

Each web app keeps its own live `WebView2` instance, so switching apps does not force a full reload every time.

## Main features

- Native Windows app (`.NET`, no Electron).
- Embedded Edge (`WebView2`) rendering for wrapped web apps.
- Auto-hidden top bar (revealed when hovering the top edge).
- Last URL persisted per wrapped app and restored on startup.
- Window position/size/state persisted and restored.
- Tray workflow: minimize-to-tray, quick app switch, refresh, settings, exit.
- Logout action clears local `WebView2` session data.
- Startup login gate:
  - If an existing Google session exists, login form stays hidden.
  - Otherwise a login window opens and follows standard Google sign-in.

## Evernote Export workspace

The `Evernote Export` app is a dedicated configuration/export UI:

- Reads Evernote local DB (`UDB-User*+RemoteGraph.sql`) from a selected root folder.
- Displays stacks/notebooks and lets you select what to export.
- Exports selected notes to markdown files under `ExportEvernote\`.
- Keeps timestamped backups and prunes old backups based on settings.
- Supports automatic polling of note changes.
- Supports automatic export when changes are detected.
- Can automatically sync exported markdown files to Google Drive.
- Can convert primary markdown exports to Google Docs.

## Google Drive sync (config + exports)

The app supports Google OAuth and Drive sync features:

- OAuth connection from Settings.
- Sync of local app config JSON to Drive.
- Optional auto-restore of config from Drive at startup.
- Sync of Evernote markdown exports to Drive folders.
- In-place update of existing Google Docs (keeps the same `fileId` when possible).

## Settings available

- Close button behavior: `Minimize to tray` or `Close app`.
- Evernote polling frequency (minutes).
- Pause/resume automatic polling.
- Max markdown backup files to keep.
- Google Drive sync enable/disable.
- Google Drive auto-restore on startup.
- OAuth client setup and connect flow.
- Optional Drive config file ID override.

## Local storage

- WebView profile/session data:
  - `%LOCALAPPDATA%\WinGeminiWrapper\WebView2`
- Local app config/state:
  - `%LOCALAPPDATA%\WinGeminiWrapper\local-config.json`
- Legacy state path still supported for migration:
  - `%LOCALAPPDATA%\WinGeminiWrapper\app-state.json`

## Prerequisites

- Windows 11
- .NET 8 SDK
- WebView2 Runtime (usually already installed with Microsoft Edge)

## Build and run

```powershell
dotnet restore
dotnet run
```

## Evernote single-note diagnostic (headings/lists)

To debug conversion for one specific note (raw ENML + converted Markdown + ENML structure dump):

```powershell
dotnet run -- --evernote-note-test --note c5592033-94e1-c16c-0416-72c4dc98b61b --root "C:\Path\To\EvernoteRoot" --out ".\tmp\evernote-note-test"
```

If `--note` is omitted, it defaults to `c5592033-94e1-c16c-0416-72c4dc98b61b`.
If `--root` is omitted, the app tries to reuse the Evernote root folder saved in app settings.

## Build release EXE and installer

```powershell
.\build-installer.ps1
```

Outputs:

- App EXE (single-file): `artifacts\publish\win-x64\WinGeminiWrapper.exe`
- Installer EXE: `artifacts\installer\WinGeminiSetup-<version>-win-x64.exe`

The setup EXE stays small by not bundling runtimes.
If missing, the installer offers to download/install:

- .NET Windows Desktop Runtime 8.x
- Microsoft Edge WebView2 Runtime

## Source layout

- `Program.cs` - startup flow (login gate then main window)
- `Core/` - shared app config/providers/navigation helpers
- `Models/` - state models and enums
- `Services/Evernote/` - Evernote local DB reading and markdown conversion
- `Services/GoogleDrive/` - OAuth, config sync, and markdown/doc sync
- `UI/Forms/` - `LoginForm`, `MainForm`, `SettingsForm`

