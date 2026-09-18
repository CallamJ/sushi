namespace Sushi.Tests.Transpilation;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sushi.Application;
using Sushi.Transpilation;
using Xunit;

public sealed class FileQueryTests
{
    [Theory]
    [InlineData(TargetLanguage.Bash, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Zsh, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Powershell51, TargetPlatform.Windows)]
    public void ReusableDynamicQuery_LowersWithoutAGlobRuntime(TargetLanguage target, TargetPlatform platform)
    {
        const string source = """
            use std.fs as fs
            var root = "src"
            var extension = "cs"
            var baseQuery = fs.query(root).recursive().includingHidden()
            var sourceQuery = baseQuery.matching("*." + extension).excluding("*Test.cs")
            string[] files = sourceQuery.files()
            string[] directories = baseQuery.directories()
            """;

        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "query.sushi",
            TargetLanguage = target,
            TargetProfile = new TargetProfile(target, platform)
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.DoesNotContain("__sushi_fs_glob", result.EmittedCode);
        Assert.DoesNotContain("__sushi_glob_regex", result.EmittedCode);
        Assert.Contains(target == TargetLanguage.Powershell51 ? "Get-ChildItem" : "find -P", result.EmittedCode);
    }

    [Fact]
    public void LegacyGlob_IsNotAPublicStandardLibraryMember()
    {
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = "use std.fs.glob\nvar files = glob(\"*.sushi\")",
            SourcePath = "legacy-glob.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.DoesNotContain("__sushi_fs_glob", result.EmittedCode);
    }

    [Theory]
    [InlineData(TargetLanguage.Bash, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Zsh, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Powershell51, TargetPlatform.Windows)]
    public void FileQuery_CanCrossATypedFunctionBoundary(TargetLanguage target, TargetPlatform platform)
    {
        const string source = """
            use std.fs as fs
            string[] list(FileQuery input) {
                return input.files()
            }
            var sourceQuery = fs.query("src").recursive().matching("*.cs")
            var functionQuery = sourceQuery
            string[] files = list(functionQuery)
            """;
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "query-function.sushi",
            TargetLanguage = target,
            TargetProfile = new TargetProfile(target, platform)
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.DoesNotContain("__sushi_fs_glob", result.EmittedCode);
        Assert.Contains(target == TargetLanguage.Powershell51 ? "[pscustomobject]" : "-A functionQuery", result.EmittedCode);
    }

