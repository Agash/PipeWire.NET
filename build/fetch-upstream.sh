#!/usr/bin/env bash
# Clone the upstream projects this library is written against into external/, for reading.
#
# Not submodules on purpose. Nothing here is built, linked or shipped: it exists so that a
# question about what the daemon actually does is answered by grepping its source rather than by
# inferring from a header. external/ is gitignored, so a clone never reaches a commit.
#
# The versions follow generate/HEADER-VERSION, which is the pin the bindings are generated from.
# Pass a ref to override, e.g. `PIPEWIRE_REF=master build/fetch-upstream.sh`.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="$here/external"
mkdir -p "$dest"

pipewire_ref="${PIPEWIRE_REF:-$(tr -d '[:space:]' < "$here/generate/HEADER-VERSION")}"
wireplumber_ref="${WIREPLUMBER_REF:-0.5.16}"
gstreamer_ref="${GSTREAMER_REF:-1.26.6}"

# Shallow and blobless. The whole history is not the point; being able to read one revision is.
clone() {
    local name="$1" url="$2" ref="$3"
    if [ -d "$dest/$name/.git" ]; then
        echo "== $name: already present at $(git -C "$dest/$name" describe --tags --always 2>/dev/null || echo unknown)"
        return
    fi

    echo "== $name @ $ref"
    git clone --depth 1 --branch "$ref" --filter=blob:none --single-branch "$url" "$dest/$name"
}

clone pipewire    https://gitlab.freedesktop.org/pipewire/pipewire.git       "$pipewire_ref"
clone wireplumber https://gitlab.freedesktop.org/pipewire/wireplumber.git    "$wireplumber_ref"
clone gstreamer   https://gitlab.freedesktop.org/gstreamer/gstreamer.git     "$gstreamer_ref"

cat <<EOF

Cloned into $dest. Worth knowing where things are:

  pipewire/src/pipewire/          the client library and the daemon's object implementations
  pipewire/src/modules/           export types live here: module-metadata, module-client-node,
                                  module-client-device, module-protocol-native
  pipewire/spa/include/spa/       the SPA headers the pod layer mirrors
  pipewire/src/tools/             pw-cli, pw-dump, pw-link, pw-cat as reference consumers
  pipewire/src/gst/               gstpipewiresrc / gstpipewiresink, the reference stream users
  wireplumber/lib/wp/             the session manager's own wrapper over the same API
  gstreamer/subprojects/          gstreamer core, for how a large consumer drives negotiation
EOF
