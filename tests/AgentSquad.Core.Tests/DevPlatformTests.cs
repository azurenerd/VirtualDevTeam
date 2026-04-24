using AgentSquad.Core.DevPlatform.Auth;
using AgentSquad.Core.DevPlatform.Config;
using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.DevPlatform.Providers.GitHub;
using AgentSquad.Core.GitHub.Models;

namespace AgentSquad.Core.Tests;

public class DevPlatformTests
{
    // ──────────────────── Model Mapping ────────────────────

    [Fact]
    public void GitHubModelMapper_ToPlatform_PullRequest_MapsAllFields()
    {
        var pr = new AgentPullRequest
        {
            Number = 42,
            Title = "feat: add auth",
            Body = "Implements JWT",
            State = "open",
            HeadBranch = "feature/auth",
            HeadSha = "abc123",
            BaseBranch = "main",
            AssignedAgent = "Software Engineer 1",
            Url = "https://github.com/test/repo/pull/42",
            CreatedAt = new DateTime(2024, 1, 1),
            UpdatedAt = new DateTime(2024, 1, 2),
            MergedAt = new DateTime(2024, 1, 3),
            Labels = ["in-progress", "ready-for-review"],
            ChangedFiles = ["src/auth.cs"],
            MergeableState = "clean"
        };

        var result = GitHubModelMapper.ToPlatform(pr);

        Assert.Equal(42, result.Number);
        Assert.Equal("feat: add auth", result.Title);
        Assert.Equal("Implements JWT", result.Body);
        Assert.Equal("open", result.State);
        Assert.Equal("feature/auth", result.HeadBranch);
        Assert.Equal("abc123", result.HeadSha);
        Assert.Equal("main", result.BaseBranch);
        Assert.Equal("Software Engineer 1", result.AssignedAgent);
        Assert.True(result.IsMerged);
        Assert.Equal(2, result.Labels.Count);
        Assert.Single(result.ChangedFiles);
        Assert.Equal("clean", result.MergeableState);
    }

    [Fact]
    public void GitHubModelMapper_ToPlatform_Issue_MapsAllFields()
    {
        var issue = new AgentIssue
        {
            GitHubId = 999,
            Number = 7,
            Title = "Bug: crash on login",
            Body = "Steps to reproduce...",
            State = "open",
            AssignedAgent = "Test Engineer",
            Url = "https://github.com/test/repo/issues/7",
            CreatedAt = new DateTime(2024, 1, 1),
            Author = "azurenerd",
            CommentCount = 3,
            Labels = ["blocker"]
        };

        var result = GitHubModelMapper.ToPlatform(issue);

        Assert.Equal(999, result.PlatformId);
        Assert.Equal(7, result.Number);
        Assert.Equal("Bug: crash on login", result.Title);
        Assert.Equal("open", result.State);
        Assert.Equal("azurenerd", result.Author);
        Assert.Equal(3, result.CommentCount);
        Assert.Equal("Issue", result.WorkItemType);
    }

    [Fact]
    public void GitHubModelMapper_ToPlatform_Comment_MapsAllFields()
    {
        var comment = new IssueComment
        {
            Id = 123,
            Author = "reviewer",
            Body = "LGTM",
            CreatedAt = new DateTime(2024, 1, 5)
        };

        var result = GitHubModelMapper.ToPlatform(comment);

        Assert.Equal(123, result.Id);
        Assert.Equal("reviewer", result.Author);
        Assert.Equal("LGTM", result.Body);
    }

    [Fact]
    public void GitHubModelMapper_ToPlatform_FileDiff_MapsAllFields()
    {
        var diff = new PullRequestFileDiff
        {
            FileName = "src/main.cs",
            Patch = "@@ -1,3 +1,5 @@",
            Status = "modified",
            Additions = 2,
            Deletions = 0
        };

        var result = GitHubModelMapper.ToPlatform(diff);

        Assert.Equal("src/main.cs", result.FileName);
        Assert.Equal("modified", result.Status);
        Assert.Equal(2, result.Additions);
    }

    [Fact]
    public void GitHubModelMapper_ToPlatform_ReviewThread_MapsAllFields()
    {
        var thread = new ReviewThread
        {
            Id = 456,
            NodeId = "RT_abc123",
            FilePath = "src/auth.cs",
            Line = 42,
            Body = "Consider using async",
            Author = "architect",
            IsResolved = false,
            CreatedAt = new DateTime(2024, 1, 10)
        };

        var result = GitHubModelMapper.ToPlatform(thread);

        Assert.Equal(456, result.Id);
        Assert.Equal("RT_abc123", result.ThreadId);
        Assert.Equal("src/auth.cs", result.FilePath);
        Assert.Equal(42, result.Line);
        Assert.False(result.IsResolved);
    }

