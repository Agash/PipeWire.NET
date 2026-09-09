#!/usr/bin/env bash
# Rewrites tests/PipeWire.NET.Tests/upstream-keys.txt from the pinned PipeWire clone.
#
# The clone under external/ is gitignored, so the snapshot is committed and the drift test reads
# that rather than the clone. Run this after moving the clone to a new release, then run the tests:
# a key that was renamed or removed shows up as a failure instead of as a property that quietly
# stopped being populated.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
clone="$root/external/pipewire"

if [ ! -f "$clone/src/pipewire/keys.h" ]; then
    echo "no PipeWire clone at $clone; nothing to refresh from" >&2
    exit 1
fi

version="$(git -C "$clone" describe --tags --always 2>/dev/null || echo unknown)"

PY_BIN="$(command -v python3 || command -v python)"
if [ -z "$PY_BIN" ]; then echo "python3 is required" >&2; exit 1; fi

"$PY_BIN" - "$clone" "$version" "$root/tests/PipeWire.NET.Tests/upstream-keys.txt" <<'PY'
import io, os, re, sys

clone, version, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
pairs = {}

def scan(path, prefix):
    src = io.open(path, encoding='utf-8', errors='replace').read()
    for name, value in re.findall(r'#define\s+(' + prefix + r'[A-Z0-9_]+)\s+"([^"]+)"', src):
        pairs.setdefault(value, name)

scan(os.path.join(clone, 'src/pipewire/keys.h'), 'PW_KEY_')
for root, _, files in os.walk(os.path.join(clone, 'spa/include')):
    for f in files:
        if f.endswith('.h'):
            scan(os.path.join(root, f), 'SPA_KEY_')

lines = [f'# pipewire {version}: PW_KEY_* from src/pipewire/keys.h and SPA_KEY_* from spa/include.',
         f'# {len(pairs)} keys. Regenerate with generate/refresh-upstream-keys.sh.']
lines += [f'{name} {value}' for value, name in sorted(pairs.items())]
io.open(out_path, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines) + '\n')
print(f'wrote {len(pairs)} keys to {out_path}')
PY
