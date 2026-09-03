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

# Enums a consumer reads directly, so they belong in the front-door namespace, not beside the ABI.
PUBLIC_ENUMS="PipeWireStreamState PipeWireStreamFlags PipeWireNodeState PipeWireLinkState
PipeWireFilterState PipeWireFilterFlags PipeWireFilterPortFlags"

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
      split(public_enums,  pe, /[ \n]+/); for (i in pe) if (pe[i] != "") isPublic[pe[i]]  = 1
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
      print "# Absent is what cannot be generated: SpaPodPropFlag, SpaDataFlag and the PW_KEY_*"
      print "# strings are #define macros, and macro binding generation is fatal on this header set."
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
        if (name in isPublic) ns = "PipeWire.NET"
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
  sed -i 's/^\(\s*\)public static extern /internal static extern /' "$OUT/${base}.g.cs"
done

printf '%s
' "$HEADER_VERSION" > "$PINNED_FILE"

count=$(ls "$OUT"/*.g.cs 2>/dev/null | wc -l)
loc=$(wc -l "$OUT"/*.g.cs 2>/dev/null | tail -1 | awk '{print $1}')
echo "Generated $count files / $loc LOC into $OUT against PipeWire $HEADER_VERSION"
echo "Review the diff and commit."
