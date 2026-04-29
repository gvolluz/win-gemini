# Win Gemini Wrapper

Windows desktop app (`WinForms + WebView2`) to keep Gemini/NotebookLM/Drive in one native shell, plus a dedicated Evernote -> Markdown export workspace with optional Google Drive sync.

Current app version in source: `2.1.4`.

**DISCLAIMER**
1) This is initially a pure personal app, just for my own use case, but feel free to use it at your own risk
2) It is "vibe" written *by* Codex, not a single line is my own (apart maybe this disclaimer :D); I do not write C# code myself, but I'm nonetheless a fullstack dev, so it should be not that bad... I just didn't want to learn C#
3) Translations have been done by Codex 5.3 in April 2026, so quality is what it is (no, I do not natively speak Russian... or even 95% of the other languages supported :D)
4) Feel free to pull request if you have improvements, especially in the translations (sub-disclaimer: if any translation is absolute BS or even worse, insulting: feel free to blame Codex :P)

## What the app includes today

- Wrapped apps: `Gemini`, `NotebookLM`, `Google Drive`, `Evernote Export`.
- One persistent `WebView2` per web app, so switching tabs does not recreate the browser each time.
- Login gate at startup:
  - If an existing Google session is already valid, login form auto-completes in background.
  - Otherwise the sign-in window is shown.
- Top bar auto-reveal on mouse hover near top edge.
- Last URL persistence per wrapped app (with URL domain guard per app type).
- Window size/position/state persistence.
- Tray behavior: hide/restore on click, app switch, refresh, settings, logout, exit.
- Logout action clears `WebView2` cookies and browsing data.
- UI localization (`auto` + many language codes from `Core/UiLanguageCatalog.cs`).
- Optional debug logs (`%LOCALAPPDATA%\WinGemini\logs`).

## Evernote export workspace

- Reads Evernote local DB from a selected root folder (`UDB-User*+RemoteGraph.sql` auto-detected under it).
- Displays stacks and notebooks in a tree.
- Lets you select notebooks, ignore nodes, and define custom output base filename per stack/notebook.
- Exports selected notes to Markdown in `ExportEvernote\`.
- Creates timestamped backups in `ExportEvernote\backups\`.
- Prunes old backups based on configurable max backup count.
- Polls Evernote changes and can auto-export changed notebooks.

## Google Drive features

- OAuth connect flow from Settings.
  - Today, the app expects a Google OAuth Desktop client JSON (`client_id` / `client_secret`), either auto-detected in local defaults or selected manually once.
- Sync local shared config JSON to Google Drive.
- Optional auto-restore of config from Drive at startup.
- Sync exported Markdown files to Drive (`Apps/WinGemini/ExportEvernote`).
- Optional Markdown -> Google Docs conversion/update.
- Distributed polling lock state in Drive to coordinate auto-polling between multiple machines/instances.

### Create the OAuth JSON (official Google flow)

If you want to use Drive sync/export features, create an OAuth client of type **Desktop app** and download its JSON.

Official docs:
- Create OAuth credentials (includes the "Desktop app" steps): https://developers.google.com/workspace/guides/create-credentials
- Configure OAuth consent screen and scopes: https://developers.google.com/workspace/guides/configure-oauth-consent
- Manage OAuth clients in Google Auth Platform (console help): https://support.google.com/cloud/answer/6158849

Quick steps:
1. Open Google Cloud project and enable the needed APIs (Drive API at minimum).
2. Configure OAuth consent screen.
3. Create OAuth client with type **Desktop app**.
4. Download the JSON credentials file.
5. In WinGemini Settings, connect using that file (or place it in the default detected location).

Note: the current approach requires this one-time setup. The long-term UX target is a direct in-app "just sign in with Google" flow.

## Settings currently available

- UI language.
- Close button behavior (`Minimize to tray` or `Close app`).
- Enable/disable debug logging.
- Enable/disable Google Drive sync.
- Enable/disable Google Drive auto-restore on startup.
- Connect Google OAuth credentials.
- Optional config file ID override for Drive.
- Export settings JSON.
- Import settings JSON.

## Local data paths

- WebView profile/session:
  - `%LOCALAPPDATA%\WinGemini\WebView2`
- Main local config/state:
  - `%LOCALAPPDATA%\WinGemini\local-config.json`
- Legacy state path still written/read for compatibility:
  - `%LOCALAPPDATA%\WinGemini\app-state.json`
- OAuth client cache file (when imported from file dialog):
  - `%LOCALAPPDATA%\WinGemini\google-oauth-client.json`
- OAuth token store:
  - `%LOCALAPPDATA%\WinGemini\GoogleDriveTokens`
- Logs:
  - `%LOCALAPPDATA%\WinGemini\logs`

## Requirements

- Windows (project target: `net8.0-windows`).
- .NET 8 SDK for development.
- Microsoft Edge WebView2 Runtime.
- For Drive features: Google OAuth desktop client credentials.

## Run from source

```powershell
dotnet restore
dotnet run
```

## Build release executable and installer

```powershell
.\build-installer.ps1
```

Outputs:

- App EXE (single-file): `artifacts\publish\win-x64\WinGemini.exe`
- Installer EXE: `artifacts\installer\WinGeminiSetup-<version>-win-x64.exe`

Notes:

- The app is published `self-contained false` (runtime not bundled).
- Installer can prompt to download missing prerequisites:
  - `.NET Windows Desktop Runtime 8.x`
  - `Microsoft Edge WebView2 Runtime`
- If Inno Setup (`ISCC.exe`) is not found, the build script falls back to an IExpress installer.

## Project layout

- `Program.cs`: startup and global exception logging.
- `Core/`: app config, navigation, localization, versioning, logging.
- `Models/`: app state and enums.
- `Services/Evernote/`: Evernote DB reading + markdown conversion + change detection.
- `Services/GoogleDrive/`: OAuth + config sync + markdown/docs sync + distributed polling state files.
- `UI/Forms/`: `LoginForm`, `MainForm`, `SettingsForm`.
- `installer/`: setup scripts and Inno Setup descriptor.

## License

This project is licensed under the MIT License.

Reference: see [LICENSE](./LICENSE).  
MIT keeps the project permissive (use, modify, fork, redistribute), with preservation of copyright and license notice.

