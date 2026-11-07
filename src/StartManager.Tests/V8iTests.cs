using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class V8iTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "v8i-tests-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "ibases.v8i");
    public V8iTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private V8iStore Write(string content)
    { File.WriteAllText(PathName, content, new UTF8Encoding(false)); return new(PathName); }
    private const string Fixture = "; comment\r\n [Учет [2026]] \r\nConnect=File=\"C:\\base\";Usr=\"sample\";\r\nID=22222222-2222-2222-2222-222222222222\r\nFolder=/Работа\r\nCustom=retain\r\nVersion=8.3\r\n[Работа]\r\nFolder=/\r\nID=11111111-1111-1111-1111-111111111111\r\nUnknown=group-value\r\n[Web]\r\nConnect=wsdl=\"https://example.invalid/base\";\r\nFolder=/\r\nWebSetting=keep\r\n";

    [Fact] public void OpeningAndNoOpDoNotChangeBytes()
    {
        var store = Write(Fixture); var bytes = File.ReadAllBytes(PathName);
        var library = store.Load(new());
        Assert.Equal("Учет [2026]", Assert.Single(library.Bases).Name);
        Assert.Single(store.Warnings);
        store.Save(library);
        Assert.Equal(bytes, File.ReadAllBytes(PathName)); Assert.False(File.Exists(PathName + ".bak"));
    }
    [Theory]
    // A quote, a «;» and spaces inside values; the key spelling and the quoting of the other parameters stay.
    [InlineData("File=\"C:\\base\";Usr=\"Иван Петров\";Pwd=\"a\"\"b;c\";", "File=\"D:\\new base\";Usr=\"Иван Петров\";Pwd=\"a\"\"b;c\";")]
    [InlineData("Srvr=\"srv\";Ref=\"erp\";Usr=admin;", "File=\"D:\\new base\";Usr=admin;")]
    [InlineData("File=C:\\base;", "File=\"D:\\new base\";")]
    public void ConnectIsRebuiltInThe1CFormat(string before, string expected)
    {
        var store = Write("[Base]\r\nConnect=" + before + "\r\nID=22222222-2222-2222-2222-222222222222\r\n");
        var library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { Kind = ConnectionKind.File, Location = "D:\\new base" }] });
        Assert.Contains("Connect=" + expected + "\r\n", File.ReadAllText(PathName));
        Assert.Equal("D:\\new base", new V8iStore(PathName).Load(new()).Bases[0].Location);
    }

    [Fact] public void EditingPreservesExtraPropertiesUnknownEntriesAndBackup()
    {
        var store = Write(Fixture); var library = store.Load(new());
        var entry = library.Bases[0] with { Name = "Изменено", Location = "D:\\new" };
        store.Save(library with { Bases = [entry] });
        var text = File.ReadAllText(PathName);
        Assert.Contains("Custom=retain", text); Assert.Contains("WebSetting=keep", text);
        Assert.Contains("sample", text); Assert.Contains("Unknown=group-value", text);
        Assert.Equal(Fixture, File.ReadAllText(PathName + ".bak"));
        Assert.Equal(entry, Assert.Single(store.Load(new()).Bases));
        store.Save(store.Load(new()) with { Bases = [] });
        Assert.DoesNotContain("[Изменено]", File.ReadAllText(PathName));
        Assert.Contains("[Web]", File.ReadAllText(PathName));
    }
    [Fact] public void ConcurrentChangeIsNotOverwritten()
    {
        var store = Write(Fixture); var library = store.Load(new());
        File.AppendAllText(PathName, "; external edit");
        Assert.Throws<ListChangedException>(() => store.Save(library with { Bases = [] }));
        Assert.EndsWith("; external edit", File.ReadAllText(PathName));
        Assert.False(File.Exists(PathName + ".bak")); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact] public void BackupFailureDoesNotChangeSource()
    {
        var store = Write(Fixture); var library = store.Load(new()); Directory.CreateDirectory(PathName + ".bak");
        Assert.ThrowsAny<IOException>(() => { try { store.Save(library with { Bases = [] }); } catch (UnauthorizedAccessException ex) { throw new IOException("denied", ex); } });
        Assert.Equal(Fixture, File.ReadAllText(PathName)); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact] public void RenameKeepsGroupIdentityAndMetadata()
    {
        var store = Write(Fixture); var library = store.Load(new());
        store.Save(Groups.Rename(library, "Работа", "Проекты/Работа"), "Работа", "Проекты/Работа");
        var text = File.ReadAllText(PathName);
        Assert.Contains("ID=11111111-1111-1111-1111-111111111111", text);
        Assert.Contains("Unknown=group-value", text);
        Assert.Equal("Проекты/Работа", store.Load(new()).Bases[0].Group);
    }
    [Fact] public void AddServerAndSwitchConnectionType()
    {
        var store = Write(""); var library = store.Load(new());
        var entry = new BaseEntry { Name = "Server", Kind = ConnectionKind.Server, Location = "host\\ref", Group = "A/B" };
        store.Save(library with { Bases = [entry] });
        library = store.Load(new()); Assert.Equal("host\\ref", library.Bases[0].Location);
        Assert.Contains("A/B", library.Groups);
        store.Save(library with { Bases = [library.Bases[0] with { Kind = ConnectionKind.File, Location = directory, Group = "" }] });
        library = store.Load(new()); Assert.Equal(directory, library.Bases[0].Location);
        Assert.DoesNotContain("srvr=", File.ReadAllText(PathName).ToLowerInvariant());
        store.Save(library with { Groups = [] }); Assert.Empty(store.Load(new()).Groups);
    }
    [Theory] [InlineData("utf-8")] [InlineData("utf-16")] [InlineData("utf-16BE")] [InlineData("windows-1251")]
    public void EncodingSurvivesEdits(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(name); File.WriteAllText(PathName, Fixture, encoding);
        var store = new V8iStore(PathName); var library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { Name = "Новое" }] });
        Assert.Contains("[Новое]", encoding.GetString(File.ReadAllBytes(PathName)));
        Assert.Equal("Новое", new V8iStore(PathName).Load(new()).Bases[0].Name);
    }
    [Fact] public void MissingIdsStayStableAfterEditAndReload()
    {
        var store = Write("[Base]\nConnect=File=\"/tmp/base\";\n"); var library = store.Load(new());
        var id = library.Bases[0].Id;
        store.Save(library with { Bases = [library.Bases[0] with { Name = "Renamed" }] });
        Assert.Equal(id, new V8iStore(PathName).Load(new()).Bases[0].Id);
    }
    [Theory]
    [InlineData("[A]\nConnect=File=x;\nID=22222222-2222-2222-2222-222222222222\n[B]\nConnect=File=y;\nID=22222222-2222-2222-2222-222222222222")]
    [InlineData("[A]\nFolder=/\n[A]\nFolder=/")]
    public void AmbiguousIdentityRejected(string content) => Assert.Throws<InvalidDataException>(() => Write(content).Load(new()));
    [Fact] public void UnknownConnectionBlocksRemovalOrRenameOfItsGroup()
    {
        var store = Write("[A]\nFolder=/\n[Web]\nFolder=/A\nConnect=wsdl=https://example.invalid;");
        var library = store.Load(new());
        Assert.Throws<InvalidOperationException>(() => store.Save(library with { Groups = [] }));
        Assert.Throws<InvalidOperationException>(() => store.Save(Groups.Rename(library, "A", "B"), "A", "B"));
    }
    [Fact] public void LocalClientOverridesAreNotWrittenIntoSharedList()
    {
        var store = Write(Fixture); var library = store.Load(new());
        library = library with { Bases = [library.Bases[0] with { Executable = "/private/client" }] };
        store.Save(library); Assert.Equal(Fixture, File.ReadAllText(PathName));
        Assert.Equal("/private/client", store.Load(library).Bases[0].Executable);
    }
    [Fact] public void SaveRequiresLoadAndRejectsNewlineInName()
    {
        var store = Write(Fixture); Assert.Throws<InvalidOperationException>(() => store.Save(new()));
        var library = store.Load(new());
        Assert.Throws<ArgumentException>(() => store.Save(library with { Bases = [library.Bases[0] with { Name = "bad\n[entry]" }] }));
    }
    [Fact] public void Unsupported77AndImplicitGroupsSurviveUnrelatedEdits()
    {
        var text = "[Old]\nVersion=7.7\nConnect=File=old;\nFolder=/Legacy\n[New]\nVersion=8.3\nConnect=File=current;\nFolder=/A/B\n";
        var store = Write(text); var library = store.Load(new());
        Assert.Single(library.Bases); Assert.Single(store.Warnings);
        store.Save(library); Assert.Equal(text, File.ReadAllText(PathName));
        store.Save(library with { Bases = [library.Bases[0] with { Name = "New name" }] });
        Assert.Contains("[Old]\nVersion=7.7\nConnect=File=old;\nFolder=/Legacy", File.ReadAllText(PathName));
    }
    [Fact] public void FailedReloadDisablesSaveUntilSuccessfulLoad()
    {
        var store = Write(Fixture); var library = store.Load(new());
        File.Delete(PathName); Assert.Throws<FileNotFoundException>(() => store.Load(library));
        Assert.Throws<InvalidOperationException>(() => store.Save(library));
    }
    [Fact] public void MixedLineEndingsLoadAndNoOpPreservesBytes()
    {
        var text = "[Base]\nConnect=File=base;\r\nVersion=8.3\r";
        var store = Write(text); var library = store.Load(new()); Assert.Single(library.Bases);
        store.Save(library); Assert.Equal(text, File.ReadAllText(PathName));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void DeleteThenEditWithoutReloadKeepsUnknownProperties(bool deleteGroup)
    {
        var first = deleteGroup ? "[Empty]\nFolder=/\n" : "[Alpha]\nConnect=File=alpha;\n";
        var store = Write(first + "[Beta]\nConnect=Srvr=host;Ref=ref;Usr=sample;\nCustom=keep\nOrderInList=7\n");
        var library = store.Load(new()); var beta = library.Bases.Last();
        library = deleteGroup ? library with { Groups = [] } : library with { Bases = [beta] };
        var saved = store.Save(library);
        Assert.Equal(beta.Id, Assert.Single(saved.Bases).Id);
        // Intentionally use the caller's previous value, matching the old GUI contract.
        store.Save(library with { Bases = [beta with { Name = "Renamed" }] });
        Assert.Contains("Custom=keep", File.ReadAllText(PathName)); Assert.Contains("Usr=sample", File.ReadAllText(PathName));
        Assert.Equal(beta.Id, new V8iStore(PathName).Load(new()).Bases[0].Id);
        var latest = store.Save(saved with { Bases = [beta with { Name = "Third" }] });
        Assert.Equal("Third", latest.Bases[0].Name); Assert.Contains("[Third]", File.ReadAllText(PathName));
    }
    [Fact] public void LocalClientChangeWithoutIdDoesNotWriteSharedFile()
    {
        const string text = "[Base]\nConnect=File=base;\n";
        var store = Write(text); var library = store.Load(new());
        var saved = store.Save(library with { Bases = [library.Bases[0] with { Executable = "/private/client" }] });
        Assert.Equal(text, File.ReadAllText(PathName)); Assert.False(File.Exists(PathName + ".bak"));
        Assert.Equal("/private/client", store.Load(saved).Bases[0].Executable);
    }
    [Fact] public void WriterLockPreventsSaveWithoutChangingBytes()
    {
        var store = Write(Fixture); var library = store.Load(new());
        using (var locked = new FileStream(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.Save(library with { Bases = [] }));
        Assert.Equal(Fixture, File.ReadAllText(PathName));
    }
    [Fact] public void HardlinkReceivesChangesAndKeepsSharedIdentity()
    {
        var store = Write(Fixture); var target = Path.Combine(directory, "target.v8i");
        File.Move(PathName, target);
        if (OperatingSystem.IsWindows()) Assert.True(CreateHardLink(PathName, target, IntPtr.Zero));
        else Assert.Equal(0, Link(target, PathName));
        var library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { Name = "Renamed" }] });
        Assert.Equal(File.ReadAllBytes(PathName), File.ReadAllBytes(target));
        Assert.Contains("[Renamed]", File.ReadAllText(target));
        File.AppendAllText(target, "; same file"); Assert.EndsWith("; same file", File.ReadAllText(PathName));
    }
    [Fact] public void UnixSymlinkAndModeSurviveSave()
    {
        if (OperatingSystem.IsWindows()) return; // Windows symlink creation needs OS privileges.
        var store = Write(Fixture); var target = Path.Combine(directory, "target.v8i"); File.Move(PathName, target);
        File.CreateSymbolicLink(PathName, target);
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(target, mode);
        var library = store.Load(new()); store.Save(library with { Bases = [] });
        Assert.NotNull(new FileInfo(PathName).LinkTarget);
        Assert.Equal(mode, File.GetUnixFileMode(target));
        Assert.Equal(File.ReadAllBytes(PathName), File.ReadAllBytes(target));
    }
    [Fact] public void WindowsAttributesAndAlternateStreamSurviveSave()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Write(Fixture); var library = store.Load(new());
        File.SetAttributes(PathName, File.GetAttributes(PathName) | FileAttributes.Hidden);
        File.WriteAllText(PathName + ":audit", "metadata");
        store.Save(library with { Bases = [] });
        Assert.True(File.GetAttributes(PathName).HasFlag(FileAttributes.Hidden));
        Assert.Equal("metadata", File.ReadAllText(PathName + ":audit"));
    }
    [Fact] public void UnrepresentableNamePreservesBytesAndAllowsRetry()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(PathName, "[База]\nConnect=File=base;\nCustom=keep\n", Encoding.GetEncoding(1251));
        var store = new V8iStore(PathName); var library = store.Load(new()); var bytes = File.ReadAllBytes(PathName);
        var error = Assert.Throws<InvalidDataException>(() => store.Save(library with { Bases = [library.Bases[0] with { Name = "漢字" }] }));
        Assert.Contains("кодировке", error.Message); Assert.Equal(bytes, File.ReadAllBytes(PathName));
        Assert.False(File.Exists(PathName + ".bak"));
        store.Save(library with { Bases = [library.Bases[0] with { Name = "Новое" }] });
        Assert.Contains("Custom=keep", File.ReadAllText(PathName, Encoding.GetEncoding(1251)));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string target, IntPtr security);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string target, string name);

    // The on-disk marker of a write in progress: the text hashes before and after it. A crash leaves it next to a torn file.
    private void Interrupted(byte[] before, byte[] after, string? list = null) => File.WriteAllText((list ?? PathName) + ".writing",
        "BaseNavigator write in progress\nbefore=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)) +
        "\nafter=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(after)) + "\nbackup=" + (list ?? PathName) + ".bak\n");
    // A save that stopped half-way: the head of the new text over the tail of the old one, the marker and the .bak stay.
    private (byte[] Before, byte[] After, byte[] Torn) TearSave()
    {
        var store = Write(Fixture); var library = store.Load(new());
        var before = File.ReadAllBytes(PathName);
        store.Save(library with { Bases = [library.Bases[0] with { Name = "Новое длинное название базы после правки" }] });
        var after = File.ReadAllBytes(PathName);
        Assert.False(File.Exists(PathName + ".writing"));
        var torn = after[..(after.Length / 2)].Concat(before[(after.Length / 2)..]).ToArray();
        File.WriteAllBytes(PathName, torn); Interrupted(before, after);
        return (before, after, torn);
    }

    [Fact] public void InterruptedWriteBlocksLoadAndRecoversTheBackup()
    {
        var (before, _, torn) = TearSave();
        var error = Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new()));
        Assert.True(error.BackupMatches); Assert.Equal(torn, error.Current);
        Assert.Equal(torn, File.ReadAllBytes(PathName));
        V8iStore.RecoverInterruptedWrite(PathName, error.Current);
        Assert.Equal(before, File.ReadAllBytes(PathName)); Assert.Equal(before, File.ReadAllBytes(PathName + ".bak"));
        Assert.False(File.Exists(PathName + ".writing"));
        Assert.Equal("Учет [2026]", Assert.Single(new V8iStore(PathName).Load(new()).Bases).Name);
    }

    [Fact] public void RecoveryRefusesAChangedFileOrAForeignBackup()
    {
        var (_, _, torn) = TearSave();
        var seen = Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new())).Current;
        File.AppendAllText(PathName, "; external edit");
        var changed = File.ReadAllBytes(PathName);
        Assert.Throws<IOException>(() => V8iStore.RecoverInterruptedWrite(PathName, seen));
        Assert.Throws<IOException>(() => V8iStore.DismissInterruptedWrite(PathName, seen));
        Assert.Equal(changed, File.ReadAllBytes(PathName)); Assert.True(File.Exists(PathName + ".writing"));
        File.WriteAllBytes(PathName, torn); File.WriteAllText(PathName + ".bak", "[Other]\r\n");
        var error = Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new()));
        Assert.False(error.BackupMatches);
        Assert.Throws<IOException>(() => V8iStore.RecoverInterruptedWrite(PathName, error.Current));
        Assert.Equal(torn, File.ReadAllBytes(PathName)); Assert.Equal("[Other]\r\n", File.ReadAllText(PathName + ".bak"));
    }

    [Fact] public void DismissKeepsTheFileAsItIs()
    {
        var (_, _, torn) = TearSave();
        var error = Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new()));
        V8iStore.DismissInterruptedWrite(PathName, error.Current);
        Assert.Equal(torn, File.ReadAllBytes(PathName)); Assert.False(File.Exists(PathName + ".writing"));
        new V8iStore(PathName).Load(new());
    }

    [Theory, InlineData(true), InlineData(false)]
    public void MarkerOfAFinishedOrRolledBackWriteIsReadPastAndGoesWithTheNextSave(bool finished)
    {
        var (before, after, _) = TearSave();
        File.WriteAllBytes(PathName, finished ? after : before);
        var store = new V8iStore(PathName); var library = store.Load(new());
        // The text before the write: a reader does not remove the marker, the same state is the start of a write another window may
        // be doing right now. The text after it: the write is over, the reader removes the leftover (Grok full audit 24.10, P2-1).
        Assert.Equal(!finished, File.Exists(PathName + ".writing"));
        Assert.Null(store.MarkerNotice);
        store.Save(library with { Bases = [library.Bases[0] with { Name = "Следующее сохранение" }] });
        Assert.False(File.Exists(PathName + ".writing"));
    }

    // Grok full audit 24.10, P2-1: a finished save left its marker (the delete failed or the program ended first); then 1C or another
    // computer changed the shared list. That good list was taken for a torn write and «Восстановить из .bak» put the older text back.
    [Fact] public void ALeftoverMarkerOfAFinishedWriteDoesNotMakeALaterEditLookTorn()
    {
        var (_, after, _) = TearSave();
        File.WriteAllBytes(PathName, after);
        Assert.Single(new V8iStore(PathName).Load(new()).Bases);
        File.WriteAllText(PathName, "[Изменено в 1С]\r\nConnect=File=\"C:\\other\";\r\nID=99999999-9999-9999-9999-999999999999\r\n", new UTF8Encoding(true));
        Assert.Equal("Изменено в 1С", Assert.Single(new V8iStore(PathName).Load(new()).Bases).Name);
    }

    // Codex re-check 25.10, R3: a restore of the same bytes made a write with equal hashes before and after; its leftover marker
    // was read past for ever, and the next outside edit looked torn. Such a restore writes nothing now, and a leftover marker of
    // equal hashes (an earlier build) is removed like that of any finished write.
    [Fact] public void ASameBytesWriteLeavesNoMarkerThatMakesALaterEditLookTorn()
    {
        var store = Write(Fixture); store.Load(new());
        var bytes = File.ReadAllBytes(PathName);
        store.Restore(bytes);
        Assert.False(File.Exists(PathName + ".bak")); Assert.False(File.Exists(PathName + ".writing"));
        Assert.Equal(bytes, File.ReadAllBytes(PathName));
        File.WriteAllBytes(PathName + ".bak", bytes); Interrupted(bytes, bytes);
        new V8iStore(PathName).Load(new());
        Assert.False(File.Exists(PathName + ".writing"));
        File.WriteAllText(PathName, "[Изменено в 1С]\r\nConnect=File=\"C:\\other\";\r\nID=99999999-9999-9999-9999-999999999999\r\n", new UTF8Encoding(true));
        Assert.Equal("Изменено в 1С", Assert.Single(new V8iStore(PathName).Load(new()).Bases).Name);
    }

    // Grok audit of the fixes 25.10, P3: the list cannot be held alone (another program has it open). The marker is not removed
    // without that hold, but the status line says it is there; once the list is free the next load removes it.
    [Fact] public void ALeftoverMarkerOfABusyListIsKeptAndTold()
    {
        var (_, after, _) = TearSave();
        File.WriteAllBytes(PathName, after);
        V8iStore store;
        using (new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            store = new V8iStore(PathName); store.Load(new());
        }
        Assert.Contains("список занят другой программой", store.MarkerNotice);
        Assert.Contains(PathName + ".writing", store.MarkerNotice);
        Assert.True(File.Exists(PathName + ".writing"));
        var next = new V8iStore(PathName); next.Load(new());
        Assert.Null(next.MarkerNotice); Assert.False(File.Exists(PathName + ".writing"));
    }

    [Fact] public void ALeftoverMarkerThatCannotBeRemovedIsTold()
    {
        var (_, after, _) = TearSave();
        File.WriteAllBytes(PathName, after);
        FileStream? held = null;
        V8iStore.BeforeMarkerRemoval = marker =>
        {
            // Windows: an open marker is not deleted; Linux: a folder without the right to write keeps it.
            if (OperatingSystem.IsWindows()) held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);
            else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        };
        try
        {
            var store = new V8iStore(PathName); store.Load(new());
            Assert.Contains("не удалена", store.MarkerNotice);
            Assert.True(File.Exists(PathName + ".writing"));
        }
        finally
        {
            V8iStore.BeforeMarkerRemoval = null; held?.Dispose();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        // Removed by the next load once it can be.
        var next = new V8iStore(PathName); next.Load(new());
        Assert.Null(next.MarkerNotice); Assert.False(File.Exists(PathName + ".writing"));
    }

    [Fact] public void ReaderDoesNotRemoveTheMarkerOfAWriteInProgress()
    {
        // The writer holds the list, its .bak and marker are out, no byte of the list has changed yet.
        var store = Write(Fixture); store.Load(new());
        var before = File.ReadAllBytes(PathName);
        File.WriteAllBytes(PathName + ".bak", before); Interrupted(before, Encoding.UTF8.GetBytes("[Новое]\r\n"));
        Assert.Single(new V8iStore(PathName).Load(new()).Bases);
        Assert.True(File.Exists(PathName + ".writing"));
        // The write then breaks off: the next load still sees it.
        File.WriteAllBytes(PathName, Encoding.UTF8.GetBytes("[Но"));
        Assert.True(Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new())).BackupMatches);
    }

    [Fact] public void SymbolicNameFindsTheMarkerOfThePhysicalFile()
    {
        var alias = Path.Combine(directory, "alias.v8i");
        var (before, _, torn) = TearSave();
        try { File.CreateSymbolicLink(alias, PathName); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException) { return; } // no symlink right
        var error = Assert.Throws<InterruptedWriteException>(() => new V8iStore(alias).Load(new()));
        Assert.True(error.BackupMatches);
        // The .bak named in the marker is used, not one next to the alias.
        V8iStore.RecoverInterruptedWrite(alias, error.Current);
        Assert.Equal(before, File.ReadAllBytes(PathName)); Assert.False(File.Exists(alias + ".bak"));
        Assert.NotEqual(torn, File.ReadAllBytes(alias));
    }

    [Fact] public void BackupNamedByAnotherClientIsFoundNextToTheListByItsHash()
    {
        // The writer saw the share under another name (another computer, another OS); the .bak next to the list is the same file.
        var (before, after, torn) = TearSave();
        var foreign = OperatingSystem.IsWindows() ? "/mnt/share/ibases.v8i.bak" : @"X:\shared\ibases.v8i.bak";
        File.WriteAllText(PathName + ".writing", File.ReadAllText(PathName + ".writing").Replace("backup=" + PathName + ".bak", "backup=" + foreign));
        Assert.Contains(foreign, File.ReadAllText(PathName + ".writing"));
        var error = Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new()));
        Assert.True(error.BackupMatches);
        V8iStore.RecoverInterruptedWrite(PathName, error.Current);
        Assert.Equal(before, File.ReadAllBytes(PathName));
        // A copy with another text is never taken, wherever it lies.
        File.WriteAllBytes(PathName, torn); Interrupted(before, after);
        File.WriteAllText(PathName + ".bak", "[Other]\r\n");
        Assert.False(Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new())).BackupMatches);
    }

    [Fact] public void ExportNeverWritesOverTheMarker()
    {
        var store = Write(Fixture); store.Load(new());
        Assert.Equal(Path.GetFullPath(PathName + ".writing"), Path.GetFullPath(store.MarkerPathName));
        Assert.ThrowsAny<Exception>(() => ListMaintenance.Export(store.MarkerPathName, Encoding.UTF8.GetBytes(Fixture), [store.MarkerPathName]));
        Assert.False(File.Exists(PathName + ".writing"));
    }

    [Fact] public void UnreadableMarkerStillOffersTheChoice()
    {
        TearSave();
        using (new FileStream(PathName + ".writing", FileMode.Open, FileAccess.Read, FileShare.None))
            if (OperatingSystem.IsWindows()) Assert.False(Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new())).BackupMatches);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(PathName + ".writing", UnixFileMode.None);
            try { Assert.False(Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new())).BackupMatches); }
            finally { File.SetUnixFileMode(PathName + ".writing", UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }
    }

    [Fact] public void ForeignMarkerIsNotTrusted()
    {
        var store = Write(Fixture); store.Load(new());
        File.WriteAllText(PathName + ".writing", "something else");
        Assert.False(Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new())).BackupMatches);
    }

    [Fact] public void FailedWriteWithRollbackLeavesNoMarker()
    {
        var store = Write(Fixture); var library = store.Load(new());
        // The marker path is taken by a folder: the save stops before the list is touched and the list stays readable.
        Directory.CreateDirectory(PathName + ".writing");
        Assert.ThrowsAny<Exception>(() => store.Save(library with { Bases = [] }));
        Assert.Equal(Fixture, File.ReadAllText(PathName));
        Directory.Delete(PathName + ".writing");
        Assert.Single(new V8iStore(PathName).Load(new()).Bases);
    }
}
