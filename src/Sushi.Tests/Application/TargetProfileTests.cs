namespace Sushi.Tests.Application;

using Sushi.Application;
using Xunit;

public sealed class TargetProfileTests
{
    [Theory]
    [InlineData("bash-linux", TargetLanguage.Bash, TargetPlatform.Linux, ".sh")]
    [InlineData("zsh-macos", TargetLanguage.Zsh, TargetPlatform.Macos, ".zsh")]
    [InlineData("powershell-windows", TargetLanguage.Powershell7, TargetPlatform.Windows, ".ps1")]
    public void TryParse_AcceptsSupportedProfiles(string text, TargetLanguage shell, TargetPlatform platform, string extension)
    {
        Assert.True(TargetProfile.TryParse(text, out var target));
        Assert.Equal(shell, target.Shell);
        Assert.Equal(platform, target.Platform);
        Assert.Equal(text, target.Id);
        Assert.Equal(extension, target.FileExtension);
    }

    [Fact]
    public void TryParse_RejectsAmbiguousShellOnlyTarget() =>
        Assert.False(TargetProfile.TryParse("Bash", out _));
}
