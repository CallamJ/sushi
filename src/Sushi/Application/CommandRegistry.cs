namespace Sushi.Application;

using System.CommandLine;
using Sushi.Application.Commands;

// TODO: Move to Models
public enum TargetLanguage {
    Powershell51,
    Bash,
    Zsh,
}

static class CommandRegistry
{
    public static void RegisterCommands(RootCommand root)
    {
        root.Subcommands.Add(TranspileCommand.Create());
        root.Subcommands.Add(CheckCommand.Create());
        root.Subcommands.Add(RunCommand.Create());
        root.Subcommands.Add(WatchCommand.Create());
        root.Subcommands.Add(BenchmarkCommand.Create());
        root.Subcommands.Add(DocsCommand.Create());
        root.Subcommands.Add(LspCommand.Create());
    }
}
