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
    [InlineData(TargetLanguage.Powershell7)]
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
}
