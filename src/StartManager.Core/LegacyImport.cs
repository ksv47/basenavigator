using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace StartManager.Core;

/// <summary>An I/O failure whose message is already written for the user: <see cref="LegacyImport.ErrorMessage"/> keeps it
/// instead of the general sentence for its code. The code (Win32 HRESULT or Unix errno) stays for diagnostics.</summary>
public sealed class ExplainedIOException(string message, int code) : IOException(message, code);

/// <param name="Backup">The page «Резервное копирование» of the bases, the backup scenarios (.bks) and «Каталог по-умолчанию».</param>
public sealed record LegacyImportOptions(bool Clients = true, bool Favorites = true, bool Appearance = false, bool Authentication = true, bool Properties = true,
    bool Backup = true);
/// <summary>The backup scenarios an import depends on: the whole set as it will be, for the store it was read from. The store is
/// checked before the bases are saved whenever an imported base refers to its scenarios; <paramref name="Write"/> — the set is
/// written after them (scenarios added or the default changed).</summary>
public sealed record LegacyBackupImport(BackupScenarioStore Store, BackupScenarioSet Result, bool Write);
public sealed record LegacyImportPreview(Library Result, IReadOnlyList<BaseEntry> Added,
    int Duplicates, IReadOnlyList<Guid> Favorites, IReadOnlyList<string> Notices,
    IReadOnlyDictionary<string, string?> SourceHashes)
{
    /// <summary>Passwords of the added bases decrypted from the original, written to the profile's vault by Apply.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<PasswordRole, string?>> Passwords { get; init; } =
        new Dictionary<Guid, IReadOnlyDictionary<PasswordRole, string?>>();
    /// <summary>The backup scenarios the import writes or the added bases refer to; null when neither.</summary>
    public LegacyBackupImport? Backup { get; init; }
}
public sealed record LegacyImportApplied(Library Library, string? FavoriteError);

