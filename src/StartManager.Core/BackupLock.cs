using System.Security.Cryptography;

namespace StartManager.Core;

/// <summary>The lock «Установить блокировку ИБ» of «Выполнение сценария» set: the folder and the exact text written. Only this text is
/// removed after the run (<see cref="BackupLock.Release"/>); a lock changed meanwhile by the page «Блокировка» or another program stays.
/// The access code never leaves the program's memory: it is in the lock file and on the command line of 1C only.</summary>
public sealed class BackupLockGrant
{
    internal BackupLockGrant(string folder, string code, string written, string? alias = null)
    { Folder = folder; Code = code; Written = written; Alias = alias; }
    public string Folder { get; }
    internal string Code { get; }
    internal string Written { get; }
    /// <summary>Unix: the private name the lock was written under, left as a second name of 1Cv8.cdn after the link (it could not be
    /// removed); <see cref="BackupLock.Release"/> removes it too (Grok audit round 2, 20.10).</summary>
    internal string? Alias { get; }
    public override string ToString() => "Блокировка " + InfobaseLock.PathIn(Folder);
}

/// <summary>The /Out log of one start of 1C: a file in a private temporary folder of this user (made with the owner's rights only on
/// Unix), read once and removed. A log that cannot be removed is told, as it may hold the password or the access code.</summary>
internal sealed class RawLog : IDisposable
{
    private readonly string folder;
    private bool removed;
    private RawLog(string folder) { this.folder = folder; Path = System.IO.Path.Combine(folder, "out.log"); }
    public string Path { get; }
    /// <summary>Tests: runs right before the log is removed.</summary>
    internal static Action<string>? BeforeRemove;

    public static RawLog Create() => new(PrivateFolder());

