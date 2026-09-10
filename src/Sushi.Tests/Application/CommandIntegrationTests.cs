namespace Sushi.Tests.Application;

using System.CommandLine;
using Sushi.Application;
using Xunit;

public sealed class CommandIntegrationTests
{
    [Fact]
    public void CommandSurface_OnlyAdvertisesImplementedCommands()
    {
        var root = CreateRoot();
        var names = root.Subcommands.Select(command => command.Name).ToArray();

        Assert.Contains("check", names);
        Assert.Contains("run", names);
        Assert.Contains("transpile", names);
        Assert.DoesNotContain("fmt", names);
    }

    [Fact]
    public async Task Check_InvalidSource_ReturnsFailure()
    {
        var sourcePath = CreateSource("var value =");
        try
        {
            var exitCode = await CreateRoot().Parse(new[] { "check", sourcePath }).InvokeAsync();
            Assert.NotEqual(0, exitCode);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task Transpile_ValidSource_WritesTargetFile()
    {
        var sourcePath = CreateSource("println(\"hello\")");
        var outputPath = Path.ChangeExtension(sourcePath, ".sh");
        try
        {
            var exitCode = await CreateRoot()
                .Parse(new[] { "transpile", sourcePath, "--target", "Bash" })
                .InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(outputPath));
            Assert.Contains("printf '%s\\n'", File.ReadAllText(outputPath));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Run_Bash_ForwardsScriptArguments()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sourcePath = CreateSource(
            "var values = std.process.args()\n" +
            "if (values[0] != \"expected value\") { std.process.exit(9) }");
        try
        {
            var exitCode = await CreateRoot()
                .Parse(new[] { "run", sourcePath, "--target", "Bash", "expected value" })
                .InvokeAsync();

            Assert.Equal(0, exitCode);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    private static RootCommand CreateRoot()
    {
        var root = new RootCommand("test");
        CommandRegistry.RegisterCommands(root);
        return root;
    }

    private static string CreateSource(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sushi-command-test-{Guid.NewGuid():N}.sushi");
        File.WriteAllText(path, source);
        return path;
    }
}
