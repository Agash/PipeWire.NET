#!/usr/bin/env bash
#
# Regenerates src/PipeWire.NET/generated/*.g.cs from the installed PipeWire headers.
# Run on Linux (or WSL) with libpipewire-0.3-dev and libclang-dev installed:
#
#   sudo apt-get install -y libpipewire-0.3-dev libclang-dev
#   dotnet tool install --global ClangSharpPInvokeGenerator --version 21.1.8.3
#   bash generate/generate.sh
#
# The generated files are committed; downstream consumers do not run this.
#
#   generate/generate.sh --refresh-names
#
# rewrites the naming block in pipewire.rsp instead. ClangSharp matches --with-namespace,
# --remap-type and --with-enum-member-strip on exact declaration names only - no wildcards - so the
# native-to-C# mapping has to be spelled out once per type, and that is what --refresh-names writes.
# Run it after changing the traversed headers, then run the script again normally.

set -uo pipefail

REFRESH_NAMES=0
ALLOW_VERSION_CHANGE=0
for arg in "$@"; do
  case "$arg" in
    --refresh-names)         REFRESH_NAMES=1 ;;
    --allow-version-change)  ALLOW_VERSION_CHANGE=1 ;;
    *) echo "ERROR: unknown option $arg"; exit 1 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOL="$HOME/.dotnet/tools/ClangSharpPInvokeGenerator"

if ! [ -x "$TOOL" ] && ! command -v ClangSharpPInvokeGenerator &>/dev/null; then
  echo "ERROR: ClangSharpPInvokeGenerator not found."
  echo "Install: dotnet tool install --global ClangSharpPInvokeGenerator --version 21.1.8.3"
  exit 1
fi
[ -x "$TOOL" ] || TOOL=ClangSharpPInvokeGenerator

if [ ! -f /usr/include/pipewire-0.3/pipewire/pipewire.h ]; then
  echo "ERROR: PipeWire headers not found at /usr/include/pipewire-0.3/."
  echo "Install: sudo apt-get install libpipewire-0.3-dev   (or pipewire-devel)"
  exit 1
fi

# The generated bindings are a wire contract, and the headers that produce them come from whatever
# the build machine happens to have installed. Two machines a release apart generate subtly
# different output from the same command, and nothing in the committed .g.cs files says which was
# used. Pin it: the version that produced the committed bindings lives in generate/HEADER-VERSION,
# and a mismatch stops the run unless it is declared with --allow-version-change.
PINNED_FILE="$REPO_ROOT/generate/HEADER-VERSION"
HEADER_VERSION="$(pkg-config --modversion libpipewire-0.3 2>/dev/null || true)"

if [ -z "$HEADER_VERSION" ]; then
  echo "ERROR: pkg-config cannot report a libpipewire-0.3 version, so the bindings would be"
  echo "generated against headers of unknown provenance. Install pkg-config and libpipewire-0.3-dev."
  exit 1
fi

if [ -f "$PINNED_FILE" ]; then
  PINNED="$(tr -d '[:space:]' < "$PINNED_FILE")"
  if [ "$PINNED" != "$HEADER_VERSION" ]; then
    if [ "$ALLOW_VERSION_CHANGE" = "0" ]; then
      echo "ERROR: the committed bindings were generated against PipeWire $PINNED, this machine has"
      echo "$HEADER_VERSION. Regenerating here would mix two header sets into one contract."
      echo "To move the pin deliberately: bash generate/generate.sh --allow-version-change"
      exit 1
    fi
    echo "Moving the header pin from $PINNED to $HEADER_VERSION."
  fi
fi

# The dotnet tool packaging does not bundle native libclang/libClangSharp; load them from the NuGet
# runtime packages on the LD path. The natives and the clang builtin-include directory must share a
# major, and that major is what this pins: libclang handed another major's headers does not fail, it
# parses less and exits 0, which writes a truncated contract. The tool itself is still published at
# 21.1.8.x and loads whichever natives it is pointed at.
LIBCLANG_VERSION=22.1.8
CLANGSHARP_VERSION=22.1.8.2
LIBCLANG_NATIVE="$HOME/.nuget/packages/libclang.runtime.linux-x64/$LIBCLANG_VERSION/runtimes/linux-x64/native"
CLANGSHARP_NATIVE="$HOME/.nuget/packages/libclangsharp.runtime.linux-x64/$CLANGSHARP_VERSION/runtimes/linux-x64/native"

if [ ! -f "$LIBCLANG_NATIVE/libclang.so" ] || [ ! -f "$CLANGSHARP_NATIVE/libClangSharp.so" ]; then
  echo "ERROR: Native libclang $LIBCLANG_VERSION / libClangSharp $CLANGSHARP_VERSION not found at"
  echo "the expected NuGet cache paths."
  echo "Provision via:"
  echo "  mkdir /tmp/clangsharp-bootstrap && cd /tmp/clangsharp-bootstrap"
  echo "  dotnet new console -o dummy && cd dummy"
  echo "  dotnet add package libclang.runtime.linux-x64        --version $LIBCLANG_VERSION"
  echo "  dotnet add package libClangSharp.runtime.linux-x64   --version $CLANGSHARP_VERSION"
  exit 1
fi

export LD_LIBRARY_PATH="$LIBCLANG_NATIVE:$CLANGSHARP_NATIVE:${LD_LIBRARY_PATH:-}"

# Clang's resource directory holds its builtin headers (stdbool.h, stddef.h, ...). The generator
# only locates it unaided when the matching LLVM release is installed system-wide, so name it.
# The major must match the pinned libclang: libclang 21
# handed clang 22's builtin headers does not fail. It parses less, ClangSharp emits fewer
# declarations, and the run reports success - which produces a truncated naming block or a
# truncated set of bindings that looks like a deliberate reduction in the diff.
LIBCLANG_MAJOR=22

