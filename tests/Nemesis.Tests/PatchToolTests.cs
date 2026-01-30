using Nemesis.AgentCore.Tools;
using Nemesis.Shared.Models;
using Xunit;

namespace Nemesis.Tests;

public class PatchToolTests
{
    [Fact]
    public void CreatePatch_ValidInput_CreatesDiff()
    {
        var patchTool = new PatchTool();
        var original = "public class Test\n{\n    public void Method() { }\n}";
        var modified = "public class Test\n{\n    public void Method()\n    {\n        Console.WriteLine(\"Hello\");\n    }\n}";

        var patch = patchTool.CreatePatch("test.cs", original, modified);

        Assert.NotNull(patch);
        Assert.Equal("test.cs", patch.FilePath);
        Assert.Equal(original, patch.OriginalContent);
        Assert.Equal(modified, patch.ModifiedContent);
        Assert.False(string.IsNullOrEmpty(patch.UnifiedDiff));
        Assert.Equal(PatchStatus.Pending, patch.Status);
    }

    [Fact]
    public void CreatePatchSet_MultipleFiles_CreatesAllPatches()
    {
        var patchTool = new PatchTool();
        var changes = new List<(string, string, string)>
        {
            ("file1.cs", "original1", "modified1"),
            ("file2.cs", "original2", "modified2")
        };

        var patchSet = patchTool.CreatePatchSet("Test patch set", changes);

        Assert.Equal(2, patchSet.Patches.Count);
        Assert.Equal("Test patch set", patchSet.Description);
        Assert.False(patchSet.IsApplied);
    }

    [Fact]
    public void GetPendingPatch_AfterAdd_ReturnsPatch()
    {
        var patchTool = new PatchTool();
        var patch = patchTool.CreatePatch("test.cs", "old", "new");
        patchTool.AddPendingPatch(patch);

        var retrieved = patchTool.GetPendingPatch(patch.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(patch.Id, retrieved.Id);
    }

    [Fact]
    public void RemovePendingPatch_AfterRemove_ReturnsNull()
    {
        var patchTool = new PatchTool();
        var patch = patchTool.CreatePatch("test.cs", "old", "new");
        patchTool.AddPendingPatch(patch);
        patchTool.RemovePendingPatch(patch.Id);

        var retrieved = patchTool.GetPendingPatch(patch.Id);

        Assert.Null(retrieved);
    }
}
