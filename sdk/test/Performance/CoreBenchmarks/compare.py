#!/usr/bin/env python3
"""Combine BenchmarkDotNet JSON reports written by ab.sh into a markdown summary.

Layout read: <out>/round<N>/<base|pr>/results/*-report-full-compressed.json
"""
import argparse
import glob
import json
import os
import re
import statistics
import sys

RUNTIME_RE = re.compile(r"Runtime=([^,)]+)")


def fmt_time(ns):
    if ns is None:
        return "n/a"
    for unit, scale in (("s", 1e9), ("ms", 1e6), ("us", 1e3)):
        if ns >= scale:
            return f"{ns / scale:,.2f} {unit}"
    return f"{ns:,.2f} ns"


def fmt_bytes(b):
    if b is None:
        return "n/a"
    if b >= 1024 * 1024:
        return f"{b / (1024 * 1024):,.2f} MB"
    if b >= 1024:
        return f"{b / 1024:,.2f} KB"
    return f"{b:,} B"


def ratio(new, old):
    if new is None or old is None:
        return "n/a"
    if old == 0:
        return "1.00" if new == 0 else "new"
    return f"{new / old:.2f}"


def load(out_dir):
    """Return {side: {key: [(mean_ns, alloc_bytes), ...per round]}}, env_header."""
    data = {"base": {}, "pr": {}}
    env_headers = {}
    rounds = sorted(
        (d for d in glob.glob(os.path.join(out_dir, "round*")) if os.path.isdir(d)),
        key=lambda p: int(re.sub(r"\D", "", os.path.basename(p)) or 0),
    )
    for rdir in rounds:
        for side in ("base", "pr"):
            for path in glob.glob(os.path.join(rdir, side, "**", "*-report-full-compressed.json"), recursive=True):
                with open(path) as f:
                    report = json.load(f)
                for b in report.get("Benchmarks", []):
                    stats = b.get("Statistics") or {}
                    if not stats:
                        continue  # failed benchmark case
                    # ab.sh writes each runtime to <side>/<tfm>/...; fall back to DisplayInfo.
                    rel = os.path.relpath(path, os.path.join(rdir, side)).split(os.sep)
                    if len(rel) > 1 and rel[0].startswith("net"):
                        runtime = rel[0]
                    else:
                        m = RUNTIME_RE.search(b.get("DisplayInfo", ""))
                        runtime = m.group(1).strip() if m else "?"
                    params = b.get("Parameters") or ""
                    name = f"{b['Type']}.{b['Method']}" + (f"({params})" if params else "")
                    key = (name, runtime)
                    alloc = (b.get("Memory") or {}).get("BytesAllocatedPerOperation")
                    data[side].setdefault(key, []).append((stats["Mean"], alloc))
                if runtime not in env_headers and side == "base":
                    md = path.replace("-report-full-compressed.json", "-report-github.md")
                    if os.path.exists(md):
                        with open(md) as f:
                            text = f.read()
                        mm = re.search(r"```(.*?)```", text, re.S)
                        if mm:
                            env_headers[runtime] = mm.group(1).strip()
    return data, env_headers, len(rounds)


def verdict(base_means, pr_means):
    """Consistent win/loss requires every PR round to beat (or lose to) every base round."""
    if not base_means or not pr_means:
        return "incomplete"
    b_med, p_med = statistics.median(base_means), statistics.median(pr_means)
    change = (p_med - b_med) / b_med if b_med else 0.0
    if max(pr_means) < min(base_means) and change <= -0.05:
        return "faster"
    if min(pr_means) > max(base_means) and change >= 0.05:
        return "**slower**"
    return "same"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out_dir")
    ap.add_argument("--base", required=True)
    ap.add_argument("--pr", required=True)
    ap.add_argument("--filter", default="")
    args = ap.parse_args()

    data, env_headers, n_rounds = load(args.out_dir)
    keys = sorted(set(data["base"]) | set(data["pr"]), key=lambda k: (k[0], k[1]))
    if not keys:
        print("No benchmark results found.", file=sys.stderr)
        sys.exit(1)

    lines = []
    lines.append("### BenchmarkDotNet A/B results")
    lines.append("")
    lines.append(f"- Base: `{args.base}`")
    lines.append(f"- PR: `{args.pr}`")
    lines.append(f"- Filter: `{args.filter}`")
    lines.append(f"- {n_rounds} interleaved rounds (base, PR, base, PR, ...), MemoryDiagnoser. "
                 "Values are the median of the per-round means.")
    lines.append("- Verdict is `faster` or `slower` only when the change is at least 5% "
                 "and every PR round beats (or loses to) every base round.")
    lines.append("")
    lines.append("| Benchmark | Runtime | Base mean | PR mean | Time ratio | Base alloc | PR alloc | Alloc ratio | Verdict |")
    lines.append("|---|---|---:|---:|---:|---:|---:|---:|---|")
    for key in keys:
        b = data["base"].get(key, [])
        p = data["pr"].get(key, [])
        bm = [x[0] for x in b]
        pm = [x[0] for x in p]
        ba = [x[1] for x in b if x[1] is not None]
        pa = [x[1] for x in p if x[1] is not None]
        b_mean = statistics.median(bm) if bm else None
        p_mean = statistics.median(pm) if pm else None
        b_alloc = int(statistics.median(ba)) if ba else None
        p_alloc = int(statistics.median(pa)) if pa else None
        lines.append(
            f"| {key[0]} | {key[1]} | {fmt_time(b_mean)} | {fmt_time(p_mean)} | {ratio(p_mean, b_mean)} "
            f"| {fmt_bytes(b_alloc)} | {fmt_bytes(p_alloc)} | {ratio(p_alloc, b_alloc)} | {verdict(bm, pm)} |"
        )

    lines.append("")
    lines.append("<details><summary>Per-round means</summary>")
    lines.append("")
    lines.append("| Benchmark | Runtime | Side | " + " | ".join(f"Round {i}" for i in range(1, n_rounds + 1)) + " |")
    lines.append("|---|---|---|" + "---:|" * n_rounds)
    for key in keys:
        for side in ("base", "pr"):
            vals = data[side].get(key, [])
            cells = [f"{fmt_time(v[0])} / {fmt_bytes(v[1])}" for v in vals]
            cells += [""] * (n_rounds - len(cells))
            lines.append(f"| {key[0]} | {key[1]} | {side} | " + " | ".join(cells) + " |")
    lines.append("")
    lines.append("</details>")

    if env_headers:
        lines.append("")
        lines.append("<details><summary>Environment</summary>")
        for rt in sorted(env_headers):
            lines.append("")
            lines.append("```")
            lines.append(env_headers[rt])
            lines.append("```")
        lines.append("")
        lines.append("</details>")

    print("\n".join(lines))


if __name__ == "__main__":
    main()
