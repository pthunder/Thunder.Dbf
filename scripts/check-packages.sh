#!/usr/bin/env bash
#     ___   ___   ___
#    |pkg| |pkg| |pkg|     ProjectThunder :: Thunder.Dbf
#    |___| |___| |___|     Checks what `dotnet pack` made before anyone can publish it:
#      \     |     /       every package has its README, icon, symbols and source commit,
#       '----+----'        the libraries carry their XML docs, and the CLI installs and runs.
#            |
#        [ nuget ]         Used by CI on every push and by the release workflow.
#
# Usage: scripts/check-packages.sh <folder with .nupkg and .snupkg files>

set -o pipefail
folder=${1:?usage: scripts/check-packages.sh <folder with the packed .nupkg files>}

failures=0
fail() {
    echo "  ✗ $1"
    failures=$((failures + 1))
}

# The one package for an id, matched on "<id>.<digit>" so Thunder.Dbf doesn't also match Thunder.Dbf.Sql.
package_for() {
    local matches=("$folder/$1".[0-9]*.nupkg)
    [[ -f "${matches[0]}" ]] && echo "${matches[0]}"
}

# grep reads to the end (no -q): stopping early would kill unzip with SIGPIPE, which pipefail
# reports as a failure on large packages.
has_entry() { unzip -Z1 "$1" | grep -x "$2" > /dev/null; }

check_common() {
    local id=$1 nupkg=$2
    has_entry "$nupkg" 'README.md' || fail "$id: no README.md"
    has_entry "$nupkg" 'icon.png' || fail "$id: no icon.png"
    [[ -f "${nupkg%.nupkg}.snupkg" ]] || fail "$id: no .snupkg symbols package"
    # PublishRepositoryUrl and Source Link put the exact commit in the nuspec.
    unzip -p "$nupkg" "$id.nuspec" | grep -E '<repository [^>]*commit="[0-9a-f]{40}"' > /dev/null \
        || fail "$id: the nuspec doesn't name the source commit"
}

for id in Thunder.Dbf Thunder.Dbf.Sql; do
    echo "• $id"
    nupkg=$(package_for "$id") || { fail "$id: no package in $folder"; continue; }
    check_common "$id" "$nupkg"
    has_entry "$nupkg" "lib/net10.0/$id.dll" || fail "$id: no lib/net10.0/$id.dll"
    has_entry "$nupkg" "lib/net10.0/$id.xml" || fail "$id: no XML docs (lib/net10.0/$id.xml)"
done

echo "• thunder-dbf"
if nupkg=$(package_for thunder-dbf); then
    check_common thunder-dbf "$nupkg"
    has_entry "$nupkg" 'tools/net10.0/any/DotnetToolSettings.xml' || fail "thunder-dbf: not packed as a .NET tool"

    # Install from this folder only (--source replaces nuget.org), so a published package
    # of the same version can't stand in for the one just built.
    tool_dir=$(mktemp -d)
    version=$(basename "$nupkg" .nupkg)
    version=${version#thunder-dbf.}
    if dotnet tool install thunder-dbf --tool-path "$tool_dir" --source "$folder" --version "$version" > /dev/null; then
        "$tool_dir/thunder-dbf" --help > /dev/null || fail "thunder-dbf: installed, but --help failed"
    else
        fail "thunder-dbf: doesn't install as a .NET tool"
    fi
    rm -rf "$tool_dir"
else
    fail "thunder-dbf: no package in $folder"
fi

# The web service ships as self-contained zips in the GitHub release, never on NuGet.
for stray in "$folder"/*Server*.nupkg; do
    [[ -e "$stray" ]] && fail "the web service shouldn't be a NuGet package: $(basename "$stray")"
done

echo
if (( failures > 0 )); then
    echo "Package check failed: $failures problem(s)."
    exit 1
fi
echo "Package check passed."
