namespace Sushi.Application.Commands;

using System.Runtime.InteropServices;
using System.CommandLine;


static class WatchCommand
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
            Description = "Path to the .sushi file to watch"
        };

        Option<TargetLanguage> targetLanguageOption = new("-t", "--target")
        {
            Description = "Language to transpile to",
            DefaultValueFactory = parseResult => GetTarget()
        };

        Option<bool> discardCommentsOption = new("-v", "--verbose")
        {
            Description = "Should discard comments?",
            DefaultValueFactory = parseResult => false
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

        var command = new Command("watch", "Automatically transpile a .sushi file on any changes")
        {
            fileArgument,
            targetLanguageOption,
            discardCommentsOption,
        };

        command.SetAction(parseResult =>
        {
            // TODO: Implement
        });

        return command;
    }
}
