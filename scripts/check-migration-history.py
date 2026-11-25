#!/usr/bin/env python3
"""Reject rewriting shipped migrations or inserting new ones before shipped history."""
import re
import subprocess
import sys
from pathlib import PurePosixPath

base = sys.argv[1] if len(sys.argv) == 2 else "HEAD"
directory = "Plugins/ZCash/Data/Migrations/"


def git(*args):
    return subprocess.check_output(["git", *args], text=True).splitlines()


def migration_id(path):
    match = re.match(r"(\d{14}_[^.]+)(?:\.Designer)?\.cs$", PurePosixPath(path).name)
    return match.group(1) if match else None


shipped = git("ls-tree", "-r", "--name-only", base, "--", directory)
latest = max((migration_id(p) for p in shipped if migration_id(p)), default="")
errors = []
for line in git("diff", "--name-status", "--no-renames", base, "--", directory):
    status, path = line.split("\t", 1)
    identifier = migration_id(path)
    if path in shipped and not path.endswith("ModelSnapshot.cs"):
        errors.append(f"Do not modify/delete a shipped migration: {path}. Add a forward migration.")
    elif status == "A" and identifier and identifier <= latest:
        errors.append(f"New migration {identifier} must sort after shipped migration {latest}.")
# Also check unstaged new migrations when the script is used locally.
for path in git("ls-files", "--others", "--exclude-standard", "--", directory):
    identifier = migration_id(path)
    if identifier and identifier <= latest:
        errors.append(f"New migration {identifier} must sort after shipped migration {latest}.")
if errors:
    sys.exit("\n".join(errors))
print(f"Migration history is append-only relative to {base}.")
