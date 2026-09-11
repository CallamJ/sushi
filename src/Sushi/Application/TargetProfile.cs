namespace Sushi.Application;

using System.Runtime.InteropServices;

/// <summary>Describes the shell and operating system a script is emitted for.</summary>
public enum TargetPlatform
{
    Linux,
    Macos,
    Windows
}

public sealed record TargetProfile(TargetLanguage Shell, TargetPlatform Platform)
{
    public string ShellName => Shell switch
    {
        TargetLanguage.Bash => "bash",
        TargetLanguage.Zsh => "zsh",
        _ => "powershell"
    };

    public string PlatformName => Platform switch
    {
        TargetPlatform.Linux => "linux",
        TargetPlatform.Macos => "macos",
        _ => "windows"
    };

    public string Id => $"{ShellName}-{PlatformName}";

    public string FileExtension => Shell switch
    {
        TargetLanguage.Bash => ".sh",
        TargetLanguage.Zsh => ".zsh",
        _ => ".ps1"
    };

    public static TargetProfile Host()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new(TargetLanguage.Powershell7, TargetPlatform.Windows);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new(TargetLanguage.Zsh, TargetPlatform.Macos);
        return new(TargetLanguage.Bash, TargetPlatform.Linux);
    }

    public static bool TryParse(string? value, out TargetProfile profile)
    {
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value))
        {
            profile = Host();
            return true;
        }

        profile = value.ToLowerInvariant() switch
        {
            "bash-linux" => new(TargetLanguage.Bash, TargetPlatform.Linux),
            "bash-macos" => new(TargetLanguage.Bash, TargetPlatform.Macos),
            "zsh-linux" => new(TargetLanguage.Zsh, TargetPlatform.Linux),
            "zsh-macos" => new(TargetLanguage.Zsh, TargetPlatform.Macos),
            "powershell-linux" => new(TargetLanguage.Powershell7, TargetPlatform.Linux),
            "powershell-macos" => new(TargetLanguage.Powershell7, TargetPlatform.Macos),
            "powershell-windows" => new(TargetLanguage.Powershell7, TargetPlatform.Windows),
            _ => null!
        };
        return profile is not null;
    }

    public static string AcceptedValues => "auto, bash-linux, bash-macos, zsh-linux, zsh-macos, powershell-linux, powershell-macos, powershell-windows";
}
