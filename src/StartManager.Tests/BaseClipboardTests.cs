using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class BaseClipboardTests
{
    private static readonly Guid Id = Guid.Parse("0F8C3B2A-5D4E-4A1B-9C7D-2E6F8A9B0C1D");

    [Fact] public void FileBaseGivesNameIdConnectionAndPath()
    {
        var entry = new BaseEntry { Id = Id, Name = "Бухгалтерия; копия", Kind = ConnectionKind.File, Location = @"D:\Bases\Бухгалтерия 3.0" };
        Assert.Equal(new[] { BaseClipboardKind.Name, BaseClipboardKind.Id, BaseClipboardKind.Connection, BaseClipboardKind.Path }, BaseClipboard.Kinds(entry));
        Assert.Equal("Бухгалтерия; копия", BaseClipboard.Text(entry, BaseClipboardKind.Name));
        // Lower case with dashes, as ID= of an .v8i record.
        Assert.Equal("0f8c3b2a-5d4e-4a1b-9c7d-2e6f8a9b0c1d", BaseClipboard.Text(entry, BaseClipboardKind.Id));
        Assert.Equal("File=\"D:\\Bases\\Бухгалтерия 3.0\";", BaseClipboard.Text(entry, BaseClipboardKind.Connection));
        Assert.Equal(@"D:\Bases\Бухгалтерия 3.0", BaseClipboard.Text(entry, BaseClipboardKind.Path));
    }

    [Fact] public void ServerBaseHasNoPath()
    {
        var entry = new BaseEntry { Id = Id, Name = "ERP", Kind = ConnectionKind.Server, Location = @"srv1c:1541\erp_prod" };
        Assert.DoesNotContain(BaseClipboardKind.Path, BaseClipboard.Kinds(entry));
        Assert.Null(BaseClipboard.Text(entry, BaseClipboardKind.Path));
        Assert.Equal("Srvr=\"srv1c:1541\";Ref=\"erp_prod\";", BaseClipboard.Text(entry, BaseClipboardKind.Connection));
    }

    [Theory]
    [InlineData(ConnectionKind.File, @"\\nas\bases\trade;old")]
    [InlineData(ConnectionKind.File, "/home/user/bases/trade")]
    [InlineData(ConnectionKind.Server, @"srv1c,srv1c-2\base=1")]
    public void CopiedConnectionStringImportsBackToTheSameBase(ConnectionKind kind, string location)
    {
        var entry = new BaseEntry { Name = "Base", Kind = kind, Location = location };
        var result = V8iImporter.Import(new Library(), $"[Base]\nConnect={BaseClipboard.ConnectionString(entry)}\n");
        var imported = Assert.Single(result.Library.Bases);
        Assert.Equal(kind, imported.Kind);
        Assert.Equal(location, imported.Location);
    }

    [Fact] public void EveryKindHasALabel()
    {
        foreach (var kind in Enum.GetValues<BaseClipboardKind>()) Assert.StartsWith("Копировать ", BaseClipboard.Label(kind));
    }

    // «Вставить» of the new base window: a connection string from the clipboard (synthetic names only).
    [Theory]
    [InlineData("Srvr=\"localhost\";Ref=\"Nordwind2\";", @"localhost\Nordwind2")]
    [InlineData("Srvr='demo-server:1541';Ref='Nordwind';", @"demo-server:1541\Nordwind")]
    [InlineData("Srvr='srv''test';Ref=\"Nord \"\"wind\"\"\";", "srv'test\\Nord \"wind\"")]
    [InlineData("Connect=Srvr=\"demo-server\";Ref=\"Nordwind\";", @"demo-server\Nordwind")]
    [InlineData("  connect=Srvr=demo-server;Ref=Nordwind\r\n", @"demo-server\Nordwind")]
    public void PastedServerStringGivesServerAndName(string text, string location)
    {
        var pasted = BaseClipboard.Paste(text, ConnectionKind.Server);
        Assert.Equal(location, pasted.Location);
        Assert.Equal("", pasted.ConnectionString);
    }

    [Fact] public void PastedServerStringWithOtherParametersIsKeptWhole()
    {
        var pasted = BaseClipboard.Paste("Connect=Srvr=\"demo-server\";Ref=\"Nordwind\";DBMS=\"PostgreSQL\";DBSrvr=\"db-test\";DB=\"nordwind\";", ConnectionKind.Server);
        Assert.Equal(@"demo-server\Nordwind", pasted.Location);
        Assert.Equal("Srvr=\"demo-server\";Ref=\"Nordwind\";DBMS=\"PostgreSQL\";DBSrvr=\"db-test\";DB=\"nordwind\";", pasted.ConnectionString);
        // The kept string is one the creation accepts, with the same target.
        var request = new NewBaseRequest { Client = "x", Kind = ConnectionKind.Server, ConnectionString = pasted.ConnectionString };
        Assert.Equal(pasted.Location, InfobaseCreation.ToEntry(request, "x", "", "").Location);
    }

    [Theory]
    [InlineData(@"File=""D:\Bases\Demo"";", @"D:\Bases\Demo")]
    [InlineData(@"Connect=File='D:\Bases\Demo''s';", @"D:\Bases\Demo's")]
    [InlineData("File=\"/home/user/bases/demo\";Usr=\"Admin\";", "/home/user/bases/demo")]
    public void PastedFileStringGivesTheFolder(string text, string folder)
    {
        var pasted = BaseClipboard.Paste(text, ConnectionKind.File);
        Assert.Equal(folder, pasted.Location);
        Assert.Equal("", pasted.ConnectionString);
        Assert.Equal(text.Contains("Usr=") ? BaseClipboard.IgnoredFileParameters : null, pasted.Notice);
    }

    [Fact] public void PastedFileStringWithAPasswordSaysItIsIgnoredWithoutTheValues()
    {
        var pasted = BaseClipboard.Paste("File=\"D:\\Bases\\Demo\";Usr=\"Admin\";Pwd=\"Synthetic-Passw0rd\";", ConnectionKind.File);
        Assert.Equal("D:\\Bases\\Demo", pasted.Location);
        Assert.Equal(BaseClipboard.IgnoredFileParameters, pasted.Notice);
        Assert.DoesNotContain("Synthetic-Passw0rd", pasted.Notice); Assert.DoesNotContain("Admin", pasted.Notice);
        Assert.Null(BaseClipboard.Paste("Srvr=\"demo-server\";Ref=\"Nordwind\";", ConnectionKind.Server).Notice);
    }

    [Theory]
    [InlineData(null, ConnectionKind.Server)]
    [InlineData("   ", ConnectionKind.Server)]
    [InlineData("Connect=", ConnectionKind.File)]
    [InlineData("просто текст", ConnectionKind.Server)]
    [InlineData("Srvr=\"demo-server", ConnectionKind.Server)]
    [InlineData("Srvr=\"demo-server\";", ConnectionKind.Server)]
    [InlineData("Srvr=\"demo-server\";Ref=\"\";", ConnectionKind.Server)]
    [InlineData(@"File=""D:\Bases\Demo"";", ConnectionKind.Server)]
    [InlineData(@"Srvr=""demo-server"";Ref=""Nordwind"";File=""D:\Bases\Demo"";", ConnectionKind.Server)]
    [InlineData(@"Srvr=""srv\test"";Ref=""Nordwind"";", ConnectionKind.Server)]
    [InlineData("Srvr=\"demo-server\";Ref=\"Nordwind\";", ConnectionKind.File)]
    [InlineData("ws=\"http://demo-server/nordwind\";", ConnectionKind.File)]
    [InlineData("File=\"D:\\Bases\\Demo\";\nFile=\"D:\\Bases\\Other\";", ConnectionKind.File)]
    [InlineData("ws=\"http://demo-server/nordwind\";", ConnectionKind.Web)]
    public void PastedTextWithoutTheBaseIsRefused(string? text, ConnectionKind kind)
    {
        var error = Assert.Throws<ArgumentException>(() => BaseClipboard.Paste(text, kind));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }
}
