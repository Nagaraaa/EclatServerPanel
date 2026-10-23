#!/usr/bin/python3
"""Remove only the launcher's empty lock directory after the unit has stopped."""
from pathlib import Path

lock = Path('/var/lib/eclat-originel/internet-running')
if lock.is_dir() and not lock.is_symlink():
    try:
        lock.rmdir()  # Never recursively delete data or nonempty locks.
    except OSError:
        pass
