using System.Globalization;
using System.Text;

namespace StartManager.Core;

/// <summary>Which commands of «Свойства ▾» for backups apply to a base: a file or a server base (the configurator connects to
/// those); a base on a web server, a group or a missing base have none.</summary>
public static class BackupCommands
{
    public static bool Applies(BaseEntry? entry) => entry is { Kind: ConnectionKind.File or ConnectionKind.Server };
}

/// <summary>A group of «Список выгрузок»: the records of one format, the newest first.</summary>
public sealed record BackupListGroup(BackupFormat Format, string Title, IReadOnlyList<BackupRecord> Records)
{
    /// <summary>The group as the original's tree shows it: the title and «  [n]» (vstBackupListGetText, _Unit206.pas:3190).</summary>
    public string Text => Title + Count;

    /// <summary>The «  [n]» part of <see cref="Text"/>: the list shows it apart, so a narrow window cuts the title, not the count.</summary>
    public string Count => $"  [{Records.Count}]";
}

/// <summary>The original's «Список выгрузок» (FormBackupList, _Unit206.pas:2052-3270) over the journal of backups.</summary>
public static class BackupList
{
    /// <summary>The original's text with its typo «иформационной» corrected.</summary>
    public const string Empty = "Выгрузки информационной базы не создавались!";
    public const string NotFound = "Файл не найден!";
    public const string DeleteQuestion = "Удалить файл архива?";

    /// <summary>The group titles of the original's TreeUpdate (_Unit206.pas:2551-2629); there is no RAR group here.</summary>
    public static string Title(BackupFormat format) => format switch
    {
        BackupFormat.Dt => "Выгрузка данных информационной базы",
        BackupFormat.Cf => "Выгрузка конфигурации информационной базы",
        BackupFormat.Zip => "ZIP-архивы данных информационной базы",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>The groups of a base in the original's order (DT, CF, ZIP); a format without records has no group, so a base
    /// without backups has an empty tree (the original then says <see cref="Empty"/>).</summary>
    public static IReadOnlyList<BackupListGroup> Groups(BackupJournal journal, Guid baseId)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var records = journal.Of(baseId).ToList();
        return [.. Enum.GetValues<BackupFormat>().Select(format => new BackupListGroup(format, Title(format), [.. records.Where(x => x.Format == format)]))
            .Where(x => x.Records.Count > 0)];
    }