/// <summary>Read-only source import into the local list. No archive entry is extracted to a supplied path.</summary>
public static class LegacyImport
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    private sealed record IniSection(string Name, Dictionary<string, string> Values);

    /// <param name="backupScenarios">The backup scenarios of the destination profile: the original's ones are added to them. Without
    /// it no scenario is imported and the scenario of a base is named as not transferred (its folder and prefix still are).</param>
    public static LegacyImportPreview Preview(Library local, string listFile, string? settingsFolder,
        LegacyImportOptions options, BackupScenarioStore? backupScenarios = null)
    {
        if (local.ListPath.Length != 0) throw new ArgumentException("Назначение импорта — локальный список.");
        LibraryStore.Validate(local);
        var hashes = new Dictionary<string, string?>();
        var notices = new List<string>();
        byte[]? Read(string file, bool required = false)
        {
            file = Path.GetFullPath(file);
            if (!File.Exists(file))
            {
                if (required) throw new FileNotFoundException("Источник импорта не найден.", file);
                hashes[file] = null; notices.Add($"Не найден необязательный файл: {Path.GetFileName(file)}."); return null;
            }
            var bytes = ReadLimited(file);
            hashes[file] = Convert.ToHexString(SHA256.HashData(bytes)); return bytes;
        }
        var list = Read(listFile, true)!;
        if (Path.GetExtension(listFile).Equals(".v8sx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var archive = new ZipArchive(new MemoryStream(list), ZipArchiveMode.Read);
                if (archive.Entries.Count > 16) throw new InvalidDataException("В снимке слишком много файлов.");
                var candidates = archive.Entries.Where(x => x.FullName.Equals("ibases.v8i", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length != 1) throw new InvalidDataException("Снимок должен содержать ровно один ibases.v8i в корне архива.");
                if (candidates[0].Length > MaximumBytes) throw new InvalidDataException("Список в снимке слишком велик.");
                using var content = candidates[0].Open(); list = ReadLimited(content);
                if (archive.Entries.Count != 1) notices.Add("Прочитан только ibases.v8i; остальные файлы снимка не извлекались.");
            }
            catch (InvalidDataException ex) { throw new InvalidDataException("Не удалось прочитать снимок .v8sx: архив повреждён, превышает ограничения или не содержит единственный корневой ibases.v8i.", ex); }
        }
        else if (!Path.GetExtension(listFile).Equals(".v8i", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выберите список .v8i или снимок .v8sx.");

        var listSections = ParseIni(Decode(list));
        if (listSections.Count == 0) throw new InvalidDataException("В источнике нет секций списка баз.");
        foreach (var section in listSections)
        {
            foreach (var key in new[] { "Version", "DefaultVersion" })
            {
                var version = section.Values.GetValueOrDefault(key, "");
                if (version.Length != 0 && version != "8" && !version.StartsWith("8.", StringComparison.Ordinal))
                    throw new InvalidDataException("Импорт поддерживает только списки платформы 1С 8.x.");
            }
        }
        if ((options.Clients || options.Favorites) && !string.IsNullOrWhiteSpace(settingsFolder) &&
            listSections.Any(x => x.Values.ContainsKey("Connect") && !Guid.TryParse(x.Values.GetValueOrDefault("ID"), out _)))
            notices.Add("У части баз нет корректного ID: их избранное и индивидуальный клиент нельзя сопоставить с настройками оригинала.");
        // Reuse the established v8i parser, including ID/group validation, in memory: no copy of the list is written.
        var incoming = V8iStore.Read(list, out var listWarnings);
        notices.AddRange(listWarnings);
        var supported = new HashSet<string>(["ID", "Connect", "Folder", "Version", "DefaultVersion", "WA"], StringComparer.OrdinalIgnoreCase);
        Unsupported("Список", listSections.SelectMany(x => x.Values.Keys).Where(x => !supported.Contains(x)), notices);
        foreach (var section in listSections.Where(x => x.Values.ContainsKey("Connect")))
        {
            try
            {
                var connection = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = section.Values["Connect"] };
                Unsupported("Строка подключения", connection.Keys.Cast<string>().Where(x => !new[] { "File", "Srvr", "Ref", "ws" }.Contains(x, StringComparer.OrdinalIgnoreCase)), notices);
            }
            catch (ArgumentException) { /* The established parser already reports this record. */ }
        }

        var metadata = new Dictionary<Guid, Dictionary<string, string>>();
        // The group records of the list by ID (a section without Connect=), as the original keys a group's settings.
        var groupRecords = new Dictionary<Guid, string>();
        foreach (var section in listSections.Where(x => !x.Values.ContainsKey("Connect")))
            if (Guid.TryParse(section.Values.GetValueOrDefault("ID"), out var id) &&
                Groups.Normalize(section.Values.GetValueOrDefault("Folder", "") + "/" + section.Name) is { Length: > 0 } path)
                groupRecords.TryAdd(id, path);
        // An ID two records of the list share (two groups, or a group and a base: Codex audit 14.10) tells nothing about which of
        // them the section of v8config.smc belongs to: its Directory is not taken and is named as not transferred.
        foreach (var shared in listSections.Select(x => Guid.TryParse(x.Values.GetValueOrDefault("ID"), out var id) ? id : (Guid?)null)
            .OfType<Guid>().GroupBy(x => x).Where(x => x.Count() > 1)) groupRecords.Remove(shared.Key);
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var passwordsReadable = true;
        var appearance = local.Appearance;
        var executable = local.Executable;
        var backupFolder = local.BackupFolder;
        // The original's scenario IDs (12 hex digits) to the scenarios of the destination; null when scenarios are not imported.
        Dictionary<string, Guid>? backupMap = null;
        (BackupScenarioStore Store, BackupScenarioSet Result, bool Changed)? backupScenarioSet = null;
        if (!string.IsNullOrWhiteSpace(settingsFolder))
        {
            if (!Directory.Exists(settingsFolder)) throw new DirectoryNotFoundException("Папка настроек StartManager не найдена.");
            if (options.Backup && backupScenarios is not null)
            {
                BackupScenarioSet? destination = null;
                try { destination = backupScenarios.Load(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { notices.Add("Сценарии резервирования не перенесены: файл сценариев этой программы не читается. " + ErrorMessage(ex)); }
                if (destination is not null)
                {
                    var (scenarios, map, changed) = BackupScenarios(settingsFolder, destination, !backupScenarios.Saved, x => Read(x), notices);
                    backupMap = map;
                    backupScenarioSet = (backupScenarios, scenarios, changed);
                }
            }
            if ((options.Clients || options.Favorites || options.Authentication || options.Properties || options.Backup) && Read(Path.Combine(settingsFolder, "v8config.smc")) is { } smc)
            {
                foreach (var section in ParseIni(Decode(smc)))
                {
                    if (!Guid.TryParse(section.Name, out var id)) { notices.Add("Пропущена секция настроек базы без корректного ID."); continue; }
                    if (!metadata.TryAdd(id, section.Values)) throw new InvalidDataException("Повторяющийся ID в v8config.smc.");
                }
                // The section of a group record keeps its folder in Directory (_Unit207.pas:33106); CategoryIcon is not transferred.
                Unsupported("Настройки баз", metadata.SelectMany(section => section.Value.Keys.Where(x =>
                    !(options.Clients && x.Equals("V8AppFile", StringComparison.OrdinalIgnoreCase)) &&
                    !(options.Clients && x.Equals("V8UseAppFile", StringComparison.OrdinalIgnoreCase)) &&
                    !(options.Favorites && x.Equals("Favorite", StringComparison.OrdinalIgnoreCase)) &&
                    !(options.Authentication && AuthenticationKeys.Contains(x)) &&
                    !(options.Properties && PropertyKeys.Contains(x)) &&
                    !(options.Backup && BackupKeys.Contains(x) && (backupMap is not null || !x.Equals("BackupScriptID", StringComparison.OrdinalIgnoreCase))) &&
                    !(options.Properties && groupRecords.ContainsKey(section.Key) && x.Equals("Directory", StringComparison.OrdinalIgnoreCase)))), notices);
                // «Конфигурация:» is the Descr of the section ConfigId of the original's Updates\v8updates.lst (TAppSession.FCfgUpdatePath =
                // the settings folder + 'Updates\', _Unit186.pas:34347; read by _Unit186.pas:26373, _Unit205.pas:25858). No file is no error.
                if (options.Properties && metadata.Values.Any(x => x.GetValueOrDefault("ConfigId", "").Length > 0) &&
                    Read(Path.Combine(settingsFolder, "Updates", "v8updates.lst")) is { } lst)
                    foreach (var section in ParseIni(Decode(lst)))
                        if (section.Values.TryGetValue("Descr", out var descr)) updates.TryAdd(section.Name, descr);
            }
            // The way the original kept passwords: the old one is read, the new one or an unknown one is never guessed.
            if (options.Authentication && metadata.Count > 0)
            {
                var method = PasswordMethod(Read(Path.Combine(settingsFolder, "settings.cnf")));
                passwordsReadable = method == false;
                if (method == true) notices.Add("В оригинале включён другой способ шифрования паролей (NewMethodEncryption): пароли не переносятся, имена пользователей переносятся. Введите пароли в свойствах баз.");
                else if (method is null) notices.Add("Способ шифрования паролей оригинала не определён (settings.cnf не найден или не читается): пароли не переносятся, имена пользователей переносятся. Введите пароли в свойствах баз.");
            }
            if ((options.Clients || options.Backup) && Read(Path.Combine(settingsFolder, "settings.cnf")) is { } cnf &&
                SettingsRoot(cnf, options.Clients, notices) is { } settings)
            {
                var version = settings.Element("InfoBases")?.Element("V8DefVersion")?.Value;
                var key = !options.Clients ? null : version switch { "8.1" => "V81AppFile", "8.2" => "V82AppFile", "8.3" => "V83AppFile", "8.4" => "V84AppFile", _ => null };
                var client = key is null ? "" : settings.Element("Launch")?.Element(key)?.Value ?? "";
                if (client.Length > 0)
                {
                    if (executable.Length != 0) notices.Add("Общий клиент назначения уже задан и сохранён.");
                    else executable = Client(client, notices);
                }
                // «Каталог по-умолчанию» of «Параметры резервного копирования» (_Unit186.pas:33058); the RAR settings are not used.
                var backup = options.Backup ? Line(settings.Element("Backup")?.Element("BackupFolder")?.Value) : "";
                if (backup.Length > 0)
                {
                    if (backupFolder.Length == 0) backupFolder = backup;
                    else if (!BaseEdit.SameFolder(backupFolder, backup)) notices.Add("Общий каталог резервных копий назначения уже задан и сохранён.");
                }
                Unsupported("Общие настройки", settings.Descendants().Where(x => !x.HasElements &&
                    !(options.Clients && x.Parent?.Name == "InfoBases" && x.Name == "V8DefVersion") &&
                    !(x.Parent?.Name == "Launch" && x.Name.LocalName == key) &&
                    !(options.Backup && x.Parent?.Name == "Backup" && x.Name == "BackupFolder")).Select(x => x.Parent!.Name.LocalName + "/" + x.Name.LocalName), notices);
            }
            if (options.Appearance)
            {
                var appearanceNoticeStart = notices.Count;
                try
                {
                    if (Read(Path.Combine(settingsFolder, "config.sme")) is { } sme)
                    {
                        var sections = ParseIni(Decode(sme));
                        var environments = sections.Where(x => x.Name.Equals("Environment", StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (environments.Length != 1) throw new InvalidDataException("В config.sme требуется одна секция Environment.");
                        var values = environments[0].Values;
                        var family = appearance.List.Family;
                        if (values.TryGetValue("FontName", out var requestedFamily))
                        {
                            if (string.IsNullOrWhiteSpace(requestedFamily)) notices.Add("Пустое имя шрифта оригинала пропущено; выбранное семейство сохранено.");
                            else
                            {
                                if (requestedFamily.Length > 256 || requestedFamily.Any(char.IsControl)) throw new InvalidDataException("Недопустимое имя шрифта в config.sme (длина до 256, без управляющих символов).");
                                family = requestedFamily;
                                notices.Add($"Семейство шрифта списка: {family}. Если оно отсутствует в ОС, используется доступная замена.");
                            }
                        }
                        var size = appearance.List.Size;
                        if (values.TryGetValue("FontSize", out var font))
                        {
                            if (!double.TryParse(font.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var points) || !double.IsFinite(points) || points <= 0)
                                throw new InvalidDataException("Некорректный FontSize в config.sme.");
                            size = Math.Clamp(Math.Round(points * 96 / 72, 2), 10, 24);
                            notices.Add($"Шрифт списка: {family}, {points} пт → {size} логических пикселей (диапазон 10–24).");
                        }
                        var height = appearance.MinimumRowHeight;
                        if (values.TryGetValue("DefNodeHeight", out var value))
                        {
                            if (!int.TryParse(value, out var pixels) || pixels <= 0) throw new InvalidDataException("Некорректный DefNodeHeight в config.sme.");
                            height = Math.Clamp(pixels, 20, 64);
                            notices.Add($"Минимальная высота строки: {pixels} → {height}; фактическая высота учитывает шрифт.");
                        }
                        if (appearance.UnrecognizedSource is not null) notices.Add("Выбранный импорт оформления заменит ранее неподдерживаемый профиль оформления.");
                        var importedAppearance = appearance with { List = appearance.List with { Family = family, Size = size }, MinimumRowHeight = height, UnrecognizedSource = null, LoadWarning = null };
                        importedAppearance.Validate();
                        appearance = importedAppearance;
                        Unsupported("Оформление", sections.SelectMany(s => s.Values.Keys.Where(k =>
                            !s.Name.Equals("Environment", StringComparison.OrdinalIgnoreCase) ||
                            !new[] { "FontName", "FontSize", "DefNodeHeight" }.Contains(k, StringComparer.OrdinalIgnoreCase)).Select(k => s.Name + "/" + k)), notices);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    appearance = local.Appearance;
                    notices.RemoveRange(appearanceNoticeStart, notices.Count - appearanceNoticeStart);
                    notices.Add("Оформление не перенесено; сохранён прежний профиль. " + ErrorMessage(ex));
                }
            }
        }
        else notices.Add("Папка SMSettings не выбрана: переносится только список баз и групп.");

        var entries = local.Bases.ToList(); var added = new List<BaseEntry>(); var favorites = new HashSet<Guid>(); var duplicates = 0;
        var passwords = new Dictionary<Guid, IReadOnlyDictionary<PasswordRole, string?>>();
        foreach (var candidate in incoming.Bases)
        {
            var sameId = entries.FirstOrDefault(x => x.Id == candidate.Id);
            if (sameId is not null && !SameConnection(sameId, candidate))
            { notices.Add($"Конфликт ID: «{candidate.Name}» пропущена; существующая база сохранена."); continue; }
            var existing = sameId ?? entries.FirstOrDefault(x => SameConnection(x, candidate));
            metadata.TryGetValue(candidate.Id, out var properties);
            var entry = candidate;
            if (existing is not null) { duplicates++; entry = existing; }
            else
            {
                if (options.Clients && properties is not null && Enabled(properties.GetValueOrDefault("V8UseAppFile")))
                    entry = entry with { Executable = Client(properties.GetValueOrDefault("V8AppFile", ""), notices) };
                if (options.Authentication && properties is not null)
                {
                    var (authentication, secrets) = Authentication(properties, passwordsReadable);
                    if (authentication is not null) entry = entry with { Authentication = authentication };
                    if (secrets.Count > 0) passwords[entry.Id] = secrets;
                }
                if (options.Properties && properties is not null) entry = OtherProperties(entry, properties, updates, notices);
                if (options.Backup && properties is not null) entry = BackupProperties(entry, properties, backupMap, notices);
                entries.Add(entry); added.Add(entry);
            }
            if (options.Favorites && properties is not null && Enabled(properties.GetValueOrDefault("Favorite"))) favorites.Add(entry.Id);
        }
        if (duplicates > 0) notices.Add("У дубликатов сохранены имя, группа и клиент назначения; импортируется только выбранная отметка избранного.");
        if (passwords.Count > 0) notices.Add($"Переносятся сохранённые пароли баз: {passwords.Count}. Они будут зашифрованы в папке настроек этой программы.");
        var importedGroups = Groups.All(new Library { Bases = added, Groups = incoming.Groups });
        var newGroups = importedGroups.Except(Groups.All(local), StringComparer.OrdinalIgnoreCase).ToArray();
        if (newGroups.Length > 0) notices.Add("Добавятся группы: " + string.Join(", ", newGroups));
        var result = local with { Bases = entries, Groups = Groups.All(local).Concat(importedGroups).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Appearance = appearance, Executable = executable,
            BackupFolder = backupFolder };
        if (options.Properties) result = result with { GroupFolders = GroupFolders(result, groupRecords, metadata, notices) };
        LibraryStore.Validate(result);
        // A base mapped to an existing scenario depends on the scenarios as read even when nothing is written (Codex audit, part 1):
        // a scenario removed in another window meanwhile is a conflict, not a silent fall back to the default one.
        var backupImport = backupScenarioSet is var (store, set, write) && (write || added.Any(x => x.BackupScriptId is not null))
            ? new LegacyBackupImport(store, set, write) : null;
        return new(result, added, duplicates, favorites.ToArray(), notices.Distinct().ToArray(), hashes) { Passwords = passwords, Backup = backupImport };
    }

    public static LegacyImportApplied Apply(LegacyImportPreview preview, LibraryStore target, ActivityStore activity)
    {
        foreach (var (path, expected) in preview.SourceHashes)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (new[] { target.PathName, target.PathName + ".bak", target.PathName + ".lock", activity.PathName,
                activity.PathName + ".bak", activity.PathName + ".lock" }.Any(x => Path.GetFullPath(x).Equals(path, comparison)))
                throw new IOException("Источник импорта совпадает с файлом назначения или его служебным файлом.");
            var actual = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(ReadLimited(path))) : null;
            if (actual != expected) throw new IOException("Источник изменился после предпросмотра. Подготовьте импорт заново.");
        }
        if (preview.Favorites.Count > 0) activity.Load(); // Fail before changing the library if activity is already unreadable.
        // Scenarios changed in another window since the preview would be overwritten: refuse before anything is written.
        preview.Backup?.Store.EnsureUnchanged();
        target.Save(preview.Result);
        string? warning = null;
        try { if (preview.Favorites.Count > 0) activity.AddFavorites(ActivityStore.Source(target.PathName, ""), preview.Favorites); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { warning = "Базы сохранены, но избранное не перенесено: " + ErrorMessage(ex) + " Повторите импорт после исправления ошибки; дубликаты баз не добавятся."; }
        try
        {
            var vault = new PasswordVault(target.PathName);
            // The bases are imported into the local list.
            foreach (var (id, secrets) in preview.Passwords) vault.Write(ActivityStore.Source(target.PathName, ""), id, secrets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { warning = (warning is null ? "Базы сохранены, " : warning + " ") + "но пароли не перенесены: " + ErrorMessage(ex) + " Введите их в свойствах баз."; }
        // A base whose scenario was not written uses the default one until the import is repeated (the IDs of imported
        // scenarios come from the original's ones, so a repeat finds the same scenario).
        try { if (preview.Backup is { Write: true } backup) backup.Store.Save(backup.Result); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { warning = (warning is null ? "Базы сохранены, " : warning + " ") + "но сценарии резервирования не перенесены: " + ErrorMessage(ex) + " Повторите импорт после исправления ошибки."; }
        return new(preview.Result, warning);
    }

    public static string ErrorMessage(Exception ex) => ex switch
    {
        DecoderFallbackException => "Файл содержит данные, не соответствующие указанной кодировке. Проверьте файл в текстовом редакторе или восстановите исправную копию.",
        PathTooLongException => "Путь к файлу слишком длинный. Выберите более короткий путь.",
        FileNotFoundException => "Файл источника не найден. Проверьте выбранный путь.",
        DirectoryNotFoundException => "Папка источника или временных файлов недоступна. Проверьте выбранный путь и системную папку TEMP.",
        UnauthorizedAccessException => "Нет доступа к файлу или папке. Проверьте права доступа.",
        ExplainedIOException => ex.Message,
        IOException io when Code(io) is FileError.Busy => "Файл занят другим процессом. Повторите операцию после освобождения файла.",
        IOException io when Code(io) is FileError.DiskFull => "Недостаточно свободного места для записи файла и резервной копии.",
        IOException io when Code(io) is FileError.LogonFailure => "Сетевая папка не приняла имя пользователя или пароль. Подключитесь к ней заново и повторите операцию.",
        IOException io when Code(io) is FileError.CredentialConflict => "К этому серверу уже есть подключение под другим именем пользователя. Отключите другие подключения к нему (net use) и повторите операцию.",
        IOException io when Code(io) is FileError.Network => "Сетевая папка недоступна или соединение с ней прервано. Проверьте подключение к сети и сетевой папке и повторите операцию.",
        IOException io when Code(io) is FileError.Timeout => "Истекло время ожидания ответа от диска или сетевой папки. Проверьте подключение и повторите операцию.",
        IOException io when Code(io) is FileError.InputOutput => "Ошибка ввода-вывода при работе с файлом или папкой. Проверьте диск или сетевую папку и повторите операцию.",
        IOException io when OperatingSystem.IsWindows() && (io.HResult & 0xFFFF) == 123 => "Недопустимый путь к файлу. Проверьте имя, длину и символы в пути.",
        IOException io when OperatingSystem.IsWindows() && (io.HResult & 0xFFFF) == 206 => "Путь к файлу слишком длинный. Выберите более короткий путь.",
        IOException io when OperatingSystem.IsWindows() && (io.HResult & 0xFFFF) == 183 => "Не удалось создать файл или папку: путь уже занят. Проверьте указанный путь и его родительские каталоги.",
        _ => ex.Message
    };

    private enum FileError { Other, Busy, DiskFull, LogonFailure, CredentialConflict, Network, Timeout, InputOutput }

    // Windows keeps a Win32 code in the low word of the HRESULT; on Unix .NET puts the raw errno into HResult (Interop.IOErrors),
    // and the same numbers mean other things there (112 is EHOSTDOWN, not a full disk), so the two tables stay apart.
    private static FileError Code(IOException io) => OperatingSystem.IsWindows()
        ? (io.HResult & 0xFFFF) switch
        {
            32 or 33 => FileError.Busy, // sharing and lock violation
            112 or 39 or 1295 => FileError.DiskFull, // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL, ERROR_DISK_QUOTA_EXCEEDED
            // ERROR_LOGON_FAILURE (the share dropped the session and refuses the user), ERROR_INVALID_PASSWORD
            1326 or 86 => FileError.LogonFailure,
            1219 => FileError.CredentialConflict, // ERROR_SESSION_CREDENTIAL_CONFLICT: one user per server in a logon session
            // REM_NOT_LIST, BAD_NETPATH, NETWORK_BUSY, DEV_NOT_EXIST, BAD_NET_RESP, UNEXP_NET_ERR, NETNAME_DELETED, BAD_NET_NAME,
            // NET_WRITE_FAULT, NO_NETWORK, CONNECTION_REFUSED, NETWORK_UNREACHABLE, HOST_UNREACHABLE, CONNECTION_ABORTED, HOST_DOWN,
            // NOT_CONNECTED. NETWORK_ACCESS_DENIED (65) is a refusal of rights, not a lost share: it keeps the system text.
            51 or 53 or 54 or 55 or 58 or 59 or 64 or 67 or 88 or 1222 or 1225 or 1231 or 1232 or 1236 or 1256 or 2250 => FileError.Network,
            121 => FileError.Timeout, // ERROR_SEM_TIMEOUT: a share or a local device that did not answer
            _ => FileError.Other
        }
        : io.HResult switch
        {
            // EAGAIN of a held flock, EBUSY of a file another computer holds on a CIFS share (stand check shared-nas-20261015), ETXTBSY
            11 or 16 or 26 => FileError.Busy,
            28 or 122 => FileError.DiskFull, // ENOSPC, EDQUOT
            127 or 129 => FileError.LogonFailure, // EKEYEXPIRED, EKEYREJECTED of a CIFS session key
            // ENETDOWN, ENETUNREACH, ECONNABORTED, ECONNRESET, ENOTCONN, ECONNREFUSED, EHOSTDOWN, EHOSTUNREACH, ESTALE, EREMOTEIO
            100 or 101 or 103 or 104 or 107 or 111 or 112 or 113 or 116 or 121 => FileError.Network,
            110 => FileError.Timeout, // ETIMEDOUT
            5 => FileError.InputOutput, // EIO: a disk as well as a share
            _ => FileError.Other
        };

    private static readonly HashSet<string> PropertyKeys = new(["DisallowDelete", "ActionDblClick", "RunAsAdmin", "App", "CheckRAgent",
        "Note", "ConfigVersion", "ConfigId"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders of the groups (Directory of the section with the ID of the group's record, _Unit207.pas:33106-33125) added
    /// to the folders of the destination: a group that already has its own folder keeps it, as the client of the destination is kept.</summary>
    private static List<GroupFolder> GroupFolders(Library result, Dictionary<Guid, string> records,
        Dictionary<Guid, Dictionary<string, string>> metadata, List<string> notices)
    {
        var folders = result.GroupFolders.ToList(); var groups = Groups.All(result);
        foreach (var (id, path) in records)
        {
            if (!metadata.TryGetValue(id, out var section) || section.GetValueOrDefault("Directory", "").Trim() is not { Length: > 0 } folder) continue;
            // The spelling of the group as the destination has it (a group of another case is the same group).
            if (groups.FirstOrDefault(x => x.Equals(path, StringComparison.OrdinalIgnoreCase)) is not { } group) continue;
            var own = Groups.Folder(result with { GroupFolders = folders }, group);
            if (own.Length > 0)
            {
                if (!BaseEdit.SameFolder(own, folder)) notices.Add($"Каталог группы «{group}» уже задан и сохранён.");
                continue;
            }
            // A path of another system (C:\… on Linux) or a broken one is named, not guessed.
            if (folder.Any(char.IsControl) || !Path.IsPathFullyQualified(folder))
            { notices.Add($"Каталог группы «{group}» не перенесён: путь «{folder}» недоступен на этой системе."); continue; }
            folders.Add(new GroupFolder { Group = group, Folder = folder, Id = id });
        }
        return folders;
    }

    /// <summary>A value of the original's settings for a field that takes no control characters: they become spaces.</summary>
    private static string Line(string? value) => new string([.. (value ?? "").Select(x => char.IsControl(x) ? ' ' : x)]).Trim();

    // The page «Прочие свойства» of the original. ActionDblClick: 0 — the general setting, then the items of its list.
    // The page «Сведения»: Note, ConfigVersion and ConfigId (_Unit205.pas:25764/25813/25848); «Конфигурация:» is the Descr of ConfigId.
    private static BaseEntry OtherProperties(BaseEntry entry, Dictionary<string, string> properties, IReadOnlyDictionary<string, string> updates, List<string> notices)
    {
        var configurationId = Line(properties.GetValueOrDefault("ConfigId"));
        entry = entry with { Comment = properties.GetValueOrDefault("Note", ""), ConfigurationRelease = Line(properties.GetValueOrDefault("ConfigVersion")),
            ConfigurationId = configurationId, Configuration = configurationId.Length > 0 ? Line(updates.GetValueOrDefault(configurationId)) : "" };
        BaseDoubleClick? action = properties.GetValueOrDefault("ActionDblClick", "0").Trim() switch
        {
            "1" => BaseDoubleClick.Enterprise, "2" => BaseDoubleClick.EnterpriseLogin, "3" => BaseDoubleClick.EnterpriseParameters,
            "4" => BaseDoubleClick.Designer, "6" => BaseDoubleClick.Properties, _ => null
        };
        if (action is null && properties.GetValueOrDefault("ActionDblClick", "0").Trim() is not ("0" or ""))
            notices.Add($"«{entry.Name}»: действие при двойном щелчке не поддерживается (например, Snegopat); используется общая настройка.");
        return entry with { DisallowDelete = Enabled(properties.GetValueOrDefault("DisallowDelete")), DoubleClick = action,
            RunAsAdmin = Enabled(properties.GetValueOrDefault("RunAsAdmin")),
            // «Перед запуском ИБ проверять версию Агента сервера» of the page «Подключение» (_Unit205.pas:25880).
            CheckServerAgent = Enabled(properties.GetValueOrDefault("CheckRAgent")),
            // «Режим» of the original's own settings goes over App= of the list, as the original reads it: 1 thin, 2 thick (ОП),
            // 3 thick (УП), 4 web; 0 or none keeps the list's App= (Codex audit 12.10).
            ClientType = properties.GetValueOrDefault("App", "").Trim() switch { "1" => "ThinClient", "2" or "3" => "ThickClient", "4" => "WebClient", _ => entry.ClientType },
            ManagedApplication = properties.GetValueOrDefault("App", "").Trim() == "3" };
    }

    // The page «Резервное копирование» of the original (smFormProperty.dfm:1056-1136, read by _Unit207.pas:39960-39990).
    private static readonly HashSet<string> BackupKeys = new(["BackupScriptID", "BackupFolder", "BackupPrefix"], StringComparer.OrdinalIgnoreCase);

    private static BaseEntry BackupProperties(BaseEntry entry, Dictionary<string, string> properties, Dictionary<string, Guid>? scenarios, List<string> notices)
    {
        Guid? scenario = null;
        if (scenarios is not null && properties.GetValueOrDefault("BackupScriptID", "").Trim() is { Length: > 0 } legacy)
        {
            if (scenarios.TryGetValue(legacy, out var id)) scenario = id;
            else notices.Add($"«{entry.Name}»: сценарий резервирования {legacy} не найден среди сценариев оригинала; используется сценарий по умолчанию.");
        }
        return entry with { BackupScriptId = scenario, BackupFolder = Line(properties.GetValueOrDefault("BackupFolder")),
            BackupPrefix = Line(properties.GetValueOrDefault("BackupPrefix")) };
    }

    /// <summary>The ID of a scenario imported from the original: made from its own ID, so a repeated import finds it again.</summary>
    internal static Guid LegacyBackupId(string legacy) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("StartManager backup script\0" + legacy.Trim().ToUpperInvariant())).AsSpan(0, 16));

    /// <summary>The original's scenarios (BackupScripts\bckscripts.lst and its .bks files, _Unit186.pas:16707-17892) added to the
    /// destination's: a scenario already there — by its ID or with the same settings, as the ready ones — is not repeated.
    /// The default one of the original becomes the default only for a profile that never saved its scenarios.</summary>
    private static (BackupScenarioSet Result, Dictionary<string, Guid> Map, bool Changed) BackupScenarios(string settingsFolder,
        BackupScenarioSet destination, bool fresh, Func<string, byte[]?> read, List<string> notices)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(settingsFolder, "BackupScripts");
        if (!Directory.Exists(folder) || read(Path.Combine(folder, "bckscripts.lst")) is not { } list) return (destination, map, false);
        var scenarios = destination.Scenarios.ToList(); var added = new List<BackupScenario>();
        string? main = null;
        foreach (var section in ParseIni(Decode(list)))
        {
            if (section.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase)) { main = section.Values.GetValueOrDefault("ID")?.Trim(); continue; }
            var legacy = section.Name.Trim();
            if (legacy.Length == 0 || legacy.Any(char.IsControl) || map.ContainsKey(legacy))
            { notices.Add($"Пропущена повторяющаяся или пустая запись сценария резервирования «{Line(legacy)}» в bckscripts.lst."); continue; }
            // Only a file of the folder itself: a name with a path is never followed.
            var file = section.Values.GetValueOrDefault("FILE", "").Trim();
            if (file.Length == 0 || file is "." or ".." || file.IndexOfAny(['/', '\\', ':']) >= 0 || file.Any(char.IsControl))
            { notices.Add($"Сценарий резервирования {legacy}: недопустимое имя файла «{Line(file)}»; сценарий не перенесён."); continue; }
            if (read(Path.Combine(folder, file)) is not { } bks) continue;
            if (Scenario(legacy, ParseIni(Decode(bks)), notices) is not { } scenario) continue;
            var same = scenarios.FirstOrDefault(x => x.Id == scenario.Id) ?? scenarios.FirstOrDefault(x => x.SameSettings(scenario));
            if (same is null) { scenarios.Add(scenario); added.Add(scenario); same = scenario; }
            map[legacy] = same.Id;
        }
        var defaultId = destination.DefaultId;
        if (main is { Length: > 0 } && map.TryGetValue(main, out var chosen) && chosen != destination.Default?.Id)
        {
            if (fresh) defaultId = chosen;
            else notices.Add("Сценарий резервирования по умолчанию назначения сохранён.");
        }
        if (added.Count > 0) notices.Add("Добавятся сценарии резервирования: " + string.Join(", ", added.Select(x => x.Name)));
        if (added.Any(x => x.Format == BackupFormat.Zip))
            notices.Add("Сжатие ZIP в оригинале не настраивалось (панель была скрыта): у перенесённых сценариев ZIP установлено NORMAL.");
        return (destination with { Scenarios = scenarios, DefaultId = defaultId }, map, added.Count > 0 || defaultId != destination.DefaultId);
    }

    private static readonly string[] ScenarioKeys = ["INFO/Name", "INFO/Descr", "FILE/FName", "FILE/ID", "FILE/FormatId",
        "TARGET/UseLocalFolder1", "TARGET/LocalFolder1", "TARGET/UseLocalFolder2", "TARGET/LocalFolder2", "TARGET/UseLocalFolder3", "TARGET/LocalFolder3",
        "ARCHIVE/CompressMethod", "ARCHIVE/SplitVolumePreset", "ARCHIVE/SplitVolume", "ARCHIVE/VolumUnit"];

    /// <summary>One .bks of the original: [INFO] Name, Descr; [FILE] FName, FormatId (0 DT, 1 CF, 2 ZIP, 3 RAR); [TARGET] the three
    /// folders and their flags; [ARCHIVE] the split (SplitVolumePreset 0–5, SplitVolume in VolumUnit «mb»). RAR becomes ZIP.</summary>
    private static BackupScenario? Scenario(string legacy, List<IniSection> sections, List<string> notices)
    {
        string? Raw(string section, string key) => sections.FirstOrDefault(x => x.Name.Equals(section, StringComparison.OrdinalIgnoreCase))?.Values.GetValueOrDefault(key);
        string Value(string section, string key) => Line(Raw(section, key));
        var name = Value("INFO", "Name") is { Length: > 0 } given ? given : "Сценарий " + Line(legacy);
        Unsupported($"Сценарий резервирования «{name}»", sections.SelectMany(s => s.Values.Keys.Select(k => s.Name + "/" + k))
            .Where(x => !ScenarioKeys.Contains(x, StringComparer.OrdinalIgnoreCase)), notices);
        BackupFormat format;
        switch (Value("FILE", "FormatId"))
        {
            case "" or "0": format = BackupFormat.Dt; break;
            case "1": format = BackupFormat.Cf; break;
            case "2": format = BackupFormat.Zip; break;
            case "3": format = BackupFormat.Zip; notices.Add($"Сценарий резервирования «{name}»: формат RAR не поддерживается, сценарий перенесён как ZIP."); break;
            default: notices.Add($"Сценарий резервирования «{name}»: неизвестный формат выгрузки; сценарий не перенесён."); return null;
        }
        var split = int.TryParse(Value("ARCHIVE", "SplitVolumePreset"), NumberStyles.None, CultureInfo.InvariantCulture, out var preset) && preset is >= 0 and <= 5
            ? (BackupSplit)preset : BackupSplit.None;
        var volume = 0;
        if (split == BackupSplit.Custom && (!int.TryParse(Value("ARCHIVE", "SplitVolume"), NumberStyles.None, CultureInfo.InvariantCulture, out volume) ||
            volume is < 1 or > BackupScenario.MaxVolumeSize || Value("ARCHIVE", "VolumUnit") is var unit && unit.Length > 0 && !unit.Equals("mb", StringComparison.OrdinalIgnoreCase)))
        {
            notices.Add($"Сценарий резервирования «{name}»: размер тома не распознан; архив не разделяется.");
            split = BackupSplit.None; volume = 0;
        }
        var scenario = new BackupScenario
        {
            Id = LegacyBackupId(legacy), Name = name, Description = Value("INFO", "Descr"),
            Template = Value("FILE", "FName") is { Length: > 0 } template ? template : BackupName.DefaultTemplate, Format = format,
            // A file without the flag of the first folder unloads there, as the ready scenarios do.
            UseFolder1 = Raw("TARGET", "UseLocalFolder1") is not { } use1 || Enabled(use1.Trim()), Folder1 = Value("TARGET", "LocalFolder1"),
            UseFolder2 = Enabled(Value("TARGET", "UseLocalFolder2")), Folder2 = Value("TARGET", "LocalFolder2"),
            UseFolder3 = Enabled(Value("TARGET", "UseLocalFolder3")), Folder3 = Value("TARGET", "LocalFolder3"),
            Compression = BackupCompression.Normal, Split = split, VolumeSize = volume
        };
        scenario.Validate();
        return scenario;
    }

    private static readonly HashSet<string> AuthenticationKeys = new(["UserLoginEnt", "EntUser", "EntPassword", "UserLoginCnf", "CfgUser",
        "CfgPassword", "OSAuthentication", "UserStorage", "StorageDir", "StorageUser", "StoragePassword"], StringComparer.OrdinalIgnoreCase);

    // The page «Аутентификация» of the original: flags and names as they are, passwords decrypted only when readable.
    private static (AuthenticationSettings? Settings, Dictionary<PasswordRole, string?> Passwords) Authentication(
        Dictionary<string, string> properties, bool passwordsReadable)
    {
        string Value(string key) => properties.GetValueOrDefault(key, "");
        var settings = new AuthenticationSettings
        {
            EnterpriseLogin = Enabled(Value("UserLoginEnt")), EnterpriseUser = Value("EntUser"),
            DesignerLogin = Enabled(Value("UserLoginCnf")), DesignerUser = Value("CfgUser"),
            OsAuthentication = Enabled(Value("OSAuthentication")),
            Repository = Enabled(Value("UserStorage")) && Value("StorageDir").Length > 0, RepositoryPath = Value("StorageDir"), RepositoryUser = Value("StorageUser")
        };
        var passwords = new Dictionary<PasswordRole, string?>();
        if (passwordsReadable)
            foreach (var (role, key) in new[] { (PasswordRole.Enterprise, "EntPassword"), (PasswordRole.Designer, "CfgPassword"), (PasswordRole.Repository, "StoragePassword") })
                if (LegacyPasswords.Decrypt(Value(key)) is { Length: > 0 } password && !password.Any(char.IsControl)) passwords[role] = password;
        try { settings.Validate(); }
        catch (ArgumentException) { return (null, []); }
        return (settings.IsEmpty ? null : settings, passwords);
    }

    /// <summary>The root of settings.cnf. With the clients a damaged file stops the import, as before; without them only the general
    /// backup folder is read from it, so the file is named and the import goes on.</summary>
    private static XElement? SettingsRoot(byte[] bytes, bool required, List<string> notices)
    {
        using var xml = XmlReader.Create(new StringReader(Decode(bytes)), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
        try
        {
            var document = XDocument.Load(xml);
            if (document.Root?.Name != "Settings") throw new InvalidDataException("Неизвестный формат settings.cnf.");
            return document.Root;
        }
        catch (Exception ex) when (!required && ex is XmlException or InvalidDataException)
        { notices.Add("Файл settings.cnf повреждён или имеет неизвестный формат: общий каталог резервных копий не перенесён."); return null; }
        catch (XmlException ex) { throw new InvalidDataException("Файл settings.cnf повреждён или содержит запрещённые XML-объявления. Отключите перенос клиентов либо исправьте копию файла.", ex); }
    }

    /// <summary>NewMethodEncryption of settings.cnf: true — the new, unknown method; false — the old one (also when the
    /// element is absent, as in versions without the choice); null — the file is missing or unreadable, nothing is known.</summary>
    private static bool? PasswordMethod(byte[]? settings)
    {
        if (settings is null) return null;
        try
        {
            using var xml = XmlReader.Create(new StringReader(Decode(settings)), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
            var document = XDocument.Load(xml);
            if (document.Root?.Name != "Settings") return null;
            var values = document.Descendants().Where(x => x.Name.LocalName.Equals("NewMethodEncryption", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value.Trim()).ToArray();
            if (values.Length == 0) return false;
            if (values.Length > 1) return null;
            return Enabled(values[0]) ? true : values[0] is "0" || values[0].Equals("false", StringComparison.OrdinalIgnoreCase) ? false : null;
        }
        catch (Exception ex) when (ex is XmlException or DecoderFallbackException) { return null; }
    }

    private static bool Enabled(string? value) => value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    private static string Client(string value, List<string> notices)
    {
        if (value.Length == 0) return "";
        if (Path.IsPathFullyQualified(value) && File.Exists(value)) return value;
        notices.Add("Путь клиента из оригинала недоступен на этой системе; клиент не перенесён."); return "";
    }
    private static bool SameConnection(BaseEntry a, BaseEntry b)
    {
        if (a.Kind != b.Kind) return false;
        if (a.Kind == ConnectionKind.Web) return BaseEntry.SameWebAddress(a.Location, b.Location);
        var insensitive = a.Kind != ConnectionKind.File || OperatingSystem.IsWindows() || LooksWindows(a.Location) || LooksWindows(b.Location);
        var left = a.Location; var right = b.Location;
        if (a.Kind == ConnectionKind.File && (OperatingSystem.IsWindows() || LooksWindows(left) || LooksWindows(right)))
        { left = left.Replace('\\', '/'); right = right.Replace('\\', '/'); }
        return left.TrimEnd('/', '\\').Equals(right.TrimEnd('/', '\\'), insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    private static bool LooksWindows(string path) => path.StartsWith("\\\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal) || (path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\');
    private static void Unsupported(string source, IEnumerable<string> keys, List<string> notices)
    {
        var names = keys.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > 0) notices.Add(source + " — не переносятся: " + string.Join(", ", names));
    }
    private static List<IniSection> ParseIni(string text)
    {
        var sections = new List<IniSection>(); IniSection? current = null;
        foreach (var raw in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { current = new(line[1..^1], new(StringComparer.OrdinalIgnoreCase)); sections.Add(current); }
            else if (current is not null && line.IndexOf('=') is var index && index > 0) current.Values[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return sections;
    }
    private static byte[] ReadLimited(string path) { using var file = File.OpenRead(path); return ReadLimited(file); }
    private static byte[] ReadLimited(Stream stream)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = stream.Read(buffer)) != 0)
        { if (output.Length + count > MaximumBytes) throw new InvalidDataException("Файл импорта превышает 16 МБ."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1251).GetString(bytes); }
    }
}
