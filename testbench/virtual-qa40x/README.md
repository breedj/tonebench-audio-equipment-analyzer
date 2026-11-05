# Virtual QA40x bench

A **QA402 in the Windows device tree** that Tonebench opens, streams and measures through exactly as
shipped — no production code change, no `--simulate`, the real `Qa40xTransport` / `TransferPump` /
`Qa40xCalibration` path over real USB. Run `dotnet test --filter "Category=VirtualDevice"` against it.

It is not a thing we wrote. [`virtual-qa40x-rs`](https://github.com/GarageDeveloper/virtual-qa40x-rs)
(MIT, Raphaël Enrici) is a USB/IP *device server* that emulates a QA402/QA403: the EP1 register bus
with the same 5-byte big-endian framing, EP2 int32-LE stereo PCM, a real factory calibration page, a
DAC→ADC loopback, real-time pacing, and MS OS 2.0 descriptors so Windows binds WinUSB on its own. We
found it through `reference/qa40x-rs-main/.../README.md`, which links it.

> **This cannot verify the QA402.** It confirms Tonebench matches *our model* of a QA402, built from
> the same four references the support was built from. The calibration-page equivalence rests on one
> third-party sentence (`ASIO40x/calibration.h:13`) plus a forum reply, and the emulator's embedded
> page was read from the author's own unit. **A green run here must never set
> `Qa40xModel.Qa402.Verified`** or remove the amber *Unverified model* badge — only
> `Qa402HardwareTests` against real silicon can do that, and `Qa402VirtualDeviceTests` asserts the
> badge is still present precisely so this stays true.

## Why it is a QA402 and not a QA403

`Qa40xTransport.Open` probes `Qa40xModel.All` with the **QA403 first**. A virtual QA403 would be both
indistinguishable from a real one and unreachable whenever a real one is plugged in. Running it as a
QA402 turns the probe order into the discriminator for free — and it is the model with no coverage.

If a real QA403 is attached it **always wins the probe**, and detaching the virtual device will not
help. Unplug the QA403, or run `--filter Category=Hardware` instead. `bench.ps1 -Attach` refuses up
front rather than letting you find out from a test failure.

## Prerequisites

1. **`usbip-win2`** — the USB/IP client, which supplies the virtual host controller:
   ```powershell
   winget install -e --id vadimgrn.usbip-win2
   ```
   Its UDE driver is **Microsoft attestation-signed on x64**, so no test-signing mode and no reboot.
   Verified on this bench: `Signer Name: Microsoft Windows Hardware Compatibility Publisher`.
   Installs to `C:\Program Files\USBip\usbip.exe`.

2. **The emulator binary.** `bench.ps1 -Attach` downloads it into `bin/` (gitignored by
   `.gitignore`'s `[Bb]in/`) and verifies the hash before extracting. Pinned:

   | | |
   |---|---|
   | Version | `v0.5.0` (commit `549e855`) |
   | Linux asset | `vqa40x-v0.5.0-x86_64-unknown-linux-musl.tar.gz` |
   | SHA-256 | `8863c7fe6c29afa7a98f412e7cb51748a529cdf8d1437ab62d4106287fb6031b` |
   | Windows asset | `vqa40x-v0.5.0-x86_64-pc-windows-msvc.zip` |
   | SHA-256 | `45417749342d12d889ad80642d5925ad26be0f2a3074b1cd641b19e62e1eb287` |

3. **A patched build**, for the channel order — see below. Needs `rustup` and `build-essential`
   inside WSL; nothing on the Windows side.

## The port-3240 problem, and why the server runs in WSL

`usbipd-win` (which this bench already uses to share the real QA403 into WSL) runs a service bound to
**0.0.0.0:3240**, and `usbip.exe` has **no port option** — `-r host:port` is parsed as a hostname, so
it always dials 3240 on whatever host you name. A local USB/IP server and `usbipd-win` are therefore
mutually exclusive on Windows: starting the emulator on Windows fails with
`os error 10013` (`WSAEACCES`).

Rather than stop a service this bench depends on, **run the emulator inside WSL2**, which has its own
network namespace and a free 3240, and attach from Windows to the WSL address. `bench.ps1` does this.
The alternative — `Stop-Service usbipd`, run on Windows, restart it afterwards — works but takes your
WSL USB sharing down with it.

WSL2 must be in the default **NAT** mode. Under `networkingMode=mirrored` localhost is shared and the
conflict comes straight back.

## Use

```powershell
powershell -File testbench/virtual-qa40x/bench.ps1 -Attach    # download, start, attach, report
powershell -File testbench/virtual-qa40x/bench.ps1 -Status    # server / port / PnP, non-zero if broken
powershell -File testbench/virtual-qa40x/bench.ps1 -Detach    # detach and stop
powershell -File testbench/virtual-qa40x/verify.ps1           # PASS/FAIL/KNOWN table
```

Then:

```
dotnet test --filter "Category=VirtualDevice"
```

The device appears as `USB\VID_16C0&PID_4E37\0402_0402`, class `USBDevice`, driver **WinUSB**.

### The sentinel serial

`--serial 0402_0402`. The emulator packs the string's hex digits into register `0x1D`, so Tonebench
reports **`67240962`** (this bench's real QA403 reads `1077527945`). `VirtualQa40xBench` requires it,
so a real QA402 gets told to run the other suite instead of silently standing in for the emulator.
Change it in one place only — `VirtualQa40xBench.SentinelSerialArgument`, which
`VirtualQa40xBenchTests` pins against this script.

## The one patch, and why it belongs in the emulator

The L/R swap on a real QA40x is the **DAC's alone**: driving wire slot 1 alone returns on RX slot 0
(measured on serial 1077527945; CLAUDE.md, *"The L/R swap is on the DAC only"*). PyQa40x — 
QuantAsylum's own library — swaps outputs only, and ASIO401 agrees. `QA40x_BareMetal` swaps both
directions, which is the one place a vendored reference is known to be wrong.

`virtual-qa40x-rs` v0.5.0 inherits that double swap, so **channels come back crossed**. Measured
against the stock build: Tonebench put the tone on TX slot 1 and it returned on RX **slot 1** at
−5.81 dBFS while slot 0 carried only the −140 dBFS noise floor. All nine QA403 loopback tests failed
from that single cause — including all eight amplitude ranges, because they read the left channel.

The fix is three lines in `crates/vqa40x-core/src/analyzer.rs` (emit `il` before `ir`). **Never fix
`ProcessRxBuffer` to match** — CLAUDE.md forbids it by name. Patched, the same capture returns the
tone on slot 0 at −5.81 dBFS against the measured −6.00 dBFS, and all 23 of the emulator's own tests
still pass (nothing in its suite covered which wire slot carries which channel, which is why the bug
survived).

```bash
# inside WSL
sudo apt-get install -y build-essential pkg-config
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal
git clone --depth 1 --branch v0.5.0 https://github.com/GarageDeveloper/virtual-qa40x-rs.git
# edit crates/vqa40x-core/src/analyzer.rs: swap the two out.extend_from_slice calls after `let ir =`
cargo build --release
```

## Register coverage

What Tonebench writes or reads, against what the emulator answers. Measured 2026-11-05 from the
emulator's own `--record` log, not inferred. `VirtualQa40xBenchTests` reflects over
`Qa40xRegisters`' byte constants and fails if one is missing from this table.

| Register | Tonebench uses it for | Emulated |
|---|---|---|
| `0x00` | `ConnectionVerify` — random u32 written and read back; connect is refused if it mismatches | yes, echo register |
| `0x05` | `InputRange`, index 0–7 | yes, and it scales the audio: the noise floor steps 6.00 dB per rung across all eight |
| `0x06` | `OutputRange`, index 0–3 | yes |
| `0x08` | `StreamingEnable`, `5` start / `0` stop | yes |
| `0x09` | `SampleRate` index, 0–3 | yes; clamped to the model max rather than refused |
| `0x0a` | `I2sControl = 0` at connect, failure swallowed | yes |
| `0x0d` | `CalibrationPageSelect = 0x10` | yes; resets the page cursor |
| `0x10` | `FirmwareVersion` | yes, reports 60 — the real units' value |
| `0x11` | `TelemetryUsbVoltage`, mV | yes, 4949 mV (bench QA403: 4844–4853) |
| `0x12` | `TelemetryUsbCurrent`, mA | yes, 782 mA (bench QA403: 865–884) |
| `0x13` | `TelemetryIsoCurrent` — **deliberately never read**, a QA403 answers a constant 0x1212 | not exercised, by design |
| `0x16` | `TelemetryTemperature`, deci-°C | yes, 250 = 25.0 °C |
| `0x19` | `CalibrationPageStream` — 128 successive reads for the 512-byte page | yes, self-advancing cursor, real factory page |
| `0x1d` | `SerialNumber`, packed u32 | yes, from `--serial` |
| `0x80` | `ReadFlag` — a flag, not a register | n/a |

## What it establishes, measured

| | |
|---|---|
| Register round-trip | **max 0.0060 ms** against `Qa40xTransport.RegisterTimeoutMs` = 20 ms, 0 of 134 over 5 ms |
| Whole connect | **106.9 ms**, including the 128-read calibration page |
| EP2 connect-drain | 64 unlinks in **4.1 ms** |
| Sustained capture | 60 s at 192 kHz, **15.35 M frames and 122 MB each way, zero dropped sequence numbers** |
| Amplitude, 8 ranges | −6.066 dBV against a commanded −6.0 at every rung |
| Channel separation | left −6.07 dBV, right −131.24 dBV |
| Descriptors | bcdUSB **0x0201**, 1 config, interface 0, class **0xFF**, **6 bulk endpoints** at `0x01/0x81/0x02/0x82/0x03/0x83`, wMaxPacketSize **512**, BOS with the MS OS 2.0 UUID and `WINUSB` compatible id |

That descriptor set is the mechanism `docs/tonebench-stack-spec.md` §9 item 1 asks about, demonstrated
working — but it is the *emulator's* descriptors, not a real QA403's. Settling that still needs
`lsusb -v -d 16c0:4e39` with a QA403 shared into WSL.

## Limits

1. **Timing fidelity is not faithful.** USB/IP over TCP is not real USB: `linux-testbench/README.md`
   measured 7–32 ms read waits from TCP bursting on this bench. DAC starvation, the 128 ms TX queue,
   `TX slack floor` and the pump-stall diagnostics mean nothing here. Functional tests yes, timing
   numbers no.
2. **No fault injection.** The emulator has no flag to drop, short-return or stall a transfer.
   `usbip detach -p 1` is an instant clean unplug and is the one fault available; killing the server
   is detected via TCP keepalive in ~30 s.
3. **One host at a time**, like a real device.
4. **No analog front end**, so there is no 24 dB attenuator relay and no range-dependent analog
   behaviour — which is exactly why the amplitude theory here is named
   `CalibrationPage_DecodesAndScalesConsistently_AtEveryInputRange` rather than claiming A2's finding.

## Why `testbench/` and not `packaging/`

`packaging/` is what ships: its contents are enumerated by `tonebench.iss`'s whitelist, `deb/control`
and the release workflow, so a bench tool there is a standing invitation to ship it. This directory is
also the tracked counterpart of `linux-testbench/` at the project root, which sits outside the git
repo on purpose (the repo is `code/`, and the project root is not one).
