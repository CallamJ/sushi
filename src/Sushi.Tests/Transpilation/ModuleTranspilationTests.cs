namespace Sushi.Tests.Transpilation;

using System;
using System.IO;
using System.Linq;
using Sushi.Application;
using Sushi.Transpilation;
using Xunit;

public sealed class ModuleTranspilationTests
{
    [Fact]
    public void RelativeImport_EmitsExportedFunctionOnceAndTracksDependency()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sushi-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var helperPath = Path.Combine(directory, "helper.sushi");
        var rootPath = Path.Combine(directory, "app.sushi");
        try
        {
            File.WriteAllText(helperPath, "box Example.Helper\nexport greet(name) { return \"Hello \" + name }");
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourcePath = rootPath,
                SourceText = "use \"./helper.sushi\" as helper\nprintln(helper.greet(\"Ada\"))",
                TargetLanguage = TargetLanguage.Bash
            });

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
            Assert.Contains("sushi_module_Example_Helper_greet", result.EmittedCode);
            Assert.Single(result.DependencyPaths);
            Assert.Equal(Path.GetFullPath(helperPath), result.DependencyPaths[0]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportedModuleWithoutBox_IsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sushi-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var helperPath = Path.Combine(directory, "helper.sushi");
        try
        {
            File.WriteAllText(helperPath, "export greet() { return \"hello\" }");
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourcePath = Path.Combine(directory, "app.sushi"),
                SourceText = "use \"./helper.sushi\"",
                TargetLanguage = TargetLanguage.Bash
            });
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1041");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PrivateModuleFunction_IsNotAccessibleThroughAlias()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sushi-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "helper.sushi"), "box Example.Helper\nsecret() { return \"no\" }");
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourcePath = Path.Combine(directory, "app.sushi"),
                SourceText = "use \"./helper.sushi\" as helper\nprintln(helper.secret())",
                TargetLanguage = TargetLanguage.Bash
            });
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1043");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
