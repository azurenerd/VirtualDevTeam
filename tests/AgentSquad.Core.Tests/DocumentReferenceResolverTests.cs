using AgentSquad.Core.DevPlatform.Capabilities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSquad.Core.Tests;

public class DocumentReferenceResolverTests
{
    private readonly Mock<IRepositoryContentService> _repoContent = new();
    private readonly DocumentReferenceResolver _resolver;
    private readonly DocumentResolutionContext _ctx = new("main");

    public DocumentReferenceResolverTests()
    {
        _resolver = new DocumentReferenceResolver(
            _repoContent.Object,
            NullLogger<DocumentReferenceResolver>.Instance);
    }

    [Fact]
    public async Task ResolveReferencesAsync_EmptyText_ReturnsEmpty()
    {
        var result = await _resolver.ResolveReferencesAsync("", _ctx);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveReferencesAsync_NullText_ReturnsEmpty()
    {
        var result = await _resolver.ResolveReferencesAsync(null!, _ctx);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveReferencesAsync_MarkdownLink_ResolvesFile()
    {
        _repoContent
            .Setup(r => r.GetFileContentAsync("AgentDocs/101/PMSpec.md", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# PM Spec content");

        var text = "See [PM Specification](AgentDocs/101/PMSpec.md) for details.";
        var result = await _resolver.ResolveReferencesAsync(text, _ctx);

        Assert.Single(result);
        Assert.Equal("AgentDocs/101/PMSpec.md", result[0].Path);
        Assert.Equal("# PM Spec content", result[0].Content);
    }

    [Fact]
    public async Task ResolveReferencesAsync_HtmlAnchor_ResolvesFile()
    {
        _repoContent
            .Setup(r => r.GetFileContentAsync("AgentDocs/101/Architecture.md", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Architecture");

        var text = """<a href="AgentDocs/101/Architecture.md">Architecture Design</a>""";
        var result = await _resolver.ResolveReferencesAsync(text, _ctx);

        Assert.Single(result);
        Assert.Equal("AgentDocs/101/Architecture.md", result[0].Path);
    }

    [Fact]
    public async Task ResolveReferencesAsync_BareFilePath_ResolvesKnownDocs()
    {
        _repoContent
            .Setup(r => r.GetFileContentAsync("PMSpec.md", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Spec");

        var text = "Read PMSpec.md for the full specification.";
        var result = await _resolver.ResolveReferencesAsync(text, _ctx);

        Assert.Single(result);
        Assert.Equal("PMSpec.md", result[0].Path);
    }

    [Fact]
    public async Task ResolveReferencesAsync_MultipleDocs_DeduplicatesByPath()
    {
        _repoContent
            .Setup(r => r.GetFileContentAsync("AgentDocs/101/PMSpec.md", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Spec");

        var text = """
            See [PM Spec](AgentDocs/101/PMSpec.md) and also
            <a href="AgentDocs/101/PMSpec.md">PM Specification</a>
            """;
        var result = await _resolver.ResolveReferencesAsync(text, _ctx);

        Assert.Single(result);
    }

    [Fact]
    public async Task ResolveReferencesAsync_FileNotFound_ExcludedFromResults()
    {
        _repoContent
            .Setup(r => r.GetFileContentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var text = "[Missing](AgentDocs/999/NotReal.md)";
        var result = await _resolver.ResolveReferencesAsync(text, _ctx);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveReferencesAsync_NonMdLinks_Ignored()
    {
        var text = """
            See [image](logo.png) and [site](https://example.com) and [anchor](#section)
            """;
        var result = await _resolver.ResolveReferencesAsync(text, _ctx);

        Assert.Empty(result);
        _repoContent.Verify(
            r => r.GetFileContentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveReferencesAsync_AdoQueryParamUrl_ExtractsPath()
    {
        _repoContent
            .Setup(r => r.GetFileContentAsync("AgentDocs/101/Research.md", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Research");

        var text = "https://dev.azure.com/org/proj/_git/repo?path=/AgentDocs/101/Research.md&version=GBmain";
        var result = await _resolver.ResolveReferencesAsync(text,
            new DocumentResolutionContext("main", RepoBaseUrl: "https://dev.azure.com/org/proj/_git/repo"));

        Assert.Single(result);
        Assert.Equal("AgentDocs/101/Research.md", result[0].Path);
    }
}
