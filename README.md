# Win Gemini Wrapper (Windows 11 Native)

A native Windows desktop wrapper for Gemini chat using `WinForms + WebView2`.

## What it does

- Native Windows app (`.NET`, no Electron).
- Uses embedded Edge (`WebView2`) to render Gemini exactly as in the browser.
- Startup sign-in gate:
  - If an existing Google/Gemini session is present, the login window stays hidden and the app opens directly.
  - If not signed in, a login window appears and uses the normal Google sign-in flow.
- System tray support:
  - Close/minimize hides the app to tray.
  - Tray menu provides `Open Gemini`, `Refresh`, and `Exit`.
- Persistent session/profile data is stored under:
  - `%LOCALAPPDATA%\WinGeminiWrapper\WebView2`

## Prerequisites

- Windows 11
- .NET 8 SDK
- WebView2 Runtime (usually already installed with Microsoft Edge)

## Build and run

```powershell
dotnet restore
dotnet run
```

## Project files

- `Program.cs` - startup flow (login gate then main window)
- `LoginForm.cs` - hidden-first login/session checker
- `MainForm.cs` - Gemini wrapper window + tray integration
- `NavigationClassifier.cs` - URL-based sign-in/session detection
- `AppConfig.cs` - shared URLs and persistent profile path
