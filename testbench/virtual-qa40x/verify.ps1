<#
.SYNOPSIS
    PASS/FAIL/KNOWN/SKIP table for the virtual QA40x bench.

.DESCRIPTION
    Checks the prerequisites, the register round-trip against the 20 ms budget that decides whether
    this approach works at all, and the Category=VirtualDevice suite.

    It re-attaches the bench first, and that is deliberate rather than tidy: a server left idle for
    an hour has been seen to come back with its register reply FIFO desynced, which fails every test
    with a mis-read serial and a calibration page of zeros. A harness that trusted whatever was
    already attached would report that as a code regression. -NoRestart skips it if you are debugging
    a live bench and want it left alone.

    -IncludeGate also runs the commit gate, which is the regression that would hurt most: the default
    filter must still pass with a virtual device attached. It is slow, so it is opt-in.

.EXAMPLE
    powershell -File verify.ps1
    powershell -File verify.ps1 -IncludeGate
#>
[CmdletBinding()]
param(
    [switch]$IncludeGate,
    [switch]$NoRestart
)

$ErrorActionPreference = "Continue"

$RegisterBudgetMs = 20.0   # Qa40xTransport.RegisterTimeoutMs, per phase
$UsbipExe = "C:\Program Files\USBip\usbip.exe"
$CodeRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$Stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$LogPath = Join-Path $PSScriptRoot "verify-$Stamp.log"

$results = New-Object System.Collections.ArrayList

function Record([string]$Verdict, [string]$Name, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ Verdict = $Verdict; Name = $Name; Detail = $Detail })
    $colour = switch ($Verdict) {
        "PASS"  { "Green" }
        "FAIL"  { "Red" }
        "KNOWN" { "Yellow" }
        default { "DarkGray" }
    }
    Write-Host ("{0,-6} {1}" -f $Verdict, $Name) -ForegroundColor $colour
    if ($Detail) { Write-Host "       $Detail" -ForegroundColor DarkGray }
    Add-Content -Path $LogPath -Value ("{0,-6} {1}`n       {2}" -f $Verdict, $Name, $Detail)
}

function Invoke-Wsl([string]$Command) {
    return ((& wsl.exe -e bash -lc $Command 2>&1) | Out-String)
}

Write-Host "Virtual QA40x bench verification - $Stamp" -ForegroundColor Cyan
Write-Host "log: $LogPath`n" -ForegroundColor DarkGray

$bench = Join-Path $PSScriptRoot "bench.ps1"

# --- prerequisites -------------------------------------------------------------------------------

if (Test-Path $UsbipExe) {
    Record "PASS" "usbip-win2 installed" $UsbipExe
} else {
    Record "FAIL" "usbip-win2 installed" "winget install -e --id vadimgrn.usbip-win2"
}

$realQa403 = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match "VID_16C0&PID_4E39" }
if ($realQa403) {
    Record "FAIL" "no real QA403 attached" ("found $($realQa403[0].InstanceId) - it wins every probe, " +
        "so the virtual QA402 is unreachable")
} else {
    Record "PASS" "no real QA403 attached" "the probe order cannot shadow the virtual QA402"
}

# --- a known-good bench --------------------------------------------------------------------------

if ($NoRestart) {
    Record "SKIP" "bench restarted for a known state" "-NoRestart given; testing whatever is attached"
} else {
    & $bench -Detach *> $null
    $attachOut = & $bench -Attach 2>&1 | Out-String
    Add-Content -Path $LogPath -Value $attachOut
    if ($LASTEXITCODE -eq 0) {
        Record "PASS" "bench restarted for a known state" "a stale server desyncs its register replies"
    } else {
        Record "FAIL" "bench restarted for a known state" "bench.ps1 -Attach failed; see $LogPath"
    }
}

