# Inline PR Review Comments Plan

## Problem

Currently, when reviewers (PM, Architect, SE Leader) request changes on a PR, they post **all feedback as a single conversation comment** on the PR's Conversation tab. This means file-specific feedback (e.g., "line 42 of Dashboard.razor needs X") gets buried in a wall of text instead of appearing inline on the actual file in the "Files Changed" tab — the way a human reviewer would do it.

The Architect and SE Leader agents already have **partial infrastructure** for inline comments via `CreatePullRequestReviewWithCommentsAsync` and `SubmitInlineReviewCommentsAsync`, but:
1. **PM agent doesn't use inline comments at all** — uses `AddPullRequestCommentAsync` only
2. **Test Engineer doesn't use inline comments** — uses `AddPullRequestCommentAsync` only
3. **`RequestChangesAsync`** in PullRequestWorkflow only posts a conversation comment — doesn't submit a GitHub Review with inline file comments
4. The existing inline comment flow on Architect/SE sometimes fails silently and falls back to body-only

## Desired Behavior

When a reviewer requests changes:
1. **File-specific feedback → Inline comments** on the actual file lines in the "Files Changed" tab (GitHub Review Comments)
2. **General feedback (no specific file) → Conversation comment** as before
3. **Always post a summary in the Conversation tab** with CHANGES REQUESTED header, so the engineer and dashboard can track it. This summary should be brief when inline comments carry the detail.
4. All reviewers (Architect, PM, SE Leader, Test Engineer) should use this pattern consistently.

## Current State Audit

| Agent | Uses `CreatePullRequestReviewWithCommentsAsync`? | Uses `AddPullRequestCommentAsync`? | Structured JSON review? |
|-------|--------------------------------------------------|-----------------------------------|------------------------|
| **Architect** | ✅ Yes (`SubmitInlineReviewCommentsAsync`) | ✅ Yes (body comment + separate inline) | ✅ Yes (JSON with `comments` array) |
| **SE Leader** | ✅ Yes (`SubmitInlineReviewCommentsAsync`) | ✅ Yes | ✅ Yes (`TryParseStructuredSeReview`) |
| **PM** | ❌ No | ✅ Yes (only) | ❌ No — uses `EvaluatePrAlignmentWithVerdictAsync` (text-based verdict) |
| **Test Engineer** | ❌ No | ✅ Yes (only) | ❌ No — posts test results as conversation comment |

### Existing Infrastructure (ready to use)

- `GitHubService.CreatePullRequestReviewWithCommentsAsync()` — submits GitHub Review with inline `DraftPullRequestReviewComment` items, maps line numbers to diff positions via `DiffPositionMapper`
- `InlineReviewComment` record — `FilePath`, `Line`, `Body`
- `StructuredReviewResult` — `Verdict`, `Summary`, `RiskLevel`, `Comments` (list of `InlineReviewComment`)
- `DiffPositionMapper.MapLineToPosition()` — maps source line number to diff hunk position
- `GetPullRequestFilesWithPatchAsync()` — gets PR file diffs for position mapping
- Config: `Review.EnableInlineComments` (default: true), `Review.MaxInlineCommentsPerReview` (default: 15)

## Implementation Plan

### Step 1: Update RequestChangesAsync to support inline comments

Modify `PullRequestWorkflow.RequestChangesAsync` to accept optional inline comments:

```csharp
public async Task RequestChangesAsync(
    int prNumber, string reviewerAgent, string details,
    IReadOnlyList<InlineReviewComment>? inlineComments = null,
    CancellationToken ct = default)
{
    // If we have inline comments, use the GitHub Review API
    if (inlineComments is not null && inlineComments.Count > 0)
    {
        var summaryBody = $"**[{reviewerAgent}] CHANGES REQUESTED**\n\n{details}";
        await _github.CreatePullRequestReviewWithCommentsAsync(
            prNumber, summaryBody, "REQUEST_CHANGES", inlineComments, ct: ct);
    }
    else
    {
        // No inline comments — use simple conversation comment as before
        var comment = $"**[{reviewerAgent}] CHANGES REQUESTED**\n\n{details}";
        await _github.AddPullRequestCommentAsync(prNumber, comment, ct);
    }
}
```

### Step 2: Update PM review to produce structured reviews with inline comments

Modify `ProgramManagerAgent.EvaluatePrAlignmentWithVerdictAsync` to return `StructuredReviewResult` instead of `(bool approved, string reviewBody)`. Update the AI prompt to output JSON with file-specific comments:

