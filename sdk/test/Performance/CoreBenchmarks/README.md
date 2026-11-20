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
BDN `Default` job, MemoryDiagnoser. Options: `--rounds`, `--runtimes`, `--job Short` (only
for quick local iteration, never for PR numbers), `--out`.

What the script does:

1. Creates detached worktrees under `~/code/perf-ab-worktrees/` for both commits.
2. Copies the PR's `CoreBenchmarks` source into the base worktree, so both sides run the
   same benchmark code and only `AWSSDK.Core` differs. Benchmarks must therefore only call
   APIs that exist on the base commit.
3. Builds only `CoreBenchmarks.csproj` and its references on each side (never the full repo).
4. Takes an exclusive lock (`/tmp/core-bench.lock`) for the measurement phase, so only one
   benchmark runs on the machine at a time. Builds happen outside the lock.
5. Writes `summary.md` to the output directory. Paste it into the PR description.

A row is marked `faster` or `slower` only when the median change is at least 5% and every
PR round beats (or loses to) every base round. Anything else is `same`.

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
