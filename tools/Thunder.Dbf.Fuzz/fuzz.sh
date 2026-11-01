#!/usr/bin/env bash
# ProjectThunder :: Thunder.Dbf fuzzer driver.
# usage: tools/Thunder.Dbf.Fuzz/fuzz.sh <firstSeed> <lastSeed>
# Runs the fuzzer in chunks and restarts it past any seed that hangs or crashes it. Findings go to
# artifacts/fuzz/findings.jsonl, with each finding's damaged files in artifacts/fuzz/seed-N/.
set -u
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
out=$root/artifacts/fuzz
work=$root/artifacts/fuzz-work
mkdir -p "$out" "$work"
dotnet build "$here" -c Release -nologo -v q > /dev/null || exit 1
dll=$(find "$here/bin/Release" -name Thunder.Dbf.Fuzz.dll | head -1)
seed=$1; last=$2; chunk=500
while [ "$seed" -le "$last" ]; do
  n=$(( last - seed + 1 < chunk ? last - seed + 1 : chunk ))
  DOTNET_GCHeapHardLimit=0x100000000 dotnet "$dll" "$root/tests/fixtures" "$work" "$out" "$seed" "$n" 2>> "$out/stderr.txt"
  code=$?
  if [ $code -eq 0 ]; then
    seed=$(( seed + n ))
  else
    stuck=$(cut -f1 "$out/progress.txt")
    what=$([ $code -eq 3 ] && echo hang || echo "crash (exit $code)")
    printf '{"seed": %s, "what": "%s"}\n' "$stuck" "$what" >> "$out/findings.jsonl"
    mkdir -p "$out/seed-$stuck" && cp "$work/c$(( stuck % 64 ))"/* "$out/seed-$stuck/" 2> /dev/null
    seed=$(( stuck + 1 ))
  fi
done
echo "Fuzzed seeds $1 to $last. Findings: $(wc -l < "$out/findings.jsonl" 2> /dev/null || echo 0)"
