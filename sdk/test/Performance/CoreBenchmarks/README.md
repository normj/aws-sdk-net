# CoreBenchmarks (fork-only)

BenchmarkDotNet microbenchmarks for `AWSSDK.Core`, used by the AI-driven performance
effort on `normj/aws-sdk-net`. This project, `ab.sh`, `compare.py` and
`.github/workflows/perf-ci.yml` live on the `perf/infra` branch and are dropped when a
PR is promoted to `aws/aws-sdk-net`.

## Layout

| File | Purpose |
|---|---|
| `CoreBenchmarks.csproj` | Targets `net10.0;net11.0`, references `AWSSDK.Core.NetStandard.csproj` by project. |
| `Benchmarks/*.cs` | One class per area (for example `SigningBenchmarks.cs`). Always `[MemoryDiagnoser]`. |
| `ab.sh` | Builds two commits in separate worktrees and runs interleaved A/B rounds. |
| `compare.py` | Turns the BDN JSON reports into the markdown table for the PR description. |

## Running an A/B comparison

```bash
sdk/test/Performance/CoreBenchmarks/ab.sh <base-ref> <pr-ref> '<bdn-filter>'
# example
sdk/test/Performance/CoreBenchmarks/ab.sh origin/perf/infra HEAD '*SigningBenchmarks*'
```

Defaults: runtimes `net10.0 net11.0`, 3 interleaved rounds (base, PR, base, PR, base, PR),
BDN `Default` job, MemoryDiagnoser. Options: `--rounds`, `--runtimes`, `--job Short` or
`--job Dry` (only for quick local iteration, never for PR numbers), `--out`,
`--background`, `--keep-worktrees`.

### Running in the background

A full run takes 15 to 30 minutes. From an agent's shell tool, start it with `--background`:

```bash
sdk/test/Performance/CoreBenchmarks/ab.sh origin/perf/infra bench/core-signing-foo '*SigningBenchmarks*' \
  --out ~/code/perf-ab-results/signing/foo --background
# poll until the log ends with "summary:" (success) or an error
tail -3 ~/code/perf-ab-results/signing/foo/ab.log
```

`--background` runs the script under `setsid nohup`, writes `ab.log` and `ab.pid` to the
output directory and returns immediately. Do not use a plain `nohup ... &`: the run is killed
when the shell tool call returns.

### What the script does

1. Resolves both refs to SHAs at start, so a branch that moves later does not affect the run.
2. Copies `ab.sh` and `compare.py` into the output directory and uses those copies. Switching
   branches or editing the scripts in the calling worktree mid-run cannot break a run.
3. Creates a fresh pair of detached worktrees for this run under
   `~/code/perf-ab-worktrees/run-<id>/` and removes them when the run exits (pass
   `--keep-worktrees` to keep them). Concurrent runs, including runs with the same base SHA,
   never share a checkout. Each run needs about 3.5 GB of disk while it is running.
4. Copies the PR's `CoreBenchmarks` source into the base worktree, so both sides run the
   same benchmark code and only `AWSSDK.Core` differs. Benchmarks must therefore only call
   APIs that exist on the base commit.
5. Builds only `CoreBenchmarks.csproj` and its references on each side (never the full repo).
6. Takes an exclusive lock (`/tmp/core-bench.lock`) for the measurement phase, so only one
   benchmark runs on the machine at a time. Worktree setup and builds happen outside the lock.
7. Before each measurement, waits until the machine is quiet: the number of other runnable
   tasks, averaged over 5 seconds, must be at or below `AB_MAX_LOAD` (default `nproc / 8`,
   minimum 2; 2 on the 16-core dev host). It waits up to `AB_LOAD_WAIT` seconds (default
   1800), then measures anyway and flags that measurement in the summary.
8. Writes `summary.md` to the output directory. Paste it into the PR description.

### Reading the summary

- **Time** is `faster` or `slower` only when the median changes by at least 5% and every PR
  round beats (or loses to) every base round. Anything else is `same`.
- **Alloc** is `less` or `more` under the same rule with a 1% threshold. Allocation is close
  to deterministic, so a small consistent change is real.
- **Overall** is `win` when at least one row is `faster` or `less` and none is `slower` or
  `more`. That is the bar for opening a perf PR. It is `regression` when any row is `slower`
  or `more`, and `no measurable change` otherwise.
- A warning block appears if any measurement started while the machine was busy. Treat time
  numbers from such a run as noisy and re-run it.

### Keeping the machine quiet

Other heavy work on the host (unit test runs, reviewer builds, `git gc`) skews timings. Agents
running heavy commands while benchmarks may be running should wait for the benchmark lock:

```bash
flock /tmp/core-bench.lock dotnet test ...
```

This only waits between whole A/B runs, not between rounds. The quiet-machine check in step 7
covers work that does not take the lock.

Always use real paths (`/local/home/...`), not the `/home` symlink, when running `dotnet`
commands by hand on the dev host. Mixed paths break MSBuild project references.

## Rules for perf PRs

- No public or protected API changes, including in `*.Internal` namespaces. Service
  packages compiled against older Core versions call those members.
- No behavior changes, including retry timing and credential refresh.
- Pool or cache only when nothing pooled or cached is reachable by callers.
- No new `unsafe` code. Prefer BCL primitives (`SearchValues`, `IndexOfAny`, `Ascii`,
  span-based APIs). .NET 10+ fast paths go behind `#if NET10_0_OR_GREATER`;
  `netstandard2.0` and `net8.0` must still compile and pass tests.
- Drop candidates with less than about 5% time improvement and no allocation reduction.
- After review-driven changes, measure against both the base and the previous PR head.
- Each PR includes a DevConfig file with a Core patch bump, so it can go upstream unchanged.
- PR descriptions and comments end with the AI-agent signature line.

## PR description template

````markdown
## Summary
<one paragraph: what hot path, what was allocated or slow, what changed>

## Benchmark
<details><summary>Benchmark source</summary>

```csharp
<contents of the benchmark class used>
```
</details>

<paste summary.md from ab.sh here>

## Testing
- `AWSSDK.UnitTests.Core` on net8.0 / net10.0 / net11.0 (fork CI)
- <any new unit tests>

🤖 Posted by AWS SDK .NET Perf (AI Agent)
````
