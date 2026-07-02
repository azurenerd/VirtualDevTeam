using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class PatchAnalyzerTests
{
    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(PatchAnalyzer.Parse(null));
        Assert.Empty(PatchAnalyzer.Parse(""));
        Assert.Empty(PatchAnalyzer.Parse("  "));
    }

    [Fact]
    public void Parse_SingleAddedFile_CorrectTypeAndLines()
    {
        var patch = """
            diff --git a/src/Hello.cs b/src/Hello.cs
            new file mode 100644
            --- /dev/null
            +++ b/src/Hello.cs
            @@ -0,0 +1,5 @@
            +namespace App;
            +public class Hello
            +{
            +    public string Greet() => "Hi";
            +}
            """;

        var files = PatchAnalyzer.Parse(patch);

        Assert.Single(files);
        var f = files[0];
        Assert.Equal("src/Hello.cs", f.Path);
        Assert.Equal(FileChangeType.Added, f.Type);
        Assert.Equal(5, f.LinesAdded);
        Assert.Equal(0, f.LinesRemoved);
        Assert.False(f.IsBinary);
    }

    [Fact]
    public void Parse_ModifiedFile_CorrectAddRemoveCounts()
    {
        var patch = """
            diff --git a/src/App.cs b/src/App.cs
            --- a/src/App.cs
            +++ b/src/App.cs
            @@ -1,3 +1,4 @@
             namespace App;
            -public class Old {}
            +public class New {}
            +// added line
            """;

        var files = PatchAnalyzer.Parse(patch);

        Assert.Single(files);
        var f = files[0];
        Assert.Equal("src/App.cs", f.Path);
        Assert.Equal(FileChangeType.Modified, f.Type);
        Assert.Equal(2, f.LinesAdded);
        Assert.Equal(1, f.LinesRemoved);
    }

    [Fact]
    public void Parse_DeletedFile_MarkedAsDeleted()
    {
        var patch = """
            diff --git a/old.txt b/old.txt
            deleted file mode 100644
            --- a/old.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -line1
            -line2
            """;

        var files = PatchAnalyzer.Parse(patch);

        Assert.Single(files);
        Assert.Equal(FileChangeType.Deleted, files[0].Type);
        Assert.Equal(2, files[0].LinesRemoved);
    }

    [Fact]
    public void Parse_RenamedFile_MarkedAsRenamed()
    {
        var patch = """
            diff --git a/old.cs b/new.cs
            rename from old.cs
            rename to new.cs
            """;

        var files = PatchAnalyzer.Parse(patch);

        Assert.Single(files);
        Assert.Equal(FileChangeType.Renamed, files[0].Type);
        Assert.Equal("new.cs", files[0].Path);
    }

    [Fact]
    public void Parse_MultipleFiles_ReturnsAll()
    {
        var patch = """
            diff --git a/a.cs b/a.cs
            new file mode 100644
            --- /dev/null
            +++ b/a.cs
            @@ -0,0 +1 @@
            +hello
            diff --git a/b.cs b/b.cs
            --- a/b.cs
            +++ b/b.cs
            @@ -1 +1 @@
            -old
            +new
            diff --git a/c.cs b/c.cs
            deleted file mode 100644
            --- a/c.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -bye
            """;

        var files = PatchAnalyzer.Parse(patch);

        Assert.Equal(3, files.Count);
        Assert.Equal(FileChangeType.Added, files[0].Type);
        Assert.Equal(FileChangeType.Modified, files[1].Type);
        Assert.Equal(FileChangeType.Deleted, files[2].Type);
    }

    [Fact]
    public void Parse_BinaryFile_MarkedAsBinary()
    {
        var patch = """
            diff --git a/image.png b/image.png
            new file mode 100644
            Binary files /dev/null and b/image.png differ
            """;

        var files = PatchAnalyzer.Parse(patch);

        Assert.Single(files);
        Assert.True(files[0].IsBinary);
        Assert.Equal(0, files[0].LinesAdded);
    }

    [Fact]
    public void ExtractPathFromDiffLine_StandardFormat()
    {
        Assert.Equal("src/foo.cs", PatchAnalyzer.ExtractPathFromDiffLine("diff --git a/src/foo.cs b/src/foo.cs"));
    }

    [Fact]
    public void ExtractPathFromDiffLine_RenameFormat()
    {
        Assert.Equal("src/new.cs", PatchAnalyzer.ExtractPathFromDiffLine("diff --git a/src/old.cs b/src/new.cs"));
    }
}
