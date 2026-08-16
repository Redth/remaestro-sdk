#!/usr/bin/env python3
"""Entrypoint for the Example Lamp plugin.

Named `main.py` and launched as `python3 main.py` rather than `python3 -m lamp` for one reason
that is not obvious: **Python puts the script's own directory on `sys.path`, and does not put the
working directory there.** A `-m` launch relies on the working directory instead, which the hub
only started setting recently — so a script entrypoint is the shape that works on every hub.

Everything else here is `sys.path` plumbing for the vendored dependencies. A plugin declaring
`"runtime": "python3"` gets an interpreter and nothing else: `grpcio` is on neither the appliance
nor in the hub container, so it ships in `_vendor/` beside this file.
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

# Ahead of the standard library's own site-packages, so a hub that happens to have a different
# grpcio installed cannot change which one this plugin runs against.
sys.path.insert(0, os.path.join(HERE, "_vendor"))
sys.path.insert(0, HERE)

from lamp.host import serve  # noqa: E402  (must follow the sys.path setup)

if __name__ == "__main__":
    sys.exit(serve())
