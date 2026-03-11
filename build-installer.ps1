param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$IsccPath = "",
    [string]$WebView2InstallerUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
    [string]$DotNetRuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe",
    [string]$RequiredDotNetRuntimePrefix = "8.0",
    [switch]$SkipWebView2Download,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot "WinGeminiWrapper.csproj"
$issFile = Join-Path $projectRoot "installer\WinGeminiWrapper.iss"
$fallbackInstallPs1 = Join-Path $projectRoot "installer\Install-WinGemini.ps1"
$fallbackInstallCmd = Join-Path $projectRoot "installer\install.cmd"
$publishDir = Join-Path $projectRoot "artifacts\publish\$Runtime"
$installerDir = Join-Path $projectRoot "artifacts\installer"

if (-not (Test-Path $projectFile)) {
    throw "Project file not found: $projectFile"
}

if (-not $Version) {
    [xml]$projectXml = Get-Content -Path $projectFile
    $versionNode = $projectXml.SelectSingleNode("//Project/PropertyGroup/Version")
    if ($versionNode) {
        $Version = $versionNode.InnerText
    } else {
        $Version = "0.0.0"
    }
}

$safeVersion = $Version -replace '[^0-9A-Za-z\.\-_]', '_'
$installerFileName = "WinGeminiSetup-$safeVersion-$Runtime.exe"
$installerOutputDir = $projectRoot
$installerOutputPath = Join-Path $installerOutputDir $installerFileName
$installerOutputBaseName = [System.IO.Path]::GetFileNameWithoutExtension($installerFileName)

if ($SkipWebView2Download) {
    Write-Warning "-SkipWebView2Download is no longer used because prerequisites are downloaded during installation."
}

Write-Host "Publishing single-file app executable..." -ForegroundColor Cyan
dotnet publish $projectFile `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:PublishTrimmed=false `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$appExe = Join-Path $publishDir "WinGeminiWrapper.exe"
if (-not (Test-Path $appExe)) {
    throw "Publish completed but app executable was not found: $appExe"
}

Write-Host "App EXE ready:" -ForegroundColor Green
Write-Host "  $appExe"

if ($SkipInstaller) {
    Write-Host "Installer step skipped because -SkipInstaller was provided." -ForegroundColor Yellow
    exit 0
}

if (-not $IsccPath) {
    try {
        $IsccPath = (Get-Command ISCC.exe -ErrorAction Stop).Source
    } catch {
        $candidates = @(
            (Join-Path $projectRoot "tools\InnoSetup\ISCC.exe"),
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe"
        )
        foreach ($candidate in $candidates) {
            if (Test-Path $candidate) {
                $IsccPath = $candidate
                break
            }
        }
    }
}

if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    Write-Warning "Inno Setup compiler (ISCC.exe) was not found. Falling back to IExpress."
    New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

    if (-not (Test-Path $fallbackInstallPs1) -or -not (Test-Path $fallbackInstallCmd)) {
        throw "Fallback installer scripts are missing in the installer folder."
    }

    $payloadDir = Join-Path $installerDir "payload"
    New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

    Copy-Item -Path $appExe -Destination (Join-Path $payloadDir "WinGeminiWrapper.exe") -Force
    Copy-Item -Path $fallbackInstallPs1 -Destination (Join-Path $payloadDir "Install-WinGemini.ps1") -Force
    Copy-Item -Path $fallbackInstallCmd -Destination (Join-Path $payloadDir "install.cmd") -Force

    $setupExe = $installerOutputPath
    $sedFile = Join-Path $installerDir "WinGeminiWrapper-iexpress.sed"
    $iExpressPath = Join-Path $env:WINDIR "System32\iexpress.exe"

    if (-not (Test-Path $iExpressPath)) {
        throw "IExpress was not found at: $iExpressPath"
    }

    $sedContent = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=1
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=Win Gemini has been installed.
TargetName=$setupExe
FriendlyName=Win Gemini Setup
AppLaunched=install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=install.cmd
UserQuietInstCmd=install.cmd
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$payloadDir\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
[Strings]
FILE0="WinGeminiWrapper.exe"
FILE1="Install-WinGemini.ps1"
FILE2="install.cmd"
"@

    Set-Content -Path $sedFile -Value $sedContent -Encoding ASCII

    Write-Host "Building installer executable with IExpress..." -ForegroundColor Cyan
    & $iExpressPath /N /Q /M $sedFile

    if ($LASTEXITCODE -ne 0) {
        throw "IExpress failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path $setupExe)) {
        throw "IExpress completed but expected setup path was not found: $setupExe"
    }

    Write-Host "Installer EXE ready:" -ForegroundColor Green
    Write-Host "  $setupExe"
    exit 0
}

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

Write-Host "Building installer executable..." -ForegroundColor Cyan
& $IsccPath `
    "/DAppVersion=$Version" `
    "/DRuntime=$Runtime" `
    "/DSourceDir=$publishDir" `
    "/DOutputDir=$installerOutputDir" `
    "/DOutputBaseFilename=$installerOutputBaseName" `
    "/DDotNetRuntimeUrl=$DotNetRuntimeUrl" `
    "/DRequiredDotNetRuntimePrefix=$RequiredDotNetRuntimePrefix" `
    "/DWebView2BootstrapperUrl=$WebView2InstallerUrl" `
    "/DProjectDir=$projectRoot" `
    $issFile

if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE."
}

$setupExe = $installerOutputPath
if (Test-Path $setupExe) {
    Write-Host "Installer EXE ready:" -ForegroundColor Green
    Write-Host "  $setupExe"
} else {
    Write-Warning "ISCC completed but expected setup path was not found: $setupExe"
}