    [Theory]
    [InlineData(TargetLanguage.Bash, "bash", ".sh")]
    [InlineData(TargetLanguage.Zsh, "zsh", ".zsh")]
    [InlineData(TargetLanguage.Powershell51, "pwsh", ".ps1")]
    public void FileQuery_UsesNativeFiltersAndPreservesReusableQueryResults(TargetLanguage target, string shell, string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sushi-query-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "keep.cs"), "");
        File.WriteAllText(Path.Combine(root, "skipTests.cs"), "");
        File.WriteAllText(Path.Combine(root, ".hidden.cs"), "");
        File.WriteAllText(Path.Combine(root, "nested", "child.cs"), "");
        File.WriteAllText(Path.Combine(root, "nested", "line\nname.cs"), "");
        var sushiRoot = root.Replace("\\", "/").Replace("\"", "\\\"");
        var source = $$"""
            use std.fs as fs
            var query = fs.query("{{sushiRoot}}").recursive().matching("*.cs").excluding("*Tests.cs")
            string[] files = query.files()
            println("count: " + files.length())
            """;

        try
        {
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourceText = source,
                SourcePath = "query-runtime.sushi",
                TargetLanguage = target,
                TargetProfile = new TargetProfile(target, TargetPlatform.Linux)
            });

            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.DoesNotContain("__sushi_query_", result.EmittedCode);
            Assert.Contains(target == TargetLanguage.Powershell51 ? "Get-ChildItem" : "find -P .", result.EmittedCode);
            Assert.Contains(target == TargetLanguage.Powershell51 ? "-Filter" : "-name", result.EmittedCode);

            var path = Path.Combine(Path.GetTempPath(), $"sushi-query-{Guid.NewGuid():N}{extension}");
            File.WriteAllText(path, result.EmittedCode);
            try
            {
                var start = target == TargetLanguage.Powershell51
                    ? new ProcessStartInfo(shell, $"-NoProfile -File \"{path}\"")
                    : new ProcessStartInfo(shell, path);
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.UseShellExecute = false;
                using var process = Process.Start(start);
                Assert.NotNull(process);
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, error);
                Assert.Contains("count: 3", output.Replace("\r\n", "\n"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(TargetLanguage.Bash, "bash", ".sh")]
    [InlineData(TargetLanguage.Zsh, "zsh", ".zsh")]
    [InlineData(TargetLanguage.Powershell51, "pwsh", ".ps1")]
    public void FileQuery_AliasesSnapshotPlansAndReassignmentReplacesOnlyTheTarget(
        TargetLanguage target, string shell, string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sushi-query-values-{Guid.NewGuid():N}");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "one.cs"), "");
        File.WriteAllText(Path.Combine(second, "two.cs"), "");
        File.WriteAllText(Path.Combine(second, "three.cs"), "");
        var firstPath = first.Replace("\\", "/").Replace("\"", "\\\"");
        var secondPath = second.Replace("\\", "/").Replace("\"", "\\\"");
        var source = $$"""
            use std.fs as fs
            var baseQuery = fs.query("{{firstPath}}")
            var savedQuery = baseQuery
            baseQuery = fs.query("{{secondPath}}")
            string[] saved = savedQuery.files()
            string[] current = baseQuery.files()
            println("saved: " + saved.length())
            println("current: " + current.length())
            """;

        try
        {
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourceText = source,
                SourcePath = "query-values.sushi",
                TargetLanguage = target,
                TargetProfile = new TargetProfile(target, TargetPlatform.Linux)
            });

            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.DoesNotContain("__sushi_query_", result.EmittedCode);
            Assert.DoesNotContain("savedQuery", result.EmittedCode);
            Assert.Contains(target == TargetLanguage.Powershell51 ? "Get-ChildItem" : "find -P .", result.EmittedCode);

            var path = Path.Combine(Path.GetTempPath(), $"sushi-query-values-{Guid.NewGuid():N}{extension}");
            File.WriteAllText(path, result.EmittedCode);
            try
            {
                var start = target == TargetLanguage.Powershell51
                    ? new ProcessStartInfo(shell, $"-NoProfile -File \"{path}\"")
                    : new ProcessStartInfo(shell, path);
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.UseShellExecute = false;
                using var process = Process.Start(start);
                Assert.NotNull(process);
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, error);
                Assert.Contains("saved: 1", output.Replace("\r\n", "\n"));
                Assert.Contains("current: 2", output.Replace("\r\n", "\n"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(TargetLanguage.Bash, "bash", ".sh")]
    [InlineData(TargetLanguage.Zsh, "zsh", ".zsh")]
    [InlineData(TargetLanguage.Powershell51, "pwsh", ".ps1")]
    public void FileQuery_ExcludesSymbolicLinks(
        TargetLanguage target, string shell, string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sushi-query-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        Directory.CreateDirectory(Path.Combine(root, "zeta"));
        File.WriteAllText(Path.Combine(root, "alpha", "first.txt"), "");
        File.WriteAllText(Path.Combine(root, "middle.txt"), "");
        File.CreateSymbolicLink(Path.Combine(root, "linked-file.txt"), Path.Combine(root, "middle.txt"));
        Directory.CreateSymbolicLink(Path.Combine(root, "linked-directory"), Path.Combine(root, "alpha"));

        var sushiRoot = root.Replace("\\", "/").Replace("\"", "\\\"");
        var source = $$"""
            use std.fs as fs
            string[] entries = fs.query("{{sushiRoot}}").recursive().entries()
            for (string entry : entries) { println(entry) }
            """;
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "query-order.sushi",
            TargetLanguage = target,
            TargetProfile = new TargetProfile(target, TargetPlatform.Linux)
        });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));

        var path = Path.Combine(Path.GetTempPath(), $"sushi-query-order-{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllText(path, result.EmittedCode);
            var start = target == TargetLanguage.Powershell51
                ? new ProcessStartInfo(shell, $"-NoProfile -File \"{path}\"")
                : new ProcessStartInfo(shell, path);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.UseShellExecute = false;
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, error);
            Assert.Equal(
                new[] { "alpha", "alpha/first.txt", "middle.txt", "zeta" },
                output.Replace("\r\n", "\n").Trim().Split('\n').OrderBy(value => value));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(TargetLanguage.Bash, "bash", ".sh")]
    [InlineData(TargetLanguage.Zsh, "zsh", ".zsh")]
    public void FileQuery_RecursiveFunctionKeepsEachInvocationResult(
        TargetLanguage target, string shell, string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sushi-query-recursion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "one", "nested"));
        Directory.CreateDirectory(Path.Combine(root, "two"));
        File.WriteAllText(Path.Combine(root, "one", "a.txt"), "");
        File.WriteAllText(Path.Combine(root, "one", "nested", "b.txt"), "");
        File.WriteAllText(Path.Combine(root, "two", "c.txt"), "");
        var sushiRoot = root.Replace("\\", "/").Replace("\"", "\\\"");
        var source = $$"""
            use std.fs as fs
            use std.path as path
            string visit(string directory) {
                string[] entries = fs.query(directory).entries()
                string output = ""
                for (string entry : entries) {
                    string entryPath = path.join(directory, entry)
                    if (fs.isDirectory(entryPath)) {
                        output += visit(entryPath)
                    } else {
                        output += entry + "\n"
                    }
                }
                return output
            }
            println(visit("{{sushiRoot}}"))
            """;

        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "query-recursion.sushi",
            TargetLanguage = target,
            TargetProfile = new TargetProfile(target, TargetPlatform.Linux)
        });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));

        var path = Path.Combine(Path.GetTempPath(), $"sushi-query-recursion-{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllText(path, result.EmittedCode);
            var start = new ProcessStartInfo(shell, path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, error);
            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" },
                output.Replace("\r\n", "\n").Trim().Split('\n').OrderBy(value => value));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
