namespace Sushi.Application.Commands;

using System.CommandLine;


static class WatchCommand
{
    public static Command Create()
    {
        Argument<string> fileArgument = new("file")
        {
            Description = "Path to the .sushi file to watch"
        };

        Option<TargetLanguage> targetLanguageOption = new("-t", "--target")
        {
            Description = "Language to transpile to",
            DefaultValueFactory = parseResult => CommandSupport.GetDefaultTarget()
        };

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

        fileArgument.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>() ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                result.AddError("File path cannot be empty.");
                return;
            }
            if (!value.EndsWith(".sushi", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("File must have a .sushi extension.");
            }
        });

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
            var target = parseResult.GetValue(targetLanguageOption);
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
            var lastResult = TranspileOnce(absolutePath, outputPath, target, verbose, runAfterTranspile, workingDirectory);
            var stopRequested = false;
            var lastWriteTime = File.GetLastWriteTimeUtc(absolutePath);

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
                    if (!File.Exists(absolutePath))
                    {
                        continue;
                    }

                    var currentWriteTime = File.GetLastWriteTimeUtc(absolutePath);
                    if (currentWriteTime == lastWriteTime)
                    {
                        continue;
                    }

                    lastWriteTime = currentWriteTime;
                    lastResult = TranspileOnce(absolutePath, outputPath, target, verbose, runAfterTranspile, workingDirectory);
                }
            }
            finally
            {
                System.Console.CancelKeyPress -= cancelHandler;
            }

            return lastResult;
        });

        return command;
    }

    private static int TranspileOnce(
        string filePath,
        string outputPath,
        TargetLanguage target,
        bool verbose,
        bool runAfterTranspile,
        string? workingDirectory)
    {
        if (!CommandSupport.TryReadSourceFile(filePath, out var source))
        {
            return 1;
        }

        var result = CommandSupport.Transpile(filePath, target, source);
        if (!result.Success || result.EmittedCode == null)
        {
            CommandSupport.PrintDiagnostics(result, verbose: true);
            return 1;
        }

        if (!CommandSupport.TryWriteOutput(outputPath, result.EmittedCode))
        {
            return 1;
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
            return 0;
        }

        System.Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] Running {outputPath}");
        return CommandSupport.ExecuteScript(target, outputPath, Array.Empty<string>(), workingDirectory);
    }
}
