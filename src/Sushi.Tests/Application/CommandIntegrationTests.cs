namespace Sushi.Tests.Application;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        Assert.Contains("lsp", names);
        Assert.Contains("docs", names);
        Assert.DoesNotContain("fmt", names);
    }

    [Fact]
    public async Task Check_InvalidSource_ReturnsFailure()
    {
        var sourcePath = CreateSource("var value =");
        try
        {
            var exitCode = await CreateRoot().Parse(new[] { "check", sourcePath })
                .InvokeAsync(null, TestContext.Current.CancellationToken);
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
        var outputPath = Path.ChangeExtension(sourcePath, ".bash-linux.sh");
        try
        {
            var exitCode = await CreateRoot()
                .Parse(new[] { "transpile", sourcePath, "--target", "bash-linux" })
                .InvokeAsync(null, TestContext.Current.CancellationToken);

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
    public async Task Docs_ExportedApi_WritesMarkdown()
    {
        var sourcePath = CreateSource("/// Say hello.\n/// @param name Who to greet.\n/// @returns A greeting.\nexport string greet(string name) { return name }");
        var outputPath = Path.ChangeExtension(sourcePath, ".md");
        try
        {
            var exitCode = await CreateRoot().Parse(new[] { "docs", sourcePath, "--output", outputPath, "--include-builtins" }).InvokeAsync(null, TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            var markdown = File.ReadAllText(outputPath);
            Assert.Contains("### greet", markdown);
            Assert.Contains("Say hello.", markdown);
            Assert.Contains("Who to greet.", markdown);
            Assert.Contains("### std.fs.glob", markdown);
            Assert.Contains("Returns a sorted `string[]`", markdown);
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
                .Parse(new[] { "run", sourcePath, "--target", "bash-linux", "expected value" })
                .InvokeAsync(null, TestContext.Current.CancellationToken);

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
