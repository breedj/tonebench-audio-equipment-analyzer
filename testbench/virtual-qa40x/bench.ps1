<#
.SYNOPSIS
    Puts a virtual QA402 in the Windows device tree, so Tonebench opens it as real hardware.

.DESCRIPTION
    Runs virtual-qa40x-rs (MIT) as a USB/IP device server inside WSL2 and attaches it from Windows
    with usbip-win2. See README.md in this directory for why the server lives in WSL (usbipd-win owns
    port 3240 and usbip.exe has no port option), why it is a QA402 and never a QA403, and what the
    bench can and cannot establish.

    -Build is a one-off: it clones the pinned tag and compiles it as it stands.
    Needs rustup and build-essential inside WSL; nothing on the Windows side.

.EXAMPLE
    powershell -File bench.ps1 -Build
    powershell -File bench.ps1 -Attach
    dotnet test --filter "Category=VirtualDevice"
    powershell -File bench.ps1 -Detach
#>
[CmdletBinding()]
param(
    [switch]$Attach,
    [switch]$Detach,
    [switch]$Status,
    [switch]$Build,

    # Produces the serial Tonebench reports as 67240962, which VirtualQa40xBench requires.
    # VirtualQa40xBenchTests pins this literal against VirtualQa40xBench.SentinelSerialArgument.
    [string]$Serial = "0402_0402",

    # qa402 on purpose: Qa40xTransport.Open probes the QA403 first, so a virtual QA403 would be
    # indistinguishable from a real one and unreachable whenever one is plugged in.
    [string]$Model = "qa402",

    [string]$Version = "0.5.0"
)

$ErrorActionPreference = "Stop"

$UsbipExe = "C:\Program Files\USBip\usbip.exe"
$WslSrcDir = "~/vqa40x-src/virtual-qa40x-rs"
$WslBinary = "$WslSrcDir/target/release/vqa40x"
$WslLog = "~/vqa40x-server.log"
$VirtualPid = "PID_4E37"
$RealQa403Pid = "PID_4E39"

function Invoke-Wsl([string]$Command) {
    $out = & wsl.exe -e bash -lc $Command 2>&1
    return ($out | Out-String)
}

function Assert-Wsl {
    $probe = Invoke-Wsl "echo wsl-ok"
    if ($probe -notmatch "wsl-ok") {
        throw "WSL is not usable: $probe"
    }
    # Under networkingMode=mirrored, localhost is shared and the 3240 conflict with usbipd-win comes
    # straight back. Warn rather than refuse: the user may have stopped usbipd themselves.
    $cfg = Join-Path $env:USERPROFILE ".wslconfig"
    if ((Test-Path $cfg) -and ((Get-Content $cfg -Raw) -match "mirrored")) {
        Write-Warning "WSL looks like networkingMode=mirrored. Port 3240 will clash with usbipd-win."
    }
}

function Get-WslAddress {
    $addr = (Invoke-Wsl "hostname -I | awk '{print `$1}'").Trim()
    if (-not $addr) { throw "Could not read the WSL address." }
    return $addr
}

function Assert-UsbipClient {
    if (-not (Test-Path $UsbipExe)) {
        throw ("usbip-win2 is not installed. It supplies the virtual USB host controller, and its " +
               "driver is Microsoft attestation-signed on x64 so no test-signing mode is needed:`n" +
               "    winget install -e --id vadimgrn.usbip-win2`n" +
               "This script will not install a kernel driver for you.")
    }
}

function Get-VirtualDevice {
    return Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match "VID_16C0&$VirtualPid" }
}

function Assert-NoRealQa403 {
    $real = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match "VID_16C0&$RealQa403Pid" }
    if ($real) {
        throw ("A real QA403 is attached ($($real[0].InstanceId)). Qa40xTransport.Open probes " +
               "Qa40xModel.All with the QA403 first, so it would win every probe and the virtual " +
               "QA402 would never be reached. Unplug it, or run --filter Category=Hardware instead.")
    }
}

function Invoke-Build {
    Assert-Wsl
    Write-Host "Cloning and building v$Version as it stands..."
    # No patch. The stock emulator emits the right ADC sample first, which is what a real
    # QA402 does (measured 2026-11-07, serial 3738123988) and what this app now expects via
    # Qa40xModel.AdcChannelsSwapped. adc-channel-order.patch used to make it emit left first,
    # i.e. behave like a QA403; it was wrong and is deleted. The reset below is what undoes it
    # on a clone that still carries it -- this is a throwaway build tree at a pinned tag, so
    # nothing of yours should live in it.
    $script = @"
set -e
. "`$HOME/.cargo/env" 2>/dev/null || true
command -v cargo >/dev/null || { echo 'MISSING_CARGO'; exit 1; }
command -v cc >/dev/null || { echo 'MISSING_CC'; exit 1; }
mkdir -p ~/vqa40x-src
if [ ! -d $WslSrcDir ]; then
  git clone --depth 1 --branch v$Version -q https://github.com/GarageDeveloper/virtual-qa40x-rs.git $WslSrcDir
fi
cd $WslSrcDir
git reset --hard -q && echo 'tree at the pinned tag'
cargo build --release 2>&1 | tail -3
ls -l target/release/vqa40x
"@
    $out = Invoke-Wsl $script
    if ($out -match "MISSING_CARGO") {
        throw ("No cargo in WSL. Install the toolchain there (not on Windows):`n" +
               "    sudo apt-get install -y build-essential pkg-config`n" +
               "    curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal")
    }
    if ($out -match "MISSING_CC") {
        throw "No C linker in WSL. Run: sudo apt-get install -y build-essential pkg-config"
    }
    Write-Host $out
}

