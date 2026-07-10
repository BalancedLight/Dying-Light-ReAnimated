#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")"
PYTHON_BIN="${PYTHON_BIN:-python3}"
"$PYTHON_BIN" -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 11) else 1)' || {
  echo "DL ReAnimated requires Python 3.11 or newer." >&2
  exit 1
}
if [ ! -x .venv/bin/python ]; then
  "$PYTHON_BIN" -m venv .venv
  .venv/bin/python -m pip install --upgrade pip setuptools wheel
  .venv/bin/python -m pip install -e '.[gui]'
fi
exec .venv/bin/python -m dlanm2_gui
