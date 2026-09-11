namespace Sushi.Tests.Transpilation;

using Sushi.Application;
using Sushi.Transpilation;
using Xunit;

public sealed class TargetIntrinsicTests
{
    [Fact]
    public void TargetCondition_IsFoldedBeforeBashEmission()
    {
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourcePath = "target.sushi",
            SourceText = "if (std.target.platform() == \"windows\") { println(\"windows\") } else { println(\"unix\") }",
            TargetLanguage = TargetLanguage.Bash,
            TargetProfile = new TargetProfile(TargetLanguage.Bash, TargetPlatform.Linux)
        });

        Assert.True(result.Success);
        Assert.Contains("unix", result.EmittedCode);
        Assert.DoesNotContain("windows", result.EmittedCode);
        Assert.DoesNotContain("std.target", result.EmittedCode);
    }
}
