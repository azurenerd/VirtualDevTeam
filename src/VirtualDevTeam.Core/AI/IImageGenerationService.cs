namespace VirtualDevTeam.Core.AI;

/// <summary>
/// .NET-side image-generation service used by the Develop wizard's "Validate Image Auth"
/// button. Agents generate via direct REST calls against the Azure OpenAI image-generation
/// endpoint using credentials injected into their CLI session env (see
/// prompts/_shared/image-gen-instructions.md), not this service.
/// </summary>
public interface IImageGenerationService
{
    /// <summary>Generates an image with deployment fallback + retry. (Stubbed for wizard validation.)</summary>
    Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lightweight validation used by the wizard's "Validate Image Auth and Models" button.
    /// </summary>
    Task<ImageValidationReport> ValidateAsync(bool runSmokeTest, string? smokeTestOutputPath = null, CancellationToken ct = default);
}

public sealed record ImageGenerationRequest
{
    public required string Prompt { get; init; }
    public required string OutputPath { get; init; }
    public string Size { get; init; } = "1024x1024";
    public string? ReferenceImagePath { get; init; }
    public int? MaxAttemptsOverride { get; init; }
}

public sealed record ImageGenerationResult
{
    public bool Success { get; init; }
    public string? SavedPath { get; init; }
    public string? DeploymentUsed { get; init; }
    public int AttemptsMade { get; init; }
    public string? FailureReason { get; init; }
    public IReadOnlyList<ImageAttemptHistory> AttemptHistory { get; init; } = Array.Empty<ImageAttemptHistory>();
    public ImageVerificationVerdict VerificationVerdict { get; init; } = ImageVerificationVerdict.NotRun;
}

public sealed record ImageAttemptHistory(
    int AttemptNumber,
    string Deployment,
    string PromptUsed,
    bool Succeeded,
    string? Error,
    ImageVerificationVerdict Verdict);

public enum ImageVerificationVerdict
{
    NotRun = 0,
    Matches = 1,
    DoesNotMatch = 2,
    Inconclusive = 3,
}

public sealed record ImageValidationReport
{
    public bool OverallSuccess { get; init; }
    public ValidationCheck AuthCheck { get; init; } = new();
    public ValidationCheck EndpointReachable { get; init; } = new();
    public ValidationCheck PrimaryDeploymentOnline { get; init; } = new();
    public IReadOnlyList<ValidationCheck> FallbackDeploymentsOnline { get; init; } = Array.Empty<ValidationCheck>();
    public ValidationCheck? SmokeTest { get; init; }
}

public sealed record ValidationCheck
{
    public string Label { get; init; } = "";
    public bool Passed { get; init; }
    public string? Detail { get; init; }
    public string? ActionHint { get; init; }
    public string? SavedPath { get; init; }
}