    [Fact]
    public void GitHubModelMapper_ToPlatform_RateLimitInfo_IncludesPlatformName()
    {
        var info = new GitHubRateLimitInfo
        {
            Remaining = 4500,
            Limit = 5000,
            ResetAt = new DateTime(2024, 1, 1, 2, 0, 0),
            TotalApiCalls = 500,
            IsRateLimited = false
        };

        var result = GitHubModelMapper.ToPlatform(info);

        Assert.Equal("GitHub", result.PlatformName);
        Assert.Equal(4500, result.Remaining);
        Assert.False(result.IsRateLimited);
    }

    [Fact]
    public void GitHubModelMapper_InlineComment_RoundTrips()
    {
        var platform = new PlatformInlineComment
        {
            FilePath = "src/test.cs",
            Line = 10,
            Body = "Fix this"
        };

        var github = GitHubModelMapper.ToGitHub(platform);
        var back = GitHubModelMapper.ToPlatform(github);

        Assert.Equal(platform.FilePath, back.FilePath);
        Assert.Equal(platform.Line, back.Line);
        Assert.Equal(platform.Body, back.Body);
    }

    [Fact]
    public void GitHubModelMapper_CommitInfo_MapsAllFields()
    {
        var commit = ("abc123", "fix: typo", new DateTime(2024, 3, 15));

        var result = GitHubModelMapper.ToPlatform(commit);

        Assert.Equal("abc123", result.Sha);
        Assert.Equal("fix: typo", result.Message);
        Assert.Equal(new DateTime(2024, 3, 15), result.CommittedAt);
    }

    // ──────────────────── Platform Capabilities ────────────────────

    [Fact]
    public void PlatformCapabilities_GitHub_HasExpectedDefaults()
    {
        var caps = PlatformCapabilities.GitHub;

        Assert.True(caps.SupportsWorkItemHierarchy);
        Assert.True(caps.SupportsWorkItemDependencies);
        Assert.True(caps.SupportsWorkItemDeletion);
        Assert.True(caps.SupportsInlineReviewComments);
        Assert.True(caps.SupportsAtomicTreeReset);
        Assert.Single(caps.SupportedWorkItemTypes);
        Assert.Contains("Issue", caps.SupportedWorkItemTypes);
    }

    [Fact]
    public void PlatformCapabilities_AzureDevOps_HasExpectedDefaults()
    {
        var caps = PlatformCapabilities.AzureDevOps;

        Assert.True(caps.SupportsWorkItemHierarchy);
        Assert.False(caps.SupportsWorkItemDeletion);
        Assert.False(caps.SupportsAtomicTreeReset);
        Assert.Contains("Task", caps.SupportedWorkItemTypes);
        Assert.Contains("Bug", caps.SupportedWorkItemTypes);
        Assert.Contains("User Story", caps.SupportedWorkItemTypes);
        Assert.Equal(5, caps.SupportedWorkItemTypes.Count);
    }

    // ──────────────────── Auth Provider ────────────────────

    [Fact]
    public async Task PatAuthProvider_ReturnsToken()
    {
        var provider = new PatAuthProvider("ghp_test123");

        var token = await provider.GetTokenAsync();

        Assert.Equal("ghp_test123", token);
        Assert.False(provider.RequiresRefresh);
        Assert.Equal("token", provider.AuthScheme);
    }

