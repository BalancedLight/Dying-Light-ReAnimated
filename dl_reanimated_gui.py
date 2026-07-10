"""PyInstaller-safe GUI entry point."""

from dlanm2_gui.__main__ import main


if __name__ == "__main__":
    raise SystemExit(main())