    /// <summary>A record as the original shows it: its time «dd.mm.yy hh:nn:ss» (24-hour clock) in the local time.</summary>
    public static string Text(BackupRecord record) => record.Time.ToLocalTime().ToString("dd.MM.yy HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The file of a record the list shows and loads: the first one it names (the main folder; the first volume of a split
    /// archive).</summary>
    public static string MainFile(BackupRecord record) => record.Files.Count > 0 ? record.Files[0] : "";

    /// <summary>The file to load: the main file, or, when it is gone, a copy of the same name in a folder 2–3 that is there; null when
    /// none is (the original: «Файл не найден!»). A split ZIP archive is taken from the first folder (main, then 2, then 3) that holds
    /// its whole set of volumes, read from the disk as the load reads it — from «.001» on without a gap, exactly as many as the record
    /// lists for that folder, no stray volume after them (even after a gap) — so a folder that lost or gained a volume does not hide a
    /// complete copy (Codex audit and recheck, part 4); the file is then the set's first volume. <paramref name="exists"/> is for the
    /// single files only; <paramref name="volumes"/> lists the volumes of a set in a folder (<see cref="BackupRun.Volumes"/> by default).
    /// A folder that cannot be read is taken as incomplete, and the next one is tried (Codex second recheck, part 4).</summary>
    public static string? Source(BackupRecord record, Func<string, bool>? exists = null, Func<string, string, List<string>>? volumes = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        exists ??= File.Exists;
        var main = MainFile(record);
        if (main.Length == 0) return null;
        var name = Path.GetFileName(main);
        if (record.Format == BackupFormat.Zip && BackupRun.VolumeSet(name) is { } set) return VolumeSource(record, set, volumes ?? BackupRun.Volumes);
        if (exists(main)) return main;
        return record.Files.Skip(1).FirstOrDefault(x => string.Equals(Path.GetFileName(x), name, BackupRun.PathComparison) && exists(x));
    }

    // The first folder of the record whose set the load would accept (BackupRestore.Plan: the same enumeration and the same rule) and
    // whose volumes are as many as the record has there.
    private static string? VolumeSource(BackupRecord record, string set, Func<string, string, List<string>> volumes)
    {
        var folders = record.Files.Where(x => BackupRun.VolumeSet(Path.GetFileName(x)) is { } own && own.Equals(set, BackupRun.PathComparison))
            .GroupBy(x => Path.GetDirectoryName(x) ?? "", BackupRun.PathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Path.IsPathFullyQualified(folder.Key)) continue;
            List<string> found;
            try { found = volumes(folder.Key, set); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            if (found.Count == folder.Count() && BackupRestore.VolumeGap(found, folder.Key, set) is null) return found[0];
        }
        return null;
    }

    /// <summary>The original's question before a load from the list (TsmMainForm, _Unit207.pas:41138-41150).</summary>
    public static string Confirmation(string baseName, string file) =>
        $"Текущие данные информационной базы \"{baseName}\"\nбудут заменены данными из архива!\nФайл источник: {file}\nПродолжать?";
}

/// <summary>The state of the busy check as a window shows it.</summary>
/// <param name="Allowed">Whether «Выполнить» / «Выполнить загрузку» may be pressed.</param>
/// <param name="Alarm">The text is a warning (red).</param>
public sealed record BackupBusyView(string Text, bool Allowed, bool Alarm);

/// <summary>The logic of the original's «Выполнение сценария» (FormScriptExecute, _Unit206.pas:5765-7330) and «Загрузка данных»
/// (FormLoad, _Unit206.pas:16362-16700) that does not need a window.</summary>
public static class BackupForms
{
    public const string ServerNotChecked = "Занятость серверной информационной базы не проверяется.";
    public const string LoadWarning = "Текущие данные информационной базы будут заменены данными из файла!";
    public const string LockSet = "Блокировка информационной базы уже установлена!";
    /// <summary>The free base after «Установить блокировку ИБ» of this window: the lock is told, the run may start.</summary>
    public const string FreeLockedForDump = "Информационная база свободна и заблокирована для выгрузки: можно начать выполнение выгрузки";
    public const string LockChanged = "Файл блокировки 1Cv8.cdn изменился во время установки блокировки. Блокировка не установлена, повторите попытку.";
    /// <summary>A lock file that is not in force (switched off or expired) is someone else's record too: it is not replaced.</summary>
    public const string LockForeign = "В каталоге базы уже есть файл блокировки 1Cv8.cdn (блокировка выключена или истекла). Блокировка не установлена: " +
        "измените её на странице «Блокировка» свойств базы или удалите командой «Разблокировать информационную базу».";
    public const string EmptyUserQuestion = "Вы не указали имя пользователя!\nВы уверены что хотите продолжить?";
    /// <summary>The text of the lock the original's «Установить блокировку ИБ» writes (butSetBlockClick, _Unit206.pas:6090-6105),
    /// framed by the lines of «=» (<see cref="InfobaseLock.Framed"/>).</summary>
    public const string LockMessage = "Выполняется выгрузка данных!\r\nДождитесь завершения операции...";
    /// <summary>The original's lock lasts 60 minutes.</summary>
    public static readonly TimeSpan LockLength = TimeSpan.FromMinutes(60);

    /// <summary>The item of «Сценарий:» chosen when the window opens: the base's own scenario when it exists, else
    /// «&lt;Использовать сценарий по умолчанию&gt;» (null).</summary>
    public static Guid? InitialChoice(BackupScenarioSet set, BaseEntry entry) =>
        entry.BackupScriptId is { } own && set.Scenarios.Any(x => x.Id == own) ? own : null;

