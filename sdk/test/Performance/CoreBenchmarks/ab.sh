#!/usr/bin/env bash
# A/B BenchmarkDotNet comparison of AWSSDK.Core between two commits.
#
# Usage:
#   ab.sh <base-ref> <pr-ref> <bdn-filter> [options]
#
# Options:
#   --runtimes "<tfms>"   Target frameworks to measure, each run as the BDN host (default: "net10.0 net11.0")
#   --rounds <n>          Interleaved rounds, base then PR each round (default: 3)
#   --job <name>          BDN job: Default, Short, Medium, Long (default: Default)
#   --out <dir>           Output directory (default: ./ab-results/<base>-<pr>-<timestamp>)
#
# The benchmark project from <pr-ref> is copied over the base worktree, so both
# sides run identical benchmark code and only AWSSDK.Core differs. Benchmarks
# must therefore only call APIs that exist on <base-ref>.
#
# Only one benchmark run executes on the machine at a time (flock on
# $AB_LOCK, default /tmp/core-bench.lock). Builds run outside the lock.
#
# Writes <out>/summary.md, a markdown table ready to paste into a PR description.

set -euo pipefail

usage() { sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }

[[ $# -ge 3 ]] || usage
BASE_REF=$1; PR_REF=$2; FILTER=$3; shift 3

RUNTIMES="net10.0 net11.0"
ROUNDS=3
JOB=Default
OUT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --runtimes) RUNTIMES=$2; shift 2 ;;
    --rounds)   ROUNDS=$2; shift 2 ;;
    --job)      JOB=$2; shift 2 ;;
    --out)      OUT=$2; shift 2 ;;
    -h|--help)  usage ;;
    *) echo "Unknown option: $1" >&2; usage ;;
  esac
done

# Resolve symlinks (for example /home -> /local/home): mixed paths break MSBuild project references.
SCRIPT_DIR=$(cd -P "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
REPO=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)
BENCH_REL=sdk/test/Performance/CoreBenchmarks
WT_ROOT=$(realpath -m "${AB_WORKTREES:-$HOME/code/perf-ab-worktrees}")
LOCK=${AB_LOCK:-/tmp/core-bench.lock}

BASE_SHA=$(git -C "$REPO" rev-parse --verify "$BASE_REF^{commit}")
PR_SHA=$(git -C "$REPO" rev-parse --verify "$PR_REF^{commit}")
B12=${BASE_SHA:0:12}; P12=${PR_SHA:0:12}
[[ -n "$OUT" ]] || OUT="$PWD/ab-results/${B12}-${P12}-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$OUT"
OUT=$(cd -P "$OUT" && pwd -P)

log() { echo "[ab] $*" >&2; }

ensure_worktree() {
  local sha=$1 dir=$2
  if [[ -d "$dir" ]]; then
    git -C "$dir" checkout --quiet --detach "$sha"
    git -C "$dir" reset --quiet --hard "$sha"   # throwaway worktree owned by this script
  else
    mkdir -p "$WT_ROOT"
    git -C "$REPO" worktree add --quiet --detach "$dir" "$sha"
  fi
}

BASE_WT="$WT_ROOT/base-$B12"
PR_WT="$WT_ROOT/pr-$P12"
log "base $BASE_SHA -> $BASE_WT"
ensure_worktree "$BASE_SHA" "$BASE_WT"
log "pr   $PR_SHA -> $PR_WT"
ensure_worktree "$PR_SHA" "$PR_WT"

[[ -f "$PR_WT/$BENCH_REL/CoreBenchmarks.csproj" ]] || { echo "No CoreBenchmarks project at $PR_REF" >&2; exit 1; }

# Use the PR's benchmark source on both sides.
log "copying benchmark source from PR to base worktree"
rm -rf "$BASE_WT/$BENCH_REL"
mkdir -p "$BASE_WT/$BENCH_REL"
( cd "$PR_WT/$BENCH_REL" && tar --exclude=bin --exclude=obj --exclude=BenchmarkDotNet.Artifacts -cf - . ) \
  | ( cd "$BASE_WT/$BENCH_REL" && tar -xf - )

for side in base pr; do
  wt=$BASE_WT; [[ $side == pr ]] && wt=$PR_WT
  log "building $side"
  dotnet build "$wt/$BENCH_REL/CoreBenchmarks.csproj" -c Release -nologo -v q > "$OUT/build-$side.log" 2>&1 \
    || { echo "Build failed for $side, see $OUT/build-$side.log" >&2; tail -20 "$OUT/build-$side.log" >&2; exit 1; }
done

run_side() {
  local side=$1 round=$2 wt=$3 tfm art
  for tfm in $RUNTIMES; do
    art="$OUT/round$round/$side/$tfm"
    mkdir -p "$art"
    log "round $round/$ROUNDS: $side ($tfm)"
    # Each runtime runs as the BDN host process (-f), so any TFM the project targets works,
    # including ones BenchmarkDotNet has no --runtimes moniker for yet.
    ( cd "$wt/$BENCH_REL" && dotnet run -c Release -f "$tfm" --no-build -- \
        --filter "$FILTER" --job "$JOB" --memory --exporters json --artifacts "$art" ) > "$art/run.log" 2>&1 \
      || { echo "Benchmark run failed ($side, $tfm, round $round), see $art/run.log" >&2; tail -30 "$art/run.log" >&2; exit 1; }
    if ! ls "$art"/results/*-report-full-compressed.json > /dev/null 2>&1; then
      echo "No BDN results for $side ($tfm, round $round); check the filter. See $art/run.log" >&2
      tail -30 "$art/run.log" >&2
      exit 1
    fi
  done
}

log "waiting for benchmark lock $LOCK"
exec 9>"$LOCK"
flock 9
log "lock acquired"
for ((r = 1; r <= ROUNDS; r++)); do
  run_side base "$r" "$BASE_WT"
  run_side pr "$r" "$PR_WT"
done
flock -u 9

python3 "$SCRIPT_DIR/compare.py" "$OUT" --base "$BASE_SHA" --pr "$PR_SHA" --filter "$FILTER" > "$OUT/summary.md"
log "summary: $OUT/summary.md"
cat "$OUT/summary.md"
