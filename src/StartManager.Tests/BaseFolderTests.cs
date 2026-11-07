using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>Edits applied to the record as it is now: «Изменить каталог» (Shift+F11), renaming, moving and the properties.</summary>
public sealed class BaseFolderTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void FolderGoesToTheCurrentRecordKeepingItsNewerSettings()
    {
        var shown = new BaseEntry { Id = Id, Name = "Бухгалтерия", Kind = ConnectionKind.File, Location = "C:\\old", Comment = "старый" };
        // While the folder was chosen the base got another comment and parameters in another window.
        var now = shown with { Comment = "новый", AdditionalParameters = "/DisableStartupMessages", Name = "Бух" };
        var updated = BaseEdit.WithFolder([now], Id, "D:\\new")!;
        Assert.Equal(now with { Location = "D:\\new" }, updated);
    }

    [Fact]
    public void RenameAndPropertiesKeepFieldsChangedMeanwhile()
    {
        var shown = new BaseEntry { Id = Id, Name = "Бухгалтерия", Group = "Учёт", Kind = ConnectionKind.File, Location = "C:\\buh", Comment = "старый",
            Authentication = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "Иванов" } };
        // Meanwhile another window changed the comment, the parameters and the authentication.
        var now = shown with { Comment = "новый", AdditionalParameters = "/DisableStartupMessages", Authentication = null, DisallowDelete = true };
        var renamed = BaseEdit.OnCurrent([now], shown, shown with { Name = "Бух" })!;
        Assert.Equal(now with { Name = "Бух" }, renamed);
        var moved = BaseEdit.OnCurrent([now], shown, shown with { Group = "Архив" })!;
        Assert.Equal(now with { Group = "Архив" }, moved);
        // What the user changed in the properties wins, the rest stays as it is now.
        var edited = BaseEdit.OnCurrent([now], shown, shown with { Comment = "мой", ExternalProcessing = "C:\\epf\\a.epf" })!;
        Assert.Equal(now with { Comment = "мой", ExternalProcessing = "C:\\epf\\a.epf" }, edited);
    }

    [Fact]
    public void ValuesTheDialogOnlyNormalisedAreNotChanges()
    {
        // Codex audit: «Alice » comes back from the dialog as «Alice»; empty settings come back as none.
        var shown = new BaseEntry { Id = Id, Name = "Бух ", Kind = ConnectionKind.File, Location = "C:\\buh ", Group = " Учёт", Executable = " ", ExternalProcessing = " ",
            Authentication = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "Alice " } };
        var now = shown with { Executable = "C:\\1cv8\\bin\\1cv8.exe", ExternalProcessing = "C:\\epf\\a.epf",
            Authentication = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "Bob" } };
        // What MainWindow's properties dialog builds when only the comment is changed: every field trimmed (written out, not computed).
        var returned = shown with { Name = "Бух", Location = "C:\\buh", Group = "Учёт", Executable = "", ExternalProcessing = "", Comment = "мой",
            Authentication = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "Alice" } };
        var saved = BaseEdit.OnCurrent([now], BaseEdit.AsDialogReturns(shown), returned)!;
        Assert.Equal(now with { Comment = "мой" }, saved);
        var empty = shown with { Authentication = new AuthenticationSettings() };
        Assert.Null(BaseEdit.AsDialogReturns(empty).Authentication);
        Assert.Equal(now.Authentication, BaseEdit.OnCurrent([now], BaseEdit.AsDialogReturns(empty), BaseEdit.AsDialogReturns(empty))!.Authentication);
    }

    [Fact]
    public void ChangedRuleKeepsTheUnknownFieldsOfTheCurrentRule()
    {
        var future = new Dictionary<string, System.Text.Json.JsonElement> { ["future"] = System.Text.Json.JsonDocument.Parse("1").RootElement };
        var newer = new Dictionary<string, System.Text.Json.JsonElement> { ["future"] = System.Text.Json.JsonDocument.Parse("2").RootElement };
        var shown = new BaseEntry { Id = Id, Kind = ConnectionKind.File, Location = "C:\\buh", PlatformRule = new PlatformRule { Version = "8.3", Extra = future } };
        var now = shown with { PlatformRule = shown.PlatformRule! with { Extra = newer } };
        // As PlatformEditor.ReadRule: the rule of the dialog with another version keeps the dictionary it was opened with.
        var saved = BaseEdit.OnCurrent([now], shown, shown with { PlatformRule = shown.PlatformRule! with { Version = "8.3.27" } })!;
        Assert.Equal("8.3.27", saved.PlatformRule!.Version);
        Assert.Same(newer, saved.PlatformRule.Extra);
    }

    [Fact]
    public void GoneOrNoLongerFileBaseGivesNothing()
    {
        Assert.Null(BaseEdit.WithFolder([], Id, "D:\\new"));
        Assert.Null(BaseEdit.WithFolder([new BaseEntry { Id = Id, Kind = ConnectionKind.Server, Location = "srv/buh" }], Id, "D:\\new"));
    }

    [Fact]
    public void SameFolderFollowsTheFileSystem()
    {
        Assert.True(BaseEdit.SameFolder("/data/erp/", "/data/erp"));
        // Codex re-check: a name ending with a space is another folder.
        Assert.False(BaseEdit.SameFolder("/tmp/erp ", "/tmp/erp"));
        Assert.False(BaseEdit.SameFolder("/data/erp", "/data/erp2"));
        // Case matters only where the file system ignores it.
        Assert.Equal(OperatingSystem.IsWindows(), BaseEdit.SameFolder("/data/ERP", "/data/erp"));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(BaseEdit.SameFolder("C:\\Bases\\", "c:\\bases"));
            // A root keeps its separator.
            Assert.True(BaseEdit.SameFolder("C:\\", "C:\\"));
            Assert.False(BaseEdit.SameFolder("C:\\", "C:"));
            // A share keeps its prefix; only the trailing separator goes.
            Assert.True(BaseEdit.SameFolder(@"\\nas\bases\erp\", @"\\NAS\Bases\erp"));
            Assert.False(BaseEdit.SameFolder(@"\\nas\bases\erp", @"\\nas\bases\erp2"));
        }
        else Assert.True(BaseEdit.SameFolder("/", "/"));
    }
}
