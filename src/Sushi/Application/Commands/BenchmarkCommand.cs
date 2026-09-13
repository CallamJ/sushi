namespace Sushi.Application.Commands;

using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class BenchmarkCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static Command Create()
    {
        Option<string> manifestOption = new("--manifest")
        {
            Description = "Path to benchmark manifest JSON.",
            DefaultValueFactory = _ => "benchmarks/manifest.json"
        };

        Option<string> targetsOption = new("--targets")
        {
            Description = "Comma-separated targets: bash,zsh,powershell.",
            DefaultValueFactory = _ => "bash,zsh,powershell"
        };

        Option<int> iterationsOption = new("--iterations")
        {
            Description = "Measured iterations per scenario/target.",
            DefaultValueFactory = _ => 15
        };

        Option<int> warmupOption = new("--warmup")
        {
            Description = "Warmup runs per scenario/target.",
            DefaultValueFactory = _ => 2
        };

        Option<int> timeoutSecondsOption = new("--timeout-seconds")
        {
            Description = "Maximum runtime for each native or transpiled process.",
            DefaultValueFactory = _ => 30
        };

        Option<string> outputDirOption = new("--output-dir")
        {
            Description = "Directory for benchmark artifacts.",
            DefaultValueFactory = _ =>
            {
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                return Path.Combine("tmp", "benchmarks", stamp);
            }
        };

        Option<bool> includeHttpOption = new("--include-http")
        {
            Description = "Allow HTTP checks in benchmark scripts.",
            DefaultValueFactory = _ => false
        };

        Option<string?> baselineOption = new("--baseline")
        {
            Description = "Optional baseline results.json path or URL for comparison."
        };

        Option<bool> strictOutputOption = new("--strict-output")
        {
            Description = "Require native and transpiled outputs/exit-codes to match.",
            DefaultValueFactory = _ => true
        };

        Option<string?> filterOption = new("--filter")
        {
            Description = "Optional case-insensitive substring filter on scenario id/name/tags."
        };

        Option<bool> allowSkippedOption = new("--allow-skipped")
        {
            Description = "Allow requested targets or scenarios to be skipped without failing the run.",
            DefaultValueFactory = _ => false
        };

        var command = new Command("benchmark", "Benchmark native scripts vs transpiled Sushi scripts.")
        {
            manifestOption,
            targetsOption,
            iterationsOption,
            warmupOption,
            timeoutSecondsOption,
            outputDirOption,
            includeHttpOption,
            baselineOption,
            strictOutputOption,
            filterOption,
            allowSkippedOption
        };

        command.SetAction(parseResult =>
        {
            var manifestPath = parseResult.GetValue(manifestOption) ?? "benchmarks/manifest.json";
            var targetCsv = parseResult.GetValue(targetsOption) ?? "bash,zsh,powershell";
            var iterations = parseResult.GetValue(iterationsOption);
            var warmup = parseResult.GetValue(warmupOption);
            var timeoutSeconds = parseResult.GetValue(timeoutSecondsOption);
            var outputDir = parseResult.GetValue(outputDirOption) ?? Path.Combine("tmp", "benchmarks");
            var includeHttp = parseResult.GetValue(includeHttpOption);
            var baseline = parseResult.GetValue(baselineOption);
            var strictOutput = parseResult.GetValue(strictOutputOption);
            var filter = parseResult.GetValue(filterOption);
            var allowSkipped = parseResult.GetValue(allowSkippedOption);

            if (iterations <= 0)
            {
                System.Console.Error.WriteLine("Iterations must be >= 1.");
                return 2;
            }

            if (warmup < 0)
            {
                System.Console.Error.WriteLine("Warmup must be >= 0.");
                return 2;
            }

            if (timeoutSeconds is < 1 or > 3600)
            {
                System.Console.Error.WriteLine("Timeout seconds must be between 1 and 3600.");
                return 2;
            }

            if (!File.Exists(manifestPath))
            {
                System.Console.Error.WriteLine($"Manifest not found: {manifestPath}");
                return 2;
            }

            IReadOnlyList<TargetLanguage> targets;
            try
            {
                targets = ParseTargets(targetCsv);
            }
            catch (ArgumentException ex)
            {
                System.Console.Error.WriteLine(ex.Message);
                return 2;
            }

            BenchmarkManifest manifest;
            try
            {
                var manifestText = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<BenchmarkManifest>(manifestText, JsonOptions)
                    ?? throw new InvalidDataException("Manifest payload is empty.");
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"Failed to load benchmark manifest: {ex.Message}");
                return 1;
            }

            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
            var run = Execute(manifest, manifestDirectory, targets, iterations, warmup, timeoutSeconds, outputDir, includeHttp, strictOutput, filter, baseline, allowSkipped);
            return run.ExitCode;
        });

        return command;
    }

    private static BenchmarkRun Execute(
        BenchmarkManifest manifest,
        string manifestDirectory,
        IReadOnlyList<TargetLanguage> targets,
        int iterations,
        int warmup,
        int timeoutSeconds,
        string outputDir,
        bool includeHttp,
        bool strictOutput,
        string? filter,
        string? baselinePathOrUrl,
        bool allowSkipped)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var rows = new List<BenchmarkResultRow>();
        var tsvRows = new List<string>
        {
            "scenario_id\ttarget\tmode\titeration\telapsed_ms\texit_code\tstdout_sha256\tstderr_sha256\tstatus\tnote"
        };

        var selected = manifest.Scenarios
            .Where(s => MatchesFilter(s, filter))
            .ToList();

        if (selected.Count == 0)
        {
            System.Console.Error.WriteLine("No benchmark scenarios selected after filtering.");
            return new BenchmarkRun(2, null);
        }

        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Failed to create benchmark output directory: {ex.Message}");
            return new BenchmarkRun(2, null);
        }

        var totalWorkItems = checked(selected.Count * targets.Count);
        var completedWorkItems = 0;
        var progressPath = Path.Combine(outputDir, "progress.txt");
        WriteProgress(progressPath, startedUtc, totalWorkItems, completedWorkItems, "starting");
        System.Console.WriteLine(
            $"Benchmark starting: {selected.Count} scenario(s) × {targets.Count} target(s) " +
            $"({warmup} warmup, {iterations} measured iteration(s) each).");
        System.Console.WriteLine($"Live artifacts: {outputDir}");

        var suiteFingerprint = ComputeSuiteFingerprint(manifest, manifestDirectory);
        BenchmarkResultFile? baselineFile;
        try
        {
            baselineFile = LoadBaseline(baselinePathOrUrl);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Failed to load benchmark baseline: {ex.Message}");
            return new BenchmarkRun(2, null);
        }

        var baselineRows = new List<BenchmarkResultRow>();
        if (baselineFile != null)
        {
            if (baselineFile.Version == 2 && baselineFile.SuiteFingerprint == suiteFingerprint)
            {
                baselineRows = baselineFile.Rows;
            }
            else
            {
                System.Console.Error.WriteLine("Benchmark baseline is incompatible with this suite; regression comparison was reset.");
            }
        }

        WriteLiveArtifacts(outputDir, startedUtc, manifest, suiteFingerprint, rows, tsvRows);

        foreach (var scenario in selected)
        {
            foreach (var target in targets)
            {
                var targetKey = ToTargetKey(target);
                var workItem = ++completedWorkItems;
                WriteProgress(progressPath, startedUtc, totalWorkItems, completedWorkItems, $"running {scenario.Id} ({targetKey})");
                System.Console.WriteLine($"[{workItem}/{totalWorkItems}] {scenario.Id} ({targetKey})");
                var nativePath = scenario.Native.GetValueOrDefault(targetKey);
                if (string.IsNullOrWhiteSpace(nativePath))
                {
                    rows.Add(BenchmarkResultRow.Skipped(scenario.Id, targetKey, "native path missing in manifest"));
                    continue;
                }

                var absSource = ResolvePath(manifestDirectory, scenario.Source);
                var absNative = ResolvePath(manifestDirectory, nativePath);
                if (!File.Exists(absSource))
                {
                    rows.Add(BenchmarkResultRow.Failed(scenario.Id, targetKey, $"source missing: {scenario.Source}"));
                    continue;
                }
                if (!File.Exists(absNative))
                {
                    rows.Add(BenchmarkResultRow.Skipped(scenario.Id, targetKey, $"native missing: {nativePath}"));
                    continue;
                }

                if (!TryResolveRunner(target, out var runner))
                {
                    rows.Add(BenchmarkResultRow.Skipped(scenario.Id, targetKey, $"runtime unavailable for target {targetKey}"));
                    continue;
                }

                var sourceText = File.ReadAllText(absSource);
                var transpileWatch = Stopwatch.StartNew();
                var transpileResult = CommandSupport.Transpile(absSource, target, sourceText);
                transpileWatch.Stop();

                if (!transpileResult.Success || transpileResult.EmittedCode == null)
                {
                    rows.Add(BenchmarkResultRow.Failed(scenario.Id, targetKey, "transpile failed"));
                    continue;
                }

                var tempScriptPath = Path.Combine(Path.GetTempPath(), $"sushi-bench-{Guid.NewGuid():N}{GetExtension(target)}");
                if (!CommandSupport.TryWriteOutput(tempScriptPath, transpileResult.EmittedCode))
                {
                    rows.Add(BenchmarkResultRow.Failed(scenario.Id, targetKey, "failed to write transpiled script"));
                    continue;
                }

                try
                {
                    var mergedEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in scenario.Env)
                    {
                        mergedEnv[entry.Key] = entry.Value;
                    }

                    mergedEnv["SUSHI_SKIP_HTTP"] = includeHttp ? "0" : "1";

                    for (var i = 0; i < warmup; i++)
                    {
                        _ = RunScript(absNative, runner, scenario.Args, mergedEnv, timeoutSeconds);
                        _ = RunScript(tempScriptPath, runner, scenario.Args, mergedEnv, timeoutSeconds);
                    }

                    var nativeTimings = new List<double>(iterations);
                    var transpiledTimings = new List<double>(iterations);
                    var strictFailures = new List<string>();

                    for (var i = 1; i <= iterations; i++)
                    {
                        var transpileFirst = (i % 2) == 1;
                        CapturedProcessResult? nativeRun = null;
                        CapturedProcessResult? transpiledRun = null;

                        if (transpileFirst)
                        {
                            transpiledRun = RunScript(tempScriptPath, runner, scenario.Args, mergedEnv, timeoutSeconds);
                            nativeRun = RunScript(absNative, runner, scenario.Args, mergedEnv, timeoutSeconds);
                        }
                        else
                        {
                            nativeRun = RunScript(absNative, runner, scenario.Args, mergedEnv, timeoutSeconds);
                            transpiledRun = RunScript(tempScriptPath, runner, scenario.Args, mergedEnv, timeoutSeconds);
                        }

                        nativeTimings.Add(nativeRun.ElapsedMs);
                        transpiledTimings.Add(transpiledRun.ElapsedMs);

                        var iterationFailures = new List<string>();
                        if (strictOutput)
                        {
                            var compare = CompareOutputs(nativeRun, transpiledRun);
                            if (compare != null)
                            {
                                iterationFailures.Add(compare);
                            }
                        }

                        AddExpectedFailures(scenario.Expected, nativeRun, "native", iterationFailures);
                        AddExpectedFailures(scenario.Expected, transpiledRun, "transpiled", iterationFailures);
                        var iterationStatus = iterationFailures.Count == 0 ? "ok" : "failed";
                        var iterationNote = string.Join("; ", iterationFailures);
                        tsvRows.Add(ToTsvRow(scenario.Id, targetKey, "native", i, nativeRun, iterationStatus, iterationNote));
                        tsvRows.Add(ToTsvRow(scenario.Id, targetKey, "transpiled", i, transpiledRun, iterationStatus, iterationNote));
                        strictFailures.AddRange(iterationFailures.Select(failure => $"iter {i}: {failure}"));
                    }

                    var nativeStats = BenchmarkStats.From(nativeTimings);
                    var transpiledStats = BenchmarkStats.From(transpiledTimings);
                    var ratio = nativeStats.MedianMs <= 0 ? 0 : transpiledStats.MedianMs / nativeStats.MedianMs;
                    var baselineDelta = GetBaselineDelta(baselineRows, scenario.Id, targetKey, ratio);
                    var ratioBudget = scenario.MaxRuntimeRatioMedian.GetValueOrDefault(targetKey);
                    if (ratioBudget > 0 && ratio > ratioBudget)
                    {
                        strictFailures.Add($"median ratio {ratio:0.000} exceeds budget {ratioBudget:0.000}");
                    }
                    var note = strictFailures.Count == 0 ? "" : string.Join("; ", strictFailures.Take(3));
                    if (strictFailures.Count > 3)
                    {
                        note = $"{note}; ... ({strictFailures.Count - 3} more)";
                    }

                    rows.Add(new BenchmarkResultRow
                    {
                        ScenarioId = scenario.Id,
                        ScenarioName = scenario.Name,
                        Target = targetKey,
                        Status = strictFailures.Count == 0 ? "ok" : "failed",
                        Note = note,
                        TranspileMs = transpileWatch.Elapsed.TotalMilliseconds,
                        GeneratedScriptBytes = Encoding.UTF8.GetByteCount(transpileResult.EmittedCode),
                        Native = nativeStats,
                        Transpiled = transpiledStats,
                        RuntimeRatioMedian = ratio,
                        MaxRuntimeRatioMedian = ratioBudget > 0 ? ratioBudget : null,
                        BaselineRatioMedian = baselineDelta?.Baseline,
                        BaselineDeltaPercent = baselineDelta?.DeltaPercent,
                        Tags = scenario.Tags
                    });
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempScriptPath))
                        {
                            File.Delete(tempScriptPath);
                        }
                    }
                    catch
                    {
                        // best effort cleanup
                    }
                }

                WriteLiveArtifacts(outputDir, startedUtc, manifest, suiteFingerprint, rows, tsvRows);
            }
        }

        var endedUtc = DateTimeOffset.UtcNow;
        WriteProgress(progressPath, startedUtc, totalWorkItems, completedWorkItems, "writing result artifacts");
        var result = WriteLiveArtifacts(outputDir, startedUtc, manifest, suiteFingerprint, rows, tsvRows, endedUtc);
        WriteProgress(progressPath, startedUtc, totalWorkItems, completedWorkItems, "complete");

        var failed = rows.Count(r => r.Status == "failed");
        var skipped = rows.Count(r => r.Status == "skipped");
        var regressions = rows.Count(r => r.BaselineDeltaPercent is > 20.0);
        System.Console.WriteLine($"Benchmark completed. Scenarios: {rows.Count}, failed: {failed}, skipped: {skipped}, regressions: {regressions}");
        System.Console.WriteLine($"Artifacts: {outputDir}");

        return new BenchmarkRun(failed > 0 || regressions > 0 || (!allowSkipped && skipped > 0) ? 1 : 0, outputDir);
    }

    private static void WriteProgress(
        string progressPath,
        DateTimeOffset startedUtc,
        int totalWorkItems,
        int completedWorkItems,
        string state)
    {
        File.WriteAllText(
            progressPath,
            $"status: {state}{Environment.NewLine}" +
            $"started_utc: {startedUtc:O}{Environment.NewLine}" +
            $"completed: {completedWorkItems}/{totalWorkItems}{Environment.NewLine}");
    }

    private static BenchmarkResultFile WriteLiveArtifacts(
        string outputDir,
        DateTimeOffset startedUtc,
        BenchmarkManifest manifest,
        string suiteFingerprint,
        List<BenchmarkResultRow> rows,
        List<string> tsvRows,
        DateTimeOffset? endedUtc = null)
    {
        var result = new BenchmarkResultFile
        {
            Version = 2,
            SuiteFingerprint = suiteFingerprint,
            StartedUtc = startedUtc,
            EndedUtc = endedUtc ?? DateTimeOffset.UtcNow,
            Host = BenchmarkHostInfo.Create(),
            Manifest = manifest.Metadata,
            Rows = rows
        };

        File.WriteAllText(Path.Combine(outputDir, "results.json"), JsonSerializer.Serialize(result, JsonOptions));
        File.WriteAllLines(Path.Combine(outputDir, "results.tsv"), tsvRows);
        File.WriteAllText(Path.Combine(outputDir, "summary.md"), RenderSummaryMarkdown(result));
        return result;
    }

    private static BenchmarkResultFile? LoadBaseline(string? baselinePathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(baselinePathOrUrl))
        {
            return null;
        }

        string payload;
        if (Uri.TryCreate(baselinePathOrUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var http = new HttpClient();
            payload = http.GetStringAsync(uri).GetAwaiter().GetResult();
        }
        else
        {
            if (!File.Exists(baselinePathOrUrl))
            {
                throw new FileNotFoundException("Baseline file was not found.", baselinePathOrUrl);
            }
            payload = File.ReadAllText(baselinePathOrUrl);
        }

        return JsonSerializer.Deserialize<BenchmarkResultFile>(payload, JsonOptions)
            ?? throw new InvalidDataException("Baseline payload is empty.");
    }

    private static string ComputeSuiteFingerprint(BenchmarkManifest manifest, string manifestDirectory)
    {
        var content = new StringBuilder(JsonSerializer.Serialize(manifest, JsonOptions));
        foreach (var scenario in manifest.Scenarios.OrderBy(scenario => scenario.Id, StringComparer.Ordinal))
        {
            var paths = new[] { scenario.Source }.Concat(scenario.Native.OrderBy(pair => pair.Key).Select(pair => pair.Value));
            foreach (var path in paths)
            {
                var absolutePath = ResolvePath(manifestDirectory, path);
                content.Append('\n').Append(path).Append('\n');
                if (File.Exists(absolutePath))
                {
                    content.Append(File.ReadAllText(absolutePath));
                }
            }
        }
        return HashText(content.ToString());
    }

    private static (double Baseline, double DeltaPercent)? GetBaselineDelta(
        IReadOnlyList<BenchmarkResultRow> baselineRows,
        string scenarioId,
        string target,
        double currentRatio)
    {
        var baseline = baselineRows.FirstOrDefault(row =>
            row.ScenarioId.Equals(scenarioId, StringComparison.OrdinalIgnoreCase) &&
            row.Target.Equals(target, StringComparison.OrdinalIgnoreCase));

        if (baseline == null || baseline.RuntimeRatioMedian <= 0)
        {
            return null;
        }

        var deltaPercent = ((currentRatio - baseline.RuntimeRatioMedian) / baseline.RuntimeRatioMedian) * 100.0;
        return (baseline.RuntimeRatioMedian, deltaPercent);
    }

    private static string RenderSummaryMarkdown(BenchmarkResultFile result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sushi Benchmark Summary");
        sb.AppendLine();
        sb.AppendLine($"- Started (UTC): {result.StartedUtc:O}");
        sb.AppendLine($"- Ended (UTC): {result.EndedUtc:O}");
        sb.AppendLine($"- OS: {result.Host.OsDescription}");
        sb.AppendLine($"- Framework: {result.Host.FrameworkDescription}");
        sb.AppendLine($"- Processor count: {result.Host.ProcessorCount}");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Target | Status | Transpile ms | Native median ms | Transpiled median ms | Ratio (t/n) | Budget | Baseline delta % |");
        sb.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var row in result.Rows.OrderBy(r => r.ScenarioId).ThenBy(r => r.Target))
        {
            var baselineDelta = row.BaselineDeltaPercent.HasValue
                ? row.BaselineDeltaPercent.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : "";
            var budget = row.MaxRuntimeRatioMedian?.ToString("0.000", CultureInfo.InvariantCulture) ?? "";
            sb.AppendLine($"| {row.ScenarioId} | {row.Target} | {row.Status} | {row.TranspileMs.ToString("0.00", CultureInfo.InvariantCulture)} | {row.Native.MedianMs.ToString("0.00", CultureInfo.InvariantCulture)} | {row.Transpiled.MedianMs.ToString("0.00", CultureInfo.InvariantCulture)} | {row.RuntimeRatioMedian.ToString("0.000", CultureInfo.InvariantCulture)} | {budget} | {baselineDelta} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Overhead by target");
        sb.AppendLine();
        sb.AppendLine("The median ratio is the typical result; P95 shows the high-end result across scenarios.");
        sb.AppendLine();
        sb.AppendLine("| Target | Median ratio | P95 ratio |");
        sb.AppendLine("| --- | ---: | ---: |");
        foreach (var group in result.Rows.Where(r => r.Status == "ok").GroupBy(r => r.Target).OrderBy(g => g.Key))
        {
            var median = group.Select(r => r.RuntimeRatioMedian).OrderBy(r => r).ToArray();
            var medianRatio = median.Length % 2 == 1
                ? median[median.Length / 2]
                : (median[(median.Length / 2) - 1] + median[median.Length / 2]) / 2.0;
            var p95Position = (median.Length - 1) * 0.95;
            var p95Lower = (int)Math.Floor(p95Position);
            var p95Upper = Math.Min(p95Lower + 1, median.Length - 1);
            var p95Fraction = p95Position - p95Lower;
            var p95Ratio = median[p95Lower] + ((median[p95Upper] - median[p95Lower]) * p95Fraction);
            sb.AppendLine($"| {group.Key} | {medianRatio.ToString("0.000", CultureInfo.InvariantCulture)}× | {p95Ratio.ToString("0.000", CultureInfo.InvariantCulture)}× |");
        }

        sb.AppendLine();
        sb.AppendLine("## Warnings");
        var warnings = result.Rows.Where(r => r.Status != "ok" || (r.BaselineDeltaPercent.HasValue && r.BaselineDeltaPercent.Value > 20.0)).ToList();
        if (warnings.Count == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var warning in warnings)
            {
                var reason = warning.Status != "ok" ? warning.Note : $"ratio delta +{warning.BaselineDeltaPercent:0.00}%";
                sb.AppendLine($"- {warning.ScenarioId} ({warning.Target}): {reason}");
            }
        }

        return sb.ToString();
    }

    private static string ToTsvRow(string scenarioId, string target, string mode, int iteration, CapturedProcessResult run, string status, string note)
    {
        return string.Join('\t', new[]
        {
            scenarioId,
            target,
            mode,
            iteration.ToString(CultureInfo.InvariantCulture),
            run.ElapsedMs.ToString("0.000", CultureInfo.InvariantCulture),
            run.ExitCode.ToString(CultureInfo.InvariantCulture),
            HashText(run.Stdout),
            HashText(run.Stderr),
            status,
            note.Replace('\t', ' ')
        });
    }

    private static string? CompareOutputs(CapturedProcessResult nativeRun, CapturedProcessResult transpiledRun)
    {
        if (nativeRun.ExitCode != transpiledRun.ExitCode)
        {
            return $"exit mismatch {nativeRun.ExitCode} vs {transpiledRun.ExitCode}";
        }

        var nativeStdout = NormalizeOutput(nativeRun.Stdout);
        var transpiledStdout = NormalizeOutput(transpiledRun.Stdout);
        if (!string.Equals(nativeStdout, transpiledStdout, StringComparison.Ordinal))
        {
            return "stdout mismatch";
        }

        var nativeStderr = NormalizeOutput(nativeRun.Stderr);
        var transpiledStderr = NormalizeOutput(transpiledRun.Stderr);
        if (!string.Equals(nativeStderr, transpiledStderr, StringComparison.Ordinal))
        {
            return "stderr mismatch";
        }

        return null;
    }

    private static string NormalizeOutput(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void AddExpectedFailures(
        BenchmarkExpected? expected,
        CapturedProcessResult run,
        string mode,
        ICollection<string> failures)
    {
        if (expected == null)
        {
            return;
        }

        if (run.ExitCode != expected.ExitCode)
        {
            failures.Add($"{mode} exit expected {expected.ExitCode}, got {run.ExitCode}");
        }
        if (expected.Stdout != null && NormalizeOutput(run.Stdout) != NormalizeOutput(expected.Stdout))
        {
            failures.Add($"{mode} stdout did not match expected output");
        }
        if (expected.Stderr != null && NormalizeOutput(run.Stderr) != NormalizeOutput(expected.Stderr))
        {
            failures.Add($"{mode} stderr did not match expected output");
        }
    }

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool MatchesFilter(BenchmarkScenario scenario, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (scenario.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            scenario.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return scenario.Tags.Any(tag => tag.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolvePath(string root, string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(root, path));
    }

    private static IReadOnlyList<TargetLanguage> ParseTargets(string csv)
    {
        var targets = new List<TargetLanguage>();
        var seen = new HashSet<TargetLanguage>();
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            TargetLanguage target = raw.ToLowerInvariant() switch
            {
                "bash" => TargetLanguage.Bash,
                "zsh" => TargetLanguage.Zsh,
                "powershell" => TargetLanguage.Powershell51,
                "powershell51" => TargetLanguage.Powershell51,
                "pwsh" => TargetLanguage.Powershell51,
                "powershell7" => TargetLanguage.Powershell51,
                _ => throw new ArgumentException($"Unsupported target: {raw}")
            };

            if (seen.Add(target))
            {
                targets.Add(target);
            }
        }

        if (targets.Count == 0)
        {
            throw new ArgumentException("No valid targets selected.");
        }

        return targets;
    }

    private static string ToTargetKey(TargetLanguage target)
    {
        return target switch
        {
            TargetLanguage.Bash => "bash",
            TargetLanguage.Zsh => "zsh",
            _ => "powershell"
        };
    }

    private static string GetExtension(TargetLanguage target)
    {
        return target switch
        {
            TargetLanguage.Bash => ".sh",
            TargetLanguage.Zsh => ".zsh",
            _ => ".ps1"
        };
    }

    private static bool TryResolveRunner(TargetLanguage target, out Runner runner)
    {
        var candidates = target switch
        {
            TargetLanguage.Bash => new[] { new Runner("bash", Array.Empty<string>()) },
            TargetLanguage.Zsh => new[] { new Runner("zsh", Array.Empty<string>()) },
            _ when OperatingSystem.IsWindows() => new[]
            {
                new Runner("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" }),
                new Runner("pwsh", new[] { "-NoLogo", "-NoProfile", "-File" })
            },
            _ => new[]
            {
                new Runner("pwsh", new[] { "-NoLogo", "-NoProfile", "-File" })
            }
        };

        foreach (var candidate in candidates)
        {
            if (IsExecutableAvailable(candidate.Executable))
            {
                runner = candidate;
                return true;
            }
        }

        runner = default;
        return false;
    }

    private static bool IsExecutableAvailable(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            var isPowerShell = executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
                               executable.Equals("powershell", StringComparison.OrdinalIgnoreCase);

            if (isPowerShell)
            {
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("$PSVersionTable.PSVersion.ToString()");
            }
            else
            {
                psi.ArgumentList.Add("--version");
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(2000);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static CapturedProcessResult RunScript(
        string scriptPath,
        Runner runner,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = runner.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };

        foreach (var prefix in runner.PrefixArgs)
        {
            psi.ArgumentList.Add(prefix);
        }

        psi.ArgumentList.Add(scriptPath);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var pair in env)
        {
            psi.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            return new CapturedProcessResult(1, "", "failed to start process", 0);
        }

        var watch = Stopwatch.StartNew();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(checked(timeoutSeconds * 1000)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort termination.
            }

            process.WaitForExit();
            Task.WaitAll(stdoutTask, stderrTask);
            watch.Stop();
            var timeoutError = stderrTask.Result;
            if (!string.IsNullOrEmpty(timeoutError))
            {
                timeoutError += Environment.NewLine;
            }
            timeoutError += $"benchmark process timed out after {timeoutSeconds}s";
            return new CapturedProcessResult(124, stdoutTask.Result, timeoutError, watch.Elapsed.TotalMilliseconds);
        }
        Task.WaitAll(stdoutTask, stderrTask);
        watch.Stop();

        return new CapturedProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, watch.Elapsed.TotalMilliseconds);
    }

    private readonly record struct Runner(string Executable, string[] PrefixArgs);

    private sealed record BenchmarkRun(int ExitCode, string? OutputDirectory);

    private sealed record CapturedProcessResult(int ExitCode, string Stdout, string Stderr, double ElapsedMs);

    private sealed class BenchmarkManifest
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "benchmark-suite";
        public string Description { get; set; } = "";
        public List<BenchmarkScenario> Scenarios { get; set; } = new();

        public BenchmarkManifestMeta Metadata => new()
        {
            Name = Name,
            Description = Description,
            Version = Version
        };
    }

    private sealed class BenchmarkManifestMeta
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int Version { get; set; }
    }

    private sealed class BenchmarkScenario
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public Dictionary<string, string> Native { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Args { get; set; } = new();
        public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Tags { get; set; } = new();
        public BenchmarkExpected? Expected { get; set; }
        public Dictionary<string, double> MaxRuntimeRatioMedian { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BenchmarkExpected
    {
        public int ExitCode { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
    }

    private sealed class BenchmarkResultFile
    {
        public int Version { get; set; }
        public string SuiteFingerprint { get; set; } = "";
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset EndedUtc { get; set; }
        public BenchmarkHostInfo Host { get; set; } = new();
        public BenchmarkManifestMeta Manifest { get; set; } = new();
        public List<BenchmarkResultRow> Rows { get; set; } = new();
    }

    private sealed class BenchmarkHostInfo
    {
        public string FrameworkDescription { get; set; } = "";
        public string OsDescription { get; set; } = "";
        public string ProcessArchitecture { get; set; } = "";
        public int ProcessorCount { get; set; }
        public string DotnetVersion { get; set; } = "";
        public string GitCommit { get; set; } = "";

        public static BenchmarkHostInfo Create()
        {
            return new BenchmarkHostInfo
            {
                FrameworkDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OsDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                DotnetVersion = Environment.Version.ToString(),
                GitCommit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? ""
            };
        }
    }

    private sealed class BenchmarkResultRow
    {
        public string ScenarioId { get; set; } = "";
        public string ScenarioName { get; set; } = "";
        public string Target { get; set; } = "";
        public string Status { get; set; } = "";
        public string Note { get; set; } = "";
        public double TranspileMs { get; set; }
        public int GeneratedScriptBytes { get; set; }
        public BenchmarkStats Native { get; set; } = new();
        public BenchmarkStats Transpiled { get; set; } = new();
        public double RuntimeRatioMedian { get; set; }
        public double? MaxRuntimeRatioMedian { get; set; }
        public double? BaselineRatioMedian { get; set; }
        public double? BaselineDeltaPercent { get; set; }
        public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

        public static BenchmarkResultRow Skipped(string scenarioId, string target, string note)
        {
            return new BenchmarkResultRow
            {
                ScenarioId = scenarioId,
                Target = target,
                Status = "skipped",
                Note = note
            };
        }

        public static BenchmarkResultRow Failed(string scenarioId, string target, string note)
        {
            return new BenchmarkResultRow
            {
                ScenarioId = scenarioId,
                Target = target,
                Status = "failed",
                Note = note
            };
        }
    }

    internal sealed class BenchmarkStats
    {
        public int Samples { get; set; }
        public double MeanMs { get; set; }
        public double MedianMs { get; set; }
        public double P95Ms { get; set; }
        public double StdDevMs { get; set; }

        public static BenchmarkStats From(IReadOnlyList<double> values)
        {
            if (values.Count == 0)
            {
                return new BenchmarkStats();
            }

            var ordered = values.OrderBy(v => v).ToArray();
            var mean = ordered.Average();
            var variance = ordered.Select(v => (v - mean) * (v - mean)).Average();

            return new BenchmarkStats
            {
                Samples = values.Count,
                MeanMs = mean,
                MedianMs = BenchmarkMath.Percentile(ordered, 0.5),
                P95Ms = BenchmarkMath.Percentile(ordered, 0.95),
                StdDevMs = Math.Sqrt(variance)
            };
        }
    }
}
