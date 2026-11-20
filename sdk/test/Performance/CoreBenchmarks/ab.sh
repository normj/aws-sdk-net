#!/usr/bin/env bash
# A/B BenchmarkDotNet comparison of AWSSDK.Core between two commits.
#
# Usage:
#   ab.sh <base-ref> <pr-ref> <bdn-filter> [options]
#
# Options:
#   --runtimes "<tfms>"   Target frameworks to measure, each run as the BDN host (default: "net10.0 net11.0")
#   --rounds <n>          Interleaved rounds, base then PR each round (default: 3)
#   --job <name>          BDN job: Default, Short, Medium, Long, Dry (default: Default)
#   --out <dir>           Output directory (default: ./ab-results/<base>-<pr>-<timestamp>)
#   --background          Detach from the calling shell (setsid nohup), log to <out>/ab.log, print the PID
#   --keep-worktrees      Keep this run's worktrees instead of removing them at exit
#
# Environment:
#   AB_LOCK        Benchmark lock file (default: /tmp/core-bench.lock)
#   AB_WORKTREES   Parent directory for per-run worktrees (default: ~/code/perf-ab-worktrees)
#   AB_MAX_LOAD    Before each measurement, wait until the number of other runnable tasks (sampled
#                  over 5 seconds) is at or below this (default: nproc / 8, minimum 2)
#   AB_LOAD_WAIT   Maximum seconds to wait for the machine to go quiet before measuring anyway (default: 1800)
#
# The benchmark project from <pr-ref> is copied over the base worktree, so both
# sides run identical benchmark code and only AWSSDK.Core differs. Benchmarks
# must therefore only call APIs that exist on <base-ref>.
#
# Each run gets its own pair of worktrees, so concurrent runs never touch each
# other's checkouts. Only one run measures at a time (flock on $AB_LOCK);
# worktree setup and builds happen outside the lock.
#
# Writes <out>/summary.md, a markdown table ready to paste into a PR description.

set -euo pipefail

