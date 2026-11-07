using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-logging-tests-" + Guid.NewGuid().ToString("N"));
    public LoggingTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string Logs => Path.Combine(root, "журнал с пробелом");
    private static string ReadLive(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private string Content => string.Join("\n", Directory.GetFiles(Logs).Select(ReadLive));

    [Theory]
    [InlineData(DiagnosticLevel.Off, 0)]
    [InlineData(DiagnosticLevel.Error, 1)]
    [InlineData(DiagnosticLevel.Warning, 2)]
    [InlineData(DiagnosticLevel.Info, 3)]
    [InlineData(DiagnosticLevel.Debug, 4)]
    public void LevelsAreCumulativeAndOffCreatesNothing(DiagnosticLevel level, int count)
    {
        using var log = new DiagnosticLog(Logs, level, "test");
        foreach (var severity in Enum.GetValues<DiagnosticLevel>()) log.Write(severity, "event." + severity);
        Assert.True(log.Flush());
        if (count == 0) { Assert.False(Directory.Exists(Logs)); return; }
        var lines = ReadLive(Directory.GetFiles(Logs).Single()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(count + 1, lines.Length);
        Assert.Contains("session", lines[0]);
        for (var i = count + 1; i <= 4; i++) Assert.DoesNotContain("event." + (DiagnosticLevel)i, Content);
    }

    [Fact]
    public void ExceptionDetailsNeverContainMessagesPathsOrData()
    {
        using var log = new DiagnosticLog(Logs, DiagnosticLevel.Debug, "test");
        try { throw new InvalidOperationException("PASSWORD=secret; Server=private", new IOException("C:/private/user/profile")); }
        catch (Exception ex) { ex.Data["password"] = "secret"; log.Write(DiagnosticLevel.Error, "operation.failed", ex); }
        Assert.True(log.Flush());
        Assert.Contains("InvalidOperationException", Content);
        Assert.Contains("LoggingTests.ExceptionDetails", Content);
        Assert.Contains("0x80131509", Content);
        Assert.DoesNotContain("secret", Content);
        Assert.DoesNotContain("private", Content);
        Assert.DoesNotContain("PASSWORD", Content);
    }

    [Fact]
    public void TheKeptLockEventHasOnlyItsTypedFields()
    {
        // Only a GUID, a reason of the enum and a file name of the exact generated form can be written: any other name — an access
        // code, a base name, a path — is left out (Codex re-check 3, 20.10).
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var kept = ".bn-lock-" + new string('a', 32) + ".cdn";
        using (var log = new DiagnosticLog(Logs, DiagnosticLevel.Warning, "test"))
        {
            log.LockKept(id, LockKeptReason.Aside, kept);
            foreach (var other in new[] { "Abc123SecretCode", "Accounting", "C:/private/base/1Cv8.cdn", ".bn-lock-0123.cdn",
                ".bn-lock-" + new string('A', 32) + ".cdn", ".bn-lock-" + new string('a', 32) + ".cdn.txt" })
                log.LockKept(id, LockKeptReason.Changed, other);
            log.LockKept(id, LockKeptReason.Undeletable, null);
            Assert.True(log.Flush());
        }
        Assert.Contains("\"code\":\"backup.lock.kept\"", Content);
        Assert.Contains("\"data\":{\"baseId\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"keptFile\":\"" + kept + "\",\"reason\":\"aside\"}", Content);
        Assert.Contains("\"data\":{\"baseId\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"reason\":\"undeletable\"}", Content);
        Assert.Equal(1, Content.Split("keptFile").Length - 1);
        foreach (var hidden in new[] { "SecretCode", "Accounting", "private", ".bn-lock-0123", new string('A', 32) }) Assert.DoesNotContain(hidden, Content);
        Assert.False(BackupLock.IsPrivateName(".bn-lock-0123.cdn")); Assert.True(BackupLock.IsPrivateName(kept));
    }

    [Fact]
    public void TheKeptRawLogEventHasOnlyTheNameOfATemporaryFolder()
    {
        // Only the name «bn-1c-…» of a folder right in the temporary folder is written; the path above it (the user's name) never is,
        // and any other folder gives the code alone (Grok audit round 2, 20.10).
        var temp = Path.GetTempPath();
        var ours = Path.Combine(temp, "bn-1c-abc123");
        using (var log = new DiagnosticLog(Logs, DiagnosticLevel.Warning, "test"))
        {
            log.RawLogKept(ours);
            foreach (var other in new[] { Path.Combine(temp, "private-1c-abc"), Path.Combine(temp, "x", "bn-1c-deep"), "bn-1c-relative",
                Path.Combine(root, "bn-1c-elsewhere"), Path.Combine(temp, "bn-1c-a b"), null })
                log.RawLogKept(other);
            Assert.True(log.Flush());
        }
        Assert.Contains("\"data\":{\"keptFolder\":\"bn-1c-abc123\"}", Content);
        Assert.Equal(1, Content.Split("keptFolder").Length - 1);
        Assert.Equal(7, Content.Split("backup.rawlog.kept").Length - 1);
        foreach (var hidden in new[] { "private-1c", "bn-1c-deep", "bn-1c-relative", "elsewhere", "bn-1c-a b", Path.TrimEndingDirectorySeparator(temp).Replace("\\", "\\\\") })
            Assert.DoesNotContain(hidden, Content);
    }

    [Fact]
    public void NativeErrorCodeIsKeptWithoutItsLocalizedMessage()
    {
        using var log = new DiagnosticLog(Logs, DiagnosticLevel.Error, "test");
        log.Write(DiagnosticLevel.Error, "native.failed", new System.ComponentModel.Win32Exception(126, "private-client-path"));
        Assert.True(log.Flush());
        Assert.Contains("\"nativeCode\":126", Content);
        Assert.DoesNotContain("private-client-path", Content);
    }

    [Fact]
    public void InvalidEventIdentifierCannotInjectFreeText()
    {
        using var log = new DiagnosticLog(Logs, DiagnosticLevel.Info, "test");
        log.Write(DiagnosticLevel.Info, "connection=private");
        log.Write(DiagnosticLevel.Info, "event\nsecret");
        Assert.True(log.Flush());
        Assert.False(Directory.Exists(Logs));
    }

    [Fact]
    public void RotationIsBoundedAndKeepsNewestEvents()
    {
        using var log = new DiagnosticLog(Logs, DiagnosticLevel.Debug, "test", 1024);
        for (var i = 0; i < 100; i++)
        {
            log.Write(DiagnosticLevel.Info, "rotation." + i);
            Assert.True(log.Flush());
        }
        Assert.InRange(Directory.GetFiles(Logs).Length, 2, 4);
        Assert.Contains("rotation.99", Content);
        Assert.DoesNotContain("rotation.0\"", Content);
        Assert.All(Directory.GetFiles(Logs), path => Assert.InRange(new FileInfo(path).Length, 1, 1800));
    }

    [Fact]
    public void RotationRecoversAndKeepsEvidenceOfTheFailure()
    {
        using var log = new DiagnosticLog(Logs, DiagnosticLevel.Info, "test", 1024);
        log.Write(DiagnosticLevel.Info, "before.failure"); Assert.True(log.Flush());
        var blocked = Directory.GetFiles(Logs).Single().Replace("-0.log", "-1.log", StringComparison.Ordinal);
        Directory.CreateDirectory(blocked);
        for (var i = 0; i < 20; i++) { log.Write(DiagnosticLevel.Info, "blocked." + i); log.Flush(); }
        Assert.NotNull(log.Failure); Assert.True(log.DroppedRecords > 0);
        Directory.Delete(blocked);
        log.Write(DiagnosticLevel.Info, "after.recovery");
        Assert.False(log.Flush()); // The session is incomplete even though disk writes have recovered.
        Assert.Contains("after.recovery", Content);
        Assert.Contains("logging.records_dropped", Content);
        Assert.Contains("before.failure", Content);
        Assert.NotNull(log.Failure);
    }

    [Fact]
    public void QueueOverloadIsAccountedForAndAStageCanWaitForSpace()
    {
        using var log = new DiagnosticLog(Logs, DiagnosticLevel.Info, "test", 64 * 1024 * 1024);
        Parallel.For(0, 20000, _ => log.Write(DiagnosticLevel.Info, "flood"));
        log.Write(DiagnosticLevel.Info, "stage.final", enqueueTimeout: 2000);
        var complete = log.Flush(5000);
        Assert.Contains("stage.final", Content); // Flush itself, not Dispose, must deliver the waiting stage.
        log.Dispose();
        var records = Content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var written = records.Count(r => r.RootElement.GetProperty("code").GetString() == "flood");
            Assert.Equal(20000, written + log.DroppedRecords);
            Assert.Contains("stage.final", Content);
            if (log.DroppedRecords > 0)
            {
                Assert.False(complete); Assert.NotNull(log.Failure);
                Assert.Contains("logging.records_dropped", Content);
            }
            else Assert.True(complete);
        }
        finally { foreach (var record in records) record.Dispose(); }
    }

    [Fact]
    public void RelativeGuiProfileRemainsCompatible()
    {
        var parsed = DiagnosticArguments.Parse(["--settings", "relative.json"]);
        Assert.Equal(Path.GetFullPath("relative.json"), parsed.SettingsPath);
        Assert.Equal(new[] { "--settings", "relative.json" }, parsed.ApplicationArguments);
    }

    [Fact]
    public void DiskFailureDoesNotThrowOrBlockCaller()
    {
        File.WriteAllText(Logs, "occupied");
        using var log = new DiagnosticLog(Logs, DiagnosticLevel.Debug, "test");
        log.Write(DiagnosticLevel.Error, "failure");
        Assert.False(log.Flush());
        Assert.NotNull(log.Failure);
        Assert.Equal("occupied", File.ReadAllText(Logs));
    }

    [Fact]
    public void IndependentWritersDoNotOverwriteEachOther()
    {
        using var first = new DiagnosticLog(Logs, DiagnosticLevel.Debug, "test");
        using var second = new DiagnosticLog(Logs, DiagnosticLevel.Debug, "test");
        first.Write(DiagnosticLevel.Info, "first"); second.Write(DiagnosticLevel.Info, "second");
        Assert.True(first.Flush()); Assert.True(second.Flush());
        Assert.Equal(2, Directory.GetFiles(Logs).Length);
        Assert.Contains("first", Content); Assert.Contains("second", Content);
    }

    [Fact]
    public void RetentionKeepsUnrelatedAndLiveFiles()
    {
        Directory.CreateDirectory(Logs);
        var unrelated = Path.Combine(Logs, "notes.log"); File.WriteAllText(unrelated, "keep");
        // int.MaxValue is not a valid live PID on either supported OS.
        for (var i = 0; i < 25; i++) File.WriteAllText(Path.Combine(Logs, $"bn-{int.MaxValue}-{Guid.NewGuid():N}-0.log"), "old");
        using var log = new DiagnosticLog(Logs, DiagnosticLevel.Info, "test");
        log.Write(DiagnosticLevel.Info, "new"); Assert.True(log.Flush());
        Assert.Equal(22, Directory.GetFiles(Logs).Length);
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Fact]
    public void LevelCanBeChangedWithoutRestart()
    {
        using var log = new DiagnosticLog(Logs, DiagnosticLevel.Off, "test");
        log.Write(DiagnosticLevel.Error, "hidden.before");
        log.Level = DiagnosticLevel.Debug;
        log.Write(DiagnosticLevel.Debug, "visible"); Assert.True(log.Flush());
        log.Level = DiagnosticLevel.Off;
        log.Write(DiagnosticLevel.Error, "hidden.after"); Assert.True(log.Flush());
        Assert.DoesNotContain("hidden", Content); Assert.Contains("visible", Content);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{\"Level\":false}")]
    [InlineData("{\"Level\":\"future\",\"Future\":42}")]
    public void InvalidDiagnosticsDoNotBlockOrDestroyProfile(string diagnostics)
    {
        var path = Path.Combine(root, "library.json");
        File.WriteAllText(path, "{\"Diagnostics\":" + diagnostics + ",\"Unknown\":42}");
        var store = new LibraryStore(path); var library = store.Load();
        Assert.Equal(DiagnosticLevel.Info, DiagnosticPreferences.Read(library.Diagnostics));
        store.Save(library with { Executable = "unchanged" });
        var restored = new LibraryStore(path).Load();
        Assert.True(JsonElement.DeepEquals(library.Diagnostics ?? JsonSerializer.SerializeToElement<object?>(null),
            restored.Diagnostics ?? JsonSerializer.SerializeToElement<object?>(null)));
        Assert.Equal(42, restored.Extra!["Unknown"].GetInt32());
    }

    [Fact]
    public void UpdatingLevelPreservesUnknownDiagnosticProperties()
    {
        var original = JsonSerializer.SerializeToElement(new { Level = "info", Future = new { value = 42 } });
        var changed = DiagnosticPreferences.WithLevel(original, DiagnosticLevel.Off);
        Assert.Equal(42, changed.GetProperty("Future").GetProperty("value").GetInt32());
        Assert.Equal(DiagnosticLevel.Off, DiagnosticPreferences.Read(changed));
        Assert.Equal("info", original.GetProperty("Level").GetString());
    }

    [Fact]
    public void LoggingArgumentsPreserveLegacyShortcutIncludingEmptyList()
    {
        var request = new ShortcutRequest(Path.Combine(root, "профиль с пробелом.json"), "", Guid.NewGuid(), true);
        var parsed = DiagnosticArguments.Parse(["--log-level", "debug", .. request.Arguments(), "--log-dir", Logs]);
        Assert.Equal(request, ShortcutRequest.Parse(parsed.ApplicationArguments));
        Assert.Equal(DiagnosticLevel.Debug, parsed.Level);
        Assert.Equal(Logs, parsed.Directory); Assert.Equal(request.Settings, parsed.SettingsPath);
    }

    [Theory]
    [InlineData("--log-level", "unknown")]
    [InlineData("--log-level", "1")]
    [InlineData("--log-dir", "relative")]
    [InlineData("--settings", "")]
    [InlineData("--unknown", "private")]
    [InlineData("--mode", "designer")]
    [InlineData("--log-level", "--log-dir")]
    public void InvalidArgumentsAreRejectedWithoutEchoingValues(string key, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse([key, value]));
        Assert.DoesNotContain("private", ex.Message);
    }

    [Fact]
    public void MissingAndRepeatedArgumentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse(["--log-level"]));
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse(["--log-level", "off", "--log-level", "debug"]));
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse(["--settings", root, "--settings", root]));
        var defaults = DiagnosticArguments.Parse([]);
        Assert.Null(defaults.Level); Assert.Null(defaults.Directory); Assert.Empty(defaults.ApplicationArguments);
    }
}
