namespace Wpk.Cli.Platform;

public static class ExecutableLocator
{
    public static string? Find(string name)
    {
        // Do not search the current directory implicitly.
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var directory = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(directory)) continue;
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return path;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