CLANG_INC=""
for candidate in /usr/lib/llvm-$LIBCLANG_MAJOR/lib/clang/*/include                  /usr/lib/clang/$LIBCLANG_MAJOR*/include; do
  [ -d "$candidate" ] && { CLANG_INC="$candidate"; break; }
done

if [ -z "$CLANG_INC" ]; then
  FOUND="$(ls -d /usr/lib/llvm-*/lib/clang/*/include /usr/lib/clang/*/include 2>/dev/null | tr '
' ' ')"
  echo "ERROR: no clang $LIBCLANG_MAJOR builtin include dir, and $LIBCLANG_MAJOR is the major the"
  echo "pinned libclang ($LIBCLANG_VERSION) needs."
  if [ -n "$FOUND" ]; then
    echo "Present instead: $FOUND"
    echo "Using one of those parses fewer declarations and still exits 0, so it is refused rather"
    echo "than allowed to write a truncated contract."
  fi
  echo "Install clang $LIBCLANG_MAJOR: apt clang-$LIBCLANG_MAJOR, or the distribution's equivalent."
  exit 1
fi
CLANG_RESOURCE_DIR="$(dirname "$CLANG_INC")"

RSP="$REPO_ROOT/generate/pipewire.rsp"
BLOCK_BEGIN='# >>> naming block'
ANONYMOUS_ENUM='__AnonymousEnum_type_L32_C1'
BLOCK_END='# <<< end naming block'

# Enums a consumer reads directly, so they are public and live beside the types that hand them out
# rather than beside the ABI: a node's state is read off a PipeWire.NET.Graph node, a stream's off a
# PipeWire.NET.Media stream. The namespace is per enum because those two answers differ; a single
# front-door namespace would put a stream flag somewhere no stream is.
PUBLIC_ENUMS="PipeWireNodeState=PipeWire.NET.Graph
PipeWireLinkState=PipeWire.NET.Graph
PipeWireFilterState=PipeWire.NET.Graph
PipeWireFilterFlags=PipeWire.NET.Graph
PipeWireFilterPortFlags=PipeWire.NET.Graph
PipeWireStreamState=PipeWire.NET.Media
PipeWireStreamFlags=PipeWire.NET.Media"