    /// <summary>The scenario of an item of «Сценарий:»: null is the default scenario.</summary>
    public static BackupScenario? Scenario(BackupScenarioSet set, Guid? choice) => set.For(choice);

    /// <summary>The values of the name's macros for a base at a time.</summary>
    public static BackupMacroValues Values(BaseEntry entry, DateTime time, string? computer = null) =>
        new(entry.Name, entry.BackupPrefix, computer ?? Environment.MachineName, time);

    /// <summary>«Имя файла:» made by the scenario's template; empty when the template gives no name (the run then refuses with
    /// the reason).</summary>
    public static string FileName(BackupScenario scenario, BackupMacroValues values)
    {
        try { return BackupName.FileName(scenario.Template, values, scenario.Format); }
        catch (ArgumentException) { return ""; }
    }

    public const string NoFileName = "Не указано имя файла!";

    /// <summary>The file name of the request: empty (made by the scenario's template) when « Имя файла:» is unchecked, the typed name
    /// when it is checked; null when it is checked and blank — the run is refused then, since the window would show no name while the
    /// file got the template's (Grok audit, part 4).</summary>
    public static string? RequestName(bool own, string? text) => !own ? "" : text?.Trim() is { Length: > 0 } name ? name : null;

    /// <summary>The user and the password the fields start with: the designer login of the base's «Аутентификация» and its saved
    /// password; nothing for OS authentication.</summary>
    public static (string User, string Password) Prefill(BaseEntry entry, Func<PasswordRole, string?> password)
    {
        var login = BackupCredentials.FromBase(entry, password);
        return login.OsAuthentication ? ("", "") : (login.User, login.Password);
    }

    /// <summary>Whether the base logs in by the OS account (/WA+): an empty «Пользователь:» then means that, not «nobody».</summary>
    public static bool OsAuthentication(BaseEntry entry) => entry.Authentication?.OsAuthentication == true;

    /// <summary>The login from the fields: the typed user with the password; with no user, the OS account when the base uses it,
    /// else nobody (/WA- when the list asks for the name and password).</summary>
    public static BackupCredentials Credentials(BaseEntry entry, string user, string password)
    {
        user = user.Trim();
        if (user.Length > 0) return new(user, password, AskCredentials: entry.AskCredentials);
        return OsAuthentication(entry) ? new(OsAuthentication: true) : new(AskCredentials: entry.AskCredentials);
    }

    /// <summary>The original's FormLoad asks before a load without a user; not when the base logs in by the OS account.</summary>
    public static bool AsksForUser(BaseEntry entry, string user) => user.Trim().Length == 0 && !OsAuthentication(entry);

    /// <summary>The status line of «Выполнение сценария» (VerifyPossibilityUnloading): a busy file base disables «Выполнить».</summary>
    /// <param name="ownLock">The window set its lock (1Cv8.cdn): a free base is told as locked for the dump (deferred note of 20.10).</param>
    public static BackupBusyView DumpState(BaseEntry entry, BaseBusy busy, bool ownLock = false)
    {
        if (entry.Kind != ConnectionKind.File) return new(ServerNotChecked, true, false);
        return busy.State switch
        {
            BaseBusyState.Free => new(ownLock ? FreeLockedForDump : BaseBusyCheck.FreeForDump, true, false),
            BaseBusyState.Busy => new(BaseBusyCheck.BusyForDump + Detail(busy), false, true),
            _ => new("Не удалось проверить, открыта ли информационная база." + Detail(busy), true, false)
        };
    }

    /// <summary>The warning of «Загрузка данных» (CheckPossibility): a busy file base disables «Выполнить загрузку»; a base that could
    /// not be checked is asked about when the load starts (<see cref="RestoreResult.Unchecked"/>).</summary>
    public static BackupBusyView LoadState(BaseEntry entry, BaseBusy busy) =>
        entry.Kind == ConnectionKind.File && busy.State == BaseBusyState.Busy ? new(BaseBusyCheck.BusyForLoad, false, true) : new(LoadWarning, true, true);

    private static string Detail(BaseBusy busy) => busy.Detail.Length > 0 ? " " + busy.Detail : "";

