using System.Text;
using System.Text.Json;

namespace CSharpMcp.Analysis;

internal sealed partial class RoslynAnalysisService
{
    /// <summary>
    /// Creates an opaque cursor tied to a stable result fingerprint and zero-based offset.
    /// </summary>
    private static string CreateCursor(string kind, string fingerprint, int offset)
    {
        var json = JsonSerializer.Serialize(new PaginationCursor(kind, fingerprint, offset));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Validates and decodes an opaque cursor, rejecting stale or cross-tool values.
    /// </summary>
    private static int ParseCursor(string? cursor, string expectedKind, string expectedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parsed = JsonSerializer.Deserialize<PaginationCursor>(Convert.FromBase64String(base64));
            if (parsed is null || parsed.Offset < 0 ||
                !parsed.Kind.Equals(expectedKind, StringComparison.Ordinal) ||
                !parsed.Fingerprint.Equals(expectedFingerprint, StringComparison.Ordinal))
            {
                throw new ArgumentException("The pagination cursor is stale or belongs to a different query.", nameof(cursor));
            }

            return parsed.Offset;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The pagination cursor is invalid.", nameof(cursor), exception);
        }
    }

    private sealed record PaginationCursor(string Kind, string Fingerprint, int Offset);
}