& $bench -Status *> $null
if ($LASTEXITCODE -eq 0) {
    Record "PASS" "attach chain (bench.ps1 -Status)" "server listening, device present on WINUSB"
} else {
    Record "FAIL" "attach chain (bench.ps1 -Status)" "run bench.ps1 -Attach, or -Build first"
}

# --- the suite -----------------------------------------------------------------------------------
# Before the latency check, because the latency figures come out of the log this run writes.

Push-Location $CodeRoot
try {
    $virtual = & dotnet test --filter "Category=VirtualDevice" --nologo 2>&1 | Out-String
    Add-Content -Path $LogPath -Value $virtual

    $failedMatch = [regex]::Match($virtual, "Failed:\s*(\d+)")
    $passedMatch = [regex]::Match($virtual, "Passed:\s*(\d+)")
    $nFailed = if ($failedMatch.Success) { [int]$failedMatch.Groups[1].Value } else { -1 }
    $nPassed = if ($passedMatch.Success) { $passedMatch.Groups[1].Value } else { "?" }

    if ($nFailed -eq 0) {
        Record "PASS" "Category=VirtualDevice suite" "$nPassed passed"
    } elseif ($virtual -match "ArrivesOnLeftAndNotOnRight.*\[FAIL\]" -and $virtual -match "unpatched") {
        Record "KNOWN" "Category=VirtualDevice suite" "channels crossed - run bench.ps1 -Build to patch the emulator"
    } else {
        Record "FAIL" "Category=VirtualDevice suite" "$nFailed failed - see $LogPath"
    }

    if ($IncludeGate) {
        $gate = & dotnet test Tonebench.sln --filter "Category!=Hardware&Category!=VirtualDevice" --nologo 2>&1 | Out-String
        Add-Content -Path $LogPath -Value $gate
        if ($gate -match "Failed!" -or $gate -match "error\s") {
            Record "FAIL" "commit gate with a virtual device attached" "see $LogPath"
        } else {
            Record "PASS" "commit gate with a virtual device attached" "the virtual tests stayed out of it"
        }
    } else {
        Record "SKIP" "commit gate with a virtual device attached" "pass -IncludeGate to run it"
    }
}
finally {
    Pop-Location
}

# --- the number that decides everything ----------------------------------------------------------
# Printed even on a pass: a clean run says only that the budget was not exceeded, never how much of
# it was used. UNANSWERED counts register reads whose reply never arrived, which is what a stale
# USB/IP connection looks like from the log's side.

$statsScript = (Invoke-Wsl "wslpath -a '$(Join-Path $PSScriptRoot "regstats.py")'").Trim()
$regStats = (Invoke-Wsl "python3 -I '$statsScript' ~ 2>&1").Trim()

if ($regStats -match "MAX=([0-9.]+)") {
    $maxMs = [double]$Matches[1]
    $detail = "$regStats ms, budget $RegisterBudgetMs ms per phase"
    if ($maxMs -lt $RegisterBudgetMs) {
        Record "PASS" "register round-trip inside the 20 ms budget" $detail
    } else {
        Record "FAIL" "register round-trip inside the 20 ms budget" $detail
    }
} elseif ($regStats -match "NOLOG|NOREADS") {
    Record "SKIP" "register round-trip inside the 20 ms budget" "no --record log with register reads yet"
} else {
    Record "SKIP" "register round-trip inside the 20 ms budget" "could not read the log: $regStats"
}

# --- summary -------------------------------------------------------------------------------------

Write-Host "`nSummary" -ForegroundColor Cyan
$results | Format-Table -AutoSize Verdict, Name | Out-String | Write-Host

# @() is load-bearing: without it a single match is a scalar whose .Count is not the match count, and
# the harness reports success with a FAIL row on screen. That happened.
$failed = @($results | Where-Object { $_.Verdict -eq "FAIL" }).Count
if ($failed -gt 0) {
    Write-Host "$failed FAIL" -ForegroundColor Red
    exit 1
}
Write-Host "no failures" -ForegroundColor Green
exit 0
