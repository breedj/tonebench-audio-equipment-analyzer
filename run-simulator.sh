#!/usr/bin/env bash
# run.sh against the synthetic analyzer -- no QA403 needed. Everything else is identical, so this
# delegates rather than copying: the .bat pair duplicates its whole body, and a fix applied to one
# copy and not the other is the failure that invites.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/run.sh" --simulate "$@"