    /// <summary>A new folder «bn-1c-…» in the temporary folder of this user for the logs of 1C; also used by the creation of a base.</summary>
    internal static string PrivateFolder()
    {
        var folder = Directory.CreateTempSubdirectory("bn-1c-").FullName;
        // Unix: owner only (0700) right away, whatever the umask: nobody else may look into the folder while 1C writes there (Grok audit).
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    public string Read() => BackupRun.ReadLog(Path);

    /// <summary>Removes the log and its folder; the text for the user when the log stays.</summary>
    public string? Remove()
    {
        removed = true;
        BeforeRemove?.Invoke(Path);
        try
        {
            if (File.Exists(Path)) { File.SetAttributes(Path, FileAttributes.Normal); File.Delete(Path); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"Временный журнал 1С не удалён ({ex.Message}): {Path}. Удалите его вручную — в нём может быть пароль или код доступа."; }
        try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // Something else 1C wrote beside the log stays with the folder: told too (Grok audit).
        return Directory.Exists(folder)
            ? $"Временная папка журнала 1С не удалена: {folder}. Удалите её вручную — в ней могут быть данные запуска 1С."
            : null;
    }

    /// <summary>Only the fallback when an exception left the run before its own <see cref="Remove"/> (which gives the text to the
    /// result): there is no result to carry the text, so a folder that stays is reported to the program's log by a code only.</summary>
    public void Dispose()
    {
        if (!removed && Remove() is not null) BackupDiagnostics.RawLogKept?.Invoke(folder);
    }
}

/// <summary>The program's log for the core of backups, set by the application.</summary>
public static class BackupDiagnostics
{
    /// <summary>A temporary 1C log folder that stays after an exception: its full path (the log writes only its checked name,
    /// <see cref="DiagnosticLog.RawLogKept"/>; Grok audit round 2).</summary>
    public static Action<string>? RawLogKept { get; set; }
}

/// <summary>Why a lock of the backup window stays: another lock is there (Changed), a foreign file keeps a private name (Aside), the file
/// cannot be removed or moved (Undeletable).</summary>
public enum LockKeptReason { Changed, Aside, Undeletable }

/// <summary>The result of <see cref="BackupLock.Release"/>: removed (or already gone), or kept with the text for the window.</summary>
/// <param name="Reason">Why it stays (for the program's log).</param>
/// <param name="KeptFile">The private name of a file kept aside (no folder): made by the program, so the log may hold it.</param>
public sealed record BackupLockRelease(bool Removed, string? Message, LockKeptReason Reason = LockKeptReason.Changed, string? KeptFile = null)
{
    public static readonly BackupLockRelease Gone = new(true, null);
    /// <summary>1Cv8.cdn itself is removed, only its second name stays (<see cref="Removed"/> is false then): the base is not locked
    /// any more, so the window lets a new lock be set and the main window does not say «не снята» (deferred note of 20.10).</summary>
    public bool LockGone { get; init; }
}

/// <summary>The lock of a file base during a backup or a load (decision of the user 19.10.2026, stage 12). The original wrote the lock
/// of «Установить блокировку ИБ» without an access code and did not pass /UC, so 1C refused the session of its own dump
/// («Начало сеанса с информационной базой запрещено»). Now that lock gets a random access code, every call of 1C on a locked base
/// passes /UC with the code of the lock in force, and the lock the run set is removed after it.</summary>
public static class BackupLock
{
    internal const int CodeLength = 16;
    // Letters and digits without the look-alikes: safe in the quoted field of 1Cv8.cdn and on any command line.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    internal static string NewCode() => RandomNumberGenerator.GetString(Alphabet, CodeLength);

    /// <summary>The access code for /UC: that of the lock of the file base when it is switched on and has one — our lock of the run or
    /// one set on the page «Блокировка» with a code (that one is used, never changed or removed). Null for a server base, without a
    /// lock, for a lock without a code (1C refuses the session then, as it would for anyone) and for an unreadable file.</summary>
    public static string? UnlockCode(BaseEntry entry)
    {
        if (entry.Kind != ConnectionKind.File || string.IsNullOrWhiteSpace(entry.Location)) return null;
        try
        {
            return InfobaseLock.Read(entry.Location) is { On: true } found && found.Code.Length > 0 && !found.Code.Any(char.IsControl)
                ? found.Code : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return null; }
    }

    /// <summary>Removes the lock the run set, only when the file is still exactly what was written. The file is first renamed to a
    /// private name in the same folder (one atomic step), the renamed file is compared, and only ours is deleted: a lock another
    /// program wrote in the meantime is never deleted. A foreign file taken aside is renamed back when no new 1Cv8.cdn appeared; else
    /// it stays under the private name and <see cref="BackupLockRelease.Message"/> names it (Codex audit of the lock fix, 19.10).</summary>
    public static BackupLockRelease Release(BackupLockGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var released = Take(grant);
        return grant.Alias is { } alias ? WithAlias(released, alias, grant.Written) : released;
    }

    // Unix: a second name of our lock left after its publication holds the access code (Grok audit round 2, 20.10). As the lock
    // itself: renamed to a fresh private name first (one atomic step, so a file put at the old name meanwhile is never touched),
    // compared, and only ours deleted; anything else goes back without replacing (PutBack). A name that stays is told, even when
    // 1Cv8.cdn is gone (Codex re-check 10).
    private static BackupLockRelease WithAlias(BackupLockRelease released, string alias, string written)
    {
        var taken = Path.Combine(Path.GetDirectoryName(alias)!, NewPrivateName());
        try { File.Move(alias, taken); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return released; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return AliasStays(released, alias); }
        string? text;
        try { text = File.ReadAllText(taken, System.Text.Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = null; }
        if (text == written)
        {
            try { Unlinking?.Invoke(taken); File.Delete(taken); return released; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Not ours, unreadable, or ours but undeletable: back to its name. A foreign file put back is not ours to tell.
        var back = PutBack(taken, alias);
        if (text is not null && text != written && back == PutBackResult.Restored) return released;
        return AliasStays(released, back == PutBackResult.Restored ? alias : taken);
    }

    private static BackupLockRelease AliasStays(BackupLockRelease released, string alias)
    {
        var told = $"второе имя файла блокировки {alias} не удалено: удалите его вручную — в нём код доступа.";
        return released.Removed
            ? new(false, "Блокировка снята, но " + told, LockKeptReason.Undeletable, Path.GetFileName(alias)) { LockGone = true }
            : released with { Message = released.Message + " Кроме того, " + told, KeptFile = released.KeptFile ?? Path.GetFileName(alias) };
    }

    private static BackupLockRelease Take(BackupLockGrant grant)
    {
        var path = InfobaseLock.PathIn(grant.Folder);
        var aside = Path.Combine(Path.GetDirectoryName(path)!, NewPrivateName());
        try { File.Move(path, aside); }
        catch (FileNotFoundException) { return BackupLockRelease.Gone; }
        catch (DirectoryNotFoundException) { return BackupLockRelease.Gone; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, Kept + " " + ex.Message, LockKeptReason.Undeletable); }
        Taken?.Invoke(aside);
        string? text;
        try { text = File.ReadAllText(aside, System.Text.Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = null; }
        if (text == grant.Written)
        {
            try { File.Delete(aside); return BackupLockRelease.Gone; }
            // Ours, but it cannot be deleted: back in its place, so the base stays locked as the user sees it.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Back(aside, path, new(false, Kept + " " + ex.Message, LockKeptReason.Undeletable)); }
        }
        return Back(aside, path, new(false, Changed, LockKeptReason.Changed));
    }

    // The file put back: the result as it is; a private name that stayed beside it (Unix, after the link) is named too (Codex re-check 5).
    private static BackupLockRelease Back(string aside, string path, BackupLockRelease restored) => PutBack(aside, path) switch
    {
        PutBackResult.Restored => restored,
        PutBackResult.AliasKept => restored with { Message = restored.Message + " " + AliasKept(aside), KeptFile = Path.GetFileName(aside) },
        _ => AsideResult(aside),
    };

    private static string AliasKept(string aside) =>
        $"Блокировка возвращена на место, но второе имя того же файла {aside} не удалено: удалите его вручную — в нём код доступа.";

    /// <summary>Tests: runs right after the lock file was renamed aside.</summary>
    internal static Action<string>? Taken;

    private const string AsidePrefix = ".bn-lock-";
    private const string AsideExtension = ".cdn";

    /// <summary>The name of a lock file: 1Cv8.cdn, or the private name a lock taken aside by <see cref="Release"/> keeps. Such files hold
    /// an access code: they never go into an archive and are never unpacked from one.</summary>
    public static bool IsLockName(string name) =>
        name.Equals(InfobaseLock.FileName, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(AsidePrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(AsideExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>A fresh private name for a lock file: a lock taken aside by <see cref="Release"/>, or a new one written before it is
    /// published (<see cref="NewLockFile"/>, Unix).</summary>
    internal static string NewPrivateName() => AsidePrefix + Guid.NewGuid().ToString("N") + AsideExtension;

    /// <summary>The exact name <see cref="Release"/> gives a file it takes aside: «.bn-lock-» and 32 hexadecimal digits, «.cdn».</summary>
    public static bool IsPrivateName(string name) =>
        name.Length == AsidePrefix.Length + 32 + AsideExtension.Length && name.StartsWith(AsidePrefix, StringComparison.Ordinal) &&
        name.EndsWith(AsideExtension, StringComparison.Ordinal) && name.AsSpan(AsidePrefix.Length, 32).ToString().All(char.IsAsciiHexDigitLower);

    /// <summary>The text for the main window when a lock was not removed and nobody saw the window's message (it was closed): the base,
    /// its folder and what to do. The access code is never in it (the message has none). The program's log gets only
    /// <see cref="DiagnosticLog.LockKept"/>.</summary>
    public static string Diagnostic(BaseEntry entry, BackupLockRelease release) => release.LockGone
        ? $"База «{entry.Name}» ({entry.Location}) после резервного копирования: {release.Message}"
        : $"Блокировка базы «{entry.Name}» ({entry.Location}) после резервного копирования не снята. {release.Message}";

    /// <summary>A release that left something (<see cref="BackupLockRelease.Removed"/> false), whatever the window does next: logged as
    /// «backup.lock.kept» with its typed fields and the text for the main window's status line returned; null when all is removed. The
    /// backup window closes itself after a successful run, so this is done when the release returns, not when the window closes (Codex
    /// re-check 10, 20.10).</summary>
    public static string? Record(BaseEntry entry, BackupLockRelease release, Action<Guid, LockKeptReason, string?> log)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(release); ArgumentNullException.ThrowIfNull(log);
        if (release.Removed) return null;
        log(entry.Id, release.Reason, release.KeptFile);
        return Diagnostic(entry, release);
    }

    private const string Kept = "Блокировка не снята: файл 1Cv8.cdn не удаётся удалить.";
    private const string Changed = "Файл блокировки 1Cv8.cdn изменён другой программой: блокировка оставлена как есть.";
    private static BackupLockRelease AsideResult(string aside) => new(false, Aside(aside), LockKeptReason.Aside, Path.GetFileName(aside));

    private static string Aside(string aside) =>
        $"Файл блокировки 1Cv8.cdn изменён другой программой во время снятия блокировки и не возвращён на место. Он сохранён как {aside}: " +
        "проверьте его и при необходимости переименуйте обратно в 1Cv8.cdn или удалите.";

    /// <summary>The file taken aside back to 1Cv8.cdn, never over a file that appeared there meanwhile. Windows: a move without
    /// replacing (MoveFileEx without the flag fails when the name exists). Unix: rename(2) would replace such a file, so a hard link is
    /// made (link(2) fails with EEXIST) and the private name removed only after it; a file system without hard links leaves the file
    /// under its private name (Grok audit, 19.10). On NFS link(2) may fail although the server made the link: 1Cv8.cdn then holding
    /// exactly the bytes of the file aside is that file put back (Grok audit round 2, 20.10); 1Cv8.cdn is never removed here.</summary>
    internal static PutBackResult PutBack(string aside, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try { File.Move(aside, path, false); return PutBackResult.Restored; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return PutBackResult.Failed; }
        }
        try { if (Link(aside, path) != 0 && !SameBytes(aside, path)) return PutBackResult.Failed; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return PutBackResult.Failed; }
        // Both names are the same file now; the lock is in place. A private name that cannot be removed stays as a second name: told.
        try { Unlinking?.Invoke(aside); File.Delete(aside); return PutBackResult.Restored; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return PutBackResult.AliasKept; }
    }

    /// <summary>Tests: runs right before the private name is removed after the link (Unix).</summary>
    internal static Action<string>? Unlinking;

    internal enum PutBackResult { Restored, Failed, AliasKept }

    private static bool SameBytes(string one, string other)
    {
        try { return File.ReadAllBytes(one).AsSpan().SequenceEqual(File.ReadAllBytes(other)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>link(2): 0, or the error number (Unix).</summary>
    internal static int Link(string from, string to) =>
        Linking is { } fake ? fake(from, to) : link(from, to) == 0 ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

    /// <summary>Tests: replaces link(2) (Unix); returns the error number, 0 when linked.</summary>
    internal static Func<string, string, int>? Linking;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    internal static extern int link(string oldPath, string newPath);

    /// <summary>The arguments for the start of 1C: /UC with the code before the command (at <paramref name="commandAt"/>, after the
    /// login), when the lock of the base has one at this moment.</summary>
    internal static List<string> Arguments(IReadOnlyList<string> planned, int commandAt, string? code)
    {
        var result = planned.ToList();
        if (code is not null) result.InsertRange(Math.Clamp(commandAt, 0, result.Count), ["/UC", code]);
        return result;
    }

    /// <summary>The arguments as they may be shown: the values after /P and /UC hidden.</summary>
    internal static IEnumerable<string> Shown(IReadOnlyList<string> arguments) =>
        arguments.Select((x, i) => i > 0 && (arguments[i - 1] == "/P" || arguments[i - 1] == "/UC") ? "***" : x);

    /// <summary>A text 1C wrote with the access code hidden.</summary>
    internal static string Mask(string text, string? code) => code is { Length: > 0 } && text.Length > 0 ? text.Replace(code, "***", StringComparison.Ordinal) : text;
}