    /// <summary>The original's «Установить блокировку ИБ»: 1Cv8.cdn for 60 minutes with its text. Unlike the original (no access code,
    /// so 1C refused the session of the dump itself), the lock gets a random access code: the runs pass it as /UC
    /// (<see cref="BackupLock.UnlockCode"/>) and the window removes the lock after the run (<see cref="BackupLock.Release"/>).
    /// The lock is written only where there is no lock file at all, and the file is created exclusively (Windows: CREATE_NEW; Unix:
    /// under a private name, then link(2) to 1Cv8.cdn — see <see cref="NewLockFile"/>), so a lock another program writes at the same
    /// moment is never overwritten. Any record already there — in force, still to begin,
    /// switched off, expired or damaged — is someone else's: it is left as it is and the window says how to change it (Codex
    /// audit of the lock fix, 19.10).</summary>
    /// <returns>Whether the lock was written, the text for the window and, when written, what to remove after the run.</returns>
    /// <summary>Tests: runs on the new lock file right before it is written.</summary>
    internal static Action<FileStream>? Writing;

    public static (bool Set, string Message, BackupLockGrant? Grant) SetLock(string folder, DateTime now)
    {
        var path = InfobaseLock.PathIn(folder);
        try
        {
            if (File.Exists(path))
            {
                InfobaseLock present;
                try { present = InfobaseLock.Parse(File.ReadAllText(path, Encoding.UTF8)); }
                catch (InvalidDataException ex) { return (false, ex.Message + " Блокировка не установлена.", null); }
                return (false, present.On && (present.End is null || present.End > now) ? LockSet : LockForeign, null);
            }
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("Каталог информационной базы не найден: " + folder.Trim());
            var end = now + LockLength;
            var written = new InfobaseLock(true, now, end, LockMessage, BackupLock.NewCode());
            var text = written.Format();
            // Another program made the file after the check: theirs stays.
            if (NewLockFile.Create(path) is not { } created) return (false, LockChanged, null);
            // The file is ours from here: a write that fails removes that very file (Windows: through the handle; Unix: its private
            // name, 1Cv8.cdn is never touched) and tells why; a file that could not be removed is named (Grok audit, Codex re-check 5, 6).
            byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
            string? alias;
            using (created)
            {
                try
                {
                    Writing?.Invoke(created.Stream);
                    created.Stream.Write(bytes);
                    created.Stream.Flush(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var kept = created.Discard();
                    return (false, "Блокировка не установлена: " + ex.Message + (kept is null ? "" : " " + kept), null);
                }
                // Unix: only the written file becomes 1Cv8.cdn (link(2), never over a file another program made meanwhile; on a failed
                // link, 1Cv8.cdn with exactly these bytes is ours — NFS, Codex re-check 7).
                var (published, told) = created.Publish(bytes);
                if (published == LockPublished.Taken) return (false, LockChanged + (told is null ? "" : " " + told), null);
                if (published == LockPublished.Failed) return (false, "Блокировка не установлена: " + told, null);
                alias = told;
            }
            return (true, $"Блокировка информационной базы установлена до {end:dd.MM.yyyy HH:mm:ss}. Она будет снята после выполнения сценария или при закрытии окна." +
                (alias is null ? "" : " " + alias), new BackupLockGrant(folder, written.Code, text, alias is null ? null : created.Hidden));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (false, "Блокировка не установлена: " + ex.Message, null); }
    }

    /// <summary>The question before files of the backup are replaced.</summary>
    public static string ReplaceQuestion(IReadOnlyList<string> existing) =>
        "Файл резервной копии уже существует:\n" + string.Join("\n", existing.Take(5)) + (existing.Count > 5 ? $"\n… и ещё {existing.Count - 5}" : "") + "\nЗаменить?";

    /// <summary>The result of a backup for the message: the message, then the warnings (copies to the folders 2–3, the journal, the
    /// rotation and the files it kept) and the log kept after a failure.</summary>
    public static string Report(BackupResult result)
    {
        var text = new StringBuilder(result.Message);
        if (result.Warnings.Count > 0)
        {
            text.Append("\n\nПредупреждения:");
            foreach (var warning in result.Warnings.Where(x => !result.Message.Contains(x, StringComparison.Ordinal))) text.Append("\n• ").Append(warning);
        }
        if (result.LogPath is { } log) text.Append("\n\nЖурнал 1С сохранён: ").Append(log);
        return text.ToString();
    }
}

/// <summary>The steps of «Выполнить» after the window: the plan, «replace?», the run (in the window of progress) and the question
/// again when the files appeared during the run. The questions and the run are given by the caller.</summary>
public static class BackupFlow
{
    /// <param name="planner">Makes the plan; by default <see cref="BackupRun.Plan"/> on a pool thread, since it checks the folders (a slow
    /// network folder would freeze the window; Codex audit, part 4). The questions and the run go on in the caller's context.</param>
    /// <returns>The result; null when the user declined to replace the files.</returns>
    public static async Task<BackupResult?> Run(BackupRequest request, Func<string, Task<bool>> ask, Func<BackupPlan, Task<BackupResult>> run,
        Func<BackupRequest, Task<BackupPlan>>? planner = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var makePlan = planner ?? (x => Task.Run(() => BackupRun.Plan(x)));
        BackupPlan plan;
        try { plan = await makePlan(request); }
        catch (ArgumentException ex) { return new(false, ex.Message); }
        if (plan.Existing.Count > 0 && !request.Overwrite)
        {
            if (!await ask(BackupForms.ReplaceQuestion(plan.Existing))) return null;
            try { plan = await makePlan(request with { Overwrite = true }); }
            catch (ArgumentException ex) { return new(false, ex.Message); }
        }
        var result = await run(plan);
        // Files of the same name made by another window while this one waited: asked again, once.
        if (result.Exists && !plan.Request.Overwrite)
        {
            BackupPlan again;
            try { again = await makePlan(plan.Request with { Overwrite = true }); }
            catch (ArgumentException ex) { return new(false, ex.Message); }
            if (!await ask(BackupForms.ReplaceQuestion(again.Existing.Count > 0 ? again.Existing : [again.Target]))) return null;
            result = await run(again);
        }
        return result;
    }
}

/// <summary>The steps of a load, shared by «Список выгрузок» and «Загрузка данных»: the plan, the original's question (from the list),
/// the warning of a server base (no rollback), the run, and the question when the file base could not be checked for sessions.</summary>
public static class RestoreFlow
{
    /// <param name="confirmation">The question before the load (the list's <see cref="BackupList.Confirmation"/>); null: none (the
    /// window «Загрузка данных» warned already).</param>
    /// <param name="planner">Makes the plan; by default <see cref="BackupRestore.Plan"/> on a pool thread, since it checks the files and
    /// lists the folder of a split archive (Codex audit, part 4). The questions and the run go on in the caller's context.</param>
    /// <returns>The result; null when the user said no.</returns>
    public static async Task<RestoreResult?> Run(RestoreRequest request, string? confirmation, Func<string, Task<bool>> ask, Func<RestorePlan, Task<RestoreResult>> run,
        Func<RestoreRequest, Task<RestorePlan>>? planner = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var makePlan = planner ?? (x => Task.Run(() => BackupRestore.Plan(x)));
        RestorePlan plan;
        try { plan = await makePlan(request); }
        catch (ArgumentException ex) { return new(false, ex.Message); }
        if (confirmation is not null && !await ask(confirmation)) return null;
        if (!plan.Recoverable && !await ask(BackupRestore.ServerNotice + "\n\nПродолжать?")) return null;
        var result = await run(plan);
        if (result.Unchecked && !request.ProceedWithoutBusyCheck)
        {
            if (!await ask(result.Message)) return null;
            try { plan = await makePlan(request with { ProceedWithoutBusyCheck = true }); }
            catch (ArgumentException ex) { return new(false, ex.Message); }
            result = await run(plan);
        }
        return result;
    }
}