# Everything runs inside main, which bash parses in full before executing. Editing
# or checking out a different version of this file mid-run cannot change a run in
# progress.
main() {
  local SCRIPT_PATH SCRIPT_DIR
  SCRIPT_PATH=$(realpath "${BASH_SOURCE[0]}")
  SCRIPT_DIR=$(dirname "$SCRIPT_PATH")

  usage() { sed -n '2,30p' "$SCRIPT_PATH" | sed 's/^# \{0,1\}//'; exit 2; }

  [[ $# -ge 3 ]] || usage
  local BASE_REF=$1 PR_REF=$2 FILTER=$3; shift 3

  local RUNTIMES="net10.0 net11.0" ROUNDS=3 JOB=Default OUT="" BACKGROUND=0
  KEEP=0  # global: read by the EXIT trap, which runs after main returns
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --runtimes) RUNTIMES=$2; shift 2 ;;
      --rounds)   ROUNDS=$2; shift 2 ;;
      --job)      JOB=$2; shift 2 ;;
      --out)      OUT=$2; shift 2 ;;
      --background) BACKGROUND=1; shift ;;
      --keep-worktrees) KEEP=1; shift ;;
      -h|--help)  usage ;;
      *) echo "Unknown option: $1" >&2; usage ;;
    esac
  done

  # Resolve symlinks (for example /home -> /local/home): mixed paths break MSBuild project references.
  # REPO is global: the EXIT trap reads it.
  local BENCH_REL=sdk/test/Performance/CoreBenchmarks WT_ROOT LOCK
  REPO=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)
  REPO=$(realpath "$REPO")
  WT_ROOT=$(realpath -m "${AB_WORKTREES:-$HOME/code/perf-ab-worktrees}")
  LOCK=${AB_LOCK:-/tmp/core-bench.lock}

  local NPROC MAX_LOAD LOAD_WAIT
  NPROC=$(nproc)
  MAX_LOAD=${AB_MAX_LOAD:-$(( NPROC / 8 > 2 ? NPROC / 8 : 2 ))}
  LOAD_WAIT=${AB_LOAD_WAIT:-1800}

  # Resolve refs once, at start. A branch that moves later does not affect this run.
  local BASE_SHA PR_SHA B12 P12
  BASE_SHA=$(git -C "$REPO" rev-parse --verify "$BASE_REF^{commit}")
  PR_SHA=$(git -C "$REPO" rev-parse --verify "$PR_REF^{commit}")
  B12=${BASE_SHA:0:12}; P12=${PR_SHA:0:12}
  [[ -n "$OUT" ]] || OUT="$PWD/ab-results/${B12}-${P12}-$(date -u +%Y%m%dT%H%M%SZ)"
  mkdir -p "$OUT"
  OUT=$(cd -P "$OUT" && pwd -P)

  if [[ $BACKGROUND == 1 ]]; then
    # setsid puts the run in its own session, so it survives the calling shell exiting.
    # A plain `nohup ... &` from an agent's shell tool is killed when the tool call returns.
    setsid nohup "$SCRIPT_PATH" "$BASE_SHA" "$PR_SHA" "$FILTER" \
      --runtimes "$RUNTIMES" --rounds "$ROUNDS" --job "$JOB" --out "$OUT" \
      $([[ $KEEP == 1 ]] && echo --keep-worktrees) \
      > "$OUT/ab.log" 2>&1 < /dev/null &
    local pid=$!
    echo "$pid" > "$OUT/ab.pid"
    echo "ab.sh running in background: pid $pid"
    echo "log:     $OUT/ab.log"
    echo "summary: $OUT/summary.md (written when the run finishes; ab.log ends with 'summary:' or an error)"
    return 0
  fi

  log() { echo "[ab] $(date -u +%H:%M:%S) $*" >&2; }

  # Copy the helper scripts into the output directory now. The summary step uses these
  # copies, so switching branches in the calling worktree mid-run cannot break it.
  cp "$SCRIPT_DIR/compare.py" "$OUT/compare.py"
  cp "$SCRIPT_PATH" "$OUT/ab.sh"

  # Per-run worktrees. Two runs with the same base SHA used to share one worktree, and the
  # second run would reset it and overwrite its benchmark source while the first was measuring.
  # REPO, RUN_WT, BASE_WT, PR_WT and KEEP are globals because the EXIT trap reads them.
  local RUN_ID
  RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$-${B12:0:8}-${P12:0:8}"
  RUN_WT="$WT_ROOT/run-$RUN_ID"
  BASE_WT="$RUN_WT/base"
  PR_WT="$RUN_WT/pr"
  mkdir -p "$RUN_WT"

  cleanup() {
    local status=$?
    if [[ $KEEP == 1 ]]; then
      log "keeping worktrees under $RUN_WT"
    else
      for wt in "$BASE_WT" "$PR_WT"; do
        [[ -d "$wt" ]] && git -C "$REPO" worktree remove --force "$wt" 2>/dev/null || true
      done
      rmdir "$RUN_WT" 2>/dev/null || true
      git -C "$REPO" worktree prune 2>/dev/null || true
    fi
    return $status
  }
  trap cleanup EXIT

  log "run $RUN_ID"
  log "base $BASE_SHA -> $BASE_WT"
  git -C "$REPO" worktree add --quiet --detach "$BASE_WT" "$BASE_SHA"
  log "pr   $PR_SHA -> $PR_WT"
  git -C "$REPO" worktree add --quiet --detach "$PR_WT" "$PR_SHA"

  [[ -f "$PR_WT/$BENCH_REL/CoreBenchmarks.csproj" ]] || { echo "No CoreBenchmarks project at $PR_REF" >&2; exit 1; }

  # Use the PR's benchmark source on both sides.
  log "copying benchmark source from PR to base worktree"
  rm -rf "${BASE_WT:?}/$BENCH_REL"
  mkdir -p "$BASE_WT/$BENCH_REL"
  ( cd "$PR_WT/$BENCH_REL" && tar --exclude=bin --exclude=obj --exclude=BenchmarkDotNet.Artifacts -cf - . ) \
    | ( cd "$BASE_WT/$BENCH_REL" && tar -xf - )

  local side wt
  for side in base pr; do
    wt=$BASE_WT; [[ $side == pr ]] && wt=$PR_WT
    log "building $side"
    dotnet build "$wt/$BENCH_REL/CoreBenchmarks.csproj" -c Release -nologo -v q > "$OUT/build-$side.log" 2>&1 \
      || { echo "Build failed for $side, see $OUT/build-$side.log" >&2; tail -20 "$OUT/build-$side.log" >&2; exit 1; }
  done

  # Average number of runnable tasks other than this sampler, over 10 samples in 5 seconds.
  # Unlike the 1-minute load average this does not lag, so it is not inflated by the
  # benchmark run that just finished, but it does see other builds and tests running now.
  busy_now() {
    local i total=0 n
    for ((i = 0; i < 10; i++)); do
      n=$(awk '/^procs_running/ { print $2 }' /proc/stat)
      total=$(( total + n - 1 ))
      sleep 0.5
    done
    awk -v t="$total" 'BEGIN { v = t / 10; printf "%.1f", (v < 0 ? 0 : v) }'
  }

  # Waits until the machine is quiet enough to measure, and records how busy it was at the start.
  wait_for_quiet() {
    local dest=$1 waited=0 busy
    busy=$(busy_now)
    while awk -v l="$busy" -v m="$MAX_LOAD" 'BEGIN { exit !(l > m) }'; do
      if (( waited >= LOAD_WAIT )); then
        log "$busy other runnable tasks, still above $MAX_LOAD after ${waited}s; measuring anyway (flagged in summary)"
        break
      fi
      (( waited == 0 )) && log "$busy other runnable tasks, above $MAX_LOAD; waiting (up to ${LOAD_WAIT}s)"
      sleep 10; waited=$(( waited + 15 ))
      busy=$(busy_now)
    done
    printf 'load1=%s max=%s waited=%s\n' "$busy" "$MAX_LOAD" "$waited" > "$dest/load.txt"
  }

  run_side() {
    local side=$1 round=$2 wt=$3 tfm art
    for tfm in $RUNTIMES; do
      art="$OUT/round$round/$side/$tfm"
      mkdir -p "$art"
      wait_for_quiet "$art"
      log "round $round/$ROUNDS: $side ($tfm), $(cat "$art/load.txt")"
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
  local r
  for ((r = 1; r <= ROUNDS; r++)); do
    run_side base "$r" "$BASE_WT"
    run_side pr "$r" "$PR_WT"
  done
  flock -u 9
  exec 9>&-

  python3 "$OUT/compare.py" "$OUT" --base "$BASE_SHA" --pr "$PR_SHA" --filter "$FILTER" > "$OUT/summary.md"
  log "summary: $OUT/summary.md"
  cat "$OUT/summary.md"
}

main "$@"
