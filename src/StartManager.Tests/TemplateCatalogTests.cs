using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class TemplateCatalogTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-templates-" + Guid.NewGuid().ToString("N"));
    public TemplateCatalogTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    private string Make(string relative, params string[] files)
    {
        var path = Path.Combine(folder, relative.Replace('\\', Path.DirectorySeparatorChar)); Directory.CreateDirectory(path);
        foreach (var file in files) File.WriteAllText(Path.Combine(path, file), "fixture");
        return path;
    }

    [Fact] public void TemplatesAreTheVersionFoldersOfVendorAndSolution()
    {
        var root = Path.Combine(folder, "tmplts");
        var old = Make(@"tmplts\1c\Accounting\3_0_9_5", "1cv8.cf", "1cv8.mft");
        var current = Make(@"tmplts\1c\Accounting\3_0_121_21", "1cv8.cf");
        Make(@"tmplts\1c\Accounting\3_0_121_22", "1cv8.cfu");              // an update cannot make a base
        Make(@"tmplts\1c\Retail\2_3_1_1", "demo.dt", "other.CF");
        Make(@"tmplts\Acme\Shop\1_0", "readme.txt");                        // nothing to make a base from
        Make(@"tmplts\Acme\Shop\2_0", "data.dt");
        Directory.CreateDirectory(Path.Combine(root, "loose folder"));
        File.WriteAllText(Path.Combine(root, "stray.cf"), "x");

        var all = TemplateCatalog.Scan([root]);
        // A version folder with two files (here a dump and a configuration) is two templates, told by the file name.
        Assert.Equal(["1c · Accounting · 3.0.121.21", "1c · Accounting · 3.0.9.5", "1c · Retail · 2.3.1.1 (other.CF)", "1c · Retail · 2.3.1.1 (demo.dt)", "Acme · Shop · 2.0"],
            all.Select(x => x.Title).ToArray());
        // Vendors in order, the newest version of a solution first (numeric, not by text: 121 is above 9).
        Assert.Equal(current, all.Single(x => x.Version == "3.0.121.21").Folder);
        Assert.Equal(Path.Combine(old, "1cv8.cf"), all.Single(x => x.Version == "3.0.9.5").File);
    }

    // Codex audit P3: every file a base can be made from is a template of its own (a configuration or a demonstration dump), told by its name.
    [Fact] public void EveryFileOfAVersionFolderIsATemplate()
    {
        var both = Make(@"t\v\s\1_0", "1cv8.dt", "1cv8.cf", "z.cf", "update.cfu");
        var named = Make(@"t\v\s\2_0", "b.dt", "a.cf", "c.CF");
        var dumpOnly = Make(@"t\v\s\3_0", "x.DT");
        var all = TemplateCatalog.Scan([Path.Combine(folder, "t")]);
        // The newest version first, in a folder 1cv8.cf, 1cv8.dt, then the other .cf, then the other .dt.
        Assert.Equal([Path.Combine(dumpOnly, "x.DT"), Path.Combine(named, "a.cf"), Path.Combine(named, "c.CF"), Path.Combine(named, "b.dt"),
            Path.Combine(both, "1cv8.cf"), Path.Combine(both, "1cv8.dt"), Path.Combine(both, "z.cf")], all.Select(x => x.File).ToArray());
        Assert.Equal(["3.0", "2.0 (a.cf)", "2.0 (c.CF)", "2.0 (b.dt)", "1.0 (1cv8.cf)", "1.0 (1cv8.dt)", "1.0 (z.cf)"], all.Select(x => x.Label).ToArray());
        Assert.False(all[0].Alternatives); Assert.True(all[1].Alternatives);
        Assert.Equal("v · s · 3.0", all[0].Title);
    }

    // Codex audit P2: the walk is bounded as a whole, by folders looked into as well as by results, and it can be cancelled.
    [Fact] public void TheWalkStopsAtItsLimitsAndCanBeCancelled()
    {
        for (var i = 1; i <= 6; i++) Make($@"t\v{i}\s\1_{i}", "1cv8.cf");
        var root = Path.Combine(folder, "t");
        var limited = TemplateCatalog.ScanAll([root], limit: 3);
        Assert.True(limited.Truncated); Assert.Equal(3, limited.Templates.Count);
        var all = TemplateCatalog.ScanAll([root]); Assert.False(all.Truncated); Assert.Equal(6, all.Templates.Count);
        // Folders without a template count too: a tree of empty versions cannot keep the walk going.
        for (var i = 1; i <= 8; i++) Make($@"e\v\s\{i}_0");
        var empty = TemplateCatalog.ScanAll([Path.Combine(folder, "e")], visitLimit: 3);
        Assert.True(empty.Truncated); Assert.Empty(empty.Templates);
        // The limit holds across roots, vendors and solutions: nothing more is looked into once it is reached.
        var many = TemplateCatalog.ScanAll([root, Path.Combine(folder, "e")], visitLimit: 2);
        Assert.True(many.Truncated); Assert.True(many.Templates.Count <= 2);
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => TemplateCatalog.ScanAll([root], source.Token));
    }

    // Codex re-check P2: every level and every file is charged, the result limit holds inside one version, cancellation reaches the walk.
    [Fact] public void EveryEntryCountsAndTheLimitHoldsInsideAVersion()
    {
        // Many vendors and solutions without a single version: the walk ends at its budget and says so.
        for (var i = 1; i <= 30; i++) Make($@"wide\vendor{i}\solution");
        var wide = TemplateCatalog.ScanAll([Path.Combine(folder, "wide")], visitLimit: 10);
        Assert.True(wide.Truncated); Assert.Empty(wide.Templates);
        Assert.False(TemplateCatalog.ScanAll([Path.Combine(folder, "wide")]).Truncated);
        // Four files of one version and a limit of one: one template, and the list is said to be incomplete.
        Make(@"one\v\s\1_0", "a.cf", "b.cf", "c.dt", "d.dt");
        var one = TemplateCatalog.ScanAll([Path.Combine(folder, "one")], limit: 1);
        Assert.True(one.Truncated); Assert.Equal("a.cf", Path.GetFileName(Assert.Single(one.Templates).File));
        // Files are entries too: a version folder full of other files ends the walk at the budget.
        Make(@"files\v\s\1_0", [.. Enumerable.Range(1, 40).Select(i => $"note{i}.txt"), "z.cf"]);
        var files = TemplateCatalog.ScanAll([Path.Combine(folder, "files")], visitLimit: 20);
        Assert.True(files.Truncated); Assert.True(files.Templates.Count <= 1);   // the order of a folder is the file system's
    }

    // Codex second re-check P2: the entries of the kind not looked for are charged too, and no root is opened after the budget is spent.
    [Fact] public void EntriesOfTheOtherKindAndLaterRootsAreCharged()
    {
        // Files where folders are looked for (a root full of files) and folders where files are looked for (a version full of folders).
        var flat = Make("flat", [.. Enumerable.Range(1, 30).Select(i => $"f{i}.txt")]);
        var scan = TemplateCatalog.ScanAll([flat], visitLimit: 5);
        Assert.True(scan.Truncated); Assert.Empty(scan.Templates);
        for (var i = 1; i <= 30; i++) Make($@"deep\v\s\1_0\sub{i}");
        Assert.True(TemplateCatalog.ScanAll([Path.Combine(folder, "deep")], visitLimit: 10).Truncated);
        Assert.False(TemplateCatalog.ScanAll([Path.Combine(folder, "deep")]).Truncated);
        // The budget spent on the first root: a later root with a template is not opened, and the result says so.
        Make(@"later\v\s\1_0", "1cv8.cf");
        var both = TemplateCatalog.ScanAll([flat, Path.Combine(folder, "later")], visitLimit: 5);
        Assert.True(both.Truncated); Assert.Empty(both.Templates);
    }

    [LinksFact] public void LinksAreSkippedAndCharged()
    {
        var real = Make(@"real\v\s\1_0", "1cv8.cf");
        var root = Path.Combine(folder, "links"); Directory.CreateDirectory(root);
        for (var i = 1; i <= 10; i++) Directory.CreateSymbolicLink(Path.Combine(root, $"link{i}"), Path.Combine(folder, "real", "v"));
        var scan = TemplateCatalog.ScanAll([root], visitLimit: 5);
        Assert.Empty(scan.Templates); Assert.True(scan.Truncated);
        Assert.Empty(TemplateCatalog.Scan([root]));
        Assert.Single(TemplateCatalog.Scan([Path.Combine(folder, "real")]));
    }

    // The manifest of a real template (the standard subsystems library 3.1.11.173): only the
    // template it names is offered, under its place in the tree of the platform; the other .cf files of the folder are not templates.
    [Fact] public void TheManifestNamesTheTemplatesOfItsFolder()
    {
        var ssl = Make(@"m\1c\SSL\3_1_11_173", "1Cv8.cf", "1Cv8_demo.cf", "1Cv8_demo.dt", "1Cv8_international.cf", "ReadMe.txt");
        File.WriteAllText(Path.Combine(ssl, "1cv8.mft"), "Vendor=Фирма \"1С\"\r\nName=БиблиотекаСтандартныхПодсистем\r\nVersion=3.1.11.173\r\nAppVersion=8.3\r\n[Config1]\r\n" +
            "Catalog=1C:Библиотека стандартных подсистем/Библиотека стандартных подсистем (демо)\r\nDestination=1C\\DemoSSL\r\nSource=1Cv8_demo.dt\r\n", new System.Text.UTF8Encoding(true));
        var one = Assert.Single(TemplateCatalog.Scan([Path.Combine(folder, "m")]));
        Assert.Equal(("1C:Библиотека стандартных подсистем", "Библиотека стандартных подсистем (демо)", "3.1.11.173", Path.Combine(ssl, "1Cv8_demo.dt"), false),
            (one.Vendor, one.Solution, one.Version, one.File, one.Alternatives));
        Assert.Equal("8.3", one.Platform);
        // Two sections under one name, a one-part catalog (the vendor of the manifest is the group), a file that is not there, a file
        // outside the folder, a section without a catalog: the usable ones only.
        var many = Make(@"n\v\s\2_0", "a.cf", "b.dt", "c.txt");
        File.WriteAllText(Path.Combine(many, "1CV8.MFT"), "Vendor=Acme\nVersion=2.0.1\n[Config1]\nCatalog=Group/Shop\nSource=a.cf\n[Config2]\nCatalog=Group/Shop\nSource=b.dt\n" +
            "[Config3]\nCatalog=Alone\nSource=a.cf\n[Config4]\nCatalog=G/Missing\nSource=none.cf\n[Config5]\nCatalog=G/Out\nSource=..\\a.cf\n[Config6]\nCatalog=G/Text\nSource=c.txt\n[Config7]\nSource=a.cf\n");
        Assert.Equal(["Acme · Alone · 2.0.1", "Group · Shop · 2.0.1 (a.cf)", "Group · Shop · 2.0.1 (b.dt)"], TemplateCatalog.Scan([Path.Combine(folder, "n")]).Select(x => x.Title).ToArray());
        // The same section twice is one template; the same file under another name stays a template of its own.
        var twice = Make(@"p\v\s\4_0", "a.cf");
        File.WriteAllText(Path.Combine(twice, "1cv8.mft"), "Version=4.0\n[Config1]\nCatalog=Group/Shop\nSource=a.cf\n[Config2]\nCatalog=Group/Shop\nSource=a.cf\n[Config3]\nCatalog=Group/Other\nSource=a.cf\n");
        Assert.Equal(["Group · Other · 4.0", "Group · Shop · 4.0"], TemplateCatalog.Scan([Path.Combine(folder, "p")]).Select(x => x.Title).ToArray());
        // A manifest with nothing usable: the files of the folder, as without one.
        var empty = Make(@"o\v\s\3_0", "1cv8.cf"); File.WriteAllText(Path.Combine(empty, "1cv8.mft"), "Vendor=X\n[Config1]\nCatalog=G/N\nSource=gone.cf\n");
        Assert.Equal("v · s · 3.0", Assert.Single(TemplateCatalog.Scan([Path.Combine(folder, "o")])).Title);
    }

    [Fact] public void MissingAndEmptyCatalogsGiveNothing()
    {
        Assert.Empty(TemplateCatalog.Scan([]));
        Assert.Empty(TemplateCatalog.Scan([Path.Combine(folder, "none")]));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        Assert.Empty(TemplateCatalog.Scan([Path.Combine(folder, "empty")]));
    }

    [Fact] public void CatalogsOfTheStartSettingsCome_BeforeTheDefaultOne()
    {
        var cfg = Path.Combine(folder, "1cestart.cfg");
        var first = Path.Combine(folder, "tmplts one"); var second = Path.Combine(folder, "tmplts two");
        File.WriteAllText(cfg, $"ConfigurationTemplatesLocation={first}\r\nOther=1\r\nconfigurationtemplateslocation=\"{second}\"\r\nConfigurationTemplatesLocation=relative\\one\r\nConfigurationTemplatesLocation={first}\r\n");
        var roots = TemplateCatalog.Roots([cfg]).ToArray();
        Assert.Equal([first, second], roots[..2]);              // a relative path is dropped, a repeat is dropped
        Assert.Equal(roots.Length, roots.Distinct().Count());
        Assert.All(roots, x => Assert.True(Path.IsPathFullyQualified(x)));
        Assert.EndsWith("tmplts", roots[^1]);                     // the default catalog is last
        // Several catalogs are scanned together.
        Make(@"tmplts one\v\a\1_0", "1cv8.cf"); Make(@"tmplts two\v\b\1_0", "1cv8.dt");
        Assert.Equal(["a", "b"], TemplateCatalog.Scan(roots[..2]).Select(x => x.Solution).ToArray());
    }
}