function Invoke-Attach {
    Assert-UsbipClient
    Assert-NoRealQa403
    Assert-Wsl

    $have = (Invoke-Wsl "test -x $WslBinary && echo yes || echo no").Trim()
    if ($have -notmatch "yes") {
        throw "No emulator at $WslBinary (in WSL). Run: bench.ps1 -Build"
    }

    if (Get-VirtualDevice) {
        Write-Host "Already attached."
        Invoke-Status
        return
    }

    # Stop a stale server before rebinding 3240, then start detached so it outlives this script.
    $start = @"
pkill -x vqa40x 2>/dev/null || true
sleep 0.3
cd ~ && setsid nohup $WslBinary --model $Model --serial $Serial --listen 0.0.0.0:3240 \
    --record ~/vqa40x-usb.jsonl > $WslLog 2>&1 < /dev/null &
sleep 1
cat $WslLog
"@
    Write-Host "Starting the server in WSL..."
    $log = Invoke-Wsl $start
    if ($log -notmatch "listening on") {
        throw "The server did not come up:`n$log"
    }
    Write-Host $log.Trim()

    $addr = Get-WslAddress
    Write-Host "Attaching from Windows (-r $addr -b 1-1)..."
    $attach = & $UsbipExe attach -r $addr -b 1-1 2>&1 | Out-String
    Write-Host $attach.Trim()

    # PnP enumeration is not instant; poll rather than assume.
    $dev = $null
    foreach ($i in 1..20) {
        $dev = Get-VirtualDevice
        if ($dev -and $dev[0].Status -eq "OK") { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $dev) { throw "Attached, but no $VirtualPid appeared in the device tree.`n$attach" }

    Invoke-Status
}

function Invoke-Detach {
    if (Test-Path $UsbipExe) {
        $ports = & $UsbipExe port 2>&1 | Out-String
        foreach ($m in [regex]::Matches($ports, "Port\s+0*(\d+):")) {
            $p = $m.Groups[1].Value
            Write-Host "Detaching port $p..."
            & $UsbipExe detach -p $p 2>&1 | Out-String | Write-Host
        }
    }
    Write-Host (Invoke-Wsl "pkill -x vqa40x && echo 'server stopped' || echo 'server was not running'").Trim()
}

function Invoke-Status {
    $ok = $true

    $listening = (Invoke-Wsl "ss -lntp 2>/dev/null | grep -c ':3240 ' || true").Trim()
    if ($listening -match "^[1-9]") {
        Write-Host "server   : listening on 3240 in WSL"
    } else {
        Write-Host "server   : NOT listening in WSL"
        $ok = $false
    }

    if (Test-Path $UsbipExe) {
        $ports = & $UsbipExe port 2>&1 | Out-String
        if ($ports -match "16c0:4e37") {
            Write-Host "usbip    : imported 16c0:4e37"
        } else {
            Write-Host "usbip    : no 16c0:4e37 in the port table"
            $ok = $false
        }
    } else {
        Write-Host "usbip    : usbip-win2 not installed"
        $ok = $false
    }

    $dev = Get-VirtualDevice
    if ($dev) {
        $svc = (Get-PnpDeviceProperty -InstanceId $dev[0].InstanceId `
                -KeyName "DEVPKEY_Device_Service" -ErrorAction SilentlyContinue).Data
        Write-Host "device   : $($dev[0].InstanceId) [$($dev[0].Status)] driver=$svc"
        if ($svc -ne "WINUSB") {
            Write-Host "           expected WINUSB - libusb cannot open it otherwise"
            $ok = $false
        }
    } else {
        Write-Host "device   : not present in the device tree"
        $ok = $false
    }

    if (-not $ok) { exit 1 }
}

if ($Build) { Invoke-Build; return }
if ($Attach) { Invoke-Attach; return }
if ($Detach) { Invoke-Detach; return }
if ($Status) { Invoke-Status; return }

Write-Host "Nothing to do. Pass -Build, -Attach, -Detach or -Status (see -? for help)."
