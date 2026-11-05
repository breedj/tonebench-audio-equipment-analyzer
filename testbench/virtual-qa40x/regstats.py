"""Register round-trip latency from the emulator's --record log.

The number this prints is the one that decides whether the whole virtual bench is viable:
Qa40xTransport.RegisterTimeoutMs is 20 ms and it bounds *both* phases of every register read, with
Qa40xCalibration.LoadAsync doing 128 consecutive reads for the 512-byte page. If a USB/IP round trip
exceeds that, the approach does not work and the fix is not in Tonebench.

Measures the emulator's own service time (request record to reply record), which is a lower bound on
the host's round trip rather than the whole of it -- the end-to-end proof is the connect completing at
all. Run it on the newest ~/vqa40x-usb*.jsonl:

    python3 -I regstats.py ~/vqa40x-usb.jsonl

Reads the log as data: -I keeps the interpreter from importing anything out of its directory.
"""

import glob
import json
import os
import sys


def newest_log(arg):
    if arg and not os.path.isdir(arg):
        return arg
    pattern = os.path.join(arg or os.path.expanduser("~"), "vqa40x-usb*.jsonl")
    files = sorted(glob.glob(pattern), key=os.path.getmtime)
    return files[-1] if files else None


def main():
    path = newest_log(sys.argv[1] if len(sys.argv) > 1 else None)
    if not path or not os.path.exists(path):
        print("NOLOG")
        return 0

    pend = None
    lat = []
    unanswered = 0
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.strip()
        if not line:
            continue
        try:
            r = json.loads(line)
        except Exception:
            continue
        kind = r.get("kind")
        if kind == "reg_read_req":
            if pend is not None:
                # A request with no reply before the next one: the reply FIFO has desynced, which is
                # what a stale USB/IP connection looks like. Worth reporting, not just ignoring.
                unanswered += 1
            pend = r
        elif kind == "reg_read" and pend is not None:
            lat.append(r["t"] - pend["t"])
            pend = None

    if not lat:
        print("NOREADS")
        return 0

    lat.sort()
    n = len(lat)
    print(
        "N=%d MAX=%.4f MEAN=%.4f P99=%.4f UNANSWERED=%d"
        % (n, lat[-1], sum(lat) / n, lat[min(n - 1, int(n * 0.99))], unanswered)
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
