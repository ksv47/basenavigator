using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>The page «Блокировка»: the lock file 1Cv8.cdn of a file base.</summary>
public sealed class InfobaseLockTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-infobase-lock-" + Guid.NewGuid().ToString("N"));
    public InfobaseLockTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string LockFile => Path.Combine(root, "1Cv8.cdn");

    // Written by 1С 8.3 with the standard subsystems («Блокировка работы пользователей»), 06.10.2026; shortened message.
    private const string Platform = "{1,20261006223200,20261007000000,\"Работы\r\n\"\"срочные\"\"\r\n\r\nДля администратора:\r\nENTERPRISE /F\"\"X:\\Bases\\Demo\"\" /UC КодРазрешения\",\"123\",\"2026.10.06 22:16:52 1\",0}";

    [Fact] public void ReadsThePlatformFileAndWritesItBackUnchanged()
    {
        File.WriteAllText(LockFile, Platform, new UTF8Encoding(true));
        var read = Assert.IsType<InfobaseLock>(InfobaseLock.Read(root));
        Assert.True(read.On);
        Assert.Equal(new DateTime(2026, 10, 6, 22, 32, 0), read.Start);
        Assert.Equal(new DateTime(2026, 10, 7), read.End);
        Assert.StartsWith("Работы\r\n\"срочные\"\r\n\r\n", read.Message);
        Assert.Contains("/F\"X:\\Bases\\Demo\" /UC", read.Message);
        Assert.Equal("123", read.Code);
        Assert.Equal("2026.10.06 22:16:52 1", read.Parameter);
        Assert.Equal("0", read.Tail);
        Assert.False(read.Framed);
        // A changed code keeps the parameter of the standard subsystems and the platform's last field.
        (read with { Code = "456" }).Write(root, Platform);
        var bytes = File.ReadAllBytes(LockFile);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        // The text set by 1С is not framed with the original's «=» lines.
        Assert.Equal(Platform.Replace(",\"123\",", ",\"456\","), text);
        var again = InfobaseLock.Read(root)!;
        Assert.Equal(read with { Code = "456" }, again);
    }

    [Fact] public void FramesTheMessageAsTheOriginalAndShowsItAsTyped()
    {
        var typed = new InfobaseLock(true, new DateTime(2026, 10, 6, 14, 30, 0), null, "Проводятся \"работы\",\n{вход} = закрыт", "a\"b");
        typed.Write(root, null);
        var text = File.ReadAllText(LockFile, Encoding.UTF8);
        Assert.Equal("{1,20261006143000,00010101000000,\"" + new string('=', 50) + "\r\nПроводятся \"\"работы\"\",\r\n{вход} = закрыт\r\n" +
            new string('=', 50) + "\",\"a\"\"b\",\"\"}", text);
        var read = InfobaseLock.Read(root)!;
        Assert.Equal("Проводятся \"работы\",\r\n{вход} = закрыт", read.Message);
        Assert.Null(read.End);
        Assert.Equal("a\"b", read.Code);
        Assert.Equal("", read.Parameter); Assert.Equal("", read.Tail);
    }

    [Fact] public void ReadsTheOriginalFileWithUndoubledQuotes()
    {
        // StartManager 1.4 wrote quotes as typed; a quote ends a string only before a comma or the brace.
        File.WriteAllText(LockFile, "{1,20261006143000,20261006150000,\"Работы \"срочные\" идут\",\"1\"2\",\"\"}", new UTF8Encoding(true));
        var read = InfobaseLock.Read(root)!;
        Assert.Equal("Работы \"срочные\" идут", read.Message);
        Assert.Equal("1\"2", read.Code);
    }

    [Theory]
    [InlineData("")]
    // Undoubled quotes before a comma: the fields could have moved (the code would become «потом"»), so damaged, not guessed.
    [InlineData("{1,00010101000000,00010101000000,\"Работы \"срочные\", потом\",\"123\",\"BSP\",0}")]
    [InlineData("{1,00010101000000,00010101000000,\"a\"b\",\"c\",\"123\",\"\"}")]
    // Message, code or parameter not quoted; a quoted date; an empty field; a nested record before the tail.
    [InlineData("{1,00010101000000,00010101000000,m,\"c\",\"\"}")]
    [InlineData("{1,00010101000000,00010101000000,\"m\",123,\"\"}")]
    [InlineData("{1,\"00010101000000\",00010101000000,\"m\",\"c\",\"\"}")]
    [InlineData("{1,00010101000000,00010101000000,\"m\",\"c\",\"\",}")]
    [InlineData("{1,00010101000000,00010101000000,\"m\",\"c\",{1}}")]
    [InlineData("{1,20261006143000}")]
    [InlineData("{1,2026100614300,20261006150000,\"m\",\"c\",\"\"}")]
    [InlineData("{1,20261306143000,20261006150000,\"m\",\"c\",\"\"}")]
    [InlineData("{2,20261006143000,20261006150000,\"m\",\"c\",\"\"}")]
    [InlineData("{1,20261006143000,20261006150000,\"m,\"c\",\"\"")]
    [InlineData("{1,20261006143000,20261006150000,\"m\",\"c\",\"\"} x")]
    public void RejectsADamagedFile(string text)
    {
        File.WriteAllText(LockFile, text, new UTF8Encoding(true));
        Assert.Contains("повреждён", Assert.Throws<InvalidDataException>(() => InfobaseLock.Read(root)).Message);
    }

    [Fact] public void NoFileIsNoLockAndRemovalDeletesTheFile()
    {
        Assert.Null(InfobaseLock.Read(root));
        InfobaseLock.Remove(root, null);
        new InfobaseLock(true, null, null, "m", "").Write(root, null);
        Assert.True(File.Exists(LockFile));
        InfobaseLock.Remove(root, File.ReadAllText(LockFile, Encoding.UTF8));
        Assert.False(File.Exists(LockFile));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact] public void WritingToAMissingFolderFailsWithoutCreatingIt()
    {
        var missing = Path.Combine(root, "нет");
        Assert.Throws<DirectoryNotFoundException>(() => new InfobaseLock(true, null, null, "m", "").Write(missing, null));
        Assert.False(Directory.Exists(missing));
    }

    [Fact] public void ExpandsTheOriginalPlaceholders()
    {
        var start = new DateTime(2026, 10, 6, 14, 30, 0);
        Assert.Equal("с 06.10.2026 14:30:00 по <Дата не определена>; 06.10.2026 14:30:00",
            InfobaseLock.Expand("с {ДатаНач} по {датакон}; {DateB}", start, null));
    }

    [Fact] public void DescribesTheState()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0);
        Assert.Equal("Блокировка действует.", new InfobaseLock(true, null, null, "", "").Describe(now));
        Assert.StartsWith("Блокировка начнётся 06.10.2026 13:00", new InfobaseLock(true, now.AddHours(1), null, "", "").Describe(now));
        Assert.StartsWith("Блокировка истекла 06.10.2026 11:00", new InfobaseLock(true, null, now.AddHours(-1), "", "").Describe(now));
        Assert.Equal("В файле блокировка выключена.", new InfobaseLock(false, null, null, "", "").Describe(now));
    }

    [Fact] public void AChangeByAnotherProgramIsNotOverwritten()
    {
        // Changed before the save: refused at once, nothing written.
        File.WriteAllText(LockFile, Platform, new UTF8Encoding(true));
        var mine = new InfobaseLock(true, null, null, "мой", "1");
        Assert.Throws<InfobaseLockChangedException>(() => mine.Write(root, null));
        Assert.Throws<InfobaseLockChangedException>(() => InfobaseLock.Remove(root, null));
        Assert.Equal(Platform, File.ReadAllText(LockFile, Encoding.UTF8));
        // Changed while the new file was being staged: the last comparison refuses the replacement, no temporary file stays.
        const string other = "{1,00010101000000,00010101000000,\"чужой\",\"2\",\"\"}";
        InfobaseLock.Staged = path => File.WriteAllText(path, other, new UTF8Encoding(true));
        try { Assert.Throws<InfobaseLockChangedException>(() => mine.Write(root, Platform)); }
        finally { InfobaseLock.Staged = null; }
        Assert.Equal(other, File.ReadAllText(LockFile, Encoding.UTF8));
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact] public void ExpandsTheEnglishPlaceholders() =>
        Assert.Equal("<Дата не определена> – 07.10.2026 00:00:00", InfobaseLock.Expand("{dateb} – {DateE}", null, new DateTime(2026, 10, 7)));
}
