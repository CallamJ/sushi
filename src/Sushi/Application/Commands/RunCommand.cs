namespace Sushi.Application.Commands;

using System.CommandLine;

static class RunCommand
{
    public static Command Create()
    {
        Argument<string> fileArgument = new("file")
        {
            Description = "Path to the .sushi file to run"
        };

        Argument<string[]> scriptArguments = new("script-args")
        {
            Description = "Arguments passed to the transpiled script",
            Arity = ArgumentArity.ZeroOrMore
        };

        Option<TargetLanguage> targetLanguageOption = new("-t", "--target")
        {
            Description = "Language to transpile to (defaults: Powershell on Windows, Zsh on macOS, Bash on Linux)",
            DefaultValueFactory = parseResult => CommandSupport.GetDefaultTarget()
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

        var command = new Command("run", "Transpile a .sushi file and execute it immediately")
        {
            fileArgument,
            targetLanguageOption,
            scriptArguments
        };

        command.SetAction(parseResult =>
        {
            var filePath = parseResult.GetValue(fileArgument) ?? "";
            var target = parseResult.GetValue(targetLanguageOption);
            var scriptArgs = parseResult.GetValue(scriptArguments) ?? Array.Empty<string>();

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

            var scriptExtension = target switch
            {
                TargetLanguage.Bash => ".sh",
                TargetLanguage.Zsh => ".zsh",
                _ => ".ps1"
            };

            var tempOutputPath = Path.Combine(
                Path.GetTempPath(),
                $"sushi-run-{Guid.NewGuid():N}{scriptExtension}");

            if (!CommandSupport.TryWriteOutput(tempOutputPath, result.EmittedCode))
            {
                return 1;
            }

            try
            {
                var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
                return CommandSupport.ExecuteScript(target, tempOutputPath, scriptArgs, workingDirectory);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempOutputPath))
                    {
                        File.Delete(tempOutputPath);
                    }
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        });

        return command;
    }
}
