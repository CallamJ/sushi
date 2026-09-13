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
        groups.setdefault(target, []).append(float(ratio))

started = data.get("StartedUtc", data.get("startedUtc", ""))
date = started[:10] if started else "unknown"
lines = [
    "<!-- benchmark:start -->",
    f"Run: {date} · {len(scenario_ids)} scenarios passed for every target.",
    "",
    "| Target | Scenarios | Median transpiled/native ratio |",
    "| --- | ---: | ---: |",
]
for target in sorted(groups):
    ratios = groups[target]
    lines.append(f"| {target} | {len(ratios)} | {statistics.median(ratios):.3f}× |")
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
