using AgentSquad.Core.DevPlatform.Models;

namespace AgentSquad.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Maps Azure DevOps REST API DTOs to platform-agnostic models.
/// </summary>
internal static class AdoModelMapper
{
    public static PlatformPullRequest ToPlatform(AdoPullRequest pr, string orgUrl, string project)
    {
        var state = pr.Status switch
        {
            "completed" => "closed",
            "abandoned" => "closed",
            _ => "open"
        };

        var isMerged = pr.Status == "completed";
        var headBranch = StripRefsPrefix(pr.SourceBranch);
        var baseBranch = StripRefsPrefix(pr.TargetBranch);

        var webUrl = $"{orgUrl}{project}/_git/{Uri.EscapeDataString(pr.SourceBranch.Split('/').Last())}/pullrequest/{pr.PullRequestId}";
        if (!string.IsNullOrEmpty(pr.Url))
        {
            // Construct proper web URL from org/project
            webUrl = $"https://dev.azure.com/{orgUrl.TrimEnd('/')}/{project}/_git/pullrequest/{pr.PullRequestId}";
        }

        return new PlatformPullRequest
        {
            Number = pr.PullRequestId,
            Title = pr.Title,
            Body = pr.Description ?? "",
            State = state,
            HeadBranch = headBranch,
            HeadSha = pr.LastMergeSourceCommit?.CommitId ?? "",
            BaseBranch = baseBranch,
            AssignedAgent = pr.CreatedBy?.DisplayName,
            Url = webUrl,
            CreatedAt = pr.CreationDate,
            UpdatedAt = pr.ClosedDate,
            MergedAt = isMerged ? pr.ClosedDate : null,
            Labels = pr.Labels?.Where(l => l.Active).Select(l => l.Name).ToList() ?? new(),
            MergeableState = pr.MergeStatus
        };
    }

    public static PlatformWorkItem ToPlatform(AdoWorkItem wi, string orgUrl, string project)
    {
        var fields = wi.Fields;

        var title = GetFieldString(fields, "System.Title");
        var description = GetFieldString(fields, "System.Description") ?? "";
        var state = GetFieldString(fields, "System.State") ?? "New";
        var assignedTo = GetFieldString(fields, "System.AssignedTo");
        var createdDate = GetFieldDateTime(fields, "System.CreatedDate") ?? DateTime.UtcNow;
        var changedDate = GetFieldDateTime(fields, "System.ChangedDate");
        var closedDate = GetFieldDateTime(fields, "Microsoft.VSTS.Common.ClosedDate");
        var commentCount = GetFieldInt(fields, "System.CommentCount");
        var workItemType = GetFieldString(fields, "System.WorkItemType") ?? "Task";
        var tags = GetFieldString(fields, "System.Tags") ?? "";

        // ADO "State" mapping to open/closed
        var normalizedState = state.ToLowerInvariant() switch
        {
            "new" or "active" or "design" => "open",
            "resolved" or "closed" or "removed" or "done" => "closed",
            _ => "open"
        };

        var webUrl = $"https://dev.azure.com/{orgUrl.TrimEnd('/')}/{project}/_workitems/edit/{wi.Id}";
        var labels = string.IsNullOrEmpty(tags)
            ? new List<string>()
            : tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // Extract display name from ADO identity field (format: "Display Name <unique@name>")
        string? agentName = null;
        if (!string.IsNullOrEmpty(assignedTo))
        {
            var bracketIdx = assignedTo.IndexOf('<');
            agentName = bracketIdx > 0 ? assignedTo[..bracketIdx].Trim() : assignedTo;
        }

        return new PlatformWorkItem
        {
            PlatformId = wi.Id,
            Number = wi.Id, // ADO uses ID as the display number
            Title = title ?? "",
            Body = description,
            State = normalizedState,
            AssignedAgent = agentName,
            Url = webUrl,
            CreatedAt = createdDate,
            UpdatedAt = changedDate,
            ClosedAt = closedDate,
            CommentCount = commentCount,
            Labels = labels,
            WorkItemType = workItemType
        };
    }

    public static PlatformComment ToPlatform(AdoPrComment comment)
    {
        return new PlatformComment
        {
            Id = comment.Id,
            Author = comment.Author?.DisplayName ?? "",
            Body = comment.Content,
            CreatedAt = comment.PublishedDate
        };
    }

    public static PlatformReviewThread ToPlatform(AdoPrThread thread)
    {
        var firstComment = thread.Comments.FirstOrDefault(c => c.CommentType != "system");
        return new PlatformReviewThread
        {
            Id = thread.Id,
            ThreadId = thread.Id.ToString(),
            FilePath = thread.ThreadContext?.FilePath ?? "",
            Line = thread.ThreadContext?.RightFileStart?.Line,
            Body = firstComment?.Content ?? "",
            Author = firstComment?.Author?.DisplayName ?? "",
            IsResolved = thread.Status is "fixed" or "closed" or "byDesign" or "wontFix",
            CreatedAt = thread.PublishedDate
        };
    }

    private static string? GetFieldString(Dictionary<string, object?> fields, string key)
    {
        if (fields.TryGetValue(key, out var val) && val is not null)
        {
            // ADO identity fields come as objects with displayName
            if (val is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                    return je.GetString();
                if (je.ValueKind == System.Text.Json.JsonValueKind.Object && je.TryGetProperty("displayName", out var dn))
                    return dn.GetString();
            }
            return val.ToString();
        }
        return null;
    }

    private static DateTime? GetFieldDateTime(Dictionary<string, object?> fields, string key)
    {
        if (fields.TryGetValue(key, out var val) && val is not null)
        {
            if (val is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (DateTime.TryParse(je.GetString(), out var dt))
                    return dt;
            }
            if (val is DateTime dt2) return dt2;
        }
        return null;
    }

    private static int GetFieldInt(Dictionary<string, object?> fields, string key)
    {
        if (fields.TryGetValue(key, out var val) && val is not null)
        {
            if (val is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number)
                return je.GetInt32();
            if (val is int i) return i;
        }
        return 0;
    }

    private static string StripRefsPrefix(string refName)
    {
        if (refName.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            return refName["refs/heads/".Length..];
        return refName;
    }
}
