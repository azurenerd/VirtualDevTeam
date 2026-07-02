using System.Text;
using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Scalar-only commit/PR trailers for strategy attribution. Each value must be a
/// single-line scalar; multi-line values are rejected so trailers stay parseable.
/// </summary>
public static class StrategyTrailers
{
    /// <summary>Canonical trailer keys — deliberately few and stable.</summary>
    public const string StrategyKey = "Strategy";
    public const string RunIdKey = "Strategy-Run-Id";
    public const string RecordIdKey = "Strategy-Record-Id";
    public const string TieBreakKey = "Strategy-Tie-Break";

    private static readonly Regex UnsafeScalar = new(@"[\r\n]", RegexOptions.Compiled);

    public static string BuildBlock(IEnumerable<KeyValuePair<string, string>> trailers)
    {
        var sb = new StringBuilder();
        foreach (var kv in trailers)
        {
            var key = kv.Key?.Trim() ?? "";
            var val = kv.Value?.Trim() ?? "";
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(val)) continue;
            if (UnsafeScalar.IsMatch(key) || UnsafeScalar.IsMatch(val))
                throw new ArgumentException($"Trailer value is not scalar: {key}");
            sb.Append(key).Append(": ").Append(val).Append('\n');
        }
        return sb.ToString();
    }

    public static string Append(string body, IEnumerable<KeyValuePair<string, string>> trailers)
    {
        var block = BuildBlock(trailers);
        if (block.Length == 0) return body;
        if (string.IsNullOrEmpty(body)) return block;
        var separator = body.EndsWith("\n\n") ? "" : body.EndsWith('\n') ? "\n" : "\n\n";
        return body + separator + block;
    }
}