OUT="$REPO_ROOT/src/PipeWire.NET/generated"
mkdir -p "$OUT"
# Wipe the entire generated/ directory. Hand-written code lives in
# src/PipeWire.NET/Native.Extensions.cs (same Generated namespace and same
# `Native` partial class), NOT here - see that file for the rationale.
rm -f "$OUT"/*.cs "$OUT"/*.g.cs

# - Naming mode ---------------------------------------------------------------------------------
#
# Discovers what the headers declare by generating once with the naming block removed, then writes
# the block from those native names. Driven by the native names rather than by the previous output,
# so running it twice is a no-op.
if [ "$REFRESH_NAMES" = "1" ]; then
  WORK=$(mktemp -d)
  trap 'rm -rf "$WORK"' EXIT

  awk -v b="$BLOCK_BEGIN" -v e="$BLOCK_END" '
    index($0,b)==1 {skip=1; next}
    index($0,e)==1 {skip=0; next}
    !skip {print}' "$RSP" > "$WORK/discover.rsp"

  "$TOOL" "@$WORK/discover.rsp" \
    --file "$REPO_ROOT/generate/pipewire_composite.h" \
    --resource-directory "$CLANG_RESOURCE_DIR" \
    --output "$WORK/discover" > "$WORK/discover.log" 2>&1
  if [ $? -ne 0 ]; then
    echo "ERROR: the discovery pass failed:"; tail -5 "$WORK/discover.log"; exit 1
  fi

  # One line per type: "enum <native> <member> <member> ..." or "type <native>".
  for f in "$WORK"/discover/*.cs; do
    native=$(basename "$f" .cs)
    # Matches internal too: the ABI layer is emitted internal by --with-access-specifier, so a
    # classifier looking only for "public enum" sees no enums at all and writes an empty block.
    if grep -qE '^(public|internal) enum ' "$f"; then
      printf 'enum %s %s\n' "$native" "$(grep -oP '^    \K[A-Za-z_]\w*' "$f" | tr '\n' ' ')"
    else
      printf 'type %s\n' "$native"
    fi
  done > "$WORK/types.txt"

  # The anonymous SPA_TYPE_* enum has no file of its own - the discovery pass scatters it across
  # Native as loose constants - so its members are collected from there and put through the same
  # naming pass. Without this the library's most-used enum keeps its C spelling.
  printf 'enum %s %s\n' "$ANONYMOUS_ENUM" \
    "$(grep -oP 'const uint \K(_?SPA_TYPE_\w+)' "$WORK"/discover/Native.cs | sort -u | tr '\n' ' ')" \
    >> "$WORK/types.txt"

  awk -v public_enums="$PUBLIC_ENUMS" '
    # Two or more capitals in a row is an acronym being shouted (RGBA, LE, VIDEO, IO), so it is
    # cased down to Rgba, Le, Video, Io. One capital followed by lower case is already a C# word
    # (MemPtr, DmaBuf, mediaType) and keeps its shape. Digits count as neither.
    # Lowercases each run of two or more capitals after its first letter, so a shouted acronym
    # becomes a word (RGBA -> Rgba, IO -> Io) while a part that is already a C# word is untouched
    # (MemPtr, PropInfo). Doing it per run rather than per part is what keeps ParamIO -> ParamIo
    # instead of the Paramio a whole-part rule produces.
    function decase(p,   out, i, j, n, c) {
      n = length(p); out = ""; i = 1
      while (i <= n) {
        c = substr(p, i, 1)
        if (c ~ /[A-Z]/) {
          j = i
          while (j < n && substr(p, j + 1, 1) ~ /[A-Z]/) j++
          out = out c (j > i ? tolower(substr(p, i + 1, j - i)) : "")
          i = j + 1
        } else {
          out = out c
          i++
        }
      }
      return out
    }

    # Capitalises the first letter of each underscore-separated part. Two or more capitals in a row
    # is an acronym being shouted (RGBA, LE, VIDEO, IO) and is cased down to Rgba, Le, Video, Io;
    # one capital followed by lower case is already a C# word (MemPtr, DmaBuf) and keeps its shape.
    # A part beginning with a digit where the last one ended in one keeps its underscore, so
    # S24_32_LE reads S24_32Le rather than the meaningless S2432Le.
    function pascal(w,   n, i, j, parts, out, p, c) {
      n = split(w, parts, "_"); out = ""
      for (i = 1; i <= n; i++) {
        p = parts[i]
        if (p == "") continue
          p = decase(p)
        for (j = 1; j <= length(p); j++) {
          c = substr(p, j, 1)
          if (c ~ /[A-Za-z]/) { p = substr(p,1,j-1) toupper(c) substr(p,j+1); break }
        }
        if (out ~ /[0-9]$/ && p ~ /^[0-9]/) out = out "_"
        out = out p
      }
      return out
    }
    # Only enums get a C# name. A struct here is a raw ABI shape whose fields keep their C
    # spelling, so dressing the type name up would only disguise what it is - and several would
    # collide with the managed types built on top of them (spa_pod against the SpaPod codec).
    function typename(native, isEnum) {
      if (!isEnum) return native
      if (native == "__AnonymousEnum_type_L32_C1") return "SpaType"
      if (native ~ /^spa_/)  return "Spa" pascal(substr(native,5))
      if (native ~ /^pw_/)   return "PipeWire" pascal(substr(native,4))
      return native
    }
    # Upstream marks its "not part of ABI" sentinels with a leading underscore (_SPA_TYPE_LAST),
    # which shares no prefix with anything and would otherwise defeat the whole computation.
    function shared_prefix(list, count,   i, j, a, b, cut, n, real) {
      n = 0
      for (i = 1; i <= count; i++) if (substr(list[i],1,1) != "_") real[++n] = list[i]
      if (n < 2) return ""
      a = real[1]
      for (i = 2; i <= n; i++) {
        b = real[i]; j = 0
        while (j < length(a) && substr(a,j+1,1) == substr(b,j+1,1)) j++
        a = substr(a, 1, j)
      }
      cut = 0
      for (j = length(a); j > 0; j--) if (substr(a,j,1) == "_") { cut = j; break }
      return substr(a, 1, cut)
    }
    BEGIN {
      failed = 0
      # Members whose mechanical name would be illegal - a name cannot start with a digit, and the
      # generator would otherwise prefix an underscore and leave 0255 sitting in the enum.
      override["SPA_VIDEO_COLOR_RANGE_0_255"]  = "Full"
      override["SPA_VIDEO_COLOR_RANGE_16_235"] = "Limited"
      override["SPA_META_TRANSFORMATION_90"]   = "Rotate90"
      override["SPA_META_TRANSFORMATION_180"]  = "Rotate180"
      override["SPA_META_TRANSFORMATION_270"]  = "Rotate270"
      split(pascal_members, pm, /[ \n]+/); for (i in pm) if (pm[i] != "") wantPascal[pm[i]] = 1
      # "Name=Namespace" per entry: the enums that are public, and where each one lives.
      split(public_enums, pe, /[ \n]+/)
      for (i in pe) {
        if (pe[i] == "") continue
        split(pe[i], kv, "=")
        publicNs[kv[1]] = kv[2]
      }
      nTypes = 0
      nPublic = 0
    }
    {
      native = $2; name = typename(native, $1 == "enum")
      order[++nTypes] = name; nativeOf[name] = native; kind[name] = $1
      if ($1 == "enum") {
        n = 0; delete members
        for (i = 3; i <= NF; i++) members[++n] = $i
        prefix[name] = shared_prefix(members, n)
        memberList[name] = ""
        delete taken
        for (i = 1; i <= n; i++) {
          short = members[i]
          if (substr(short,1,1) == "_") {
            sentinels[name] = sentinels[name] members[i] " "
            continue
          }
          if (prefix[name] != "" && index(short, prefix[name]) == 1)
            short = substr(short, length(prefix[name]) + 1)
          nice = (members[i] in override) ? override[members[i]] : pascal(short)
          if (nice in taken) {
            print "ERROR: " name "." nice " would be declared twice (from " members[i] " and "                   taken[nice] "). Add a --remap for one of them." > "/dev/stderr"
            failed = 1
          }
          taken[nice] = members[i]
          if (nice != short) memberList[name] = memberList[name] members[i] "=" nice " "
        }
      }
    }
    END {
      # sorted by C# name, so the block has a stable order run to run
      n = asort(order)
      print "# >>> naming block - generated by \"generate.sh --refresh-names\", do not edit by hand"
      print "#"
      print "# ClangSharp matches these on exact names only, so every type is listed. Refresh it"
      print "# after changing the traversed headers; a normal run fails if any type is left in a"
      print "# .Generated namespace, which is what catches a new one arriving without an entry."
      print "#"
      print "# Enum members keep the upstream spelling where that IS the idiomatic name - a pixel"
      print "# format is I420 - and are Pascal-cased where the member names a state, mode or flag."
      print "#"
      print "# Only C enums are named here. Upstream #define families - the PW_KEY_* strings and the"
      print "# SPA_*_FLAG_* / *_CHANGE_MASK_* bits - come out of the separate constants pass and are"
      print "# lifted into PipeWireKeys and the [Flags] enums in SpaFlags.g.cs at the end of generate.sh."
      print ""
      for (i = 1; i <= n; i++) {
        name = order[i]
        if (nativeOf[name] != name) { print "--remap-type"; print nativeOf[name] "=" name }
      }
      print ""
      print "# Types that belong somewhere other than the default ABI namespace."
      for (i = 1; i <= n; i++) {
        name = order[i]
        # PipeWire.NET.Interop is the default namespace (--namespace), so only the types that
        # move out of it need an entry.
        if (name in publicNs) ns = publicNs[name]
        else if (name ~ /^Spa/) ns = "PipeWire.NET.Spa"
        else continue
        print "--with-namespace"; print name "=" ns
        public_names[++nPublic] = name
      }
      print ""
      print "# Opted back out of the internal catch-all: the same types, for the same reason. A"
      print "# caller that reads a format or a state reads these; everything else is plumbing."
      for (i = 1; i <= nPublic; i++) {
        print "--without-access-specifier"; print public_names[i]
      }
      print ""
      print "# Members: drop the prefix the C name already spells out."
      for (i = 1; i <= n; i++) {
        name = order[i]
        if (kind[name] == "enum" && prefix[name] != "") {
          print "--with-enum-member-strip"; print name "=prefix:" prefix[name]
        }
      }
      print ""
      print "# Members get a C# name: the prefix drops off and each part is Pascal-cased."
      for (i = 1; i <= n; i++) {
        name = order[i]
        if (memberList[name] == "") continue
        n2 = split(memberList[name], pairs, " ")
        for (j = 1; j <= n2; j++) if (pairs[j] != "") { print "--remap"; print pairs[j] }
      }
      print ""
      print "# Upstream marks these \"not part of ABI\" with a leading underscore: each is one past"
      print "# the end of a range, never a value anything sends. Left out rather than shown to a"
      print "# caller as though it were a choice."
      for (i = 1; i <= n; i++) {
        name = order[i]
        if (kind[name] != "enum") continue
        n2 = split(sentinels[name], s2, " ")
        for (j = 1; j <= n2; j++) if (s2[j] != "") { print "--exclude"; print s2[j] }
      }
      print "# <<< end naming block"
      if (failed) exit 1
    }' "$WORK/types.txt" > "$WORK/block.txt"

  # A refresh replaces the committed contract, so a discovery pass that saw less than the last one
  # must not be written. It exits 0 either way: a resource-directory or header problem shows up as
  # fewer declarations, not as an error, and the resulting block reads in the diff like a deliberate
  # reduction. Growth is fine and is the reason to run this; a collapse is not.
  OLD_OPTS=$(grep -c '^--' "$RSP")
  NEW_OPTS=$(grep -c '^--' "$WORK/block.txt")
  STATIC_OPTS=$(awk -v b="$BLOCK_BEGIN" -v e="$BLOCK_END" '
    index($0,b)==1 {skip=1} !skip && /^--/ {n++} index($0,e)==1 {skip=0} END {print n+0}' "$RSP")

  if [ "$NEW_OPTS" -lt $(( (OLD_OPTS - STATIC_OPTS) * 9 / 10 )) ]; then
    echo "ERROR: discovery found $NEW_OPTS naming options where the committed block has"
    echo "$(( OLD_OPTS - STATIC_OPTS )). That is a collapse, not a refresh, and it is almost always"
    echo "a clang resource directory or header set the parser could not read - which exits 0."
    echo "The committed block was left alone. New block kept at $WORK/block.txt for inspection."
    trap - EXIT
    exit 1
  fi

  awk -v b="$BLOCK_BEGIN" -v e="$BLOCK_END" -v blockfile="$WORK/block.txt" '
    index($0,b)==1 { while ((getline line < blockfile) > 0) print line; skip=1; next }
    index($0,e)==1 { skip=0; next }
    !skip { print }' "$RSP" > "$WORK/pipewire.rsp"
  mv "$WORK/pipewire.rsp" "$RSP"

  echo "Refreshed the naming block in $RSP ($(grep -c '^--' "$RSP") options)."
  echo "Run generate/generate.sh to regenerate against it."
  exit 0
fi

WORK=$(mktemp -d)
trap "rm -rf $WORK" EXIT
cd "$WORK"

# NOTE on traversal: ClangSharp's --traverse matches whole file paths, not
# directory prefixes (a bare dir traverses nothing; verified empirically), and it
# has no "traverse everything reachable" mode. So the set of headers to emit is an
# explicit list in pipewire.rsp. It is curated deliberately - adding a header there
# is the way to expose more native surface. Keep it in sync when bumping PipeWire.

# Header file prepended to each generated file - keeps generated types public while
# suppressing missing-XML-doc warnings and a few naming-style analyzers.
cat > header.txt << 'HEADER'
// <auto-generated/>
// Generated by ClangSharpPInvokeGenerator from libpipewire-0.3 headers.
// Run generate/generate.sh to regenerate.

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
#pragma warning disable CA1716 // Identifiers should not match keywords
#pragma warning disable CA1720 // Identifiers should not contain type names
#pragma warning disable CA1815 // Override Equals and operator equals on value types
HEADER

"$TOOL" "@$REPO_ROOT/generate/pipewire.rsp" \
  --file "$REPO_ROOT/generate/pipewire_composite.h" \
  --header-file "$WORK/header.txt" \
  --resource-directory "$CLANG_RESOURCE_DIR" \
  --output "$WORK" 2>&1 | tee "$WORK/gen.log"

EXIT=${PIPESTATUS[0]}
if [ $EXIT -ne 0 ]; then
  echo "Generator failed with exit $EXIT."
  exit $EXIT
fi

# Every anonymous enum is meant to be named by a --remap-type in pipewire.rsp, so this line means
# one got away - most likely a header edit moved a declaration and its line-numbered placeholder
# name no longer matches. Left alone the values quietly degrade to loose constants.
if grep -q "Found anonymous enum" "$WORK/gen.log"; then
  echo "ERROR: an anonymous enum was mapped to constants. Update its --remap-type in pipewire.rsp:"
  grep "Found anonymous enum" "$WORK/gen.log"
  exit 1
fi

if grep -qi "^Warning:" "$WORK/gen.log"; then
  echo "ERROR: the generator warned. Fix it rather than committing the output:"
  grep -i "^Warning:" "$WORK/gen.log"
  exit 1
fi

# ClangSharp matches --with-namespace on exact names only, so a type arriving from a newly
# traversed header silently lands in the default namespace instead of the one it belongs in. Every
# Spa* type is meant to be in PipeWire.NET.Spa, so one sitting in the ABI namespace is the tell.
STRAYS=$(grep -l "^namespace PipeWire.NET.Interop;" "$WORK"/Spa*.cs 2>/dev/null)
if [ -n "$STRAYS" ]; then
  echo "ERROR: these types have no --with-namespace entry. Run generate.sh --refresh-names:"
  echo "$STRAYS" | xargs -n1 basename
  exit 1
fi

# The same check for the enums named in PUBLIC_ENUMS. They are matched by exact name too, so one
# whose entry went missing lands back in the ABI namespace as internal - which reads in the diff as
# a type that simply disappeared from the public surface.
for entry in $PUBLIC_ENUMS; do
  enum="${entry%%=*}"; want="${entry#*=}"
  if [ ! -f "$WORK/$enum.cs" ]; then
    echo "ERROR: $enum is named in PUBLIC_ENUMS but the generator emitted no such type."
    exit 1
  fi
  if ! grep -q "^namespace $want;" "$WORK/$enum.cs"; then
    echo "ERROR: $enum is not in $want. Run generate.sh --refresh-names:"
    grep "^namespace" "$WORK/$enum.cs"
    exit 1
  fi
done

# Rename plain .cs to .g.cs and copy into the repo.
#
# Empty opaque structs are collapsed on the way. A type that several traversed headers
# forward-declare comes out once per declaration - "public partial struct pw_client {}" twice in
# one file. It compiles, because they are partial, but it is noise in generated output and reads
# like a generator fault every time somebody opens the file.
for f in "$WORK"/*.cs; do
  base=$(basename "$f" .cs)
  awk '
    /^public partial struct [A-Za-z0-9_]+$/ {
      name = $4
      if (name in seen) { skip = 1; next }
      seen[name] = 1
    }
    skip && /^\{$/ { next }
    skip && /^\}$/ { skip = 0; blank = 1; next }
    skip { next }
    blank && /^$/ { blank = 0; next }
    { blank = 0; print }
  ' "$f" > "$OUT/${base}.g.cs"

  # --with-access-specifier sets the type's accessibility, not its members', so an internal class
  # keeps public methods and every signature naming an internal struct is then a CS0050/CS0051.
  # Their effective accessibility is already internal because the container bounds it, so this only
  # makes the declaration say what is already true.
  # The backreference puts the indentation back. Without it every extern line loses its leading
  # whitespace, which is a whole-file diff on each regeneration that hides the real change.
  sed -i 's/^\(\s*\)public static extern /\1internal static extern /' "$OUT/${base}.g.cs"

  # ClangSharp emits every C enum as a plain enum, so a bitmask like pw_stream_flags reaches C# as
  # something the compiler and the debugger treat as a single value: ToString prints a number for
  # any combination, and nothing marks `Autoconnect | MapBuffers` as intended. The C side does not
  # mark bitmasks either, so it is read off the initialisers - two or more members written as a
  # shift (1 << n) is a bitmask. Hex initialisers are deliberately not the signal: SPA uses them for
  # range starts (SPA_TYPE_START = 0x10000), and flagging those would be wrong.
  if [ "$(grep -cE '^\s+[A-Za-z_][A-Za-z0-9_]* = .*<<' "$OUT/${base}.g.cs")" -ge 2 ]; then
    sed -i -E 's/^((public|internal) enum [A-Za-z_][A-Za-z0-9_]*)/[System.Flags]\n\1/' "$OUT/${base}.g.cs"
    echo "$base" >> "$WORK/flags-enums.txt"
  fi
done

# The enums named in PUBLIC_ENUMS live outside the ABI namespace, and ClangSharp only writes the
# using for that in the file it puts the P/Invokes in (Native.cs). Every other file that names one -
# pw_stream_events, pw_node_info, the event structs - gets no using and no qualification, so the
# output does not compile. This was invisible while they were in PipeWire.NET: the ABI namespace is
# nested inside it, so the names resolved with no using at all.
#
# So the usings are added here. Sorted into the existing block rather than appended, because
# ClangSharp writes that block sorted and CI regenerates and diffs - an unsorted insertion would
# show up as drift on every run.
python3 - "$OUT" "$PUBLIC_ENUMS" <<'PY'
import io, os, re, sys

out_dir, public_enums = sys.argv[1], sys.argv[2]
wanted = dict(line.split('=') for line in public_enums.split() if line)

for fn in sorted(os.listdir(out_dir)):
    if not fn.endswith('.g.cs'):
        continue
    path = os.path.join(out_dir, fn)
    text = io.open(path, encoding='utf-8').read()
    if '\nnamespace ' not in text:
        continue
    head, body = text.split('\nnamespace ', 1)
    declared = re.search(r'^namespace ([\w.]+);', 'namespace ' + body).group(1)

    needed = {ns for enum, ns in wanted.items()
              if ns != declared and re.search(r'\b' + enum + r'\b', body)}
    usings = set(re.findall(r'^using ([\w.]+);', head, re.M))
    missing = needed - usings
    if not missing:
        continue

    block = '\n'.join(f'using {u};' for u in sorted(usings | missing))
    if usings:
        head = re.sub(r'(^using [\w.]+;\n)+', block + '\n', head, count=1, flags=re.M)
    else:
        head = head.rstrip('\n') + '\n\n' + block + '\n'
    io.open(path, 'w', encoding='utf-8', newline='\n').write(head + '\nnamespace ' + body)
    print(f"  added {', '.join(sorted(missing))} to {fn}")
PY

# The flagged set is reviewed, not merely accepted: a new bitmask from a header bump should be seen
# by someone who can confirm it really is one, rather than slipping through as a side effect.
EXPECTED_FLAGS="PipeWireFilterFlags PipeWireFilterPortFlags PipeWireMemblockFlags PipeWireMemmapFlags PipeWireStreamFlags SpaVideoChromaSite SpaVideoFlags SpaVideoMultiviewFlags"
ACTUAL_FLAGS=$(sort -u "$WORK/flags-enums.txt" 2>/dev/null | tr '\n' ' ' | sed 's/ $//')
if [ "$ACTUAL_FLAGS" != "$EXPECTED_FLAGS" ]; then
  echo "ERROR: the set of enums marked [Flags] changed. Confirm each is a bitmask, then update EXPECTED_FLAGS:"
  echo "  expected: $EXPECTED_FLAGS"
  echo "  actual:   $ACTUAL_FLAGS"
  exit 1
fi

# The macro passes: one per library whose headers they read.
#
# Split from the ABI pass because emitting macros needs --generate macro-bindings, and that leaves
# diagnostics behind from macros ClangSharp cannot translate. The ABI pass fails on any diagnostic
# at all, which is what catches a type landing in the wrong namespace; keeping the noisy work here
# lets that guard stay absolute while these tolerate exactly two known classes.
#
# Split three ways rather than one because the output is read by people: a fourcc code from
# drm_fourcc.h and an errno from the C library are not PipeWire constants, and one class called
# NativeConstants holding all of them said they were. Each pass names its own class - see
# generate/pipewire-constants.rsp, libc.rsp and libdrm.rsp - and each runs with file=multi, so the
# structs land in files of their own rather than inside the constants file.
#
# The old comment claiming macro-bindings is fatal on the PipeWire headers was wrong - it exits 0.
# The reason to split from the ABI pass is the noise, not a failure.
{
  sed 's/from libpipewire-0.3 headers/from the libpipewire-0.3, libdrm and libc headers/' "$WORK/header.txt"
  printf '\nusing PipeWire.NET.Interop;\n'
} > "$WORK/header-constants.txt"

# <rsp base name> <the class it must emit>. Runs the pass, checks its log, and copies its output
# into generated/ as .g.cs.
run_macro_pass() {
  local name="$1"
  local class="$2"
  local dir="$WORK/$name"
  mkdir -p "$dir"

  "$TOOL" "@$REPO_ROOT/generate/$name.rsp" \
    --file "$REPO_ROOT/generate/${name}_composite.h" \
    --header-file "$WORK/header-constants.txt" \
    --resource-directory "$CLANG_RESOURCE_DIR" \
    --output "$dir" 2>&1 | tee "$WORK/gen-$name.log"

  local pass_exit=${PIPESTATUS[0]}

  # Two known classes, and only those. Function-like macros take arguments, so they are not
  # constants and there is nothing here that wants them - drm_fourcc.h's AMD_FMT_MOD_SET and the
  # BROADCOM column-height helpers are the whole of that class. 'Const' is glibc's
  # __attribute_const__ on gnu_dev_makedev/major/minor: an optimiser hint that the result depends
  # only on the arguments, with nothing a binding could carry, so dropping it loses nothing.
  # Anything else in the log is a real diagnostic and fails the run.
  local other
  other=$(grep -i "warning\|error" "$WORK/gen-$name.log" \
    | grep -v "Function like macro definition records are not supported" \
    | grep -v "Unsupported attribute: 'Const'" \
    | grep -v "^Processing " || true)

  if [ -n "$other" ]; then
    echo "ERROR: the $name pass reported an unexpected diagnostic:"
    echo "$other"
    exit 1
  fi

  if [ ! -s "$dir/$class.cs" ]; then
    echo "ERROR: the $name pass produced no $class.cs (exit $pass_exit)."
    exit 1
  fi
}

run_macro_pass pipewire-constants NativeConstants
run_macro_pass libc              NativeLibc
run_macro_pass libdrm            NativeLibdrm

PW_CONSTANTS="$WORK/pipewire-constants/NativeConstants.cs"

# A cheap tripwire on each allowlist: if a --include glob stops matching because a header moved or
# a family was renamed, the file still generates and simply omits them, which nothing else notices.
for family in PW_KEY_ PW_VERSION_ PW_TYPE_INTERFACE_; do
  n=$(grep -oE "${family}[A-Za-z_0-9]+" "$PW_CONSTANTS" | sort -u | wc -l)
  if [ "$n" -lt 9 ]; then
    echo "ERROR: only $n ${family}* constants were generated; the allowlist or a traversed header moved."
    exit 1
  fi
done

n=$(grep -oE "DRM_FORMAT_[A-Za-z_0-9]+" "$WORK/libdrm/NativeLibdrm.cs" | sort -u | wc -l)
if [ "$n" -lt 9 ]; then
  echo "ERROR: only $n DRM_FORMAT_* constants were generated; the allowlist or a header moved."
  exit 1
fi

# C writes octal with a leading zero (PW_PERM_R is 0400) and ClangSharp copies the literal across
# unchanged. C# has no octal literal and reads a leading zero as nothing, so 0400 compiles - as
# decimal 400 instead of 256. Nothing fails; every permission built from it is simply wrong. So the
# initialisers are rewritten to hex here, and the run stops if any leading-zero literal survives.
# The NativeTypeName strings keep their octal on purpose: they quote the C source, they are not math.
python3 - "$PW_CONSTANTS" "$WORK/libc/NativeLibc.cs" "$WORK/libdrm/NativeLibdrm.cs" <<'PY'
import io, re, sys

octal = re.compile(r'(?<![\w.x])0([0-7]+)(?![\w.])')
fixed = 0
for path in sys.argv[1:]:
    lines = io.open(path, encoding='utf-8').read().split('\n')
    for i, line in enumerate(lines):
        m = re.match(r'(\s*public const \w+ \w+ = )(.*)$', line)
        if not m:
            continue
        new = octal.sub(lambda t: hex(int(t.group(1), 8)), m.group(2))
        if new != m.group(2):
            lines[i] = m.group(1) + new
            fixed += 1
    io.open(path, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines))

    left = [l.strip() for l in lines
            if re.match(r'\s*public const ', l) and octal.search(l.split('=', 1)[1])]
    if left:
        print(f'ERROR: leading-zero literals remain in constant initialisers in {path}:')
        print('\n'.join(left))
        sys.exit(1)
print(f"Rewrote {fixed} octal initialisers to hex")
PY

# Into generated/, one .g.cs per emitted file. The extern-visibility rewrite is the ABI loop's, for
# the same reason: --with-access-specifier sets the type's accessibility, not its members'.
for dir in pipewire-constants libc libdrm; do
  for f in "$WORK/$dir"/*.cs; do
    base=$(basename "$f" .cs)
    cp "$f" "$OUT/${base}.g.cs"
    sed -i 's/^\(\s*\)public static extern /\1internal static extern /' "$OUT/${base}.g.cs"
  done
done


# A third pass for spa/pod/filter.h was tried and removed; see HANDOFF for the measurements.
# Short version: ClangSharp translates filter.h's own bodies fine, but they call 41 symbols from a
# closure that runs through spa/pod/builder.h, which has 42 compound-literal constructs ClangSharp
# cannot translate. generate/podfilter.rsp is kept for whoever picks it up.

# The string-valued constants again, as `const string`.
#
# ClangSharp renders a string macro as a UTF-8 span, which is the right shape for handing a key to
# a spa_dict without transcoding, and the wrong one for everything else: PipeWireProperties is keyed
# by string, so a caller reading a property off a graph object needs the same key as a string. There
# is no generator option for that, and hand-writing a second copy is what let the old list drift to
# 59 of 192 entries.
#
# So it is derived from the generated file rather than written: same literals, transposed into the
# other representation. No names are invented here - the identifier is carried across unchanged -
# which is what keeps this a mechanical step rather than a second source of truth.
python3 - "$PW_CONSTANTS" "$OUT/PipeWireKeys.g.cs" "$WORK/header.txt" <<'PY'
import io, re, sys

src, out_path, header_path = sys.argv[1], sys.argv[2], sys.argv[3]
text = io.open(src, encoding='utf-8').read()

pattern = re.compile(
    r'ReadOnlySpan<byte>\s+(PW_KEY_[A-Za-z_0-9]+|SPA_KEY_[A-Za-z_0-9]+|PW_TYPE_INTERFACE_[A-Za-z_0-9]+)\s*=>\s*"([^"]*)"u8;')

seen = {}
for name, value in pattern.findall(text):
    seen.setdefault(name, value)

if len(seen) < 100:
    raise SystemExit(f"only {len(seen)} string constants found; the generated shape changed")

header = io.open(header_path, encoding='utf-8').read().rstrip('\n')
lines = [header, '']
lines.append('namespace PipeWire.NET.Graph;')
lines.append('')
lines.append('/// <summary>')
lines.append('/// PipeWire property keys and interface type names, as strings.')
lines.append('/// </summary>')
lines.append('/// <remarks>')
lines.append('/// Derived from the generated UTF-8 spans in <c>NativeConstants</c>, which is the form the')
lines.append('/// native side wants. These are the same values as <see cref="string"/>, for reading a')
lines.append('/// property off a graph object - <c>PipeWireProperties</c> is keyed by string.')
lines.append('/// </remarks>')
lines.append('public static partial class PipeWireKeys')
lines.append('{')

for name in sorted(seen):
    lines.append(f'    /// <summary><c>{seen[name]}</c></summary>')
    lines.append(f'    public const string {name} = "{seen[name]}";')
    lines.append('')
if lines and lines[-1] == '':
    lines.pop()
lines.append('}')

io.open(out_path, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines) + '\n')
print(f"Derived {len(seen)} string constants into {out_path}")
PY

# Flag families that upstream spells as #define rather than as an enum. The macro pass emits them as
# loose NativeConstants, which is the right form for nothing: a node flag and a param-info flag are
# both a bare uint there, so passing one where the other belongs compiles - and that is exactly how
# SPA_NODE_FLAG_RT came to be announced as SPA_NODE_FLAG_OUT_PORT_CONFIG from a hand copy.
#
# So each family becomes a [Flags] enum whose members are initialised *from* the generated constant,
# never from a number, which keeps this a transposition rather than a second source of truth. The
# underlying type is the one of the struct field the family is stored in (spa_node_info.flags is a
# uint64_t, spa_param_info.flags a uint32_t). Member names are the macro suffix Pascal-cased; the one
# addition is a zero None where upstream has none, because a flags enum without one has no name for
# "nothing set".
python3 - "$PW_CONSTANTS" "$OUT" "$WORK/header.txt" "$REPO_ROOT/generate/pipewire-constants.rsp" <<'PY'
import io, re, sys, os

src, out_dir, header_path, rsp_path = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
text = io.open(src, encoding='utf-8').read()

# Upstream's own member documentation: the /**< ... */ after each #define, from the headers the
# constants pass traverses. Lifted rather than written so the docs cannot drift from the header.
headers = [l.strip() for l in io.open(rsp_path, encoding='utf-8')
           if l.strip().startswith('/usr/include/') and l.strip().endswith('.h')]
