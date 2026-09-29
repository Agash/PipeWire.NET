#!/usr/bin/env bash
#
# Builds the patched PipeWire that build/verify-linux.sh runs its private legs against: a module farm
# whose module-metadata carries repro/module-metadata.patch (~/pw-mods-patched) and a libpipewire
# carrying repro/libpipewire-permissions.patch (~/pw-lib-patched), both from the release of the
# installed daemon. verify-linux.sh uses them only when their version matches the installed daemon,
# so run this again after every PipeWire upgrade.
#
#   build/patched-pipewire.sh
#
#   PWNET_PIPEWIRE_SRC     a git clone of gitlab.freedesktop.org/pipewire/pipewire (default ~/src/pipewire)
#   PWNET_PATCHED_MODULES  where the module farm goes (default ~/pw-mods-patched)
#   PWNET_PATCHED_LIB      where the library goes (default ~/pw-lib-patched)
#
# Needs meson, ninja and a C toolchain. A patch that does not apply stops the build: a farm built
# without its patch would pass for a patched one. On a box whose logind has KillUserProcesses=yes, run
# it as a transient user unit.

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="${PWNET_PIPEWIRE_SRC:-$HOME/src/pipewire}"
MODULES="${PWNET_PATCHED_MODULES:-$HOME/pw-mods-patched}"
LIB="${PWNET_PATCHED_LIB:-$HOME/pw-lib-patched}"
BUILD="$(mktemp -d /tmp/pwnet-pipewire.XXXXXX)"
trap 'rm -rf "$BUILD"; git -C "$SRC" reset -q --hard' EXIT

version="$(pipewire --version | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | tail -1)"
echo "installed daemon: $version"

# Nothing half-built may pass for a patched build.
rm -rf "$MODULES" "$LIB"

git -C "$SRC" fetch -q https://gitlab.freedesktop.org/pipewire/pipewire.git \
  "refs/tags/$version:refs/tags/$version"
git -C "$SRC" checkout -q -f "$version"
git -C "$SRC" reset -q --hard
for patch in module-metadata libpipewire-permissions; do
  git -C "$SRC" apply "$ROOT/repro/$patch.patch"
  echo "applied repro/$patch.patch"
done

# Arch's paths, which SteamOS shares; only the module farm and the library are taken from the build.
meson setup "$BUILD" "$SRC" -Dauto_features=disabled -Dexamples=disabled -Dtests=disabled \
  -Dman=disabled -Ddocs=disabled -Dsession-managers=[] -Dprefix=/usr -Dlibdir=lib \
  -Dsysconfdir=/etc -Dlocalstatedir=/var > "$BUILD/setup.log"
ninja -C "$BUILD" > "$BUILD/ninja.log"

mkdir -p "$MODULES" "$LIB"
cp "$BUILD"/src/modules/libpipewire-module-*.so "$MODULES/"
cp -a "$BUILD"/src/pipewire/libpipewire-0.3.so* "$LIB/"
echo "module farm: $MODULES ($(strings "$MODULES/libpipewire-module-metadata.so" | grep -m1 -xE '[0-9]+\.[0-9]+\.[0-9]+'))"
echo "library:     $LIB ($(strings "$LIB/libpipewire-0.3.so.0" | grep -m1 -xE '[0-9]+\.[0-9]+\.[0-9]+'))"
