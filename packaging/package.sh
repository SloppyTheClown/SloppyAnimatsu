#!/usr/bin/env bash
# Builds the self-contained SloppyAnimatsu packages for Linux and Windows.
#
#   ./packaging/package.sh                     both runtimes, into dist/
#   ./packaging/package.sh linux-x64           one runtime
#   ./packaging/package.sh --version 1.0.0 --out /tmp/dist --keep-stage
#
# Publishes SloppyAnimatsu.Viewer and SloppyAnimatsu.Cli for each runtime and packs them as
# dist/SloppyAnimatsu-<version>-<runtime>.tar.gz (Linux) or .zip (Windows), with the user documentation
# in docs/, a short README.txt and, on Linux, install.sh and its icon. Writes dist/SHA256SUMS.
#
# The version comes from Directory.Build.props; the .NET 9 SDK is the only requirement and either
# platform can cross-publish the other's package. The tar.gz is written by tools/PackageTool so its
# permissions do not depend on the machine that built it.
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
version=""
out="$root/dist"
keep_stage=0
runtimes=()

usage() {
    cat <<'EOF'
usage: packaging/package.sh [options] [win-x64|linux-x64|linux-arm64]...

  --version V       overrides the version in Directory.Build.props
  --out DIR         writes the packages there (default: dist beside the solution)
  --keep-stage      keeps dist/.stage, to inspect a package without extracting it
  -h, --help        this text
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version=${2:?--version needs a value}; shift 2 ;;
        --out) out=${2:?--out needs a value}; shift 2 ;;
        --keep-stage) keep_stage=1; shift ;;
        -h|--help) usage; exit 0 ;;
        win-x64|linux-x64|linux-arm64) runtimes+=("$1"); shift ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done
[ ${#runtimes[@]} -gt 0 ] || runtimes=(win-x64 linux-x64)

command -v dotnet >/dev/null 2>&1 || {
    echo "dotnet is not on PATH; the .NET 9 SDK is needed to package." >&2
    exit 1
}
if [ -z "$version" ]; then
    version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n 1)
    [ -n "$version" ] || { echo "Directory.Build.props has no <Version> element." >&2; exit 1; }
fi

mkdir -p "$out"
out=$(cd "$out" && pwd)
stage_root="$out/.stage"
rm -rf "$stage_root"
mkdir -p "$stage_root"

cleanup() {
    if [ -n "${tool_dir:-}" ]; then rm -rf "$tool_dir"; fi
    if [ "$keep_stage" -eq 0 ]; then rm -rf "$stage_root"; fi
}
trap cleanup EXIT

publish() { # project runtime target
    # The .pdb files stay beside the exes: a crash log's stack trace carries file names and line
    # numbers only when the matching symbols are next to the assembly.
    dotnet publish "$root/$1" -c Release -r "$2" --self-contained true \
        -p:Version="$version" -p:DebugType=portable -p:DebugSymbols=true \
        -p:GenerateDocumentationFile=false --nologo -v:minimal -o "$3"
}

# The archiver is published once: it writes the tar.gz modes itself, so the Linux package does not
# depend on the permissions the build machine's file system reports.
tool_dir="$out/.tool"
dotnet publish "$root/tools/PackageTool" -c Release --nologo -v:quiet -o "$tool_dir"
tool="$tool_dir/PackageTool"
chmod 755 "$tool"

archive() { # kind source target [executable...]
    local kind=$1 source=$2 target=$3
    shift 3
    local args=("$kind" "$source" "$target") e
    for e in "$@"; do args+=(--exec "$e"); done
    "$tool" "${args[@]}"
}

echo "SloppyAnimatsu $version: packing for ${runtimes[*]}"
artifacts=()
for runtime in "${runtimes[@]}"; do
    name="SloppyAnimatsu-$version-$runtime"
    # One folder per runtime, so a package's archive holds exactly one top-level folder.
    container="$stage_root/$runtime"
    stage="$container/$name"
    mkdir -p "$stage"
    echo "== $runtime"
    publish src/SloppyAnimatsu.Viewer "$runtime" "$stage"
    publish src/SloppyAnimatsu.Cli "$runtime" "$stage"
    cp -R "$root/docs" "$stage/"
    cp "$root/LICENSE" "$stage/LICENSE.txt"
    case "$runtime" in
        win-*)
            cp "$root/packaging/windows/README.txt" "$stage/README.txt"
            target="$out/$name.zip"
            archive zip "$container" "$target"
            ;;
        *)
            cp "$root/packaging/linux/README.txt" "$stage/README.txt"
            cp "$root/packaging/linux/install.sh" "$stage/"
            cp "$root/packaging/linux/sloppyanimatsu.svg" "$stage/"
            chmod 755 "$stage/install.sh" "$stage/SloppyAnimatsu.Viewer" "$stage/SloppyAnimatsu.Cli"
            target="$out/$name.tar.gz"
            archive tar "$container" "$target" "$name/SloppyAnimatsu.Viewer" "$name/SloppyAnimatsu.Cli"
            ;;
    esac
    artifacts+=("$target")
done

: > "$out/SHA256SUMS"
for artifact in "${artifacts[@]}"; do
    printf '%s  %s\n' "$(sha256sum "$artifact" | cut -d' ' -f1)" "$(basename "$artifact")" >> "$out/SHA256SUMS"
done

echo
echo "Packages in $out"
for artifact in "${artifacts[@]}"; do
    printf '  %-40s %8s\n' "$(basename "$artifact")" "$(du -h "$artifact" | cut -f1)"
done
echo "  SHA256SUMS"