docs = {}
for h in headers:
    if not os.path.exists(h):
        continue
    body = io.open(h, encoding='utf-8', errors='replace').read()
    for m in re.finditer(r'#define\s+([A-Z][A-Z0-9_]*)[^\n]*?/\*\*<(.*?)\*/', body, re.S):
        # Continuation lines of a C block comment start with ' * '; that star is layout, not text.
        docs.setdefault(m.group(1), ' '.join(re.sub(r'\n\s*\*(?!/)', ' ', m.group(2)).split()))

S, G = 'PipeWire.NET.Spa', 'PipeWire.NET.Graph'
# (macro prefix, enum, underlying, access, namespace, stored in, renames, excluded macros)
families = [
    ('SPA_NODE_FLAG_',                 'SpaNodeFlags',              'ulong', 'internal', S, 'spa_node_info.flags', {}, ()),
    ('SPA_NODE_CHANGE_MASK_',          'SpaNodeChangeMask',         'ulong', 'internal', S, 'spa_node_info.change_mask', {}, ()),
    ('SPA_PORT_FLAG_',                 'SpaPortFlags',              'ulong', 'internal', S, 'spa_port_info.flags', {}, ()),
    ('SPA_PORT_CHANGE_MASK_',          'SpaPortChangeMask',         'ulong', 'internal', S, 'spa_port_info.change_mask', {}, ()),
    ('SPA_PARAM_INFO_',                'SpaParamInfoFlags',         'uint',  'public',   S, 'spa_param_info.flags', {'READWRITE': 'ReadWrite'}, ()),
    ('SPA_POD_PROP_FLAG_',             'SpaPodPropFlags',           'uint',  'public',   S, 'spa_pod_prop.flags', {}, ()),
    ('SPA_DATA_FLAG_',                 'SpaDataFlags',              'uint',  'internal', S, 'spa_data.flags', {'READWRITE': 'ReadWrite'}, ()),
    ('SPA_CHUNK_FLAG_',                'SpaChunkFlags',             'int',   'internal', S, 'spa_chunk.flags', {}, ()),
    ('SPA_META_HEADER_FLAG_',          'SpaMetaHeaderFlags',        'uint',  'internal', S, 'spa_meta_header.flags', {}, ()),
    ('SPA_META_SYNC_TIMELINE_',        'SpaMetaSyncTimelineFlags',  'uint',  'public',   S, 'spa_meta_sync_timeline.flags', {}, ()),
    ('SPA_DEVICE_CHANGE_MASK_',        'SpaDeviceChangeMask',       'ulong', 'internal', S, 'spa_device_info.change_mask', {}, ()),
    ('SPA_DEVICE_OBJECT_CHANGE_MASK_', 'SpaDeviceObjectChangeMask', 'ulong', 'internal', S, 'spa_device_object_info.change_mask', {}, ()),
    ('SPA_IO_CLOCK_FLAG_',             'SpaIoClockFlags',           'uint',  'internal', S, 'spa_io_clock.flags', {}, ()),
    ('SPA_IO_VIDEO_SIZE_',             'SpaIoVideoSizeFlags',       'uint',  'internal', S, 'spa_io_video_size.flags', {}, ()),
    ('SPA_IO_SEGMENT_FLAG_',           'SpaIoSegmentFlags',         'uint',  'internal', S, 'spa_io_segment.flags', {}, ()),
    ('SPA_IO_SEGMENT_BAR_FLAG_',       'SpaIoSegmentBarFlags',      'uint',  'internal', S, 'spa_io_segment_bar.flags', {}, ()),
    ('SPA_IO_SEGMENT_VIDEO_FLAG_',     'SpaIoSegmentVideoFlags',    'uint',  'internal', S, 'spa_io_segment_video.flags', {}, ()),
    ('SPA_STATUS_',                    'SpaStatus',                 'int',   'internal', S, 'spa_io_buffers.status', {}, ()),
    # The single letters are upstream's spelling; the names are the ones this library has always
    # used for them. PW_PERM_INVALID is a sentinel meaning "no such permission set", not a bit.
    ('PW_PERM_',                       'PipeWirePermissions',       'uint',  'public',   G, 'pw_permission.permissions',
        {'R': 'Read', 'W': 'Write', 'X': 'Execute', 'M': 'Metadata', 'L': 'Link', 'RW': 'ReadWrite',
         'RWX': 'ReadWriteExecute', 'RWXM': 'ReadWriteExecuteMetadata',
         'RWXML': 'ReadWriteExecuteMetadataLink', 'ALL': 'All'},
        ('PW_PERM_INVALID',)),
]


