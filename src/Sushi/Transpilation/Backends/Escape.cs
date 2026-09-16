namespace Sushi.Transpilation.Backends;

public static class Escape
{
    public static string PosixSingleQuoted(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    public static string PowerShellSingleQuoted(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }
}
