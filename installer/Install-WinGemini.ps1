Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sourceExe = Join-Path $PSScriptRoot "WinGemini.exe"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\Win Gemini"
$installedExe = Join-Path $installDir "WinGemini.exe"
$dotNetRuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
$webView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
$dotNetVersionPrefix = "8.0."

if (-not (Test-Path $sourceExe)) {
    throw "Payload executable not found: $sourceExe"
}

function Test-DotNetWindowsDesktopRuntime {
    $keyPath = "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
    if (-not (Test-Path $keyPath)) {
        return $false
    }

    foreach ($item in Get-ChildItem -Path $keyPath -ErrorAction SilentlyContinue) {
        if ($item.PSChildName.StartsWith($dotNetVersionPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-WebView2Runtime {
    $paths = @(
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    )

    foreach ($path in $paths) {
        if (Test-Path $path) {
            $pv = (Get-ItemProperty -Path $path -Name pv -ErrorAction SilentlyContinue).pv
            if ($pv) {
                return $true
            }
        }
    }

    return $false
}

function Install-FromUrl {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$OutFileName,
        [Parameter(Mandatory = $true)][string]$Arguments
    )

    $outFile = Join-Path $env:TEMP $OutFileName
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $outFile
    Start-Process -FilePath $outFile -ArgumentList $Arguments -Wait
}

$missingDotNet = -not (Test-DotNetWindowsDesktopRuntime)
$missingWebView2 = -not (Test-WebView2Runtime)

if ($missingDotNet -or $missingWebView2) {
    Write-Host ""
    Write-Host "Some components appear to be missing and can be downloaded now:" -ForegroundColor Yellow
    if ($missingDotNet) { Write-Host "- .NET Windows Desktop Runtime 8.x" -ForegroundColor Yellow }
    if ($missingWebView2) { Write-Host "- Microsoft Edge WebView2 Runtime" -ForegroundColor Yellow }
    $answer = Read-Host "Download and install these components now? (y/n)"
    if ($answer -in @("y", "Y", "yes", "YES")) {
        if ($missingDotNet) {
            Install-FromUrl -Url $dotNetRuntimeUrl -OutFileName "windowsdesktop-runtime-installer.exe" -Arguments "/install /quiet /norestart"
        }

        if ($missingWebView2) {
            Install-FromUrl -Url $webView2BootstrapperUrl -OutFileName "MicrosoftEdgeWebView2Setup.exe" -Arguments "/silent /install"
        }
    } else {
        Write-Host "Continuing without downloading prerequisites. Setup may fail at runtime if components are truly missing." -ForegroundColor Yellow
    }
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
