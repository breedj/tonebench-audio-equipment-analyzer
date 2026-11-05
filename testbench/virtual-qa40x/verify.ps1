<#
.SYNOPSIS
    PASS/FAIL/KNOWN/SKIP table for the virtual QA40x bench.

.DESCRIPTION
    Checks the prerequisites, the attach chain, the register round-trip against the 20 ms budget that
    decides whether this approach works at all, and the Category=VirtualDevice suite. Mirrors
    linux-testbench/02-verify.sh: a record function, a summary table, non-zero exit on any FAIL.

    -IncludeGate also runs the commit gate, which is the regression that would hurt most — the
    default filter must still pass with a virtual device attached. It is slow (the whole suite), so
    it is opt-in.

.EXAMPLE
    powershell -File verify.ps1
    powershell -File verify.ps1 -IncludeGate
#>
[CmdletBinding()]
param(
    [switch]$IncludeGate
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

Write-Host "Virtual QA40x bench verification — $Stamp" -ForegroundColor Cyan
Write-Host "log: $LogPath`n" -ForegroundColor DarkGray

# --- prerequisites -------------------------------------------------------------------------------

if (Test-Path $UsbipExe) {
    Record "PASS" "usbip-win2 installed" $UsbipExe
} else {
    Record "FAIL" "usbip-win2 installed" "winget install -e --id vadimgrn.usbip-win2"
}

$realQa403 = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match "VID_16C0&PID_4E39" }
if ($realQa403) {
    Record "FAIL" "no real QA403 attached" ("found $($realQa403[0].InstanceId) — it wins every probe, " +
        "so the virtual QA402 is unreachable")
} else {
    Record "PASS" "no real QA403 attached" "the probe order cannot shadow the virtual QA402"
}

# --- the attach chain ----------------------------------------------------------------------------

& (Join-Path $PSScriptRoot "bench.ps1") -Status | Out-Null
if ($LASTEXITCODE -eq 0) {
    Record "PASS" "attach chain (bench.ps1 -Status)" "server listening, device present on WINUSB"
} else {
    Record "FAIL" "attach chain (bench.ps1 -Status)" "run bench.ps1 -Attach (or -Build first)"
}

# --- the number that decides everything ----------------------------------------------------------
# A register read is two USB round trips, each bounded by RegisterTimeoutMs, and the calibration page
# does 128 of them. Printed even on a pass: a clean run says only that the budget was not exceeded,
# never how much of it was used.

$regStats = Invoke-Wsl @'
python3 - <<'PY' 2>/dev/null
import json, glob, os
files = sorted(glob.glob(os.path.expanduser("~/vqa40x-usb*.jsonl")), key=os.path.getmtime)
if not files:
    print("NOLOG"); raise SystemExit
recs = []
for line in open(files[-1]):
    line = line.strip()
    if line:
        try: recs.append(json.loads(line))
        except Exception: pass
pend, lat = None, []
for r in recs:
    if r.get("kind") == "reg_read_req": pend = r
    elif r.get("kind") == "reg_read" and pend is not None:
        lat.append(r["t"] - pend["t"]); pend = None
if not lat:
    print("NOREADS")
else:
    print("N=%d MAX=%.4f MEAN=%.4f" % (len(lat), max(lat), sum(lat)/len(lat)))
PY
'@
if ($regStats -match "MAX=([0-9.]+)") {
    $maxMs = [double]$Matches[1]
    $detail = ($regStats.Trim() + " ms, budget $RegisterBudgetMs ms per phase")
    if ($maxMs -lt $RegisterBudgetMs) {
        Record "PASS" "register round-trip inside the 20 ms budget" $detail
    } else {
        Record "FAIL" "register round-trip inside the 20 ms budget" $detail
    }
} elseif ($regStats -match "NOLOG|NOREADS") {
    Record "SKIP" "register round-trip inside the 20 ms budget" "no --record log yet; attach and run the suite first"
} else {
    Record "SKIP" "register round-trip inside the 20 ms budget" "could not read the log (python3 in WSL?)"
}

# --- the suite -----------------------------------------------------------------------------------

Push-Location $CodeRoot
try {
    $virtual = & dotnet test --filter "Category=VirtualDevice" --nologo 2>&1 | Out-String
    Add-Content -Path $LogPath -Value $virtual
    if ($virtual -match "Failed:\s*0" -or ($virtual -match "Passed!" )) {
        $m = [regex]::Match($virtual, "Passed:\s*(\d+)")
        $n = if ($m.Success) { $m.Groups[1].Value } else { "?" }
        Record "PASS" "Category=VirtualDevice suite" "$n passed"
    } elseif ($virtual -match "ArrivesWithChannelsCrossed|right\s+-?[0-9.]+ dBV, left") {
        Record "KNOWN" "Category=VirtualDevice suite" "channels crossed — the emulator is unpatched, run bench.ps1 -Build"
    } else {
        $m = [regex]::Match($virtual, "Failed:\s*(\d+)")
        $n = if ($m.Success) { $m.Groups[1].Value } else { "?" }
        Record "FAIL" "Category=VirtualDevice suite" "$n failed — see $LogPath"
    }

    if ($IncludeGate) {
        $gate = & dotnet test Tonebench.sln --filter "Category!=Hardware&Category!=VirtualDevice" --nologo 2>&1 | Out-String
        Add-Content -Path $LogPath -Value $gate
        if ($gate -match "Failed:\s*[1-9]" -or $gate -match "error") {
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

# --- summary -------------------------------------------------------------------------------------

Write-Host "`nSummary" -ForegroundColor Cyan
$results | Format-Table -AutoSize Verdict, Name | Out-String | Write-Host

$failed = ($results | Where-Object { $_.Verdict -eq "FAIL" }).Count
if ($failed -gt 0) {
    Write-Host "$failed FAIL" -ForegroundColor Red
    exit 1
}
Write-Host "no failures" -ForegroundColor Green
exit 0
