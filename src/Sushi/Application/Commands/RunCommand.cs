namespace Sushi.Application.Commands;
using System.Runtime.InteropServices;
using System.CommandLine;

static class RunCommand
{
    public static TargetLanguage GetTarget()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? TargetLanguage.Powershell7
            : TargetLanguage.Bash;
    }

    public static Command Create()
    {
        Argument<string> fileArgument = new("file")
        {
            Description = "Path to the .sushi file to run"
        };

        Option<TargetLanguage> targetLanguageOption = new("-t", "--target")
        {
            Description = "Language to transpile to (defaults to Powershell on Windows, Bash on Linux/macOS)",
            DefaultValueFactory = parseResult => GetTarget()
        };

        fileArgument.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>() ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                result.AddError("File path cannot be empty.");
                return;
            }
            if (!value.EndsWith(".sushi"))
            {
                result.AddError("File must have a .sushi extension.");
            }
        });

        var command = new Command("run", "Transpile a .sushi file and execute it immediately")
        {
            fileArgument,
            targetLanguageOption
        };

        command.SetAction(parseResult =>
        {
            // TODO: Implement
        });

        return command;
    }
}
