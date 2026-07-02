namespace VirtualDevTeam.Core.DevPlatform.Capabilities;

public record RepositoryCreationResult(bool Success, string? RepositoryUrl, string? ErrorMessage);

public interface IRepositoryManagementService
{
    Task<RepositoryCreationResult> CreateRepositoryAsync(string name, bool isPrivate = true, CancellationToken ct = default);
}
