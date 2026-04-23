using System.Text;
using System.Web;

namespace AgentSquad.Dashboard.Tests.Helpers;

/// <summary>
/// Generates a self-contained HTML report from scenario test results.
/// Embeds animated GIFs inline, shows pass/fail badges, duration, and error messages.
/// </summary>
public static class GifReportGenerator
{
    public static string Generate(List<ScenarioResult> results, string outputDir)
    {
        var passed = results.Count(r => r.Passed);
        var failed = results.Count - passed;
        var totalDuration = TimeSpan.FromMilliseconds(results.Sum(r => r.DurationMs));

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang='en'>");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset='utf-8'/>");
        sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1'/>");
        sb.AppendLine("<title>AgentSquad UI Test Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Header
        sb.AppendLine("<div class='header'>");
        sb.AppendLine("<h1>🎬 AgentSquad UI Test Report</h1>");
        sb.AppendLine($"<div class='summary'>");
        sb.AppendLine($"<span class='badge pass'>{passed} Passed</span>");
        if (failed > 0)
            sb.AppendLine($"<span class='badge fail'>{failed} Failed</span>");
        sb.AppendLine($"<span class='badge info'>⏱ {totalDuration:mm\\:ss}</span>");
        sb.AppendLine($"<span class='badge info'>📅 {DateTime.Now:yyyy-MM-dd HH:mm}</span>");
        sb.AppendLine("</div></div>");

        // Scenarios
        sb.AppendLine("<div class='scenarios'>");
        foreach (var r in results)
        {
            var statusClass = r.Passed ? "pass" : "fail";
            var statusIcon = r.Passed ? "✅" : "❌";
            var duration = TimeSpan.FromMilliseconds(r.DurationMs);

            sb.AppendLine($"<div class='scenario {statusClass}'>");
            sb.AppendLine($"<div class='scenario-header'>");
            sb.AppendLine($"<span class='status-icon'>{statusIcon}</span>");
            sb.AppendLine($"<h2>{HttpUtility.HtmlEncode(r.Id)} — {HttpUtility.HtmlEncode(r.Name)}</h2>");
            sb.AppendLine($"<span class='duration'>⏱ {duration:mm\\:ss\\.f}</span>");
            sb.AppendLine("</div>");

            if (!r.Passed && r.ErrorMessage is not null)
            {
                sb.AppendLine($"<pre class='error'>{HttpUtility.HtmlEncode(r.ErrorMessage)}</pre>");
            }

            // GIF
            if (r.GifPath is not null && File.Exists(r.GifPath))
            {
                var relativePath = Path.GetRelativePath(outputDir, r.GifPath).Replace('\\', '/');
                sb.AppendLine($"<div class='gif-container'>");
                sb.AppendLine($"<img src='{relativePath}' alt='{HttpUtility.HtmlEncode(r.Name)}' loading='lazy'/>");
                sb.AppendLine("</div>");
            }

            // Screenshots
            if (r.ScreenshotPaths is not null)
            {
                var shots = r.ScreenshotPaths.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (shots.Length > 0)
                {
                    sb.AppendLine("<div class='screenshots'>");
                    foreach (var shot in shots)
                    {
                        if (File.Exists(shot))
                        {
                            var rel = Path.GetRelativePath(outputDir, shot).Replace('\\', '/');
                            sb.AppendLine($"<img src='{rel}' alt='screenshot' class='screenshot' loading='lazy'/>");
                        }
                    }
                    sb.AppendLine("</div>");
                }
            }

            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");

        sb.AppendLine("</body></html>");

        var reportPath = Path.Combine(outputDir, "index.html");
        File.WriteAllText(reportPath, sb.ToString());
        return reportPath;
    }

    private const string Css = """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
               background: #0d1117; color: #e6edf3; padding: 24px; }
        .header { text-align: center; margin-bottom: 32px; }
        .header h1 { font-size: 28px; margin-bottom: 12px; }
        .summary { display: flex; gap: 12px; justify-content: center; flex-wrap: wrap; }
        .badge { padding: 6px 16px; border-radius: 20px; font-size: 14px; font-weight: 600; }
        .badge.pass { background: #238636; color: #fff; }
        .badge.fail { background: #da3633; color: #fff; }
        .badge.info { background: #30363d; color: #8b949e; }
        .scenarios { max-width: 1200px; margin: 0 auto; display: flex; flex-direction: column; gap: 24px; }
        .scenario { background: #161b22; border: 1px solid #30363d; border-radius: 12px;
                    padding: 20px; overflow: hidden; }
        .scenario.pass { border-left: 4px solid #238636; }
        .scenario.fail { border-left: 4px solid #da3633; }
        .scenario-header { display: flex; align-items: center; gap: 12px; margin-bottom: 16px; }
        .scenario-header h2 { font-size: 18px; flex: 1; }
        .status-icon { font-size: 24px; }
        .duration { color: #8b949e; font-size: 14px; white-space: nowrap; }
        .error { background: #1c0a0a; border: 1px solid #da3633; border-radius: 8px;
                padding: 12px; font-size: 13px; color: #f85149; overflow-x: auto;
                margin-bottom: 16px; white-space: pre-wrap; word-break: break-word; }
        .gif-container { text-align: center; }
        .gif-container img { max-width: 100%; border-radius: 8px; border: 1px solid #30363d; }
        .screenshots { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 12px; }
        .screenshot { width: 280px; border-radius: 6px; border: 1px solid #30363d; cursor: pointer; }
        .screenshot:hover { border-color: #58a6ff; }
        """;
}
