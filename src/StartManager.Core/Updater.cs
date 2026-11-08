using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace StartManager.Core;

/// <param name="AssetSizes">The sizes of the release files by name (gh release view … assets); null or a missing name — not known.</param>
/// <param name="AssetUrls">HTTPS download addresses by name (the GitHub API answered); null — the release came through gh.</param>
/// <param name="AssetDigests">SHA256 of the files by name (lowercase hex, the API's «digest»); a missing name — not known.</param>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, IReadOnlyDictionary<string, long>? AssetSizes = null,
    IReadOnlyDictionary<string, string>? AssetUrls = null, IReadOnlyDictionary<string, string>? AssetDigests = null);

/// <summary>The stages of preparing a release, in their order.</summary>
public enum UpdateStage { Downloading, Verifying, Extracting, TrialRun }

/// <summary>How far the preparation of a release is: the stage and, while downloading, the bytes received of the expected total
/// (null when the release did not tell the size: the bar then only shows that the work goes on).</summary>
public sealed record UpdateProgress(UpdateStage Stage, long Received = 0, long? Total = null)
{
    /// <summary>The part downloaded, 0–1; null when it is not known (another stage, or no size).</summary>
    public double? Fraction => Stage == UpdateStage.Downloading && Total is > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : null;

    public string Text => Stage switch
    {
        UpdateStage.Downloading when Total is > 0 => $"Загрузка обновления… {Megabytes(Math.Min(Received, Total.Value))} из {Megabytes(Total.Value)} МБ",
        UpdateStage.Downloading => Received > 0 ? $"Загрузка обновления… {Megabytes(Received)} МБ" : "Загрузка обновления…",
        UpdateStage.Verifying => "Проверка контрольной суммы…",
        UpdateStage.Extracting => "Распаковка…",
        _ => "Пробный запуск…",
    };

    /// <summary>The short form of the status line of the main window: the percent while it is known.</summary>
    public string Percent => Fraction is { } part ? $"Загрузка обновления… {Math.Floor(part * 100):0} %" : Text;

    private static string Megabytes(long bytes) => (bytes / 1048576d).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>A release prepared for the helper. HelperError: the package is pending, but the helper did not start; the next start
/// of the program starts it (Program.PrepareUpdateAtStart), so the version is applied after that run closes.</summary>
public sealed record StagedUpdate(Version Version, string? HelperError)
{
    /// <summary>The status bar notice of the prepared release.</summary>
    public string Notice => HelperError is null
        ? $"Загружена версия {Version}; она будет установлена после закрытия программы"
        : $"Загружена версия {Version}, но установщик не запустился ({HelperError.TrimEnd('.')}). " +
          "Она будет установлена после следующего запуска и закрытия программы";
}

/// <summary>A running copy's hold on .run.lock (null if it could not be taken); Waited — files were being replaced at start.</summary>
public sealed record Registration(FileStream? Hold, bool Waited, bool TimedOut) : IDisposable
{
    public void Dispose() => Hold?.Dispose();
}

/// <summary>Where releases come from; the application uses GitHub over HTTPS with gh as the fallback, tests use a folder.</summary>
public interface IReleaseSource
{
    ReleaseInfo Latest();
    /// <summary>Downloads the named assets of the release into directory.</summary>
    void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory);

    /// <summary>As <see cref="Download(ReleaseInfo, IReadOnlyList{string}, string)"/>, stopped when cancel is set; a source that
    /// cannot stop midway (a folder of the tests) only refuses to start.</summary>
    void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Download(release, assets, directory);
    }
}

/// <summary>Reads releases with the GitHub CLI (the fallback of <see cref="GitHubHttpReleaseSource"/>; needed for a private
/// repository); its login is reused, no token is stored.</summary>
/// <param name="gh">GitHub CLI executable; tests substitute another program.</param>
public sealed class GitHubReleaseSource(string repository = GitHubReleaseSource.DefaultRepository, string gh = "gh") : IReleaseSource
{
    public string Repository { get; } = repository;
    public const string DefaultRepository = "ksv47/basenavigator";

    public static bool IsRepository(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$") && !value.Contains("..", StringComparison.Ordinal);

    public ReleaseInfo Latest() => ParseLatest(Gh(TimeSpan.FromSeconds(60), default, "release", "view", "--repo", Repository, "--json", "tagName,body,assets"));

    /// <summary>The answer of gh release view; assets (name, size) are optional: an answer without them (older calls) or with a size
    /// that is not a positive number leaves that size unknown.</summary>
    public static ReleaseInfo ParseLatest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var tag = document.RootElement.GetProperty("tagName").GetString() ?? "";
        var version = TagVersion(tag);
        Dictionary<string, long>? sizes = null;
        if (document.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
                if (asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    name.GetString() is { Length: > 0 } file && asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number &&
                    size.TryGetInt64(out var bytes) && bytes > 0)
                    (sizes ??= new(StringComparer.Ordinal))[file] = bytes;
        return new(version, tag, document.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", sizes);
    }

    /// <summary>The version of a release tag «v0.4.0.2».</summary>
    internal static Version TagVersion(string tag) => tag.StartsWith('v') && System.Version.TryParse(tag[1..], out var version)
        ? version : throw new InvalidDataException($"Непонятный номер последнего выпуска: {tag}.");

    /// <summary>The failure of a gh that is not installed: the fallback names what a private repository needs instead.</summary>
    internal static bool IsMissing(Exception error) => error is InvalidOperationException { InnerException: System.ComponentModel.Win32Exception };

    public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory) => Download(release, assets, directory, default);

    /// <summary>Cancel stops gh (its process tree is killed); the caller removes what it left in directory.</summary>
    public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory, CancellationToken cancel)
    {
        var arguments = new List<string> { "release", "download", release.Tag, "--repo", Repository, "--dir", directory, "--clobber" };
        foreach (var asset in assets) { arguments.Add("--pattern"); arguments.Add(asset); }
        Gh(TimeSpan.FromMinutes(10), cancel, [.. arguments]);
    }

