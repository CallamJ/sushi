namespace Sushi.Application.Commands;

using System.CommandLine;
using Sushi.Application;


static class WatchCommand
{
    public static Command Create()
    {
        Argument<string> fileArgument = new("file")
        {
            Description = "Path to the .sushi file to watch"
        };

        var targetLanguageOption = TranspileCommand.CreateTargetOption();

        Option<bool> verboseOption = new("-v", "--verbose")
        {
            Description = "Print diagnostics and transpile details on each change",
            DefaultValueFactory = parseResult => false
        };

        Option<bool> runOption = new("--run")
        {
            Description = "Execute transpiled output after each successful update",
            DefaultValueFactory = _ => false
        };

        Option<int> pollIntervalMsOption = new("--poll-interval-ms")
        {
            Description = "Polling interval for file change detection",
            DefaultValueFactory = _ => 250
        };

        TranspileCommand.AddFileValidator(fileArgument);

        var command = new Command("watch", "Automatically transpile a .sushi file on any changes")
        {
            fileArgument,
            targetLanguageOption,
            verboseOption,
            runOption,
            pollIntervalMsOption
        };

        command.SetAction(parseResult =>
        {
            var filePath = parseResult.GetValue(fileArgument) ?? "";
            var targetText = parseResult.GetValue(targetLanguageOption) ?? "auto";
            if (!CommandSupport.TryParseTarget(targetText, out var target))
            {
                System.Console.Error.WriteLine($"Invalid target '{targetText}'. Choose one of: {TargetProfile.AcceptedValues}.");
                return 1;
            }
            if (parseResult.GetValue(runOption) && !CommandSupport.CanRunLocally(target))
            {
                System.Console.Error.WriteLine($"Cannot use --run for target '{target.Id}' on this host.");
                return 1;
            }
            var verbose = parseResult.GetValue(verboseOption);
            var runAfterTranspile = parseResult.GetValue(runOption);
            var pollIntervalMs = parseResult.GetValue(pollIntervalMsOption);

            if (pollIntervalMs < 50)
            {
                System.Console.Error.WriteLine("poll interval must be at least 50ms.");
                return 1;
            }

            if (!File.Exists(filePath))
            {
                System.Console.Error.WriteLine($"Input file not found: {filePath}");
                return 1;
            }

            var absolutePath = Path.GetFullPath(filePath);
            var outputPath = CommandSupport.GetOutputPath(absolutePath, target);
            var workingDirectory = Path.GetDirectoryName(absolutePath);
            var cycle = TranspileOnce(absolutePath, outputPath, target, verbose, runAfterTranspile, workingDirectory);
            var stopRequested = false;
            var watchedFiles = CreateWatchSnapshot(absolutePath, cycle.Dependencies);

            System.Console.WriteLine($"Watching {absolutePath} -> {outputPath}");
            System.Console.WriteLine("Press Ctrl+C to stop.");

            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopRequested = true;
            };

            System.Console.CancelKeyPress += cancelHandler;
            try
            {
                while (!stopRequested)
                {
                    Thread.Sleep(pollIntervalMs);
                    if (!HasChanged(watchedFiles))
                    {
                        continue;
                    }

                    cycle = TranspileOnce(absolutePath, outputPath, target, verbose, runAfterTranspile, workingDirectory);
                    watchedFiles = CreateWatchSnapshot(absolutePath, cycle.Dependencies);
                }
            }
            finally
            {
                System.Console.CancelKeyPress -= cancelHandler;
            }

            return cycle.ExitCode;
        });

        return command;
    }

    private static WatchCycleResult TranspileOnce(
        string filePath,
        string outputPath,
        TargetProfile target,
        bool verbose,
        bool runAfterTranspile,
        string? workingDirectory)
    {
        if (!CommandSupport.TryReadSourceFile(filePath, out var source))
        {
            return new WatchCycleResult(1, Array.Empty<string>());
        }

        var result = CommandSupport.Transpile(filePath, target, source);
        if (!result.Success || result.EmittedCode == null)
        {
            CommandSupport.PrintDiagnostics(result, verbose: true);
            return new WatchCycleResult(1, result.DependencyPaths);
        }

        if (!CommandSupport.TryWriteOutput(outputPath, result.EmittedCode))
        {
            return new WatchCycleResult(1, result.DependencyPaths);
        }

        if (verbose)
        {
            System.Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] Transpiled {filePath} -> {outputPath}");
            if (result.Diagnostics.Count > 0)
            {
                CommandSupport.PrintDiagnostics(result, verbose: true);
            }
        }

        if (!runAfterTranspile)
        {
            return new WatchCycleResult(0, result.DependencyPaths);
        }

        System.Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] Running {outputPath}");
        return new WatchCycleResult(
            CommandSupport.ExecuteScript(target, outputPath, Array.Empty<string>(), workingDirectory),
            result.DependencyPaths);
    }

    private static Dictionary<string, DateTime?> CreateWatchSnapshot(string root, IReadOnlyList<string> dependencies) =>
        dependencies.Prepend(root).Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null, StringComparer.Ordinal);

    private static bool HasChanged(IReadOnlyDictionary<string, DateTime?> snapshot) =>
        snapshot.Any(item => (File.Exists(item.Key) ? File.GetLastWriteTimeUtc(item.Key) : (DateTime?)null) != item.Value);

    private sealed record WatchCycleResult(int ExitCode, IReadOnlyList<string> Dependencies);
}
