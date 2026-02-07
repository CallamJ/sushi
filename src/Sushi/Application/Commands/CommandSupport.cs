namespace Sushi.Application.Commands;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sushi.Application.Console;
using Sushi.Transpilation;

internal static class CommandSupport
{
    public static TargetLanguage GetDefaultTarget()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return TargetLanguage.Powershell7;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return TargetLanguage.Zsh;
        }

        return TargetLanguage.Bash;
    }

    public static string GetOutputPath(string inputPath, TargetLanguage target)
    {
        var extension = target switch
        {
            TargetLanguage.Bash => ".sh",
            TargetLanguage.Zsh => ".zsh",
            _ => ".ps1"
        };

        var directory = Path.GetDirectoryName(inputPath) ?? ".";
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, fileNameWithoutExtension + extension);
    }

    public static bool TryReadSourceFile(string filePath, out string sourceText)
    {
        sourceText = string.Empty;
        if (!File.Exists(filePath))
        {
            System.Console.Error.WriteLine($"Input file not found: {filePath}");
            return false;
        }

        try
        {
            sourceText = File.ReadAllText(filePath);
            return true;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Failed to read input file: {ex.Message}");
            return false;
        }
    }

    public static TranspileResult Transpile(string filePath, TargetLanguage target, string sourceText)
    {
        var transpiler = new Transpiler();
        return transpiler.Transpile(new TranspileRequest
        {
            SourceText = sourceText,
            SourcePath = filePath,
            TargetLanguage = target
        });
    }

    public static bool TryWriteOutput(string outputPath, string code)
    {
        try
        {
            File.WriteAllText(outputPath, code);
            return true;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Failed to write output file: {ex.Message}");
            return false;
        }
    }

    public static int ExecuteScript(TargetLanguage target, string scriptPath, IReadOnlyList<string> scriptArgs, string? workingDirectory)
    {
        foreach (var candidate in GetRunnerCandidates(target))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = candidate.Executable,
                UseShellExecute = false,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Directory.GetCurrentDirectory()
                    : workingDirectory
            };

            foreach (var arg in candidate.PrefixArguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.ArgumentList.Add(scriptPath);
            foreach (var arg in scriptArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            try
            {
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    System.Console.Error.WriteLine($"Failed to start runtime '{candidate.Executable}'.");
                    return 1;
                }

                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Win32Exception ex) when (IsMissingExecutable(ex))
            {
                continue;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"Failed to execute transpiled script: {ex.Message}");
                return 1;
            }
        }

        System.Console.Error.WriteLine($"No compatible runtime found for target '{target}'.");
        return 1;
    }

    public static void PrintDiagnostics(TranspileResult result, bool verbose)
    {
        DiagnosticPrinter.Print(result.Diagnostics, includeWarnings: verbose);
    }

    private static bool IsMissingExecutable(Win32Exception ex)
    {
        return ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3;
    }

    private static IEnumerable<RunnerCandidate> GetRunnerCandidates(TargetLanguage target)
    {
        return target switch
        {
            TargetLanguage.Bash => new[]
            {
                new RunnerCandidate("bash", Array.Empty<string>())
            },
            TargetLanguage.Zsh => new[]
            {
                new RunnerCandidate("zsh", Array.Empty<string>())
            },
            _ => new[]
            {
                new RunnerCandidate("pwsh", new[] { "-NoLogo", "-NoProfile", "-File" }),
                new RunnerCandidate("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" })
            }
        };
    }

    private sealed record RunnerCandidate(string Executable, string[] PrefixArguments);
}
