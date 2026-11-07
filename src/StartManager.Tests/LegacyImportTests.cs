using System.IO.Compression;
using System.Text;
using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class LegacyImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "legacy-import-" + Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();
    private string Target => Path.Combine(root, "library.json");
    public LegacyImportTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string Write(string name, string text) { var path = Path.Combine(root, name); File.WriteAllText(path, text); return path; }
    private string ListText => $"[Бухгалтерия]\nID={id}\nConnect=Srvr=\"server\";Ref=\"accounting\";\nFolder=/Работа\nVersion=8.3\n";
    private LegacyImportPreview Preview(string? text = null, LegacyImportOptions? options = null, Library? local = null, bool settings = true) =>
        LegacyImport.Preview(local ?? new(), Write("input.v8i", text ?? ListText), settings ? root : null, options ?? new());
    private string Zip(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(root, "snapshot.v8sx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        return path;
    }

    [Fact]
    public void ImportsSupportedSettingsWithPreviewAndPreservesBothSourceAndDestinationExtras()
    {
        var client = Write("1cv8-test", "never executed");
        Write("v8config.smc", $"[{id}]\nFavorite=1\nV8UseAppFile=true\nV8AppFile={client}\nEntPassword=secret-value\n");
        Write("settings.cnf", $"<Settings><InfoBases><V8DefVersion>8.3</V8DefVersion></InfoBases><Launch><V83AppFile>{client}</V83AppFile><Password>secret-value</Password></Launch></Settings>");
        Write("config.sme", "[Environment]\nFontName=Verdana\nFontSize=10\nDefNodeHeight=22\nSortByAlfa=1\n[MainForm]\nWidth=900\n");
        var store = new LibraryStore(Target);
        store.Save(new Library { Extra = new() { ["future"] = JsonSerializer.SerializeToElement(42) } });
        var before = File.ReadAllBytes(Target);
        var plan = Preview(options: new(true, true, true), local: store.Load());
        Assert.Equal(before, File.ReadAllBytes(Target));
        var entry = Assert.Single(plan.Added);
        Assert.Equal(id, entry.Id); Assert.Equal("Работа", entry.Group); Assert.Equal(client, entry.Executable);
        Assert.Equal(client, plan.Result.Executable); Assert.Equal("Verdana", plan.Result.Appearance.List.Family);
        Assert.Equal(13.33, plan.Result.Appearance.List.Size); Assert.Equal(22, plan.Result.Appearance.MinimumRowHeight);
        Assert.DoesNotContain("secret-value", string.Join("\n", plan.Notices));
        // The page «Аутентификация» is imported now (decision 04.10.2026): the password goes to the vault, not to the notices.
        Assert.DoesNotContain(plan.Notices, x => x.Contains("EntPassword"));
        Assert.True(plan.Passwords.ContainsKey(id));
        var originals = plan.SourceHashes.Keys.ToDictionary(x => x, File.ReadAllBytes);
        var activity = new ActivityStore(Target);
        Assert.Null(LegacyImport.Apply(plan, store, activity).FavoriteError);
        Assert.Equal(id, Assert.Single(activity.Load().Favorites).Id);
        Assert.Equal(42, store.Load().Extra!["future"].GetInt32());
        Assert.Equal(before, File.ReadAllBytes(Target + ".bak"));
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
        var repeated = Preview(options: new(true, true, true), local: store.Load());
        Assert.Empty(repeated.Added); Assert.Equal(1, repeated.Duplicates);
        LegacyImport.Apply(repeated, store, activity);
        Assert.Single(activity.Load().Favorites);
    }

    [Fact]
    public void DuplicateConnectionMapsFavoriteToExistingIdButKeepsItsProperties()
    {
        var existing = new BaseEntry { Name = "Моё имя", Kind = ConnectionKind.Server, Location = "SERVER\\ACCOUNTING", Group = "Моя группа", Executable = "my-client" };
        Write("v8config.smc", $"[{id}]\nFavorite=True\nV8UseAppFile=1\nV8AppFile=missing\n");
        var plan = Preview(local: new Library { Bases = [existing] });
        Assert.Empty(plan.Added); Assert.Equal(1, plan.Duplicates);
        Assert.Equal(existing, Assert.Single(plan.Result.Bases)); Assert.Equal(existing.Id, Assert.Single(plan.Favorites));
        var conflict = Preview(local: new Library { Bases = [existing with { Id = id, Location = "server\\different" }] });
        Assert.Empty(conflict.Added); Assert.Empty(conflict.Favorites);
        Assert.Contains(conflict.Notices, x => x.Contains("Конфликт ID"));
    }

    [Fact]
    public void DisabledOptionsDoNotReadMalformedSettingsOrResetAppearance()
    {
        foreach (var name in new[] { "v8config.smc", "settings.cnf", "config.sme" }) Write(name, "not valid settings");
        var library = new Library { Executable = "existing" };
        var plan = Preview(options: new(false, false, false, false, false, false), local: library);
        Assert.Empty(plan.Favorites); Assert.Same(library.Appearance, plan.Result.Appearance);
        Assert.Equal("existing", plan.Result.Executable); Assert.Single(plan.SourceHashes);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("1251")]
    public void ReadsOriginalEncodings(string format)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = format switch { "utf16le" => Encoding.Unicode, "utf16be" => Encoding.BigEndianUnicode, "1251" => Encoding.GetEncoding(1251), _ => Encoding.UTF8 };
        var path = Path.Combine(root, "encoded.v8i"); File.WriteAllText(path, ListText, encoding);
        Assert.Equal("Бухгалтерия", Assert.Single(LegacyImport.Preview(new(), path, null, new()).Added).Name);
    }

    [Fact]
    public void ReadsSnapshotWithoutExtractingOtherEntries()
    {
        var path = Zip(("ibases.v8i", ListText), ("../outside.txt", "untrusted"));
        var plan = LegacyImport.Preview(new(), path, null, new());
        Assert.Single(plan.Added); Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        Assert.Contains(plan.Notices, x => x.Contains("не извлекались"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    [InlineData("entries")]
    public void RejectsInvalidSnapshot(string mode)
    {
        var entries = mode switch {
            "duplicate" => new[] { ("ibases.v8i", ListText), ("IBASES.V8I", ListText) },
            "oversize" => new[] { ("ibases.v8i", new string('x', LegacyImport.MaximumBytes + 1)) },
            "entries" => Enumerable.Range(0, 17).Select(i => ($"{i}.txt", "x")).ToArray(),
            _ => new[] { ("../ibases.v8i", ListText) }
        };
        Assert.Throws<InvalidDataException>(() => LegacyImport.Preview(new(), Zip(entries), null, new()));
    }

    [Fact]
    public void RejectsOversizedInputAndUnsupportedSource()
    {
        Assert.Throws<InvalidDataException>(() => Preview(new string('x', LegacyImport.MaximumBytes + 1)));
        Assert.Throws<InvalidDataException>(() => Preview("garbage"));
        Assert.Throws<InvalidDataException>(() => Preview(ListText.Replace("Version=8.3", "Version=9.0")));
        Assert.Throws<InvalidDataException>(() => LegacyImport.Preview(new(), Write("input.txt", ListText), null, new()));
        Assert.Throws<FileNotFoundException>(() => LegacyImport.Preview(new(), Path.Combine(root, "missing.v8i"), null, new()));
        Assert.Throws<DirectoryNotFoundException>(() => LegacyImport.Preview(new(), Write("input.v8i", ListText), Path.Combine(root, "missing"), new()));
        Assert.Throws<ArgumentException>(() => Preview(local: new Library { ListPath = "shared.v8i" }));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("missing-appears")]
    [InlineData("target")]
    [InlineData("activity")]
    public void ChangedOrDamagedFilesAbortBeforeDestinationWrite(string mode)
    {
        Write("v8config.smc", $"[{id}]\nFavorite=1\n");
        var store = new LibraryStore(Target); store.Save(new());
        var plan = Preview(local: store.Load()); var before = File.ReadAllBytes(Target);
        var activity = new ActivityStore(Target);
        if (mode == "source") File.AppendAllText(Path.Combine(root, "input.v8i"), ";changed");
        if (mode == "missing-appears") Write("settings.cnf", "<Settings/>");
        if (mode == "target") { new LibraryStore(Target).Save(new Library { Executable = "other-client" }); before = File.ReadAllBytes(Target); }
        if (mode == "activity") File.WriteAllText(activity.PathName, "broken json");
        Assert.ThrowsAny<Exception>(() => LegacyImport.Apply(plan, store, activity));
        Assert.Equal(before, File.ReadAllBytes(Target));
    }

    [Fact]
    public void SourceCannotBeDestinationOrBackup()
    {
        var path = Write("library.v8i", ListText); var plan = LegacyImport.Preview(new(), path, null, new());
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => LegacyImport.Apply(plan, new LibraryStore(path), new ActivityStore(Target)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("<Wrong/>")]
    [InlineData("<!DOCTYPE Settings [<!ENTITY x SYSTEM 'file:///nonexistent'>]><Settings>&x;</Settings>")]
    [InlineData("<Settings>")]
    public void RejectsMalformedOrExternalEntityXml(string xml)
    {
        Write("settings.cnf", xml);
        Assert.ThrowsAny<Exception>(() => Preview());
        Assert.False(File.Exists(Target));
    }

    [Theory]
    [InlineData("FontSize=NaN")]
    [InlineData("FontSize=-1")]
    [InlineData("DefNodeHeight=oops")]
    [InlineData("DefNodeHeight=0")]
    public void InvalidAppearanceKeepsProfileAndAllowsListImport(string value)
    {
        Write("config.sme", "[Environment]\n" + value);
        var local = new Library(); var plan = Preview(options: new(false, false, true), local: local);
        Assert.Single(plan.Added); Assert.Same(local.Appearance, plan.Result.Appearance);
        Assert.Contains(plan.Notices, x => x.Contains("Оформление не перенесено"));
    }

    [Fact]
    public void ClampsAppearanceAndKeepsOtherFontRoles()
    {
        Write("config.sme", "[Environment]\nFontSize=100\nDefNodeHeight=1000");
        var local = new Library(); var plan = Preview(options: new(false, false, true), local: local);
        Assert.Equal(24, plan.Result.Appearance.List.Size); Assert.Equal(64, plan.Result.Appearance.MinimumRowHeight);
        Assert.Equal(local.Appearance.Dialogs, plan.Result.Appearance.Dialogs);
        Write("config.sme", "[Other]\nx=1");
        Assert.Contains(Preview(options: new(false, false, true)).Notices, x => x.Contains("Оформление не перенесено"));
    }

    [Fact]
    public void MissingClientIsNotImportedAndExistingGlobalClientWins()
    {
        Write("v8config.smc", $"[invalid-id]\nx=1\n[{id}]\nV8UseAppFile=1\nV8AppFile=missing\n");
        Write("settings.cnf", "<Settings><InfoBases><V8DefVersion>8.3</V8DefVersion></InfoBases><Launch><V83AppFile>missing</V83AppFile></Launch></Settings>");
        var plan = Preview(); Assert.Empty(plan.Result.Executable); Assert.Empty(plan.Added[0].Executable);
        Assert.Contains(plan.Notices, x => x.Contains("недоступен"));
        Assert.Equal("retained", Preview(local: new Library { Executable = "retained" }).Result.Executable);
        Write("v8config.smc", $"[{id}]\nFavorite=1\n[{id}]\nFavorite=0");
        Assert.Throws<InvalidDataException>(() => Preview());
    }

    [Fact]
    public void AppearanceIgnoresEmptyFamilyAcceptsCommaAndReportsFamilyOnlyChange()
    {
        var appearance = new AppearanceSettings { List = new FontPreference { Family = "Consolas", Size = 16 } };
        var local = new Library { Appearance = appearance };
        Write("config.sme", "[Environment]\nFontName= \nFontSize=8,5");
        var plan = Preview(options: new(false, false, true), local: local);
        Assert.Equal("Consolas", plan.Result.Appearance.List.Family); Assert.Equal(11.33, plan.Result.Appearance.List.Size);
        Assert.Contains(plan.Notices, x => x.Contains("Пустое имя"));
        Write("config.sme", "[Environment]\nFontName=Tahoma");
        plan = Preview(options: new(false, false, true), local: local);
        Assert.Equal("Tahoma", plan.Result.Appearance.List.Family); Assert.Contains(plan.Notices, x => x.Contains("Семейство") && x.Contains("Tahoma"));
        Write("config.sme", "[Environment]\nFontName=" + new string('x', 257));
        plan = Preview(options: new(false, false, true), local: local);
        Assert.Same(appearance, plan.Result.Appearance); Assert.Single(plan.Added);
        Assert.Contains(plan.Notices, x => x.Contains("имя шрифта"));
    }

    [Fact]
    public void SkippedEntriesDoNotCreateGroupsButExplicitEmptyGroupsAreImported()
    {
        var existing = new BaseEntry { Id = id, Name = "existing", Kind = ConnectionKind.Server, Location = "server\\different" };
        var plan = Preview(ListText + "[Пустая]\nFolder=/Пустая\n", local: new Library { Bases = [existing] });
        Assert.DoesNotContain("Работа", plan.Result.Groups); Assert.Contains("Пустая", plan.Result.Groups);
        Assert.Contains(plan.Notices, x => x.Contains("Добавятся группы") && x.Contains("Пустая"));
    }

    [Fact]
    public void WindowsSeparatorsMatchAcrossSystemsAndMissingIdIsExplained()
    {
        var existing = new BaseEntry { Name = "existing", Location = "C:\\bases\\buh" };
        var plan = Preview("[Imported]\nConnect=File=\"C:/bases/buh/\";\n", local: new Library { Bases = [existing] });
        Assert.Empty(plan.Added); Assert.Equal(1, plan.Duplicates);
        Assert.Contains(plan.Notices, x => x.Contains("нет корректного ID"));
        if (!OperatingSystem.IsWindows())
        {
            plan = Preview("[Imported]\nConnect=File=\"/tmp/A:b\";\n", local: new Library { Bases = [existing with { Location = "/tmp/a:b" }] });
            Assert.Single(plan.Added);
        }
        Assert.Throws<InvalidDataException>(() => Preview(ListText + "DefaultVersion=7.7\n"));
    }

    [Fact]
    public void UnknownFieldsInsideExistingBaseSurviveImport()
    {
        var old = new BaseEntry { Name = "existing", Location = root, Extra = new() { ["futureBaseField"] = JsonSerializer.SerializeToElement(new[] { 1, 2 }) } };
        var store = new LibraryStore(Target); store.Save(new Library { Bases = [old] });
        var plan = Preview(local: store.Load()); LegacyImport.Apply(plan, store, new ActivityStore(Target));
        Assert.Equal("[1,2]", store.Load().Bases[0].Extra!["futureBaseField"].GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", ""));
    }

    [Fact]
    public void FavoriteLockFailureReportsPartialSuccessAndRetryDoesNotDuplicateBases()
    {
        Write("v8config.smc", $"[{id}]\nFavorite=1\n");
        var store = new LibraryStore(Target); var activity = new ActivityStore(Target);
        var plan = Preview(local: store.Load());
        using (var locked = new FileStream(activity.PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var result = LegacyImport.Apply(plan, store, activity);
            Assert.NotNull(result.FavoriteError); Assert.Contains("Базы сохранены", result.FavoriteError);
            Assert.Single(store.Load().Bases); Assert.Empty(activity.Load().Favorites);
        }
        var retry = Preview(local: store.Load()); Assert.Empty(retry.Added);
        Assert.Null(LegacyImport.Apply(retry, store, activity).FavoriteError);
        Assert.Single(store.Load().Bases); Assert.Single(activity.Load().Favorites);
    }

    [Fact]
    public void EmptyOptionalSettingsDoNotPreventListImport()
    {
        Write("settings.cnf", "<Settings/>");
        Assert.Single(Preview().Added);
    }

    [Fact]
    public void BulkFavoritesPreservesExistingUnknownMetadataAndHistory()
    {
        var activity = new ActivityStore(Target); var source = ActivityStore.Source(Target, "");
        File.WriteAllText(activity.PathName, JsonSerializer.Serialize(new Activity {
            Favorites = [new(source, id) { Extra = new() { ["custom"] = JsonSerializer.SerializeToElement("retained") } }],
            History = [new(source, id, "Old", false, DateTimeOffset.UtcNow)],
            Extra = new() { ["future"] = JsonSerializer.SerializeToElement(42) }
        }));
        var extra = Guid.NewGuid(); activity.AddFavorites(source, [id, extra, extra]);
        var result = activity.Load(); Assert.Equal(2, result.Favorites.Count); Assert.Single(result.History);
        Assert.Equal("retained", result.Favorites[0].Extra!["custom"].GetString());
        Assert.Equal(42, result.Extra!["future"].GetInt32());
    }
}
