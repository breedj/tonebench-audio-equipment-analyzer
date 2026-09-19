<#
.SYNOPSIS
    Builds the Windows installer: frontend -> dotnet publish (win-x64) -> Inno Setup.

.DESCRIPTION
    The one script both a developer and .github/workflows/release.yml use, so the publish command
    and the ISCC invocation exist in exactly one place. Output: dist\qa403-audio-analyzer-<version>-win-x64-setup.exe.

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
$project = Join-Path $root "src\Qa403.App\Qa403.App.csproj"
if (-not $PublishDir) { $PublishDir = Join-Path $root "publish\win-x64" }
# Absolute, because Inno Setup resolves a relative Source: against the .iss file's own folder.
$PublishDir = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $PublishDir))
$distDir = Join-Path $root "dist"
$script = Join-Path $PSScriptRoot "qa403.iss"

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

if (-not $NoPublish) {
    if (-not $SkipFrontend) {
        Push-Location (Join-Path $root "web")
        try {
            Invoke-Step "npm ci" { npm ci }
            Invoke-Step "npm run build" { npm run build }
        } finally { Pop-Location }
    }

    # The same one-liner as release.yml and README.md; the single-file/trimmed/self-contained shape
    # is in Qa403.App.csproj under the publish condition.
    Invoke-Step "dotnet publish (win-x64)" {
        dotnet publish $project -c $Configuration -r win-x64 -p:Version=$Version -o $PublishDir --nologo
    }
}

# --simulate smoke tests never load libusb, so a publish that lost the DLL passes them. It travels
# inside the single-file exe (the bundle manifest names it), and QuestPDF's font archive beside it;
# this is the check that catches either going missing before the installer ships without it.
foreach ($required in @("Qa403.App.exe", "QuestPDF.Fonts.Lato.br", "appsettings.json")) {
    if (-not (Test-Path (Join-Path $PublishDir $required))) {
        throw "Publish output is missing $required in $PublishDir"
    }
}
$exeBytes = [System.IO.File]::ReadAllBytes((Join-Path $PublishDir "Qa403.App.exe"))
if (-not [System.Text.Encoding]::GetEncoding(28591).GetString($exeBytes).Contains("libusb-1.0.dll")) {
    throw "Qa403.App.exe does not carry libusb-1.0.dll in its single-file bundle"
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

$setup = Join-Path $distDir "qa403-audio-analyzer-$Version-win-x64-setup.exe"
if (-not (Test-Path $setup)) { throw "Inno Setup reported success but $setup does not exist" }
Write-Host ""
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
