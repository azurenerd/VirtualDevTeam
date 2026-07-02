using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.Tests;

public class DiffPositionMapperTests
{
    [Fact]
    public void MapLineToPosition_NullPatch_ReturnsNull()
    {
        Assert.Null(DiffPositionMapper.MapLineToPosition(null, 1));
    }

    [Fact]
    public void MapLineToPosition_EmptyPatch_ReturnsNull()
    {
        Assert.Null(DiffPositionMapper.MapLineToPosition("", 5));
    }

    [Fact]
    public void MapLineToPosition_InvalidLineNumber_ReturnsNull()
    {
        Assert.Null(DiffPositionMapper.MapLineToPosition("@@ -1,3 +1,4 @@\n context\n+added", 0));
        Assert.Null(DiffPositionMapper.MapLineToPosition("@@ -1,3 +1,4 @@\n context\n+added", -1));
    }

    [Fact]
    public void MapLineToPosition_SingleHunk_AddedLine()
    {
        // Hunk starts at new-file line 1
        // Position 1 = @@ header
        // Position 2 = context line (new line 1)
        // Position 3 = added line (new line 2)
        var patch = "@@ -1,2 +1,3 @@\n context\n+added line\n more context";

        Assert.Equal(3, DiffPositionMapper.MapLineToPosition(patch, 2)); // added line = new line 2
    }

    [Fact]
    public void MapLineToPosition_SingleHunk_ContextLine()
    {
        var patch = "@@ -1,2 +1,3 @@\n context\n+added line\n more context";

        Assert.Equal(2, DiffPositionMapper.MapLineToPosition(patch, 1)); // first context line
        Assert.Equal(4, DiffPositionMapper.MapLineToPosition(patch, 3)); // second context line
    }

    [Fact]
    public void MapLineToPosition_DeletionsSkipNewLine()
    {
        // Deletions don't advance the new-file line counter
        var patch = "@@ -1,3 +1,2 @@\n context\n-deleted\n more context";

        // Position 1 = @@ header
        // Position 2 = context (new line 1)
        // Position 3 = deleted (no new line)
        // Position 4 = context (new line 2)
        Assert.Equal(2, DiffPositionMapper.MapLineToPosition(patch, 1));
        Assert.Equal(4, DiffPositionMapper.MapLineToPosition(patch, 2));
    }

    [Fact]
    public void MapLineToPosition_MultiHunk()
    {
        var patch =
            "@@ -1,3 +1,4 @@\n line1\n+added1\n line2\n line3\n" +
            "@@ -10,3 +11,4 @@\n line10\n+added10\n line11\n line12";

        // First hunk: new lines 1-4
        Assert.Equal(2, DiffPositionMapper.MapLineToPosition(patch, 1));  // line1
        Assert.Equal(3, DiffPositionMapper.MapLineToPosition(patch, 2));  // added1

        // Second hunk: starts at new line 11
        // Position 6 = second @@ header
        // Position 7 = line10 (new line 11)
        // Position 8 = added10 (new line 12)
        Assert.Equal(7, DiffPositionMapper.MapLineToPosition(patch, 11));
        Assert.Equal(8, DiffPositionMapper.MapLineToPosition(patch, 12));
    }

    [Fact]
    public void MapLineToPosition_LineNotInDiff_ReturnsNull()
    {
        var patch = "@@ -1,3 +1,3 @@\n line1\n line2\n line3";

        // Line 100 isn't in this diff
        Assert.Null(DiffPositionMapper.MapLineToPosition(patch, 100));
    }

    [Fact]
    public void GetCommentableLines_SingleHunk()
    {
        var patch = "@@ -1,2 +1,3 @@\n context\n+added\n more";

        var lines = DiffPositionMapper.GetCommentableLines(patch);
        Assert.Equal([1, 2, 3], lines);
    }

    [Fact]
    public void GetCommentableLines_NullPatch_ReturnsEmpty()
    {
        Assert.Empty(DiffPositionMapper.GetCommentableLines(null));
    }

    [Fact]
    public void GetCommentableLines_DeletionsExcluded()
    {
        var patch = "@@ -1,3 +1,2 @@\n context\n-deleted\n more";

        var lines = DiffPositionMapper.GetCommentableLines(patch);
        Assert.Equal([1, 2], lines); // deleted line is not commentable on new side
    }

    [Fact]
    public void MapLineToPosition_RealWorldPatch()
    {
        // Realistic patch from a C# file modification
        var patch =
            "@@ -15,6 +15,8 @@ namespace MyApp.Services;\n" +
            " public class UserService\n" +
            " {\n" +
            "+    private readonly ILogger _logger;\n" +
            "+    private readonly IUserRepository _repo;\n" +
            "     private readonly string _connectionString;\n" +
            " \n" +
            "     public UserService(string connectionString)";

        // New file line 17 = first added line (_logger)
        Assert.Equal(4, DiffPositionMapper.MapLineToPosition(patch, 17));
        // New file line 18 = second added line (_repo)
        Assert.Equal(5, DiffPositionMapper.MapLineToPosition(patch, 18));
        // New file line 15 = "public class UserService" (context)
        Assert.Equal(2, DiffPositionMapper.MapLineToPosition(patch, 15));
    }
}
