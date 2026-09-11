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

    [Fact]
    public void ImportedFunction_SupportsNamedArguments()
    {
        WithModules(
            "box Example.Helper\nexport greet(name, punctuation = \"!\") { return name + punctuation }",
            "use \"./helper.sushi\" as helper\nprintln(helper.greet(punctuation: \"?\", name: \"Ada\"))",
            result => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message))));
    }

    [Theory]
    [InlineData(TargetLanguage.Bash)]
    [InlineData(TargetLanguage.Zsh)]
    [InlineData(TargetLanguage.Powershell7)]
    public void ImportedClassAndEnum_AreResolvedStatically(TargetLanguage target)
    {
        WithModules(
            """
            box Example.Model
            export class Person {
                string name
                new(string name) { this.name = name }
                string label() -> this.name
                string() -> this.name
            }
            export enum State { Ready, Done }
            export Person create(string name) { return new Person(name) }
            """,
            """
            use "./helper.sushi" as model
            var person = new model.Person(name: "Ada")
            println(person.label())
            println(string(person))
            println(model.State.Done.name)
            var returned = model.create(name: "Grace")
            println(returned.label())
            """,
            result =>
            {
                Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Contains("sushi_module_Example_Model_Person", result.EmittedCode);
                Assert.Contains("sushi_module_Example_Model_State_Done", result.EmittedCode);
            },
            target);
    }

    [Fact]
    public void ImportedVariable_IsReadOnly()
    {
        WithModules(
            "box Example.Settings\nexport var value = 1",
            "use \"./helper.sushi\" as settings\nsettings.value = 2",
            result =>
            {
                Assert.False(result.Success);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1045");
            });
    }

    [Fact]
    public void MissingImport_IsIncludedInWatchDependencies()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sushi-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var missing = Path.Combine(directory, "missing.sushi");
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourcePath = Path.Combine(directory, "app.sushi"),
                SourceText = "use \"./missing.sushi\"",
                TargetLanguage = TargetLanguage.Bash
            });
            Assert.False(result.Success);
            Assert.Contains(Path.GetFullPath(missing), result.DependencyPaths);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportCycle_IsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sushi-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var a = Path.Combine(directory, "a.sushi");
            var b = Path.Combine(directory, "b.sushi");
            File.WriteAllText(a, "box Example.A\nuse \"./b.sushi\"");
            File.WriteAllText(b, "box Example.B\nuse \"./a.sushi\"");
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourcePath = a,
                SourceText = File.ReadAllText(a),
                TargetLanguage = TargetLanguage.Bash
            });
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1042");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DiamondDependency_IsInitializedExactlyOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sushi-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "shared.sushi"), "box Example.Shared\nprintln(\"shared-init\")\nexport value() { return 1 }");
            File.WriteAllText(Path.Combine(directory, "left.sushi"), "box Example.Left\nuse \"./shared.sushi\" as shared\nexport left() { return shared.value() }");
            File.WriteAllText(Path.Combine(directory, "right.sushi"), "box Example.Right\nuse \"./shared.sushi\" as shared\nexport right() { return shared.value() }");
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourcePath = Path.Combine(directory, "app.sushi"),
                SourceText = "use \"./left.sushi\" as left\nuse \"./right.sushi\" as right\nprintln(left.left() + right.right())",
                TargetLanguage = TargetLanguage.Bash
            });
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal(1, result.EmittedCode!.Split("shared-init", StringSplitOptions.None).Length - 1);
            Assert.Equal(3, result.DependencyPaths.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DefaultAlias_IsAValidIdentifier()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sushi-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "123-tools.sushi"), "box Example.Tools\nexport answer() { return 42 }");
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourcePath = Path.Combine(directory, "app.sushi"),
                SourceText = "use \"./123-tools.sushi\"\nprintln(_123_tools.answer())",
                TargetLanguage = TargetLanguage.Bash
            });
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WithModules(
        string helperSource,
        string rootSource,
        Action<TranspileResult> assertion,
        TargetLanguage target = TargetLanguage.Bash)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sushi-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "helper.sushi"), helperSource);
            var result = new Transpiler().Transpile(new TranspileRequest
            {
                SourcePath = Path.Combine(directory, "app.sushi"),
                SourceText = rootSource,
                TargetLanguage = target
            });
            assertion(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
