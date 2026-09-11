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

        var targetLanguageOption = TranspileCommand.CreateTargetOption();

        TranspileCommand.AddFileValidator(fileArgument);

        var command = new Command("run", "Transpile a .sushi file and execute it immediately")
        {
            fileArgument,
            targetLanguageOption,
            scriptArguments
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
            if (!CommandSupport.CanRunLocally(target))
            {
                System.Console.Error.WriteLine($"Cannot run target '{target.Id}' on this host. Transpile it and execute it on {target.PlatformName} instead.");
                return 1;
            }
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

            var scriptExtension = target.FileExtension;

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