```json
{
  "verdict": "REQUEST_CHANGES",
  "summary": "Brief overview of issues found",
  "riskLevel": "MEDIUM",
  "comments": [
    { "file": "ReportingDashboard/Components/Pages/Dashboard.razor", "line": 42, "body": "Missing null check on Data property" },
    { "file": "ReportingDashboard/Data/DashboardData.cs", "line": 15, "body": "Property should be required" }
  ]
}
```

Then wire it up the same way the Architect does:
- If `comments` has entries AND `EnableInlineComments` → submit via `CreatePullRequestReviewWithCommentsAsync`
- Post a brief summary in conversation: "CHANGES REQUESTED — see inline comments on 3 files"
- If no file-specific comments, post full details in conversation as before

### Step 3: Update Test Engineer review to use inline comments

The TE has a different pattern — it doesn't "review" code the same way. But when it finds test failures related to specific files, it should:
- Post inline comments on the source files that caused test failures (if identifiable from stack traces)
- Post test result summary in conversation

This is lower priority since TE mainly posts test results, not code critique.

### Step 4: Clean up Architect/SE Leader to use consistent pattern

The Architect already has inline comments but posts them as a **separate** review from the conversation comment. This creates two entries:
1. A conversation comment with the full review body
2. A GitHub Review with inline comments

Consolidate these into a **single GitHub Review** that has:
- The review body (shown in Conversation tab)
- Inline comments (shown in Files Changed tab)

Currently (ArchitectAgent lines 974-981):
```csharp
// Posts conversation comment
await _github.AddPullRequestCommentAsync(pr.Number, approvalComment, ct);
// THEN posts separate GitHub Review with inline comments
await SubmitInlineReviewCommentsAsync(pr.Number, reviewResult, "APPROVE", ct);
```

Should become:
```csharp
// Single GitHub Review with body + inline comments
if (reviewResult.Comments.Count > 0 && _config.Review.EnableInlineComments)
{
    await _github.CreatePullRequestReviewWithCommentsAsync(
        pr.Number, approvalComment, "APPROVE", reviewResult.Comments, ct: ct);
}
else
{
    await _github.AddPullRequestCommentAsync(pr.Number, approvalComment, ct);
}
```

### Step 5: Improve AI prompt for inline comment quality

Update review prompts to:
1. **Include the PR diff** in the prompt so the AI can reference exact line numbers
2. **Require file path + line number** for each issue found
3. **Separate general vs file-specific feedback** — general goes in summary, file-specific goes in comments array
4. **Use the actual file paths from the diff** (not guessed paths)

The Architect already does this well (lines 1392-1413). Replicate the same JSON structure for PM and TE.

### Step 6: Summary comment when inline comments exist

When a reviewer uses inline comments, the conversation comment should be shorter:

**Current behavior** (PM):
```
**[ProgramManager] CHANGES REQUESTED**

[Full 500-word review with all file-specific issues mixed in]
```

**New behavior**:
```
**[ProgramManager] CHANGES REQUESTED**

Found 4 issues across 3 files — see inline comments on the Files Changed tab.

**Summary:** The data model is missing validation and the dashboard page has accessibility gaps. Please address the inline comments and re-request review.
```

The detailed per-file feedback appears as inline comments on the actual code lines.

## Files to Modify

| File | Changes |
|------|---------|
| `src/VirtualDevTeam.Core/GitHub/PullRequestWorkflow.cs` | Update `RequestChangesAsync` to accept and submit inline comments |
| `src/VirtualDevTeam.Agents/ProgramManagerAgent.cs` | Switch to structured JSON review, produce `InlineReviewComment`s, use `CreatePullRequestReviewWithCommentsAsync` for REQUEST_CHANGES |
| `src/VirtualDevTeam.Agents/ArchitectAgent.cs` | Consolidate dual comment+review into single GitHub Review |
| `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs` | Already has it — verify consistency with new pattern |
| `src/VirtualDevTeam.Agents/TestEngineerAgent.cs` | Add inline comments for test failure file references (lower priority) |
| `prompts/` | Update review prompts to require structured JSON with file/line/body |

## Config (already exists)

```json
{
  "Review": {
    "EnableInlineComments": true,
    "MaxInlineCommentsPerReview": 15
  }
}
```

No new config needed — the existing `EnableInlineComments` flag already controls this behavior.

## Priority Order

1. **Step 4** (Architect consolidation) — quick win, infrastructure already works
2. **Step 1** (RequestChangesAsync) — shared method update
3. **Step 2** (PM inline comments) — biggest gap, most visible improvement
4. **Step 6** (Summary comments) — UX improvement
5. **Step 5** (Prompt improvements) — quality improvement
6. **Step 3** (TE inline comments) — lower priority, different review pattern