    [Fact]
    public void PatAuthProvider_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => new PatAuthProvider(null!));
    }

    // ──────────────────── Config ────────────────────

    [Fact]
    public void DevPlatformConfig_DefaultsToGitHub()
    {
        var config = new DevPlatformConfig();

        Assert.Equal(DevPlatformType.GitHub, config.Platform);
        Assert.Equal(DevPlatformAuthMethod.Pat, config.AuthMethod);
        Assert.Null(config.AzureDevOps);
    }

    [Fact]
    public void DevPlatformConfig_CanConfigureForAdo()
    {
        var config = new DevPlatformConfig
        {
            Platform = DevPlatformType.AzureDevOps,
            AuthMethod = DevPlatformAuthMethod.AzureCliBearer,
            AzureDevOps = new AzureDevOpsConfig
            {
                Organization = "myorg",
                Project = "MyProject",
                Repository = "MyRepo",
                TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
                StateMappings = new()
                {
                    ["Open"] = "New",
                    ["InProgress"] = "Active",
                    ["Resolved"] = "Closed"
                }
            }
        };

        Assert.Equal(DevPlatformType.AzureDevOps, config.Platform);
        Assert.Equal("myorg", config.AzureDevOps!.Organization);
        Assert.Equal(3, config.AzureDevOps.StateMappings.Count);
    }

    // ──────────────────── Platform Models ────────────────────

    [Fact]
    public void PlatformPullRequest_IsMerged_WhenMergedAtHasValue()
    {
        var pr = new PlatformPullRequest { MergedAt = DateTime.UtcNow };
        Assert.True(pr.IsMerged);
    }

    [Fact]
    public void PlatformPullRequest_NotMerged_WhenMergedAtNull()
    {
        var pr = new PlatformPullRequest();
        Assert.False(pr.IsMerged);
    }

    [Fact]
    public void PlatformWorkItem_DefaultType_IsIssue()
    {
        var wi = new PlatformWorkItem();
        Assert.Equal("Issue", wi.WorkItemType);
    }

    [Fact]
    public void PlatformFileCommit_RequiredProperties()
    {
        var fc = new PlatformFileCommit { Path = "src/test.cs", Content = "// test" };
        Assert.Equal("src/test.cs", fc.Path);
        Assert.Equal("// test", fc.Content);
    }

    // ──────────────────── ADO Config ────────────────────

    [Fact]
    public void AzureDevOpsConfig_HasSensibleDefaults()
    {
        var config = new AzureDevOpsConfig();

        Assert.Equal("", config.Organization);
        Assert.Equal("", config.Project);
        Assert.Equal("", config.Repository);
        Assert.Equal("main", config.DefaultBranch);
        Assert.Equal("Task", config.DefaultWorkItemType);
        Assert.Equal("User Story", config.ExecutiveWorkItemType);
        Assert.Empty(config.StateMappings);
    }

    [Fact]
    public void DevPlatformConfig_StateMappings_CanBeConfigured()
    {
        var config = new DevPlatformConfig
        {
            StateMappings = new()
            {
                ["Open"] = "New",
                ["InProgress"] = "Active",
                ["Blocked"] = "Active",
                ["Resolved"] = "Closed"
            }
        };

        Assert.Equal(4, config.StateMappings.Count);
        Assert.Equal("New", config.StateMappings["Open"]);
        Assert.Equal("Closed", config.StateMappings["Resolved"]);
    }

    [Fact]
    public void AzureDevOpsConfig_FullConfiguration_AllFieldsPersist()
    {
        var config = new DevPlatformConfig
        {
            Platform = DevPlatformType.AzureDevOps,
            AuthMethod = DevPlatformAuthMethod.AzureCliBearer,
            AzureDevOps = new AzureDevOpsConfig
            {
                Organization = "contoso",
                Project = "ProjectX",
                Repository = "main-repo",
                TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
                DefaultBranch = "develop",
                DefaultWorkItemType = "Bug",
                ExecutiveWorkItemType = "Feature",
                StateMappings = new()
                {
                    ["Open"] = "New",
                    ["InProgress"] = "Active",
                    ["Blocked"] = "Active",
                    ["Resolved"] = "Closed"
                }
            }
        };

        Assert.Equal(DevPlatformType.AzureDevOps, config.Platform);
        Assert.Equal(DevPlatformAuthMethod.AzureCliBearer, config.AuthMethod);
        Assert.NotNull(config.AzureDevOps);
        Assert.Equal("contoso", config.AzureDevOps.Organization);
        Assert.Equal("ProjectX", config.AzureDevOps.Project);
        Assert.Equal("main-repo", config.AzureDevOps.Repository);
        Assert.Equal("develop", config.AzureDevOps.DefaultBranch);
        Assert.Equal("Bug", config.AzureDevOps.DefaultWorkItemType);
        Assert.Equal("Feature", config.AzureDevOps.ExecutiveWorkItemType);
        Assert.Equal(4, config.AzureDevOps.StateMappings.Count);
    }

    [Fact]
    public void DevPlatformConfig_PatAuth_DefaultsCorrectly()
    {
        var config = new DevPlatformConfig
        {
            Platform = DevPlatformType.AzureDevOps,
            AuthMethod = DevPlatformAuthMethod.Pat,
            AzureDevOps = new AzureDevOpsConfig
            {
                Organization = "myorg",
                Pat = "ado-pat-token"
            }
        };

        Assert.Equal(DevPlatformAuthMethod.Pat, config.AuthMethod);
        Assert.Equal("ado-pat-token", config.AzureDevOps.Pat);
    }

    [Fact]
    public void DevPlatformAuthMethod_HasAllExpectedValues()
    {
        var values = Enum.GetValues<DevPlatformAuthMethod>();
        Assert.Contains(DevPlatformAuthMethod.Pat, values);
        Assert.Contains(DevPlatformAuthMethod.AzureCliBearer, values);
        Assert.Contains(DevPlatformAuthMethod.ServicePrincipal, values);
    }
}
