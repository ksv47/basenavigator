using System.Buffers.Binary;
using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class PlatformTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-platform-" + Guid.NewGuid().ToString("N"));
    public PlatformTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static BaseEntry Entry => new() { Name = "Test", Kind = ConnectionKind.Server, Location = "server\\base" };
    private static PlatformInstallation Item(string version, PlatformArchitecture arch) => new(version + arch, Version.Parse(version), arch);

    [Theory]
    [InlineData("8.3", true)] [InlineData("8.5.1.22", true)] [InlineData("8.3.27", true)]
    [InlineData("7.7", false)] [InlineData("8", false)] [InlineData("8.3.*", false)]
    [InlineData("8.3.1.2.3", false)] [InlineData("8.3.-1", false)] [InlineData("8.3.99999999999", false)]
    [InlineData("8.03", false)] [InlineData("8.3.0027", false)]
    public void ValidatesVersion(string value, bool valid) => Assert.Equal(valid, PlatformCatalog.IsVersion(value));

    [Fact] public void NumericOrderingAndSegmentBoundaries()
    {
        var items = new[] { Item("8.3.9.100", PlatformArchitecture.X64), Item("8.3.10.20", PlatformArchitecture.X64), Item("8.30.99.1", PlatformArchitecture.X64) };
        Assert.Equal(items[1], PlatformCatalog.Select(new(), items, PlatformArchitecture.X64, true));
        Assert.Equal(items[0], PlatformCatalog.Select(new() { Version = "8.3.9" }, items, PlatformArchitecture.X64, true));
        Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Select(new() { Version = "8.3.9.101" }, items, PlatformArchitecture.X64, true));
    }
    [Fact] public void StrictArchitectureDoesNotUseNewerOtherArchitecture()
    {
        var items = new[] { Item("8.3.27.1", PlatformArchitecture.X86), Item("8.3.28.1", PlatformArchitecture.X64) };
        Assert.Equal(items[0], PlatformCatalog.Select(new() { Architecture = PlatformArchitecture.X86 }, items, PlatformArchitecture.X64, true));
        Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Select(new() { Architecture = PlatformArchitecture.Arm64 }, items, PlatformArchitecture.X64, true));
    }
    // Launch diagnostics: the message about a missing platform says what was found, or where to add a folder.
    [Fact] public void MissingPlatformMessageNamesWhatWasFound()
    {
        var items = new[] { Item("8.3.27.1", PlatformArchitecture.X86), Item("8.3.27.1", PlatformArchitecture.X86), Item("8.3.28.1", PlatformArchitecture.X64) };
        var wrong = Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Select(new() { Version = "8.5" }, items, PlatformArchitecture.X64, true)).Message;
        Assert.Contains("8.5", wrong); Assert.Contains("Найдено: 8.3.27.1 (x86); 8.3.28.1 (x64).", wrong);
        Assert.Contains("выберите клиент вручную", wrong);
        var none = Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Select(new() { Version = "8.3" }, [], PlatformArchitecture.X64, true)).Message;
        Assert.Contains("Пути поиска платформы", none); Assert.DoesNotContain("Найдено:", none);
        // A long list is cut.
        var many = Enumerable.Range(1, 12).Select(i => Item($"8.3.{i}.1", PlatformArchitecture.X64)).ToArray();
        var cut = PlatformCatalog.Found(many);
        Assert.Contains("и ещё 4", cut); Assert.DoesNotContain("8.3.12.1", cut);
    }

    // The tables of Windows and of Linux are tested on any system (Codex audit P3.3: a row of the other system must not pass empty).
    [Theory]
    [InlineData(true, 2, "не найден")] [InlineData(true, 3, "не найден")] [InlineData(true, 5, "отказала в доступе")] [InlineData(true, 32, "занят другим процессом")]
    [InlineData(true, 193, "не исполняемый файл Windows")] [InlineData(true, 216, "не исполняемый файл Windows")] [InlineData(true, 225, "антивирус")]
    [InlineData(true, 740, "права администратора")] [InlineData(true, 1260, "политикой безопасности")]
    [InlineData(false, 2, "загрузчик (интерпретатор)")] [InlineData(false, 8, "не исполняемый файл этой операционной системы")] [InlineData(false, 12, "Не хватает памяти")]
    [InlineData(false, 13, "chmod +x")] [InlineData(false, 20, "не является каталогом")] [InlineData(false, 26, "открыт на запись")]
    public void OperatingSystemCodeOfARefusedStartIsExplained(bool windows, int code, string expected)
    {
        var message = Launcher.Win32Message(code, windows);
        Assert.Contains(expected, message); Assert.Contains($"код ОС {code}", message);
        // A code of the other system must not be explained by this table's text.
        Assert.NotEqual(Launcher.Win32Message(code, !windows), message);
    }

    [Fact] public void NoexecIsNamedForLinuxPermissionDenied() => Assert.Contains("noexec", Launcher.Win32Message(13, false));

    [Theory] [InlineData(true, 999)] [InlineData(false, 999)] [InlineData(true, 13)] [InlineData(false, 5)]
    public void UnknownCodeGetsTheGeneralAdviceAndTheCode(bool windows, int code)
    {
        // 13 is no code of the Windows table and 5 none of the Linux one: they fall to the general advice too.
        var message = Launcher.Win32Message(code, windows);
        Assert.Contains("Проверьте, что выбран исполняемый файл", message); Assert.Contains($"код ОС {code}", message);
    }

    // The exception reaches the user with the table of the running system and never with the raw system text.
    [Fact] public void ErrorMessageUsesTheTableOfTheRunningSystem()
    {
        var message = Launcher.ErrorMessage(new System.ComponentModel.Win32Exception(2, "raw English diagnostic"));
        Assert.Equal(Launcher.Win32Message(2, OperatingSystem.IsWindows()), message); Assert.DoesNotContain("raw English", message);
    }

    [Fact] public void PreferenceUsesLatestVersionThenArchitecture()
    {
        var items = new[] { Item("8.3.27.1", PlatformArchitecture.X86), Item("8.3.28.1", PlatformArchitecture.X64) };
        Assert.Equal(items[1], PlatformCatalog.Select(new() { Architecture = PlatformArchitecture.PreferX86 }, items, PlatformArchitecture.X64, true));
        var equal = new[] { items[1], Item("8.3.28.1", PlatformArchitecture.X86) };
        Assert.Equal(equal[1], PlatformCatalog.Select(new(), equal, PlatformArchitecture.X64, true));
        Assert.Equal(equal[0], PlatformCatalog.Select(new() { Architecture = PlatformArchitecture.PreferX64 }, equal, PlatformArchitecture.X64, true));
    }
    [Fact] public void LinuxRequiresNativeArchitectureAndWindows32Rejects64()
    {
        var items = new[] { Item("8.3.27.1", PlatformArchitecture.X64), Item("8.3.28.1", PlatformArchitecture.X86), Item("8.3.29.1", PlatformArchitecture.Arm64) };
        Assert.Equal(items[0], PlatformCatalog.Select(new(), items, PlatformArchitecture.X64, false));
        Assert.Equal(items[2], PlatformCatalog.Select(new(), items, PlatformArchitecture.Arm64, false));
        Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Select(new() { Architecture = PlatformArchitecture.X86 }, items, PlatformArchitecture.X64, false));
        Assert.Equal(items[1], PlatformCatalog.Select(new(), items, PlatformArchitecture.X86, true));
    }
    [Fact] public void UnknownArchitectureNeverSelectedAutomatically() =>
        Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Select(new(), [Item("8.3.27.1", PlatformArchitecture.Any)], PlatformArchitecture.X64, true));

    [Fact] public void LegacyManualPathsAndOverridePrecedence()
    {
        var library = new Library { Executable = "default" };
        Assert.Equal("default", PlatformCatalog.Resolve(library, Entry, []));
        Assert.Equal("individual", PlatformCatalog.Resolve(library with { PlatformRule = new() }, Entry with { Executable = "individual" }, []));
        var item = Item("8.3.27.1", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? PlatformArchitecture.Arm64 : PlatformArchitecture.X64);
        Assert.Equal(item.Executable, PlatformCatalog.Resolve(library, Entry with { Executable = "ignored", PlatformRule = new() }, [item]));
        Assert.Equal(item.Executable, PlatformCatalog.Resolve(library with { PlatformRule = new() }, Entry, [item]));
    }
    [Fact] public void RulesRoundtripPreserveUnknownFieldsAndSourceIdentity()
    {
        var path = Path.Combine(folder, "settings.json");
        var extra = new Dictionary<string, JsonElement> { ["future"] = JsonSerializer.SerializeToElement("keep") };
        var entry = Entry with { PlatformRule = new() { Version = "8.3.27", Architecture = PlatformArchitecture.X64, Extra = extra }, Extra = extra };
        var store = new LibraryStore(path); store.Save(new() { Bases = [entry], PlatformRule = new(), Extra = extra });
        var loaded = store.Load();
        Assert.Equal("keep", loaded.Bases[0].PlatformRule!.Extra!["future"].GetString());
        Assert.Equal("keep", loaded.Bases[0].Extra!["future"].GetString());
        Assert.Equal(entry.Id, LibrarySources.Select(loaded, path, "").Bases[0].Id);
        Assert.Equal(PlatformArchitecture.X64, new ShortcutRequest(path, "", entry.Id, false).Resolve().Entry.PlatformRule!.Architecture);
    }
    [Fact] public void SharedListRulesRemainLocalAndPreserveBytes()
    {
        var path = Path.Combine(folder, "list.v8i"); var id = Guid.NewGuid();
        File.WriteAllText(path, $"[Test]\nID={id}\nConnect=Srvr=\"server\";Ref=\"base\";\nVersion=8.3\nDefaultVersion=8.3.27\nAppArch=x86_64\nUnknown=keep\n");
        var bytes = File.ReadAllBytes(path); var source = new V8iStore(path);
        var library = source.Load(new());
        var edited = library with { Bases = [library.Bases[0] with { PlatformRule = new() { Architecture = PlatformArchitecture.X64 } }] };
        var saved = source.Save(edited);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(PlatformArchitecture.X64, source.Load(saved).Bases[0].PlatformRule!.Architecture);
        Assert.Equal("8.3", source.Load(saved).Bases[0].PlatformVersion);
    }
    [Fact] public void RuleLaunchUsesSelectedFileAndDisablesClientReselection()
    {
        var native = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? PlatformArchitecture.Arm64 : PlatformArchitecture.X64;
        var installation = new PlatformInstallation(Environment.ProcessPath!, new Version(8, 3, 27, 1), native);
        var start = Launcher.Build(new Library { PlatformRule = new() }, Entry, true, [installation]);
        Assert.Equal(Environment.ProcessPath, start.FileName);
        Assert.Equal(new[] { "DESIGNER", "/S", Entry.Location, "/AppAutoCheckVersion-" }, start.ArgumentList);
        Assert.DoesNotContain("/AppAutoCheckVersion-", Launcher.Build(new Library { Executable = Environment.ProcessPath! }, Entry, false, []).ArgumentList);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ArchitectureReadFromBinaryAndCatalogDeduplicates(bool elf)
    {
        var path = Path.Combine(folder, "x86_64", "8.3.27.2214", "bin", OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[128];
        if (elf) { bytes[0] = 0x7f; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F'; bytes[4] = 2; bytes[5] = 1; bytes[18] = 62; }
        else { bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64); bytes[64] = (byte)'P'; bytes[65] = (byte)'E'; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664); }
        File.WriteAllBytes(path, bytes);
        Assert.Equal(PlatformArchitecture.X64, PlatformCatalog.ReadArchitecture(path));
        var scan = PlatformCatalog.Scan([folder, folder], [path]);
        Assert.Equal(new Version(8, 3, 27, 2214), Assert.Single(scan.Installations).Version);
        Assert.Empty(scan.Warnings);
    }
    [Fact] public void BadPeOffsetAndShortFilesAreNotGuessed()
    {
        var path = Path.Combine(folder, "test"); File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal(PlatformArchitecture.Any, PlatformCatalog.ReadArchitecture(path));
        var bytes = new byte[64]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), int.MaxValue);
        File.WriteAllBytes(path, bytes); Assert.Equal(PlatformArchitecture.Any, PlatformCatalog.ReadArchitecture(path));
    }

    [Fact] public void UnknownVersionProducesWarningAndNoGuess()
    {
        var path = Path.Combine(folder, "v8.3", "x86_64", OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "unknown-version fixture");
        var result = PlatformCatalog.Scan([folder]);
        Assert.Empty(result.Installations); Assert.Contains(path, Assert.Single(result.Warnings));
    }

    [Fact] public void DisplayUsesSameRuleForWhitespaceManualPath()
    {
        var native = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? PlatformArchitecture.Arm64 : PlatformArchitecture.X64;
        var item = new PlatformInstallation(Environment.ProcessPath!, new(8, 3, 27, 1), native);
        var result = BaseInfo.Read(Entry with { Executable = "  " }, new Library { PlatformRule = new() { Version = "8.3.27" } }, [item]);
        Assert.Equal("8.3.27", result.Platform); Assert.False(result.PlatformMissing);
    }
    [Fact] public void WindowsPathCaseDoesNotLoseArchitecture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var item = new PlatformInstallation(Environment.ProcessPath!, new(8, 3, 27, 1), PlatformArchitecture.X64);
        var result = BaseInfo.Read(Entry with { Executable = Environment.ProcessPath!.ToUpperInvariant() }, new Library(), [item]);
        Assert.Equal("x64", result.ArchitectureLabel);
    }
    [Fact] public void MissingRuleExplainsRequestedArchitectureInRussian()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Select(new() { Architecture = PlatformArchitecture.PreferX64 }, [], PlatformArchitecture.X64, true));
        Assert.Contains("предпочитать x64", error.Message); Assert.DoesNotContain("PreferX64", error.Message);
    }
    [Fact] public void UnknownFutureArchitectureDoesNotOverwriteProfile()
    {
        var path = Path.Combine(folder, "future.json");
        const string json = "{\"PlatformRule\":{\"Version\":\"8.3\",\"Architecture\":999}}";
        File.WriteAllText(path, json);
        var store = new LibraryStore(path);
        Assert.Contains("более новой", Assert.Throws<ArgumentException>(() => store.Load()).Message);
        Assert.Throws<ArgumentException>(() => store.Save(new()));
        Assert.Equal(json, File.ReadAllText(path));
    }
}
