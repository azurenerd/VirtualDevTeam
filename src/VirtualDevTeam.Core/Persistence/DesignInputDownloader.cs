using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Persistence;

/// <summary>
/// Downloads HTTP(S) URLs containing design input (specs, mockups, docs),
/// extracts text from common formats (.docx, .pdf, .html), and caches results
/// in a local .design-input-cache/ directory.
/// </summary>
public sealed class DesignInputDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DesignInputDownloader> _logger;
    private readonly string _cacheDir;

    public DesignInputDownloader(
        HttpClient httpClient,
        ILogger<DesignInputDownloader> logger,
        string workspaceRoot)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cacheDir = Path.Combine(workspaceRoot, ".design-input-cache");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Downloads and extracts text content from a URL, caching the result.
    /// Supports .html, .htm, .txt, .md, and plain text content types.
    /// For .docx/.pdf, stores raw bytes and returns a placeholder indicating manual review needed.
    /// </summary>
    public async Task<string> DownloadAndExtractAsync(
        string url,
        Dictionary<string, string>? authHeaders = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var cacheKey = ComputeCacheKey(url);
        var cachePath = Path.Combine(_cacheDir, $"{cacheKey}.txt");

        // Return cached content if available
        if (File.Exists(cachePath))
        {
            _logger.LogDebug("Design input cache hit for {Url}", url);
            return await File.ReadAllTextAsync(cachePath, ct);
        }

        _logger.LogInformation("Downloading design input from {Url}", url);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (authHeaders is not null)
            {
                foreach (var (key, value) in authHeaders)
                    request.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var ext = GetExtensionFromUrl(url);

            string extractedText;

            if (IsHtmlContent(contentType, ext))
            {
                var html = await response.Content.ReadAsStringAsync(ct);
                extractedText = ExtractTextFromHtml(html);
            }
            else if (IsPlainTextContent(contentType, ext))
            {
                extractedText = await response.Content.ReadAsStringAsync(ct);
            }
            else
            {
                // Binary format (.docx, .pdf, etc.) — save raw bytes and return placeholder
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                var rawPath = Path.Combine(_cacheDir, $"{cacheKey}{ext}");
                await File.WriteAllBytesAsync(rawPath, bytes, ct);
                extractedText = $"[Binary design document downloaded: {url}]\n" +
                    $"Format: {ext} ({contentType})\n" +
                    $"Cached at: {rawPath}\n" +
                    "Note: Text extraction for this format requires manual review.";
            }

            // Cache the extracted text
            await File.WriteAllTextAsync(cachePath, extractedText, ct);
            _logger.LogInformation("Cached design input ({Length} chars) from {Url}", extractedText.Length, url);

            return extractedText;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download design input from {Url}", url);
            return $"[Failed to download design input: {url}] Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Downloads all configured URLs and returns combined extracted text.
    /// </summary>
    public async Task<string> DownloadAllAsync(
        IEnumerable<string> urls,
        Dictionary<string, string>? authHeaders = null,
        CancellationToken ct = default)
    {
        var results = new StringBuilder();
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = await DownloadAndExtractAsync(url, authHeaders, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                results.AppendLine($"## Design Input: {url}");
                results.AppendLine(text);
                results.AppendLine();
            }
        }
        return results.ToString();
    }

    private static string ExtractTextFromHtml(string html)
    {
        // Strip HTML tags and decode entities for a best-effort text extraction
        var noTags = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        noTags = Regex.Replace(noTags, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        noTags = Regex.Replace(noTags, @"<[^>]+>", " ");
        noTags = System.Net.WebUtility.HtmlDecode(noTags);
        // Collapse whitespace
        noTags = Regex.Replace(noTags, @"\s+", " ").Trim();
        return noTags;
    }

    private static bool IsHtmlContent(string contentType, string ext) =>
        contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
        ext is ".html" or ".htm";

    private static bool IsPlainTextContent(string contentType, string ext) =>
        contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("text/markdown", StringComparison.OrdinalIgnoreCase) ||
        ext is ".txt" or ".md" or ".markdown";

    private static string GetExtensionFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var ext = Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? ".html" : ext.ToLowerInvariant();
        }
        catch
        {
            return ".html";
        }
    }

    private static string ComputeCacheKey(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
