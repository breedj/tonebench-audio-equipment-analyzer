<#
.SYNOPSIS
    Builds the Windows installer: frontend -> dotnet publish (win-x64) -> Inno Setup.

.DESCRIPTION
    The one script both a developer and .github/workflows/release.yml use, so the publish command
    and the ISCC invocation exist in exactly one place. Output: dist\tonebench-<version>-win-x64-setup.exe.

    -Version       Defaults to <Version> in Directory.Build.props (read through MSBuild, so it stays
                   single-sourced). The workflow passes the git tag without its "v".
    -SkipFrontend  Reuse the wwwroot the last `npm run build` wrote instead of rebuilding it.
    -NoPublish     Skip `dotnet publish` and package -PublishDir as it is (the workflow, which has
                   already published and smoke-tested that folder).
    -Iscc          Path to ISCC.exe when Inno Setup is not in its default location.

.EXAMPLE
    powershell -File packaging\windows\build.ps1
    powershell -File packaging\windows\build.ps1 -Version 1.2.0 -SkipFrontend
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [switch]$SkipFrontend,
    [switch]$NoPublish,
    [string]$PublishDir,
    [string]$Iscc
)

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $root "src\Tonebench.App\Tonebench.App.csproj"
if (-not $PublishDir) { $PublishDir = Join-Path $root "publish\win-x64" }
# Absolute, because Inno Setup resolves a relative Source: against the .iss file's own folder.
# Only a relative -PublishDir is resolved against the caller's directory: Join-Path concatenates an
# already-rooted path onto it ("C:\code\C:\publish"), which GetFullPath rejects outright — so the
# default, which is absolute, took the script down before it built anything.
if (-not [System.IO.Path]::IsPathRooted($PublishDir)) {
    $PublishDir = Join-Path (Get-Location) $PublishDir
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)
$distDir = Join-Path $root "dist"
$script = Join-Path $PSScriptRoot "tonebench.iss"

function Invoke-Step([string]$What, [scriptblock]$Command) {
    Write-Host ""
    Write-Host "==> $What" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

if (-not $Version) {
    $Version = (& dotnet msbuild $project -getProperty:Version -nologo).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $Version) { throw "Could not read <Version> from the project" }
}
Write-Host "Version $Version"

# A vendored native DLL must not need the Visual C++ redistributable, and this is the check rather
# than a note, because nothing about the failure points at it: the DLL ships *inside* the exe and is
# self-extracted, so it is present and correct on the user's disk while Windows reports its missing
# dependency with the same status as a missing file — which .NET surfaces as
# "DllNotFoundException: Dll was not found." Seen in the wild 2026-11-05 on Windows 10 1903, where
# libusb was upstream's VS2022 build (/MD, so it imports VCRUNTIME140.dll) and the QA402 could never
# attach. tonebench.iss is PrivilegesRequired=lowest and cannot install a redistributable, and
# INSTALL.md promises none is needed, so the dependency is the thing that has to go: portaudio.dll is
# built with /MT (packaging/native/portaudio.md) and libusb-1.0.dll is upstream 1.0.30's MinGW64
# build, which needs only the in-box msvcrt.dll (packaging/native/libusb.md).
# Scanned out of the import names rather than read with dumpbin, which is not on every runner — the
# same technique as the bundle check further down, and enough: a DLL cannot import VCRUNTIME140
# without naming it.
foreach ($native in @("libusb-1.0.dll", "portaudio.dll")) {
    $nativePath = Join-Path $root "native\win-x64\$native"
    if (-not (Test-Path $nativePath)) { throw "Vendored native library is missing: $nativePath" }
    $nativeText = [System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($nativePath))
    foreach ($forbidden in @("VCRUNTIME", "MSVCP")) {
        if ($nativeText.Contains($forbidden)) {
            throw "$native names $forbidden, so it needs the Visual C++ redistributable that this per-user installer cannot supply — see packaging/native/libusb.md"
        }
    }
}

if (-not $NoPublish) {
    if (-not $SkipFrontend) {
        Push-Location (Join-Path $root "web")
        try {
            Invoke-Step "npm ci" { npm ci }
            Invoke-Step "npm run build" { npm run build }
        } finally { Pop-Location }
    }

    # The same one-liner as release.yml and README.md; the single-file/trimmed/self-contained shape
    # is in Tonebench.App.csproj under the publish condition.
    Invoke-Step "dotnet publish (win-x64)" {
        dotnet publish $project -c $Configuration -r win-x64 -p:Version=$Version -o $PublishDir --nologo
    }
}

# --simulate smoke tests never load libusb or portaudio, so a publish that lost either DLL passes
# them. Both travel inside the single-file exe (the bundle manifest names them), and QuestPDF's font
# archive beside it; this is the check that catches any of them going missing before the installer
# ships without it.
foreach ($required in @("Tonebench.App.exe", "QuestPDF.Fonts.Lato.br", "appsettings.json")) {
    if (-not (Test-Path (Join-Path $PublishDir $required))) {
        throw "Publish output is missing $required in $PublishDir"
    }
}
$exeBytes = [System.IO.File]::ReadAllBytes((Join-Path $PublishDir "Tonebench.App.exe"))
$exeText = [System.Text.Encoding]::GetEncoding(28591).GetString($exeBytes)
foreach ($native in @("libusb-1.0.dll", "portaudio.dll")) {
    if (-not $exeText.Contains($native)) {
        throw "Tonebench.App.exe does not carry $native in its single-file bundle"
    }
}

if (-not $Iscc) {
    $Iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
}
if (-not (Test-Path $Iscc)) {
    throw "Inno Setup 6 not found at $Iscc (install it from https://jrsoftware.org/isinfo.php or pass -Iscc)"
}

New-Item -ItemType Directory -Force $distDir | Out-Null
Invoke-Step "Inno Setup" {
    & $Iscc "/DAppVersion=$Version" "/DPublishDir=$PublishDir" "/DOutputDir=$distDir" $script
}

$setup = Join-Path $distDir "tonebench-$Version-win-x64-setup.exe"
if (-not (Test-Path $setup)) { throw "Inno Setup reported success but $setup does not exist" }
Write-Host ""
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
