using System.Security.Cryptography;
using System.Text;

namespace Wpk.Core;

public static class Identity
{
    public static string RecordId(string source, params string?[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts)
            .Normalize(NormalizationForm.FormKC).ToUpperInvariant()));
        return $"{source}:{Convert.ToHexStringLower(bytes)[..20]}";
    }

    public static string Text(string? text) => string.Concat((text ?? "")
        .Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    public static string? PathKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(expanded)) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    public static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b) && StringComparer.OrdinalIgnoreCase.Equals(a, b);
}
