#!/usr/bin/env python3
"""Update the generated benchmark block in README.md from results.json."""
import json
import pathlib
import statistics
import sys
from datetime import datetime

if len(sys.argv) != 2:
    raise SystemExit("usage: update-benchmark-readme.py RESULTS_JSON")

results_path = pathlib.Path(sys.argv[1])
data = json.loads(results_path.read_text())
records = data.get("Rows", data.get("rows", []))
if not records or any(str(item.get("Status", item.get("status", ""))).lower() == "failed" for item in records):
    raise SystemExit("benchmark results must be non-empty and successful")

groups = {}
scenario_ids = set()
for item in records:
    scenario_ids.add(item.get("ScenarioId", item.get("scenarioId", "")))
    target = item.get("Target", item.get("target", "")).lower()
    ratio = item.get("RuntimeRatioMedian", item.get("runtimeRatioMedian"))
    if ratio is not None:
        groups.setdefault(target, []).append((float(ratio), item.get("ScenarioId", item.get("scenarioId", ""))))

started = data.get("StartedUtc", data.get("startedUtc", ""))
date = started[:10] if started else "unknown"

def percentile(values, probability):
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * probability
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    fraction = position - lower
    return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction

lines = [
    "<!-- benchmark:start -->",
    f"Run: {date} · {len(scenario_ids)} scenarios passed for every target.",
    "",
    "| Target | Scenarios | Median ratio | P95 ratio |",
    "| --- | ---: | ---: | ---: |",
]
for target in sorted(groups):
    samples = groups[target]
    ratios = [ratio for ratio, _ in samples]
    lines.append(f"| {target} | {len(ratios)} | {statistics.median(ratios):.3f}× | {percentile(ratios, 0.95):.3f}× |")
lines.append("<!-- benchmark:end -->")
block = "\n".join(lines)

readme = pathlib.Path("README.md")
text = readme.read_text()
start = "<!-- benchmark:start -->"
end = "<!-- benchmark:end -->"
if start not in text or end not in text:
    raise SystemExit("README benchmark markers are missing")
before = text[:text.index(start)]
after = text[text.index(end) + len(end):]
readme.write_text(before + block + after)
