namespace Sushi.Tests.Transpilation;

using System.Linq;
using Sushi.Application;
using Sushi.Transpilation;
using Xunit;

public sealed class ObjectOrientedTranspilationTests
{
    private const string CounterSource = """
        class Counter {
            int count
            new(int start) { this.count = start }
            increment() { this.count += 1 }
            int value() { return this.count }
        }
        var counter = new Counter(7)
        counter.increment()
        println(counter.value())
        """;

    [Theory]
    [InlineData(TargetLanguage.Bash)]
    [InlineData(TargetLanguage.Zsh)]
    [InlineData(TargetLanguage.Powershell51)]
    public void ConstructorBodyAndFieldMutation_AreEmitted(TargetLanguage target)
    {
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourcePath = "counter.sushi",
            SourceText = CounterSource,
            TargetLanguage = target
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("count", result.EmittedCode);
    }

    [Fact]
    public void ClassFieldInitializerTypeMismatch_IsReported()
    {
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourcePath = "field-type-mismatch.sushi",
            SourceText = "class Settings { string label = 9 }",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1048");
    }

    [Fact]
    public void CommaSeparatedAndChainedClassFields_AreLoweredAsIndependentFields()
    {
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourcePath = "shared-fields.sushi",
            SourceText = "class Settings { string dee = \"hello\", dum = \"world\", doo = too = foo = \"many things\" }",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.All(new[] { "dee", "dum", "doo", "too", "foo" }, field => Assert.Contains(field, result.EmittedCode));
    }

    [Theory]
    [InlineData(TargetLanguage.Bash)]
    [InlineData(TargetLanguage.Zsh)]
    [InlineData(TargetLanguage.Powershell51)]
    public void ClassesEnumsAndAdapters_UseNativeObjectsWithoutJsonHelpers(TargetLanguage target)
    {
        const string source = """
            class Person {
                string name
                new(string value) { this.name = value }
                string label() -> this.name
                Person copy() -> new Person(this.name)
                string() -> this.name
            }
            enum Priority(int weight) {
                Low(1), High(2);
                urgent() -> this.weight > 1
                string() -> this.name
            }
            enum ExitCode { Ok = 0, Failed = 1 }
            enum Axis { X { offset: 10 }, Y { offset: 20 } }
            Person identity(Person value) { return value }
            var person = new Person("Ada")
            println(person.label())
            println(string(person))
            println(Priority.High.name)
            println(Priority.High.weight)
            println(Priority.High.urgent())
            println(string(Priority.Low))
            println(Priority.Low != Priority.High)
            println(ExitCode.Failed.value)
            println(Axis.Y.offset)
            var returned = identity(person)
            println(returned.name)
            var copy = person.copy()
            println(copy.name)
            """;

        var result = Transpile(source, target);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("__sushi_json_member", result.EmittedCode);
        Assert.DoesNotContain("__sushi_call_method", result.EmittedCode);
        if (target == TargetLanguage.Powershell51)
        {
            Assert.Contains("class Person", result.EmittedCode);
            Assert.Contains("class Priority", result.EmittedCode);
            Assert.Contains("[Priority]::High", result.EmittedCode);
            Assert.Contains("[Person]::new", result.EmittedCode);
            Assert.Contains("ToString()", result.EmittedCode);
            Assert.Contains("enum ExitCode", result.EmittedCode);
            Assert.DoesNotContain("person_string", result.EmittedCode);
        }
        else
        {
            Assert.Contains("person_string", result.EmittedCode);
            Assert.Contains("Priority_High", result.EmittedCode);
        }
    }

    [Fact]
    public void EnumMutation_IsRejected()
    {
        var result = Transpile("enum State { Ready }\nState.Ready.name = \"changed\"", TargetLanguage.Bash);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1032");
    }

    [Fact]
    public void UnknownClassMember_IsRejected()
    {
        var result = Transpile("class Person { string name }\nvar person = new Person(\"Ada\")\nprintln(person.missing)", TargetLanguage.Bash);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1031");
    }

    [Fact]
    public void UnknownClassAndEnumValue_AreRejected()
    {
        var missingClass = Transpile("var value = new Missing()", TargetLanguage.Bash);
        Assert.False(missingClass.Success);
        Assert.Contains(missingClass.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1034");

        var missingValue = Transpile("enum State { Ready }\nprintln(State.Missing)", TargetLanguage.Bash);
        Assert.False(missingValue.Success);
        Assert.Contains(missingValue.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1031");
    }

    [Fact]
    public void ExplicitEnumConstructor_IsLowered()
    {
        const string source = """
            enum Status {
                Good("ok"), Bad("bad");
                new(string text) { this.text = text }
                string() -> this.text
            }
            println(string(Status.Good))
            """;
        var result = Transpile(source, TargetLanguage.Bash);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("status_new", result.EmittedCode);
        Assert.DoesNotContain("status_good_new", result.EmittedCode);
        Assert.DoesNotContain("status_bad_new", result.EmittedCode);
    }

    [Fact]
    public void TypedMethodReturn_IsValidated()
    {
        var result = Transpile("class Wrong { int value() { return \"no\" } }", TargetLanguage.Bash);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1024");
    }

    private static TranspileResult Transpile(string source, TargetLanguage target) =>
        new Transpiler().Transpile(new TranspileRequest
        {
            SourcePath = "objects.sushi",
            SourceText = source,
            TargetLanguage = target
        });
}
