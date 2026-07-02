using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Centralized URL builder for PR / Issue / file references shown in the dashboard.
///
/// By default (driven by <c>Dashboard:InternalNavigationDefault</c>, default <c>true</c>) every
/// reference resolves to an internal route under <c>/repository/...</c> so the operator stays on
/// the dashboard.  When the flag is flipped off the helper falls back to the platform URL —
/// useful for operators who prefer GitHub / Azure DevOps directly.
///
/// This is the single seam every Razor surface should call instead of hardcoding
/// <c>pr.Url</c> / <c>issue.Url</c> / <c>github.com/...</c> in markup.  Companion component
/// <see cref="VirtualDevTeam.Dashboard.Components.Shared.PlatformLinkButton"/> is what the
/// in-dashboard PR / Issue / File detail pages render in the top-right to give the user an
/// explicit "Open in GitHub ↗" / "Open in Azure DevOps ↗" escape hatch.
/// </summary>
public interface IPlatformLinkService
{
    /// <summary>True when internal navigation is the default for PR / Issue / file links.</summary>
    bool InternalNavigationDefault { get; }

    /// <summary>"GitHub" or "Azure DevOps" — used in button labels and tooltips.</summary>
    string PlatformDisplayName { get; }

    /// <summary>
    /// Build a link target for a pull request.  Returns the internal
    /// <c>/repository/pull-request/{N}</c> route when internal nav is enabled, otherwise the
    /// supplied <paramref name="platformUrl"/>.  Falls back to the internal route if
    /// <paramref name="platformUrl"/> is null/empty even when internal nav is disabled.
    /// </summary>
    string BuildPullRequestUrl(int prNumber, string? platformUrl = null);

    /// <summary>Same as <see cref="BuildPullRequestUrl(int, string?)"/> but for issues / work items.</summary>
    string BuildIssueUrl(int issueNumber, string? platformUrl = null);

    /// <summary>
    /// Build a link target for a file at a specific branch / path.  Returns the internal
    /// <c>/repository/files/{path}</c> route (the page resolves branch from query / config) when
    /// internal nav is enabled.  When disabled returns <paramref name="platformUrl"/>.
    /// </summary>
    string BuildFileUrl(string path, string? branch = null, string? platformUrl = null);

    /// <summary>True when the link returned by Build* opens an internal dashboard route.</summary>
    bool IsInternal(string? url);

    /// <summary>
    /// Convenience: when an internal URL has been returned, callers should NOT add
    /// <c>target="_blank"</c>.  Returns <c>"_blank"</c> for platform URLs and <c>null</c> for
    /// internal routes so Razor can splat it onto the anchor tag.
    /// </summary>
    string? TargetForUrl(string? url);
}