    private string Gh(TimeSpan timeout, CancellationToken cancel, params string[] arguments)
    {
        cancel.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(gh) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить gh."); }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new InvalidOperationException("Для обновления нужна программа GitHub CLI (gh). Установите её и выполните «gh auth login».", ex); }
        using (process)
        {
            // Its identity is taken now, while it surely is this program's child (RC8-1).
            var owned = OwnedProcess.Of(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var elapsed = Stopwatch.StartNew();
            bool Stopped() => cancel.IsCancellationRequested || elapsed.Elapsed >= timeout;
            // The root first, then its output: a descendant may keep the output open after the root has exited (a wrapper), so
            // the wait for the output is bound by the cancel and the time limit too (Codex re-check 5, RC5-3). Only the root gh is
            // stopped; the program looks for no other process (re-checks 6 and 7, RC6-1, RC7-1).
            while (!process.WaitForExit(200)) if (Stopped()) Stop(process, owned, cancel, output, error);
            while (!Task.WaitAll([output, error], 200)) if (Stopped()) Stop(process, owned, cancel, output, error);
            process.WaitForExit();
            if (process.ExitCode != 0) throw Failure(process.ExitCode, error.Result);
            return output.Result;
        }
    }

    /// <summary>
    /// Stops gh while it runs and waits for its exit (bounded), so its files are closed and the caller can remove them; then the
    /// output is no longer read and the program's ends of the pipes are closed — always, also when stopping failed. Only the root
    /// process is ended, as that very process (<see cref="OwnedProcess"/>: its handle on Windows, a pidfd checked by the start time
    /// on Linux; Codex re-checks 7 and 8, RC7-1, RC8-1). Descendants of gh are neither looked for nor ended and may outlive a cancel
    /// (a limitation, update-progress-plan.md). A root still alive afterwards — also when it could not be signalled safely — is a
    /// clear error, never a successful cancel.
    /// </summary>
    private static void Stop(Process process, OwnedProcess owned, CancellationToken cancel, params Task[] reads)
    {
        var stopped = true;
        var refused = false;
        try
        {
            if (Alive(process))
            {
                // Whatever the call throws (an aggregate of a partial failure among them), the exit is checked below (RC7-3).
                try { refused = KillRoot(owned) == OwnedProcess.Outcome.Refused; } catch (Exception) { }
                if (refused) stopped = !Alive(process);
                else try { stopped = process.WaitForExit(StopWait); } catch (SystemException) { stopped = !Alive(process); }
            }
        }
        finally
        {
            foreach (var read in reads) _ = read.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            foreach (var stream in new StreamReader[] { process.StandardOutput, process.StandardError })
                try { stream.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        }
        if (!stopped)
            throw new InvalidOperationException("Не удалось остановить GitHub CLI (gh)" + (refused ? " безопасно: система не даёт остановить именно этот процесс (нет pidfd)" : "") +
                "; загрузка не отменена до конца. Удалите оставшуюся папку .update-* рядом с программой после его завершения.");
        cancel.ThrowIfCancellationRequested();
        throw new TimeoutException("GitHub не ответил вовремя. Проверьте подключение и повторите.");
    }

    private static bool Alive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>How the root gh is ended; tests substitute a failing one.</summary>
    internal static Func<OwnedProcess, OwnedProcess.Outcome> KillRoot { get; set; } = static process => process.Kill();

    /// <summary>How long the exit of a stopped gh is waited for.</summary>
    internal static TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>User-facing error for a failed gh call; gh never prints the token itself.</summary>
    public static InvalidOperationException Failure(int code, string error)
    {
        var message = error.Trim();
        if (message.Contains("auth login", StringComparison.OrdinalIgnoreCase) || message.Contains("not logged", StringComparison.OrdinalIgnoreCase))
            return new("GitHub CLI не выполнил вход. Выполните «gh auth login» и повторите.");
        return new("GitHub CLI вернул ошибку: " + (message.Length > 0 ? message : "код " + code));
    }
}

/// <summary>A complete copy of an earlier version in the archive (.versions/&lt;version&gt;) or the .previous of older builds.</summary>
/// <param name="Light">The build variant of the copy; null when it is not known (copies saved by older builds).</param>
/// <param name="Saved">When the copy was saved (UTC): the newest one is the version the last operation replaced.</param>
public sealed record SavedVersion(Version Version, string Folder, bool? Light, DateTime Saved)
{
    public string Variant => Light switch { true => "лёгкий", false => "автономный", null => "—" };
    /// <summary>A program runs from this folder (this process, or an older build holding the folder's .run.lock): it is never
    /// replaced or trimmed while so.</summary>
    public bool InUse { get; init; }
}

/// <summary>
/// Replaces the program files next to the executable with a release package. Settings, the portable marker and
/// anything not in the package are left alone; the replaced version is kept in the archive .versions for Rollback.
/// </summary>
public sealed class Updater
{
    /// <summary>The single saved copy of builds before 0.3.0.8; read as one more archived version and moved into the archive.</summary>
    public const string PreviousFolder = ".previous";
    /// <summary>Archive of earlier versions: .versions/&lt;version&gt;/ holds the program files, version.txt and variant.txt.</summary>
    public const string ArchiveFolder = ".versions";
    public const int DefaultKeep = 3, MaxKeep = 10;
    private const string Staging = ".previous.new", Committed = ".update-committed", Manifest = "files.txt", VersionFile = "version.txt",
        VariantFile = "variant.txt", InventoryFile = "inventory.txt", Retired = ".old-", Unidentified = "unidentified";
    /// <summary>Files waiting for the program to exit; applied by the helper, never by the running program itself.</summary>
    public const string PendingFolder = ".pending-update";
    private const string PendingInfo = "update.txt", RestartMarker = "restart", RestartArguments = "arguments.txt", ResultFile = ".update-result.txt",
        HelperLock = "running.lock", HelperLog = ".update-helper.log", RunLock = ".run.lock", AppliedFile = ".update-applied";
    private readonly string directory;
    private readonly string executable;
    private readonly IReleaseSource source;
    private readonly Func<string, string?> probe;

    /// <param name="probe">Runs a candidate executable with --version and returns its output; null if it failed.</param>
    /// <param name="executable">The running program; a copy started from .previous or .versions/&lt;version&gt; targets the program folder.</param>
    public Updater(string directory, string executable, IReleaseSource source, Func<string, string?>? probe = null)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(directory);
        if (Path.GetFileName(directory) == PreviousFolder && parent is not null) { FromPrevious = true; this.directory = parent; }
        else if (parent is not null && Path.GetFileName(parent) == ArchiveFolder && Path.GetDirectoryName(parent) is { } main)
        { FromPrevious = true; this.directory = main; }
        else this.directory = directory;
        RunningExecutable = Path.GetFullPath(executable);
        this.executable = Path.Combine(this.directory, Path.GetFileName(executable));
        this.source = source;
        this.probe = probe ?? ProbeVersion;
    }

    /// <summary>How many earlier versions the archive keeps (1–10; the setting «Хранить прежних версий»). Null: the archive is not
    /// trimmed by this updater (the helper repairing an interrupted operation does not know the setting).</summary>
    public int? Keep { get; init; }

    /// <summary>Variant of the running build, which is the variant of the program files it replaces; null — unknown.</summary>
    public bool? Light { get; init; }

    /// <summary>light (needs installed .NET) or self-contained; written into the assembly by the build.</summary>
    public static bool IsLight(Assembly assembly) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(x => x.Key == "PackageVariant")?.Value != "self-contained";

    /// <summary>Portable runtime of the release assets. RuntimeInformation.RuntimeIdentifier is not used: a .NET
    /// installed from a Linux distribution reports its own RID (ubuntu.24.04-x64), which no release carries.</summary>
    public static string CurrentRuntime => Runtime(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux",
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

    public static string Runtime(string os, System.Runtime.InteropServices.Architecture architecture) =>
        os + "-" + architecture.ToString().ToLowerInvariant();

    public static string AssetName(Version version, string runtime, bool light) =>
        $"BaseNavigator-{version}-{runtime}{(light ? "-light" : "")}{(runtime.StartsWith("win", StringComparison.Ordinal) ? ".zip" : ".tar.gz")}";

    /// <summary>The running copy is a saved one (.previous or the archive; started because the installed version does not work).</summary>
    public bool FromPrevious { get; }

    /// <summary>Program files were replaced by this process: the running version no longer describes the files on disk,
    /// so further updates and rollbacks wait for a restart (window, background check and command line alike).</summary>
    public static bool ChangedInThisProcess { get; internal set; }

    /// <summary>The reason the helper did not start for the files prepared in this process (any window or the background
    /// download), null when it started: then nothing applies them until the next start, and a restart request would only exit.</summary>
    public static string? HelperFailure { get; internal set; }

    /// <summary>Test hook: runs after the commit marker, before the staging folder is finished.</summary>
    internal static Action<string>? BeforeFinish { get; set; }

    private static void EnsureUnchanged()
    {
        if (ChangedInThisProcess) throw new InvalidOperationException("Файлы программы уже изменены. Перезапустите программу и повторите.");
    }
    /// <summary>The executable of this process. The helper sets its own copy: the program file is then not running, so it is
    /// replaced in one atomic rename.</summary>
    internal string RunningExecutable { get; init; }

    public ReleaseInfo Latest() => source.Latest();

    /// <summary>Where the last operation kept the files of a program folder that did not tell its version; null if none.</summary>
    public string? Parked { get; private set; }

    /// <summary>The note for a result line about such files.</summary>
    public string ParkedNote => Parked is null ? "" : $"; файлы прежней основной папки (версия не определилась) сохранены в {Parked}";

    /// <summary>The archive folder of this program folder (it may not exist yet).</summary>
    public string ArchivePath => Path.Combine(directory, ArchiveFolder);

    /// <summary>The most recently saved version (the one the last operation replaced); null when the archive is empty.</summary>
    public string? PreviousVersion => SavedVersions().OrderByDescending(x => x.Saved).ThenByDescending(x => x.Version).FirstOrDefault()?.Version.ToString();

    /// <summary>Complete saved versions, newest version first: the archive and a .previous of older builds not yet moved into it.</summary>
    public IReadOnlyList<SavedVersion> SavedVersions() =>
        Entries().Where(x => x.Complete).Select(x => x.Saved).OrderByDescending(x => x.Version).ThenByDescending(x => x.Saved).ToList();

    /// <summary>Every saved copy with a readable version, complete or not; the archive's copy of a version comes before .previous.</summary>
    private List<(SavedVersion Saved, bool Complete)> Entries()
    {
        var found = new List<(SavedVersion Saved, bool Complete)>();
        var archive = Path.Combine(directory, ArchiveFolder);
        if (Directory.Exists(archive))
            // In name order: Linux lists folders in no particular order, and choices between copies must not depend on it.
            foreach (var folder in Directory.GetDirectories(archive).Order(StringComparer.Ordinal))
                if (ReadEntry(folder) is { } entry && NamedFor(Path.GetFileName(folder), entry.Saved.Version)) found.Add(entry);
        // .previous is hidden only by a complete archived copy of its version (Codex audit 15.10: an incomplete one hid a usable copy).
        if (ReadEntry(Path.Combine(directory, PreviousFolder)) is { } legacy &&
            !found.Any(x => x.Complete && x.Saved.Version == legacy.Saved.Version)) found.Add(legacy);
        return found;
    }

    /// <summary>An archive folder name of the version: «0.3.0.7», or «0.3.0.7~<id>» — a second copy kept alongside while the
    /// first one was in use (Codex re-check 15.10).</summary>
    private static bool NamedFor(string name, Version version) =>
        name == version.ToString() || name.StartsWith(version + Alongside, StringComparison.Ordinal) && name.Length > version.ToString().Length + 1;

    private const string Alongside = "~";

    /// <summary>A saved copy: its version.txt, variant.txt and whether its files are a complete program; null when unreadable.</summary>
    private (SavedVersion Saved, bool Complete)? ReadEntry(string folder)
    {
        try
        {
            var file = Path.Combine(folder, VersionFile);
            if (!File.Exists(file) || !Version.TryParse(File.ReadLines(file).FirstOrDefault()?.Trim(), out var version)) return null;
            var variant = Path.Combine(folder, VariantFile);
            bool? light = File.Exists(variant) ? File.ReadAllText(variant).Trim() switch { "light" => true, "self-contained" => false, _ => null } : null;
            // Only program files count: an older build started from the folder leaves its .run.lock, logs or a .previous
            // there (Grok audit 15.10); they are ignored and never copied back.
            var complete = SavedFiles(folder).Contains(Path.GetFileName(executable));
            return (new SavedVersion(version, folder, light, File.GetLastWriteTimeUtc(file)) { InUse = InUse(folder) }, complete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The program files of a saved copy (service files and anything a program running there left are not).</summary>
    private string[] SavedFiles(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .Select(x => Path.GetRelativePath(folder, x)).Where(IsProgramFile).ToArray();

    /// <summary>A program runs from the folder: this process, or another one holding the folder's .run.lock (an older build
    /// started from the archive registers there; newer ones register in the program folder and are kept out by the helper).</summary>
    private bool InUse(string folder) => RunsFrom(folder) || Held(Path.Combine(folder, RunLock));

    /// <summary>The copy to restore: the given version, or the most recently saved one other than the running build (version and
    /// variant, as the window's «уже используется»; as the single .previous was: a second call goes back); a saved copy started by
    /// itself restores itself.</summary>
    /// <param name="folder">The copy chosen in the window: two copies of one version (variants) are told apart by their folder.</param>
    private (SavedVersion Saved, bool Complete) Resolve(Version current, Version? target, string? folder = null)
    {
        var entries = Entries();
        if (folder is not null)
            return entries.Where(x => string.Equals(x.Saved.Folder, folder, Comparison)).Cast<(SavedVersion, bool)?>().FirstOrDefault()
                ?? throw new InvalidOperationException($"Копия {folder} больше не сохранена в архиве.");
        if (target is not null)
            // Several copies of the version (one kept alongside): a complete one, the main copy of the version, then the newest
            // (Linux run 15.10: the choice followed the order the system listed the folders in).
            return entries.Where(x => x.Saved.Version == target).OrderByDescending(x => x.Complete)
                .ThenByDescending(x => Path.GetFileName(x.Saved.Folder) == target.ToString()).ThenByDescending(x => x.Saved.Saved)
                .Cast<(SavedVersion, bool)?>().FirstOrDefault()
                ?? throw new InvalidOperationException($"Версия {target} не сохранена в архиве.");
        if (FromPrevious && ReadEntry(Path.GetDirectoryName(RunningExecutable)!) is { } self) return self;
        return entries.Where(x => !IsRunningBuild(x.Saved, current)).OrderByDescending(x => x.Saved.Saved).ThenByDescending(x => x.Saved.Version)
            .ThenBy(x => x.Saved.Folder, StringComparer.Ordinal)
            .Cast<(SavedVersion, bool)?>().FirstOrDefault() ?? throw new InvalidOperationException("Ни одна прежняя версия не сохранена.");
    }

    /// <summary>The saved copy is the running build: the same version and, when both are known, the same variant.</summary>
    public bool IsRunningBuild(SavedVersion saved, Version current) =>
        saved.Version == current && (saved.Light is null || Light is null || saved.Light == Light);

    private bool RunsFrom(string folder) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
        Path.GetDirectoryName(RunningExecutable), Comparison);

    /// <summary>Every program file now in the folder (the archived copy is complete even when a package drops a file).</summary>
    private IEnumerable<string> ProgramFilesInFolder()
    {
        var top = Directory.GetFiles(directory).Select(Path.GetFileName).OfType<string>();
        var assets = Path.Combine(directory, "Assets");
        var inAssets = Directory.Exists(assets) ? Directory.GetFiles(assets).Select(x => Path.Combine("Assets", Path.GetFileName(x))) : [];
        return top.Concat(inAssets).Where(IsProgramFile);
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Only program files may come from a package: settings, the portable marker and service folders never do.</summary>
    private bool IsProgramFile(string relative)
    {
        var name = relative.Replace(Path.DirectorySeparatorChar, '/');
        return string.Equals(name, Path.GetFileName(executable), Comparison) || string.Equals(name, "Start-with-log.cmd", Comparison) ||
            string.Equals(name, "THIRD-PARTY-NOTICES.txt", Comparison) ||
            name.StartsWith("Assets/", Comparison) && name.Count(x => x == '/') == 1 && name.Length > "Assets/".Length;
    }

    /// <summary>Downloads, verifies and installs the release; returns the replaced file names.</summary>
    /// <param name="current">Version of the files now in the folder (they become the saved previous version).</param>
    public IReadOnlyList<string> Install(ReleaseInfo release, Version current, string runtime, bool light)
    {
        if (FromPrevious) throw new InvalidOperationException(SavedCopyRefusal);
        using var gate = Lock();
        Recover();
        EnsureUnchanged();
        var work = Path.Combine(directory, ".update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (package, files) = PrepareRelease(release, runtime, light, work);
            Transact(package, files, new(current.ToString(), release.Version.ToString(), Light, Keep, Exact: true));
            return files;
        }
        finally { TryDelete(work); }
    }

    private const string SavedCopyRefusal = "Это сохранённая копия прежней версии. Обновляйте из основной папки программы.";

    /// <summary>Downloads, verifies, unpacks and trial-runs a release in work; nothing in the program folder changes.</summary>
    /// <param name="progress">The stages and, while downloading, the size of the files in work polled against the release's sizes
    /// (gh tells nothing on the way); reported from a worker thread.</param>
    /// <param name="cancel">Stops the download (gh is killed) or the work between the stages; work is removed by the caller.</param>
    private (string Package, IReadOnlyList<string> Files) PrepareRelease(ReleaseInfo release, string runtime, bool light, string work,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancel = default)
    {
        var asset = AssetName(release.Version, runtime, light);
        Directory.CreateDirectory(work);
        {
            string[] assets = [asset, "SHA256SUMS.txt"];
            var total = DownloadSize(release, assets);
            progress?.Report(new(UpdateStage.Downloading, 0, total));
            using (progress is null ? null : new DownloadWatch(work, total, progress))
                source.Download(release, assets, work, cancel);
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new(UpdateStage.Downloading, DownloadWatch.Received(work), total));
            progress?.Report(new(UpdateStage.Verifying));
            VerifyDigests(release, assets, work);
            Verify(Path.Combine(work, asset), Path.Combine(work, "SHA256SUMS.txt"));
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new(UpdateStage.Extracting));
            var package = Path.Combine(work, "package");
            var files = Extract(Path.Combine(work, asset), package);
            if (files.FirstOrDefault(x => !IsProgramFile(x)) is { } foreign)
                throw new InvalidDataException($"Выпуск содержит файл вне состава программы: {foreign}. Установка отменена.");
            var candidate = Path.Combine(package, Path.GetFileName(executable));
            if (!files.Contains(Path.GetFileName(executable)))
                throw new InvalidDataException($"В выпуске нет файла {Path.GetFileName(executable)} для этой системы.");
            // The program files become those of the release, so a release must bring the whole program (Codex re-check 15.10).
            if (RequiredFiles(runtime).FirstOrDefault(x => !files.Contains(x, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) is { } absent)
                throw new InvalidDataException($"В выпуске нет файла {absent.Replace(Path.DirectorySeparatorChar, '/')}; установка отменена.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(candidate, (UnixFileMode)0b111_101_101);
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new(UpdateStage.TrialRun));
            var reported = probe(candidate)?.Trim();
            // A trial run must not leave anything in the package (older builds register their .run.lock there).
            if (File.Exists(Path.Combine(package, RunLock))) File.Delete(Path.Combine(package, RunLock));
            if (reported != release.Version.ToString())
                throw new InvalidOperationException(light
                    ? "Новая версия не запустилась для проверки. Для лёгкого варианта нужен установленный .NET 10 Runtime."
                    : "Новая версия не запустилась для проверки; установка отменена.");
            // Closed during the trial run: still nothing is made pending.
            cancel.ThrowIfCancellationRequested();
            return (package, files);
        }
    }

    /// <summary>What every release of the runtime carries (the publish output, scripts/github_export.py): the program, its icon and
    /// the notices; Windows releases also Start-with-log.cmd.</summary>
    public IReadOnlyList<string> RequiredFiles(string runtime) =>
        [Path.GetFileName(executable), Path.Combine("Assets", "basenavigator.png"), "THIRD-PARTY-NOTICES.txt",
         .. runtime.StartsWith("win", StringComparison.Ordinal) ? new[] { "Start-with-log.cmd" } : []];

    /// <summary>Installs a saved version (by default the most recently saved one other than current); the current files are
    /// archived. The saved copy is copied, never moved.</summary>
    /// <remarks>Direct replacement: the caller holds the exclusive .run.lock (the command line); windows use StageRollback.</remarks>
    public string Rollback(Version current, Version? target = null, string? folder = null)
    {
        using var gate = Lock();
        Recover();
        EnsureUnchanged();
        var work = Path.Combine(directory, ".update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (version, files, exact) = PrepareRollback(work, Resolve(current, target, folder));
            // Started from a saved copy: the files it replaces are those of the program folder, whose version the copy learns
            // by running it; a program that does not answer is kept apart (.versions/.unidentified-*), never dropped.
            var main = FromPrevious ? MainVersion() ?? Unidentified : current.ToString();
            Transact(work, files, new(main, version, FromPrevious ? null : Light, Keep, exact));
            return version;
        }
        finally { TryDelete(work); }
    }

    /// <summary>Version of the program folder, asked from its executable (a saved copy does not run it); null if it does not answer.</summary>
    private string? MainVersion()
    {
        if (!File.Exists(executable)) return null;
        return Version.TryParse(probe(executable)?.Trim(), out var version) ? version.ToString() : null;
    }

    /// <summary>Copies a saved version into work and checks it as a release is checked: the files recorded when it was archived
    /// (size and SHA-256), then a trial run of the copy that must report its version. Nothing in the program folder changes.</summary>
    /// <returns>Exact: the copy has an inventory, so its files are the whole program of that version; a copy of an older build
    /// (no inventory) only replaces files and never removes others.</returns>
    private (string Version, IReadOnlyList<string> Files, bool Exact) PrepareRollback(string work, (SavedVersion Saved, bool Complete) entry)
    {
        var folder = entry.Saved.Folder;
        var version = entry.Saved.Version;
        var files = SavedFiles(folder);
        if (!entry.Complete || !files.Contains(Path.GetFileName(executable)))
            throw new InvalidDataException($"Сохранённая версия {version} неполная или повреждена.");
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(work, file))!);
            File.Copy(Path.Combine(folder, file), Path.Combine(work, file));
        }
        // Copies of older builds (.previous) have no inventory: only the trial run checks them. The inventory is read once: the
        // lines checked are the lines that make the copy «whole», and they go with it (into a prepared package, for the helper).
        var inventory = Path.Combine(folder, InventoryFile);
        string[]? lines = null;
        try { lines = File.ReadAllLines(inventory); } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        if (lines is not null && CheckInventory(work, files, lines) is { } damage)
            throw new InvalidDataException($"Сохранённая версия {version} повреждена ({damage}); восстановление отменено.");
        if (lines is not null) File.WriteAllLines(Path.Combine(work, InventoryFile), lines);
        var candidate = Path.Combine(work, Path.GetFileName(executable));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(candidate, (UnixFileMode)0b111_101_101);
        var reported = probe(candidate)?.Trim();
        if (File.Exists(Path.Combine(work, RunLock))) File.Delete(Path.Combine(work, RunLock));
        if (reported != version.ToString())
            throw new InvalidOperationException($"Сохранённая версия {version} не запустилась для проверки; восстановление отменено." +
                (entry.Saved.Light != false ? " Для лёгкого варианта нужен установленный .NET 10 Runtime." : ""));
        return (version.ToString(), files, lines is not null);
    }

    /// <summary>Lines «sha256 size path» for the files of a copy (paths with «/»).</summary>
    private static IEnumerable<string> Inventory(string folder, IEnumerable<string> files) => files.Select(file =>
    {
        using var stream = File.OpenRead(Path.Combine(folder, file));
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return $"{hash} {stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} {file.Replace(Path.DirectorySeparatorChar, '/')}";
    });

    /// <summary>The first difference between the copied files and the inventory written when the copy was archived; null if none.</summary>
    private static string? CheckInventory(string work, IReadOnlyList<string> files, IEnumerable<string> lines)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var expected = new Dictionary<string, string>(comparer);
        foreach (var line in lines.Where(x => x.Length > 0))
        {
            var parts = line.Split(' ', 3);
            if (parts.Length != 3) return "неверная опись файлов";
            expected[parts[2]] = parts[0] + " " + parts[1];
        }
        var actual = Inventory(work, files).Select(x => x.Split(' ', 3)).ToDictionary(x => x[2], x => x[0] + " " + x[1], comparer);
        if (expected.Keys.FirstOrDefault(x => !actual.ContainsKey(x)) is { } missing) return "нет файла " + missing;
        if (actual.Keys.FirstOrDefault(x => !expected.ContainsKey(x)) is { } extra) return "лишний файл " + extra;
        return actual.FirstOrDefault(x => !string.Equals(x.Value, expected[x.Key], StringComparison.OrdinalIgnoreCase)) is { Key: { } changed }
            ? "изменён файл " + changed : null;
    }

    /// <summary>Prepares a release to be applied after the program exits (window and background: the running file is never replaced).</summary>
    /// <param name="progress">The stages of the preparation (see <see cref="UpdateProgress"/>); without it nothing is reported.</param>
    /// <param name="cancel">Stops the preparation before the package is made pending (OperationCanceledException); nothing stays.</param>
    public void Stage(ReleaseInfo release, string runtime, bool light, IProgress<UpdateProgress>? progress = null, CancellationToken cancel = default)
    {
        if (FromPrevious) throw new InvalidOperationException(SavedCopyRefusal);
        StageWith(work =>
        {
            var (package, files) = PrepareRelease(release, runtime, light, work, progress, cancel);
            // The files just checked (checksum, trial run) are recorded: the helper applies only these very files (Codex re-check 3).
            File.WriteAllLines(Path.Combine(package, InventoryFile), Inventory(package, files));
            return (release.Version.ToString(), package, WholeRelease);
        });
    }

    /// <summary>Prepares a saved version (by default the most recently saved one other than current) to be applied after the program exits.</summary>
    /// <remarks>Also from a saved copy (Codex audit 15.10): the helper replaces the files under the exclusive .run.lock once every
    /// copy has closed, and archives the version of the program folder, which the copy asks from its executable now.</remarks>
    public string StageRollback(Version current, Version? target = null, string? folder = null)
    {
        var version = "";
        StageWith(work =>
        {
            var entry = Resolve(current, target, folder);
            var prepared = PrepareRollback(work, entry);
            version = prepared.Version;
            return (version, work, prepared.Exact ? WholeByInventory : "");
        }, FromPrevious ? "probe" : null);
        return version;
    }

    /// <param name="archive">From a saved copy: «probe» — the helper asks the program folder its version after it has finished an
    /// interrupted operation, so the label always matches the files it archives.</param>
    /// <summary>The sixth line of update.txt: what proves the package a whole program — the required files of a release, or the
    /// inventory of an archived copy. The helper checks it again on the files it applies; empty — replacement only.</summary>
    private const string WholeRelease = "release", WholeByInventory = "inventory";

    private void StageWith(Func<string, (string Version, string Package, string Whole)> prepare, string? archive = null)
    {
        using var gate = Lock();
        // A saved copy is what is left when the program folder does not start: the helper finishes the interrupted operation
        // first (ApplyPending recovers before it applies), so the copy may prepare (Grok audit 15.10).
        if (NeedsRecovery && archive is null) throw new InvalidOperationException("Незавершённое обновление ещё не восстановлено. Перезапустите программу.");
        EnsureUnchanged();
        var work = Path.Combine(directory, ".update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (version, package, whole) = prepare(work);
            var main = archive;
            var pending = Path.Combine(directory, PendingFolder);
            TryDelete(pending);
            Directory.Move(package, pending);
            // Second line: identity of this package; once applied it is recorded in .update-applied (see ApplyLocked).
            // Then what the helper cannot know itself: the archive size and the variant of the files it replaces.
            // A saved copy fills the fifth line: the version of the program folder (its own variant is not that of the folder).
            // The sixth: what makes the package a whole program, whose other program files go (packages of older builds never remove).
            File.WriteAllLines(Path.Combine(pending, PendingInfo), [version, Guid.NewGuid().ToString("N"),
                Keep?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", main is null ? VariantName(Light) : "", main ?? "", whole]);
            ChangedInThisProcess = true;
        }
        finally { TryDelete(work); }
    }

    public string? PendingVersion
    {
        get
        {
            var file = Path.Combine(directory, PendingFolder, PendingInfo);
            return File.Exists(file) ? File.ReadLines(file).FirstOrDefault()?.Trim() : null;
        }
    }

    /// <summary>Prepares the release and starts the helper (window, background check and the update offer alike). A download
    /// or check failure throws and leaves nothing pending; a helper that does not start is reported with the package kept.</summary>
    public StagedUpdate StageAndLaunch(ReleaseInfo release, Version current, string runtime, bool light, IEnumerable<string> restartArguments,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancel = default)
    {
        Stage(release, runtime, light, progress, cancel);
        return new(release.Version, TryLaunchHelper(current, restartArguments));
    }

    /// <summary>LaunchHelper for prepared files: null when the helper started (or one already waits), else the reason.</summary>
    public string? TryLaunchHelper(Version current, IEnumerable<string> restartArguments)
    {
        try { LaunchHelper(current, restartArguments); HelperFailure = null; return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return HelperFailure = ex.Message; }
    }

    /// <summary>
    /// Starts a copy of the running program that applies the pending files once no copy of the program runs any more.
    /// The copy lives outside the program files, so nothing it runs from is replaced. Only one helper works at a time.
    /// </summary>
    /// <returns>False when a helper is already waiting (it applies the newest pending files).</returns>
    public bool LaunchHelper(Version current, IEnumerable<string> restartArguments, int? processId = null)
    {
        var pending = Path.Combine(directory, PendingFolder);
        if (!Directory.Exists(pending)) throw new InvalidOperationException("Нет подготовленного обновления.");
        if (HelperRunning()) return false;
        var helperFolder = Path.Combine(directory, ".helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperFolder);
        var helper = Path.Combine(helperFolder, Path.GetFileName(RunningExecutable));
        File.Copy(RunningExecutable, helper);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, (UnixFileMode)0b111_101_101);
        // Restart the way the program was started: same arguments, same working folder (relative --settings).
        File.WriteAllLines(Path.Combine(helperFolder, RestartArguments), [Environment.CurrentDirectory, .. restartArguments]);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = helperFolder };
        foreach (var argument in new[] { "--apply-update", (processId ?? Environment.ProcessId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                     directory, Path.GetFileName(executable), current.ToString() })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить установку обновления.");
        return true;
    }

    /// <summary>Removes a prepared update (after it failed to apply).</summary>
    public void DiscardPending()
    {
        using var gate = Lock();
        TryDelete(Path.Combine(directory, PendingFolder));
    }

    /// <summary>Asks the helper to start the program again after applying the update.</summary>
    public void RequestRestart() => File.WriteAllText(Path.Combine(directory, PendingFolder, RestartMarker), "");

    private bool HelperRunning() => Directory.GetDirectories(directory, ".helper-*").Any(x => Held(Path.Combine(x, HelperLock)));

    /// <summary>A lock file some process keeps open with FileShare.None (it disappears or opens freely once that process ends).</summary>
    private static bool Held(string file)
    {
        if (!File.Exists(file)) return false;
        try { using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Registration of a running copy: a shared hold on .run.lock. The helper (and a direct command-line update) needs
    /// an exclusive hold, which the system grants only when no copy holds it, and which keeps new copies out meanwhile.
    /// Windows enforces this with file sharing modes; .NET on Linux with flock (shared/exclusive).
    /// </summary>
    public static Registration Register(string directory, TimeSpan wait)
    {
        var path = Path.Combine(new Updater(directory, "x", new NoSource()).directory, RunLock);
        var watch = Stopwatch.StartNew();
        var waited = false;
        while (true)
        {
            // Read-only: .NET on Linux takes the shared flock for such a descriptor on every file system (for writable ones it
            // skips NFS/SMB). No FileShare.Delete: the lock file keeps its name while copies run.
            try
            {
                if (!File.Exists(path)) using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                return new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), waited, false);
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException && !(ex is PathTooLongException))
            {
                // Held exclusively: the program files are being replaced right now.
                waited = true;
                if (watch.Elapsed > wait) return new(null, true, true);
                Thread.Sleep(250);
            }
            // Cannot register at all (no rights, name taken by a folder): then nobody can take it exclusively either,
            // so no update can replace the files while this copy runs.
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return new(null, waited, false); }
        }
    }

    /// <summary>Exclusive hold on .run.lock: succeeds only when no copy of the program runs; null otherwise.</summary>
    public FileStream? TryExclusive() => TryExclusive(out _);

    /// <param name="impossible">The file cannot be opened at all (rights, a folder in its place): waiting will not help.</param>
    public FileStream? TryExclusive(out bool impossible)
    {
        impossible = false;
        // Without file locking in .NET (switch or environment) the exclusive hold proves nothing: updates are not applied.
        if (FileLockingDisabled) { impossible = true; return null; }
        try { return new FileStream(Path.Combine(directory, RunLock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (UnauthorizedAccessException) { impossible = true; return null; }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException) { impossible = true; return null; }
        catch (IOException) { return null; }
    }

    public static bool FileLockingDisabled =>
        AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var disabled) && disabled ||
        // Same rule as the runtime: "1" or "true" in any letter case.
        Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING")?.Trim() is { } value &&
        (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>An interrupted update left files to put back; this must happen outside the running program.</summary>
    public bool NeedsRecovery => Directory.Exists(Path.Combine(directory, Staging));

    /// <summary>Starts the helper to repair an interrupted update after this process exits, then to start the program again.</summary>
    public void LaunchRecovery(Version current, IEnumerable<string> restartArguments)
    {
        var pending = Path.Combine(directory, PendingFolder);
        Directory.CreateDirectory(pending);
        RequestRestart();
        LaunchHelper(current, restartArguments);
    }

    /// <summary>The repair of an interrupted update at the start of the program: true when the helper took it (this process is to
    /// exit), else the notice for the user or null. A helper that does not start is said, not dropped (Grok full audit 24.10, P2-2):
    /// the folder of the program may hold old and new files; .previous.new stays, so the next start tries again.</summary>
    public (bool Started, string? Notice, Exception? Failure) RecoverAtStart(Version current, IEnumerable<string> restartArguments)
    {
        if (!NeedsRecovery || FromPrevious) return (false, null, null);
        // The last repair failed: do not repeat it at every start; the reason is shown and the program runs.
        if (TakeResult(directory) is { } last && last.StartsWith("error ", StringComparison.Ordinal))
            return (false, "Не удалось восстановить прерванное обновление: " + last[6..], null);
        try { LaunchRecovery(current, restartArguments); return (true, null, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return (false, HelperNotStarted(ex), ex); }
    }

    /// <summary>A prepared update the helper of the previous run did not apply (for example after a reboot): a helper waits for this
    /// run. The notice of this start; null when nothing is prepared. A helper that does not start is said (Grok full audit 24.10, P2-2).</summary>
    public string? ResumePending(Version current, IEnumerable<string> restartArguments)
    {
        if (PendingVersion is not { } pending || FromPrevious) return null;
        return TryLaunchHelper(current, restartArguments) is { } error
            ? $"Версия {pending} не установлена: установщик не запустился ({error.TrimEnd('.')}). Установка будет повторена при следующем запуске."
            : $"Версия {pending} будет установлена после закрытия всех окон программы";
    }

    /// <summary>The notice of a repair that did not start: the program runs on the folder as the interrupted update left it.</summary>
    public static string HelperNotStarted(Exception error) =>
        $"Прерванное обновление не восстановлено: установщик не запустился ({error.Message.TrimEnd('.')}). Папка программы может " +
        "содержать файлы разных версий; восстановление будет повторено при следующем запуске.";

    /// <summary>
    /// Helper side: waits for the program to exit and then for an exclusive hold on .run.lock (no copy of the program runs,
    /// none can start), applies the pending files with the usual transaction (from a fresh copy for each attempt),
    /// records the outcome and restarts the program when asked and its arguments were read.
    /// </summary>
    public static int ApplyPending(int processId, string directory, string executableName, Version current, Func<int, bool>? running = null,
        Action<string, IReadOnlyList<string>>? start = null, string? helperFolder = null, TimeSpan? lockWait = null, Func<string, string?>? probe = null)
    {
        var executable = Path.Combine(directory, executableName);
        // Another copy of the program holding .run.lock is waited for, but not forever: the helper reports and exits,
        // the program files untouched and the prepared update kept for the next attempt.
        var patience = lockWait ?? TimeSpan.FromMinutes(10);
        helperFolder ??= Path.GetDirectoryName(Environment.ProcessPath) ?? directory;
        var log = Path.Combine(directory, HelperLog);
        void Log(string text)
        {
            try
            {
                if (File.Exists(log) && new FileInfo(log).Length > 1_000_000) File.Delete(log);
                File.AppendAllText(log, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {text}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        string result = "";
        var code = 0;
        var restart = false;
        IReadOnlyList<string>? arguments = null;
        try
        {
            using var helperLock = new FileStream(Path.Combine(helperFolder, HelperLock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Log($"started for process {processId}, version {current}");
            running ??= IsRunning;
            while (running(processId)) Thread.Sleep(500);
            // The helper runs from its own copy: the program file is not running and is replaced in one rename (Codex audit 15.10).
            var updater = new Updater(directory, executable, new NoSource(), probe) { RunningExecutable = Path.Combine(helperFolder, executableName) };
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var run = updater.TryExclusive(out var impossible);
                    if (impossible) throw new InvalidOperationException(FileLockingDisabled
                        ? "Блокировки файлов .NET отключены (DOTNET_SYSTEM_IO_DISABLEFILELOCKING); обновление не установлено."
                        : "Нет доступа к файлу .run.lock в папке программы; обновление не установлено.");
                    using var gate = run is null ? null : updater.TryLock();
                    if (run is null || gate is null)
                    {
                        if (waiting.Elapsed >= patience)
                            throw new TimeoutException($"Другая копия БазоНавигатора не закрылась за {patience.TotalMinutes:0.#} мин.; обновление не установлено. Закройте все копии программы и запустите её снова.");
                        Thread.Sleep((int)Math.Clamp(patience.TotalMilliseconds, 1, 500)); attempt--; continue;
                    }
                    var pending = Path.Combine(directory, PendingFolder);
                    restart = File.Exists(Path.Combine(pending, RestartMarker));
                    var saved = Path.Combine(helperFolder, RestartArguments);
                    arguments = File.Exists(saved) ? File.ReadAllLines(saved) : null;
                    var recovered = updater.NeedsRecovery;
                    updater.Recover();
                    var version = updater.ApplyLocked(current);
                    result = version is not null ? "ok " + version + updater.ParkedNote : recovered ? "recovered" + updater.ParkedNote : "";
                    Log(result.Length > 0 ? result : "nothing to apply");
                    break;
                }
                catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
                {
                    Log($"attempt {attempt}: {ex.GetType().Name}");
                    Thread.Sleep(500);
                }
            }
        }
        catch (Exception ex)
        {
            result = "error " + LegacyImport.ErrorMessage(ex); code = 7;
            Log($"failed: {ex.GetType().Name}: {ex.Message}");
        }
        try { if (result.Length > 0) File.WriteAllText(Path.Combine(directory, ResultFile), result); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // Restart only when this helper installed or repaired something, with the original arguments: never twice,
        // never with a default profile by accident.
        if (restart && code == 0 && result.Length > 0 && arguments is { Count: > 0 } && File.Exists(executable))
        {
            var folder = Directory.Exists(arguments[0]) ? arguments[0] : directory;
            Log("restarting the program");
            (start ?? StartProgram)(executable, [folder, .. arguments.Skip(1)]);
        }
        else if (restart) Log("not restarting: the update failed or the start arguments are unknown");
        return code;
    }

    private string? ApplyLocked(Version current)
    {
        var pending = Path.Combine(directory, PendingFolder);
        var version = PendingVersion;
        if (version is null) { TryDelete(pending); return null; }
        // A package whose removal failed after it was applied must not be applied again (it would replace .previous).
        var identity = File.ReadLines(Path.Combine(pending, PendingInfo)).Skip(1).FirstOrDefault()?.Trim() ?? "";
        var applied = Path.Combine(directory, AppliedFile);
        if (identity.Length > 0 && File.Exists(applied) && File.ReadAllText(applied).Trim() == identity) { TryDelete(pending); return null; }
        var info = File.ReadLines(Path.Combine(pending, PendingInfo)).Skip(2).Select(x => x.Trim()).ToArray();
        int? keep = info.Length > 0 && int.TryParse(info[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : Keep;
        var light = info.Length > 1 ? ParseVariant(info[1]) ?? Light : Light;
        // Prepared by a saved copy: the program folder is asked its version now, after Recover; one that does not answer is kept apart.
        var saved = info.Length > 2 && info[2].Length > 0 ? info[2] : null;
        var label = saved is null ? current.ToString() : (saved == "probe" ? MainVersion() : Version.TryParse(saved, out var main) ? main.ToString() : null) ?? Unidentified;
        if (saved is not null) light = null;
        var whole = info.Length > 3 ? info[3] : "";
        var files = Directory.GetFiles(pending, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(pending, x))
            .Where(x => x is not (PendingInfo or RestartMarker or RestartArguments or InventoryFile)).ToArray();
        if (!files.Contains(Path.GetFileName(executable)) || files.Any(x => !IsProgramFile(x)))
            throw new InvalidDataException("Подготовленное обновление неполное или повреждено.");
        // Each attempt works on a copy: the prepared package stays complete until the update has succeeded.
        var work = Path.Combine(directory, ".update-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(work, file))!);
                File.Copy(Path.Combine(pending, file), Path.Combine(work, file));
            }
            // A package that removes files is checked again here, on the very files applied (Codex re-check 2, 15.10): the
            // prepared folder may have lost a file since it was prepared; such a package is refused, nothing changes.
            // Packages of this build carry the inventory of the files checked when they were prepared (a release: after its checksum
            // and trial run; a restoration: the archive's inventory); the copy applied must match it (Codex re-check 3, 15.10).
            // A package flagged whole but without an inventory (an intermediate format of this step) is taken as one of an older
            // build: it only replaces files, and the trial run below still checks it (Codex re-check 4).
            var inventory = Path.Combine(pending, InventoryFile);
            var exact = whole.Length > 0 && File.Exists(inventory);
            if (File.Exists(inventory) && CheckInventory(work, files, File.ReadAllLines(inventory)) is { } damage)
                throw new InvalidDataException($"Подготовленное обновление повреждено ({damage}); ничего не изменено.");
            if (exact && whole == WholeRelease && RequiredFiles(CurrentRuntime).FirstOrDefault(x => !files.Contains(x, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) is { } absent)
                throw new InvalidDataException($"В подготовленном обновлении нет файла {absent.Replace(Path.DirectorySeparatorChar, '/')}; ничего не изменено.");
            // Every package, also one prepared by an older build without an inventory, is trial-run as the copy that will be put in place.
            var candidate = Path.Combine(work, Path.GetFileName(executable));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(candidate, (UnixFileMode)0b111_101_101);
            var reported = probe(candidate)?.Trim();
            if (File.Exists(Path.Combine(work, RunLock))) File.Delete(Path.Combine(work, RunLock));
            if (reported != version)
                throw new InvalidOperationException($"Подготовленная версия {version} не запустилась для проверки; ничего не изменено.");
            // The identity is recorded with the commit of the transaction (Finish, also after an interruption): an undone
            // replacement leaves the package applicable, a committed one is never applied a second time.
            Transact(work, files, new(label, version, light, keep, exact), identity: identity);
        }
        finally { TryDelete(work); }
        TryDelete(pending);
        return version;
    }

    /// <summary>Outcome of an update applied after the previous run ("ok 0.2.0.21", "recovered" or "error …"); read once.</summary>
    public static string? TakeResult(string directory)
    {
        var file = Path.Combine(Path.GetFullPath(directory), ResultFile);
        try
        {
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file).Trim();
            File.Delete(file);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsRunning(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <param name="arguments">The working folder first, then the program arguments.</param>
    private static void StartProgram(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = arguments[0] };
        foreach (var argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
        try { using var process = Process.Start(start); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>At start: finishes or undoes an interrupted update and removes leftovers; skipped while another process updates.</summary>
    public static void Cleanup(string directory, string executable)
    {
        try
        {
            var updater = new Updater(directory, executable, new NoSource());
            using var gate = updater.TryLock();
            // Putting back files of an interrupted update would replace the running program: the helper does it (NeedsRecovery).
            if (gate is null || updater.NeedsRecovery) return;
            updater.Recover();
            var archive = Path.Combine(updater.directory, ArchiveFolder);
            // Retired archive copies whose removal failed after a finished operation (a copy that was running from one, say).
            foreach (var stale in Directory.GetDirectories(updater.directory, ".update-*")
                         .Concat(Directory.GetDirectories(updater.directory, PreviousFolder + Retired + "*"))
                         .Concat(Directory.Exists(archive) ? Directory.GetDirectories(archive, Retired + "*") : [])) TryDelete(stale);
            // A helper that is still running keeps its files (Linux would let them be removed).
            // A just started helper has not taken its lock yet: only folders older than a day are considered.
            foreach (var helper in Directory.GetDirectories(updater.directory, ".helper-*"))
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(helper) > TimeSpan.FromDays(1) && !Held(Path.Combine(helper, HelperLock)))
                    TryDelete(helper);
            foreach (var old in Directory.GetFiles(updater.directory, Path.GetFileName(updater.executable) + ".old*"))
                try { File.SetAttributes(old, FileAttributes.Normal); File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The bytes to download: the package and the checksums, when the release told the size of the package.</summary>
    internal static long? DownloadSize(ReleaseInfo release, IReadOnlyList<string> assets) =>
        release.AssetSizes is { } sizes && sizes.GetValueOrDefault(assets[0]) > 0 ? assets.Sum(x => sizes.GetValueOrDefault(x)) : null;

    /// <summary>Polls the size of the download folder while gh writes there (whatever names it uses for its files).</summary>
    private sealed class DownloadWatch : IDisposable
    {
        private readonly Timer timer;
        private readonly Lock gate = new();
        private bool stopped;
        private long last;

        public DownloadWatch(string folder, long? total, IProgress<UpdateProgress> progress) =>
            timer = new(_ =>
            {
                lock (gate)
                {
                    if (stopped) return;
                    var received = Received(folder);
                    if (received == last) return;
                    last = received; progress.Report(new(UpdateStage.Downloading, received, total));
                }
            }, null, ProgressInterval, ProgressInterval);

        /// <summary>No report comes after this returns.</summary>
        public void Dispose() { lock (gate) stopped = true; timer.Dispose(); }

        public static long Received(string folder)
        {
            long sum = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    try { sum += new FileInfo(file).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return sum;
        }
    }

    /// <summary>How often the download folder is polled.</summary>
    internal static TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    private sealed class NoSource : IReleaseSource
    {
        public ReleaseInfo Latest() => throw new InvalidOperationException();
        public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory) => throw new InvalidOperationException();
    }

    private FileStream Lock() => TryLock() ?? throw new IOException("Обновление уже выполняется другим экземпляром программы. Повторите позже.");

    private FileStream? TryLock()
    {
        try { return new FileStream(Path.Combine(directory, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException) { return null; }
    }

    /// <summary>What a transaction saves: the version of the files it replaces (and their variant), the version it installs
    /// (not counted against Keep) and the archive size; null Keep — the archive is not trimmed.</summary>
    /// <param name="Exact">The incoming files are the whole program of that version: program files it lacks are removed.</param>
    private sealed record Saving(string Current, string Installed, bool? Light, int? Keep, bool Exact = false);

    private static string VariantName(bool? light) => light switch { true => "light", false => "self-contained", null => "" };
    private static bool? ParseVariant(string value) => value switch { "light" => true, "self-contained" => false, _ => null };

    /// <summary>
    /// 1) the current program files (every name in the package and every program file in the folder) go to .previous.new with
    /// version.txt, variant.txt, inventory.txt and the manifest (written last); 2) files are replaced; 3) the commit marker is
    /// written (with the archive size and the installed version); 4) .previous.new becomes .versions/&lt;current&gt;, then
    /// .previous is moved in and the archive trimmed, and only then the marker goes. A failure before 3 restores from
    /// .previous.new; after 3 Recover finishes step 4 (also when only the trimming was left). Restore — also of older builds —
    /// reads only the "+" and "-" lines of the manifest.
    /// </summary>
    /// <param name="identity">Pending package being applied: recorded as applied together with the commit (see Finish).</param>
    private void Transact(string package, IReadOnlyList<string> files, Saving saving, bool promote = true, string identity = "")
    {
        var staging = Path.Combine(directory, Staging);
        TryDelete(staging);
        Directory.CreateDirectory(staging);
        var manifest = new List<string>();
        var names = new HashSet<string>(files, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in files.Concat(ProgramFilesInFolder().Where(x => !names.Contains(x))))
        {
            var current = Path.Combine(directory, file);
            // "+" — existed before and is saved; "-" — new, removed on undo. A missing copy never means "delete".
            // Files the package does not replace are saved too ("+", equal on undo), so the archived copy is complete.
            manifest.Add((File.Exists(current) ? "+" : "-") + file);
            if (!File.Exists(current)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(staging, file))!);
            File.Copy(current, Path.Combine(staging, file));
            // A read-only original must not make the saved copy undeletable at the next update.
            File.SetAttributes(Path.Combine(staging, file), FileAttributes.Normal);
        }
        // The archived program stays executable on Linux whatever the copy kept (Grok audit 15.10).
        if (!OperatingSystem.IsWindows() && File.Exists(Path.Combine(staging, Path.GetFileName(executable))))
            File.SetUnixFileMode(Path.Combine(staging, Path.GetFileName(executable)), (UnixFileMode)0b111_101_101);
        var others = ProgramFilesInFolder().Where(x => !names.Contains(x)).ToList();
        File.WriteAllText(Path.Combine(staging, VersionFile), saving.Current);
        // Everything the archive needs is in the folder before it is moved there (Codex audit 15.10).
        if (saving.Light is not null) File.WriteAllText(Path.Combine(staging, VariantFile), VariantName(saving.Light));
        File.WriteAllLines(Path.Combine(staging, InventoryFile), Inventory(staging, SavedFiles(staging)));
        File.WriteAllLines(Path.Combine(staging, Manifest), manifest);
        var keep = saving.Keep is { } count ? Math.Clamp(count, 1, MaxKeep).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        try
        {
            foreach (var file in files) Put(Path.Combine(package, file), Path.Combine(directory, file), copy: false);
            // The program files become exactly those of the installed version: what it does not have goes (saved above as "+",
            // so an undo puts it back). Otherwise a newer asset would stay and be archived later under the older version.
            // Only for a verified whole program (Codex re-check 15.10): a release checked against RequiredFiles, an archived copy
            // with its inventory.
            if (saving.Exact) foreach (var other in others) File.Delete(Path.Combine(directory, other));
            // From here the operation is done; Recover finishes it ("keep": the saved copy stays as it is).
            // Older builds read the first two lines only.
            WriteAtomically(Path.Combine(directory, Committed), [promote ? "promote" : "keep", identity, keep, saving.Installed]);
        }
        catch
        {
            // Keep the original error; a restore that cannot finish leaves .previous.new for the next start.
            try { Restore(staging); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        ChangedInThisProcess = true;
        BeforeFinish?.Invoke(staging);
        // The new files are in place; if the folders cannot be finished now, Recover completes it at the next start.
        try { Finish(staging, promote, identity, Housekeeping.Of(keep, saving.Installed)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Drops a finished staging folder: the manifest goes first, so a partial deletion is never restored again.</summary>
    private static void Discard(string staging)
    {
        File.Delete(Path.Combine(staging, Manifest));
        TryDelete(staging);
    }

    /// <summary>Completes a committed operation or undoes an interrupted one; the saved .previous is never touched before that.</summary>
    private void Recover()
    {
        var staging = Path.Combine(directory, Staging);
        var committed = Path.Combine(directory, Committed);
        if (Directory.Exists(staging) && File.Exists(Path.Combine(staging, Manifest)))
        {
            var marker = File.Exists(committed) ? File.ReadAllLines(committed) : [];
            // Only a complete marker means "committed"; anything else is an unfinished operation and is undone.
            if (marker.Length >= 2 && marker[0].Trim() is "promote" or "keep")
                Finish(staging, marker[0].Trim() == "promote", marker[1].Trim(), Housekeeping.Of(marker));
            // Files of an unfinished operation are put back: the running build may no longer match them.
            else Restore(staging, () => ChangedInThisProcess = true);
        }
        else
        {
            TryDelete(staging);
            // Interrupted after the saved files reached the archive: what was left is moving .previous in and the trimming.
            var marker = File.Exists(committed) ? File.ReadAllLines(committed) : [];
            // A marker without the archive folder (written before the folder was recorded) cannot tell which copy the operation
            // made: this recovery does not trim at all; the next operation does (Codex re-check 4, 15.10).
            if (marker.Length >= 2 && marker[0].Trim() == "promote")
            {
                var tidy = Housekeeping.Of(marker);
                Tidy(tidy.Archived is null ? tidy with { Keep = null } : tidy, ArchivedFolder(tidy.Archived));
            }
        }
        if (File.Exists(committed)) File.Delete(committed);
        if (File.Exists(committed + ".tmp")) File.Delete(committed + ".tmp");
    }

    /// <summary>Completes a committed operation; the applied package is recorded first, while the commit marker still exists.</summary>
    /// <summary>Writes the whole file under a temporary name, flushes it and renames it: the file is never seen incomplete.</summary>
    private static void WriteAtomically(string path, IEnumerable<string> lines)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            foreach (var line in lines) writer.WriteLine(line);
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    /// <summary>The archive size and the installed version a committed operation trims with (from its commit marker).</summary>
    /// <param name="Archived">The archive folder name the saved files went to; written into the marker by Promote before the move.</param>
    private sealed record Housekeeping(int? Keep, Version? Installed, string? Archived = null)
    {
        public static Housekeeping Of(string keep, string installed) => new(
            int.TryParse(keep.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                ? Math.Clamp(count, 1, MaxKeep) : null,
            Version.TryParse(installed.Trim(), out var version) ? version : null);
        public static Housekeeping Of(string[] marker) => Of(marker.Length > 2 ? marker[2] : "", marker.Length > 3 ? marker[3] : "") with
            { Archived = marker.Length > 4 && marker[4].Trim().Length > 0 ? marker[4].Trim() : null };
    }

    private void Finish(string staging, bool promote, string identity, Housekeeping housekeeping)
    {
        if (identity.Length > 0) File.WriteAllText(Path.Combine(directory, AppliedFile), identity);
        if (promote) Promote(staging, housekeeping);
        else { Discard(staging); File.Delete(Path.Combine(directory, Committed)); }
    }

    /// <summary>
    /// The saved files become .versions/&lt;version&gt; (a copy of the same version already there is replaced by this newer one).
    /// Each step is a rename, and until the marker is gone Recover repeats what is left: an interruption loses neither the
    /// saved files nor another archived version. Only then, as housekeeping that never fails the operation, .previous of an
    /// older build is moved into the archive and the archive is trimmed.
    /// </summary>
    private void Promote(string staging, Housekeeping housekeeping)
    {
        var versionFile = Path.Combine(staging, VersionFile);
        var archive = Path.Combine(directory, ArchiveFolder);
        var label = File.Exists(versionFile) ? File.ReadLines(versionFile).FirstOrDefault()?.Trim() : null;
        if (label == Unidentified && SavedFiles(staging).Length > 0)
        {
            // Files of a program folder that did not tell its version: kept apart, outside the list and the trimming.
            Directory.CreateDirectory(archive);
            var parked = Path.Combine(archive, $".{Unidentified}-{DateTime.Now:yyyyMMdd-HHmmss}");
            if (Directory.Exists(parked)) parked += "-" + Guid.NewGuid().ToString("N")[..8];
            // The explanation goes in before the move (Codex re-check 2): it stays with the files even if an interruption loses the
            // result line; a repeated attempt after an interruption simply writes it again.
            File.WriteAllText(Path.Combine(staging, "README.txt"), $"Файлы основной папки программы от {DateTime.Now:dd.MM.yyyy HH:mm}: её версия не определилась " +
                "(программа не ответила на --version), поэтому они сохранены здесь, вне списка «Восстановление версии». Удалите папку, если она не нужна.");
            RecordArchived(parked);
            Directory.Move(staging, parked);
            AfterPark?.Invoke(parked);
            File.Delete(Path.Combine(parked, Manifest));
            Parked = parked;
            Tidy(housekeeping, null);
            File.Delete(Path.Combine(directory, Committed));
            return;
        }
        if (!Version.TryParse(label, out var version))
        {
            // Not written by any build (version.txt precedes the manifest): nothing could find this copy; the new files are in place.
            Discard(staging); File.Delete(Path.Combine(directory, Committed)); return;
        }
        Directory.CreateDirectory(archive);
        var target = Path.Combine(archive, version.ToString());
        // Never worse than what is there: saved files without the program (it was missing from the folder) are not archived, and
        // the copy a saved program runs from is not replaced (Windows refuses, Linux would pull its file from under it).
        if (ReadEntry(staging) is not { Complete: true })
        {
            Discard(staging); File.Delete(Path.Combine(directory, Committed)); return;
        }
        // The older copy of the version is replaced when it can be retired; one in use (a program runs from it) stays, and the new
        // copy is kept alongside under «<version>~<id>» — never dropped (Codex re-check 15.10: two variants of one version differ).
        if (Directory.Exists(target) && !TryRetire(target, Path.Combine(archive, Retired + Guid.NewGuid().ToString("N"))))
            target = Path.Combine(archive, version + Alongside + Guid.NewGuid().ToString("N")[..8]);
        RecordArchived(target);
        Directory.Move(staging, target);
        File.Delete(Path.Combine(target, Manifest));
        // The saved files are archived. What follows only removes copies and never fails the operation; the marker goes last, so an
        // interruption here is resumed by Recover, and a failure leaves the copies for Cleanup or the next operation.
        Tidy(housekeeping, target);
        File.Delete(Path.Combine(directory, Committed));
    }

    /// <summary>
    /// Writes the archive folder the saved files are about to get into the commit marker (fifth line, atomically) before they are
    /// moved: if the move is interrupted, Recover knows which copy this operation made and never trims it — no guessing by time
    /// (Codex re-check 3, 15.10).
    /// </summary>
    private void RecordArchived(string folder)
    {
        var committed = Path.Combine(directory, Committed);
        var marker = (File.Exists(committed) ? File.ReadAllLines(committed) : []).ToList();
        if (marker.Count < 2) return;
        while (marker.Count < 5) marker.Add("");
        marker[4] = Path.GetFileName(folder);
        WriteAtomically(committed, marker);
        AfterRecord?.Invoke([.. marker]);
    }

    /// <summary>Test hook: runs after Promote recorded its archive folder in the marker, before the move.</summary>
    internal static Action<string[]>? AfterRecord { get; set; }

    /// <summary>The full path of an archive folder named in the marker; only a plain child name of the archive is accepted.</summary>
    private string? ArchivedFolder(string? name) =>
        name is { Length: > 0 } && name == Path.GetFileName(name) && name is not ("." or "..") ? Path.Combine(directory, ArchiveFolder, name) : null;

    /// <summary>Housekeeping of a committed operation: .previous of an older build and the trimming.</summary>
    /// <param name="saved">The copy this operation archived (from the marker when resumed); null — none to protect.</param>
    private void Tidy(Housekeeping housekeeping, string? saved)
    {
        try { MoveLegacy(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (housekeeping.Keep is { } limit) Trim(limit, housekeeping.Installed, saved);
    }

    /// <summary>.previous of an older build becomes an archived version: one rename, or dropped when the archive already holds a
    /// complete copy of that version. An incomplete one, or the folder this copy runs from, is left as it is.</summary>
    private void MoveLegacy()
    {
        var legacy = Path.Combine(directory, PreviousFolder);
        if (!Directory.Exists(legacy) || InUse(legacy) || ReadEntry(legacy) is not { Complete: true } entry) return;
        var target = Path.Combine(directory, ArchiveFolder, entry.Saved.Version.ToString());
        if (Directory.Exists(target))
        {
            if (ReadEntry(target) is { Complete: true }) { TryRetire(legacy, legacy + Retired + Guid.NewGuid().ToString("N")); return; }
            // An incomplete copy is removed through the same guard: a program running from it keeps it, .previous then waits.
            if (!TryRetire(target, Path.Combine(directory, ArchiveFolder, Retired + Guid.NewGuid().ToString("N")))) return;
        }
        // The source through the same guard (Codex re-check 2): a build starting from .previous meanwhile keeps it.
        TryMove(legacy, target);
    }

    /// <summary>Keeps the newest <paramref name="keep"/> complete versions besides the installed one; the copy this process runs
    /// from is never removed. A copy that cannot be renamed (a program running from it) stays for the next time.</summary>
    private void Trim(int keep, Version? installed, string? archivedNow = null)
    {
        var archive = Path.Combine(directory, ArchiveFolder);
        if (!Directory.Exists(archive)) return;
        List<(SavedVersion Saved, bool Complete)> entries;
        try { entries = Entries(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var archived = entries.Where(x => x.Complete && string.Equals(Path.GetDirectoryName(x.Saved.Folder), archive, Comparison)).ToList();
        // The copy the operation archived (named by Promote, or by the commit marker when resumed) is always among the kept ones
        // (Codex re-checks 2 and 3, 15.10 — a copy kept alongside must not go at once; never guessed by time). The rest in a fixed
        // order: version, then time saved.
        var newest = archivedNow;
        foreach (var (saved, _) in archived.Where(x => x.Saved.Version != installed)
                     .OrderByDescending(x => string.Equals(x.Saved.Folder, newest, Comparison))
                     .ThenByDescending(x => x.Saved.Version).ThenByDescending(x => x.Saved.Saved).Skip(keep))
        {
            // Another program running from it (Linux would let the folder be renamed and deleted from under it): next time.
            TryRetire(saved.Folder, Path.Combine(archive, Retired + Guid.NewGuid().ToString("N")));
        }
    }

    /// <summary>Test hook: runs right after unidentified files were moved into their folder.</summary>
    internal static Action<string>? AfterPark { get; set; }

    /// <summary>Test hook: runs between the check that no program uses a folder and its rename.</summary>
    internal static Action<string>? BeforeRetire { get; set; }

    /// <summary>
    /// Removes a saved copy unless a program uses it — the one way every path (trimming, replacing, .previous) removes a copy. The
    /// folder's .run.lock is taken exclusively: an older build started from the folder holds it shared. On Linux the lock is kept
    /// through the rename, so a build starting there meanwhile cannot register (Codex re-check 15.10: no gap between check and
    /// act); on Windows a handle inside the folder would block its rename, so the lock is released first, and a build started
    /// in between blocks the rename with its running image just the same. The copy is renamed before it is deleted: a deletion
    /// that fails half-way never leaves a broken copy under a version name.
    /// </summary>
    /// <returns>False when the copy is in use or could not be renamed: it stays for the next time.</returns>
    private bool TryRetire(string folder, string retired)
    {
        if (!TryMove(folder, retired)) return false;
        TryDelete(retired);
        return true;
    }

    /// <summary>Renames a saved copy unless a program uses it, with the guard described at TryRetire.</summary>
    private bool TryMove(string folder, string destination)
    {
        if (RunsFrom(folder)) return false;
        FileStream? hold = null;
        try
        {
            hold = new FileStream(Path.Combine(folder, RunLock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (OperatingSystem.IsWindows()) { hold.Dispose(); hold = null; }
            BeforeRetire?.Invoke(folder);
            Directory.Move(folder, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { hold?.Dispose(); }
        return true;
    }

    /// <param name="changed">Called as soon as a program file is changed back, before anything else can fail.</param>
    private void Restore(string staging, Action? changed = null)
    {
        foreach (var line in File.ReadAllLines(Path.Combine(staging, Manifest)).Where(x => x.Length > 1 && x[0] is '+' or '-'))
        {
            var file = line[1..];
            if (!IsProgramFile(file)) continue;
            var saved = Path.Combine(staging, file);
            var target = Path.Combine(directory, file);
            if (line[0] == '+')
            {
                // A missing saved copy means it was already restored; files not replaced yet are already original.
                if (!File.Exists(saved) || File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(saved))) continue;
                Put(saved, target, copy: true);
                changed?.Invoke();
            }
            else if (File.Exists(target) && !string.Equals(target, RunningExecutable, Comparison)) { File.Delete(target); changed?.Invoke(); }
        }
        Discard(staging);
    }

    /// <summary>Test hook: runs right before the new file takes the path of the running Windows executable (the old one is
    /// already renamed aside).</summary>
    internal static Action<string>? BeforeReplace { get; set; }

    /// <summary>
    /// Places a file in one rename: the program path always names a whole file (a copy goes to a temporary folder first). The
    /// running Windows executable is the exception. A single-file program keeps its file mapped, and Windows then refuses to
    /// remove or overwrite any name of it — a second hard link included (live check 07.11: --update failed «Нет доступа»
    /// since 0.3.0.8) — but lets it be renamed. So the running file is renamed aside (removed at the next start, see Cleanup)
    /// and the new one takes the path; a failure in between puts the old one back. Only an interruption between the two renames
    /// leaves the path empty: the old program is then in .previous.new and under the aside name. The helper (window, background
    /// update) runs from its own copy and replaces in one rename.
    /// </summary>
    private void Put(string from, string target, bool copy)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string? temporary = null;
        try
        {
            if (copy)
            {
                temporary = Path.Combine(directory, ".update-put-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                var copied = Path.Combine(temporary, Path.GetFileName(target));
                File.Copy(from, copied);
                from = copied;
            }
            if (OperatingSystem.IsWindows() && File.Exists(target) && string.Equals(target, RunningExecutable, Comparison))
            {
                var old = target + Retired + Guid.NewGuid().ToString("N");
                File.Move(target, old);
                try
                {
                    BeforeReplace?.Invoke(target);
                    File.Move(from, target, true);
                }
                catch
                {
                    // The original error is kept; if the old file cannot come back, Transact restores the saved copy.
                    try { File.Move(old, target); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    throw;
                }
            }
            else File.Move(from, target, true);
        }
        finally { if (temporary is not null) TryDelete(temporary); }
    }

    /// <summary>The files against the digests GitHub told for them (any source that has them), before SHA256SUMS.txt.</summary>
    private static void VerifyDigests(ReleaseInfo release, IEnumerable<string> assets, string work)
    {
        foreach (var name in assets)
        {
            if (release.AssetDigests?.GetValueOrDefault(name) is not { } expected) continue;
            using var stream = File.OpenRead(Path.Combine(work, name));
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Файл {name} не совпадает с контрольной суммой GitHub; файл повреждён или подменён.");
        }
    }

    private static void Verify(string asset, string sums)
    {
        var name = Path.GetFileName(asset);
        var expected = File.ReadLines(sums).Select(x => x.Split("  ", 2)).FirstOrDefault(x => x.Length == 2 && x[1].Trim() == name)?[0]
            ?? throw new InvalidDataException($"Для {name} нет контрольной суммы.");
        using var stream = File.OpenRead(asset);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Контрольная сумма {name} не совпадает; файл повреждён или подменён.");
    }

    /// <summary>Unpacks BaseNavigator/... entries only; absolute paths and ".." are refused.</summary>
    public static IReadOnlyList<string> Extract(string archive, string target)
    {
        const string prefix = "BaseNavigator/";
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        string Destination(string name)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Неожиданная структура архива выпуска: " + name);
            var relative = name[prefix.Length..];
            var path = Path.GetFullPath(Path.Combine(target, relative));
            if (relative.Length == 0 || !path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Недопустимый путь в архиве: " + name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            files.Add(Path.GetRelativePath(target, path));
            return path;
        }
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries.Where(x => !x.FullName.EndsWith('/')))
                entry.ExtractToFile(Destination(entry.FullName), false);
        }
        else
        {
            using var stream = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            using var reader = new TarReader(stream);
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.Directory) continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("Архив выпуска содержит недопустимую запись: " + entry.Name);
                entry.ExtractToFile(Destination(entry.Name), false);
            }
        }
        return files;
    }

    public static string? ProbeVersion(string candidate)
    {
        var start = new ProcessStartInfo(candidate) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--version");
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            // A child writing much to stderr must not block on a full pipe (Grok audit 15.10).
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30))) { process.Kill(true); return null; }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
