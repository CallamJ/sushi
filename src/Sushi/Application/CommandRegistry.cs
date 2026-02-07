namespace Sushi.Application;

using System.CommandLine;
using Sushi.Application.Commands;

// TODO: Move to Models
public enum TargetLanguage {
    Powershell7,
    Bash,
    Zsh,
}

static class CommandRegistry
{
    public static void RegisterCommands(RootCommand root)
    {
        root.Subcommands.Add(TranspileCommand.Create());
        root.Subcommands.Add(CheckCommand.Create());
        root.Subcommands.Add(FormatCommand.Create());
        root.Subcommands.Add(RunCommand.Create());
        root.Subcommands.Add(WatchCommand.Create());
    }
}
