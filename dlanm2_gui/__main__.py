from __future__ import annotations

import sys


def main() -> int:
    if "--self-test" in sys.argv:
        from .environment_check import main as check_main

        args = ["--gui", "--pipeline"]
        if "--report" in sys.argv:
            index = sys.argv.index("--report")
            if index + 1 < len(sys.argv):
                args.extend(["--report", sys.argv[index + 1]])
        return check_main(args)

    from .unified_gui import main as gui_main

    return gui_main()


if __name__ == "__main__":
    raise SystemExit(main())
