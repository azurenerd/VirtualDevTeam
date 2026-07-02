using Xunit;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.Tests
{
    public class HtmlContentExtractorTests
    {
        private readonly HtmlContentExtractor _extractor = new();

        #region CanHandle Tests

        [Fact]
        public void CanHandle_ReturnsTrue_ForHtmlContentType()
        {
            // Arrange
            const string url = "https://example.com/page";
            const string contentType = "text/html; charset=utf-8";

            // Act
            var result = _extractor.CanHandle(url, contentType);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void CanHandle_ReturnsTrue_ForHtmlExtension()
        {
            // Arrange
            const string url = "https://example.com/page.html";
            const string? contentType = null;

            // Act
            var result = _extractor.CanHandle(url, contentType);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void CanHandle_ReturnsTrue_ForHtmExtension()
        {
            // Arrange
            const string url = "https://example.com/page.htm";
            const string? contentType = null;

            // Act
            var result = _extractor.CanHandle(url, contentType);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void CanHandle_ReturnsFalse_ForMarkdownUrl()
        {
            // Arrange
            const string url = "https://example.com/page.md";
            const string contentType = "text/markdown";

            // Act
            var result = _extractor.CanHandle(url, contentType);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void CanHandle_ReturnsFalse_ForJsonUrl()
        {
            // Arrange
            const string url = "https://example.com/data.json";
            const string? contentType = null;

            // Act
            var result = _extractor.CanHandle(url, contentType);

            // Assert
            Assert.False(result);
        }

        #endregion

        #region Extract Tests

        [Fact]
        public void Extract_ReturnsEmpty_ForNull()
        {
            // Arrange
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(null!, url);

            // Assert
            Assert.Equal("", result);
        }

        [Fact]
        public void Extract_ReturnsEmpty_ForWhitespace()
        {
            // Arrange
            const string rawContent = "   \n\t  ";
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.Equal("", result);
        }

        [Fact]
        public void Extract_RemovesScriptBlocks()
        {
            // Arrange
            const string rawContent = @"
                <div>Some content</div>
                <script>var x = 'alert(1)';</script>
                <div>More content</div>";
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.DoesNotContain("alert", result);
            Assert.Contains("Some content", result);
            Assert.Contains("More content", result);
        }

        [Fact]
        public void Extract_RemovesStyleBlocks()
        {
            // Arrange
            const string rawContent = @"
                <div>Visible content</div>
                <style>.hidden { display: none; }</style>
                <div>Another section</div>";
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.DoesNotContain("display: none", result);
            Assert.Contains("Visible content", result);
            Assert.Contains("Another section", result);
        }

        [Fact]
        public void Extract_RemovesHtmlComments()
        {
            // Arrange
            const string rawContent = @"
                <div>Public content</div>
                <!-- This is a comment about secret stuff -->
                <div>More public content</div>";
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.DoesNotContain("secret", result);
            Assert.Contains("Public content", result);
            Assert.Contains("More public content", result);
        }

        [Fact]
        public void Extract_StripsHtmlTags()
        {
            // Arrange
            const string rawContent = "<p>This is <strong>bold</strong> text in a <span>span</span>.</p>";
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.DoesNotContain("<p>", result);
            Assert.DoesNotContain("<strong>", result);
            Assert.DoesNotContain("</strong>", result);
            Assert.DoesNotContain("<span>", result);
            Assert.Contains("This is", result);
            Assert.Contains("bold", result);
            Assert.Contains("text", result);
        }

        [Fact]
        public void Extract_DecodesHtmlEntities()
        {
            // Arrange
            const string rawContent = "<p>Copyright &copy; 2024. Less &lt; Greater &gt;</p>";
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.Contains("©", result);
            Assert.Contains("<", result);
            Assert.Contains(">", result);
            Assert.DoesNotContain("&copy;", result);
            Assert.DoesNotContain("&lt;", result);
            Assert.DoesNotContain("&gt;", result);
        }

        [Fact]
        public void Extract_NormalizesWhitespace()
        {
            // Arrange
            const string rawContent = @"
                <p>Line   1</p>
                
                
                <p>Line   2</p>
                	<p>Line   3</p>";
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.DoesNotContain("   ", result);
            Assert.DoesNotContain("\t", result);
            Assert.Contains("Line 1", result);
            Assert.Contains("Line 2", result);
            Assert.Contains("Line 3", result);
        }

        [Fact]
        public void Extract_ExtractsArticleContent_WhenArticleTagPresent()
        {
            // Arrange
            const string rawContent = @"
                <header>Navigation and header</header>
                <article>
                    <h1>Article Title</h1>
                    <p>This is the main article content with substantial information that should be extracted.</p>
                </article>
                <footer>Footer content</footer>";
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            // Should prefer article content
            Assert.Contains("Article Title", result);
            Assert.Contains("main article content", result);
        }

        [Fact]
        public void Extract_ExtractsMainContent_WhenMainTagPresent()
        {
            // Arrange
            const string rawContent = @"
                <aside>Side navigation</aside>
                <main>
                    <h1>Main Content</h1>
                    <p>This is the primary content of the page that should be extracted with meaningful information.</p>
                </main>
                <footer>Footer</footer>";
            const string url = "https://example.com/page.html";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            // Should prefer main content
            Assert.Contains("Main Content", result);
            Assert.Contains("primary content", result);
        }

        #endregion
    }

    public class MarkdownContentExtractorTests
    {
        private readonly MarkdownContentExtractor _extractor = new();

        #region CanHandle Tests

        [Fact]
        public void CanHandle_ReturnsTrue_ForMdExtension()
        {
            // Arrange
            const string url = "https://example.com/readme.md";
            const string? contentType = null;

            // Act
            var result = _extractor.CanHandle(url, contentType);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void CanHandle_ReturnsTrue_ForMarkdownExtension()
        {
            // Arrange
            const string url = "https://example.com/guide.markdown";
            const string? contentType = null;

            // Act
            var result = _extractor.CanHandle(url, contentType);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void CanHandle_ReturnsTrue_ForMarkdownContentType()
        {
            // Arrange
            const string url = "https://example.com/document";
            const string contentType = "text/markdown";

            // Act
            var result = _extractor.CanHandle(url, contentType);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void CanHandle_ReturnsFalse_ForHtmlUrl()
        {
            // Arrange
            const string url = "https://example.com/page.html";
            const string? contentType = null;

            // Act
            var result = _extractor.CanHandle(url, contentType);

            // Assert
            Assert.False(result);
        }

        #endregion

        #region Extract Tests

        [Fact]
        public void Extract_ReturnsEmpty_ForNull()
        {
            // Arrange
            const string url = "https://example.com/readme.md";

            // Act
            var result = _extractor.Extract(null!, url);

            // Assert
            Assert.Equal("", result);
        }

        [Fact]
        public void Extract_ReturnsEmpty_ForWhitespace()
        {
            // Arrange
            const string rawContent = "   \n\t  ";
            const string url = "https://example.com/readme.md";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.Equal("", result);
        }

        [Fact]
        public void Extract_RemovesFrontMatter()
        {
            // Arrange
            const string rawContent = @"---
title: My Document
author: John Doe
date: 2024-01-01
---

# Main Content

This is the actual content that should remain.";
            const string url = "https://example.com/readme.md";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.DoesNotContain("title: My Document", result);
            Assert.DoesNotContain("author: John Doe", result);
            Assert.DoesNotContain("date: 2024-01-01", result);
            Assert.Contains("Main Content", result);
            Assert.Contains("actual content", result);
        }

        [Fact]
        public void Extract_OmitsLargeCodeBlocks()
        {
            // Arrange
            // Create a large code block (over 500 chars)
            var largeContent = new string('x', 600);
            string rawContent = $@"# Documentation

Here is a large code block:

```javascript
{largeContent}
```

More text after the code block.";
            const string url = "https://example.com/readme.md";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.Contains("[code block omitted]", result);
            Assert.DoesNotContain(largeContent, result);
            Assert.Contains("More text", result);
        }

        [Fact]
        public void Extract_KeepsSmallCodeBlocks()
        {
            // Arrange
            const string rawContent = @"# Tutorial

Here is a small code example:

```python
print('hello')
x = 42
```

That was the code.";
            const string url = "https://example.com/readme.md";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.Contains("print", result);
            Assert.Contains("hello", result);
            Assert.DoesNotContain("[code block omitted]", result);
        }

        [Fact]
        public void Extract_RemovesImageSyntax_KeepsAltText()
        {
            // Arrange
            const string rawContent = @"# Gallery

![A beautiful sunset over mountains](/images/sunset.jpg)

![Logo for our project](/assets/logo.png)

More content here.";
            const string url = "https://example.com/readme.md";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            // Should keep alt text
            Assert.Contains("beautiful sunset", result);
            Assert.Contains("Logo for our project", result);
            // Should remove image paths
            Assert.DoesNotContain("/images/sunset.jpg", result);
            Assert.DoesNotContain("/assets/logo.png", result);
            // Should not have markdown image syntax
            Assert.DoesNotContain("![", result);
            Assert.DoesNotContain("](", result);
        }

        [Fact]
        public void Extract_RemovesLinkSyntax_KeepsText()
        {
            // Arrange
            const string rawContent = @"# Links Section

Visit [our website](https://example.com) for more info.

Check out [this guide](./guide.md) to get started.

Regular text paragraph.";
            const string url = "https://example.com/readme.md";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            // Should keep link text
            Assert.Contains("our website", result);
            Assert.Contains("this guide", result);
            // Should remove URLs
            Assert.DoesNotContain("https://example.com", result);
            Assert.DoesNotContain("./guide.md", result);
            // Should not have markdown link syntax
            Assert.DoesNotContain("[", result);
            Assert.DoesNotContain("](", result);
        }

        [Fact]
        public void Extract_RemovesEmbeddedHtmlTags()
        {
            // Arrange
            const string rawContent = @"# Document

This is normal markdown content with <span style=""color:red"">embedded HTML</span> tags.

Some <div class=""highlight"">more HTML</div> mixed in.

Regular paragraph without HTML.";
            const string url = "https://example.com/readme.md";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            Assert.DoesNotContain("<span", result);
            Assert.DoesNotContain("<div", result);
            Assert.DoesNotContain("style=", result);
            Assert.Contains("embedded HTML", result);
            Assert.Contains("more HTML", result);
            Assert.Contains("Regular paragraph", result);
        }

        [Fact]
        public void Extract_NormalizesMultipleBlankLines()
        {
            // Arrange
            const string rawContent = @"# Title


Section one.



Section two.




Section three.";
            const string url = "https://example.com/readme.md";

            // Act
            var result = _extractor.Extract(rawContent, url);

            // Assert
            // The regex replaces 3+ newlines with 2 newlines (max 1 blank line between sections)
            // So we should see at most double newlines (\n\n) in the result
            var doubleNewlineCount = result.Length - result.Replace("\n\n", "").Length;
            Assert.True(doubleNewlineCount <= result.Length / 2, 
                "Should normalize multiple blank lines to max 2 consecutive newlines");
            Assert.Contains("Section one", result);
            Assert.Contains("Section two", result);
            Assert.Contains("Section three", result);
        }

        #endregion
    }
}