def pascal(suffix):
    return ''.join(p[:1].upper() + p[1:].lower() for p in suffix.split('_') if p)


def xml(s):
    return s.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')


header = io.open(header_path, encoding='utf-8').read().rstrip('\n')
by_ns = {}
for prefix, name, underlying, access, ns, field, renames, excluded in families:
    consts = [(m, v) for m, v in re.findall(r'public const \w+ (' + prefix + r'[A-Z0-9_]+) = ([^;]+);', text)
              if m not in excluded]
    if not consts:
        raise SystemExit(f"no {prefix}* constants found; the generated shape changed")
    out = by_ns.setdefault(ns, [])
    out.append('/// <summary>Upstream <c>' + prefix + '*</c>, stored in <c>' + field + '</c>.</summary>')
    out.append('[System.Flags]')
    out.append(f'{access} enum {name} : {underlying}')
    out.append('{')
    members = [(renames.get(m[len(prefix):], pascal(m[len(prefix):])), m, v.strip()) for m, v in consts]
    if not any(v in ('0', '(0)') for _, _, v in members):
        out.append('    /// <summary>No flag set.</summary>')
        out.append('    None = 0,')
        out.append('')
    for member, macro, _ in members:
        doc = f': {xml(docs[macro])}' if macro in docs else ''
        out.append(f'    /// <summary><c>{macro}</c>{doc}</summary>')
        out.append(f'    {member} = unchecked(({underlying})PipeWire.NET.Interop.NativeConstants.{macro}),')
        out.append('')
    if out[-1] == '':
        out.pop()
    out.append('}')
    out.append('')

files = {S: 'SpaFlags.g.cs', G: 'PipeWireFlags.g.cs'}
for ns, body in by_ns.items():
    if body and body[-1] == '':
        body.pop()
    path = os.path.join(out_dir, files[ns])
    io.open(path, 'w', encoding='utf-8', newline='\n').write(
        '\n'.join([header, '', f'namespace {ns};', ''] + body) + '\n')
print(f"Derived {len(families)} flag enums into {', '.join(files[ns] for ns in by_ns)}")
PY


printf '%s
' "$HEADER_VERSION" > "$PINNED_FILE"

count=$(ls "$OUT"/*.g.cs 2>/dev/null | wc -l)
loc=$(wc -l "$OUT"/*.g.cs 2>/dev/null | tail -1 | awk '{print $1}')
echo "Generated $count files / $loc LOC into $OUT against PipeWire $HEADER_VERSION"
echo "Review the diff and commit."
