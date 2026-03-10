Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sourceExe = Join-Path $PSScriptRoot "WinGeminiWrapper.exe"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\Win Gemini"
$installedExe = Join-Path $installDir "WinGeminiWrapper.exe"

if (-not (Test-Path $sourceExe)) {
    throw "Payload executable not found: $sourceExe"
}

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path $sourceExe -Destination $installedExe -Force

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenuDir "Win Gemini.lnk"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = $installedExe
$shortcut.Save()

Start-Process -FilePath $installedExe
