using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

/// <summary>«Параметры выгрузки» of a backup scenario: FormatId 0–2 of the original's .bks (RAR, FormatId=3, is not made).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BackupFormat>))]
public enum BackupFormat { Dt, Cf, Zip }

/// <summary>«Сжатие:» of a ZIP scenario, in the order of the original's list (SAVE … ULTRA).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BackupCompression>))]
public enum BackupCompression { Save, Fast, Normal, Maximum, Ultra }

/// <summary>«Разделение:» of a ZIP scenario, in the order of the original's list (SplitVolumePreset 0–5): none, «Определить»
/// (the size of a volume is <see cref="BackupScenario.VolumeSize"/>), 50, 100, 200 and 500 MB.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BackupSplit>))]
public enum BackupSplit { None, Custom, Mb50, Mb100, Mb200, Mb500 }

/// <summary>A backup scenario (the original's «Сценарий резервирования», one .bks file): what is unloaded, how the file is
/// named and where it goes. Folders may hold the macros of <see cref="BackupName"/>; an empty «1. Каталог» uses the default one.</summary>
public sealed record BackupScenario
{
    /// <summary>The largest «Размер тома» in MB, as the original keeps it (SplitVolume is a Word).</summary>
    public const int MaxVolumeSize = 65535;
    /// <summary>The largest «Хранить копий».</summary>
    public const int MaxKeepCopies = 9999;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    /// <summary>A new scenario gets its own ID; a stored one must have it written (bases refer to it), so a file without it is
    /// refused as damaged instead of getting a new ID on every read.</summary>
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>«Наименование:».</summary>
    public string Name { get; init; } = "";
    /// <summary>«Краткое описание:»; empty shows the name, as the original does.</summary>
    public string Description { get; init; } = "";
    /// <summary>«Шаблон имени файла архива:» without the extension (the format gives it).</summary>
    public string Template { get; init; } = BackupName.DefaultTemplate;
    public BackupFormat Format { get; init; }
    /// <summary>«Выполнить выгрузку на компьютер или сетевой диск» and «1. Каталог:» (empty — the default folder).</summary>
    public bool UseFolder1 { get; init; } = true;
    public string Folder1 { get; init; } = "";
    /// <summary>«2. Каталог:» and «3. Каталог:» with their flags: the ready file is copied there as well.</summary>
    public bool UseFolder2 { get; init; }
    public string Folder2 { get; init; } = "";
    public bool UseFolder3 { get; init; }
    public string Folder3 { get; init; } = "";
    /// <summary>Whether the ready file is copied to «2. Каталог» / «3. Каталог»: their own flag and the master flag «Выполнить выгрузку на
    /// компьютер или сетевой диск» (the editor disables them under it, as the original's FormRefresh), and a folder given. The one rule
    /// of the editor's check and of the copies of a backup (Codex audit, part 3).</summary>
    [JsonIgnore] public bool CopiesToFolder2 => UseFolder1 && UseFolder2;
    [JsonIgnore] public bool CopiesToFolder3 => UseFolder1 && UseFolder3;
    public BackupCompression Compression { get; init; } = BackupCompression.Normal;
    public BackupSplit Split { get; init; }
    /// <summary>The size of a volume in MB for <see cref="BackupSplit.Custom"/>; ignored otherwise.</summary>
    public int VolumeSize { get; init; }
    /// <summary>«Хранить копий»: how many latest copies of a base made by this scenario stay; 0 keeps all.</summary>
    public int KeepCopies { get; init; }

    /// <summary>The description as the list shows it: the name when none is given.</summary>
    [JsonIgnore] public string ShownDescription => string.IsNullOrWhiteSpace(Description) ? Name : Description;

    /// <summary>The size of one ZIP volume in bytes; null when the archive is not split.</summary>
    [JsonIgnore] public long? VolumeBytes => Split switch
    {
        BackupSplit.Custom => VolumeSize * 1024L * 1024L, BackupSplit.Mb50 => 50L << 20, BackupSplit.Mb100 => 100L << 20,
        BackupSplit.Mb200 => 200L << 20, BackupSplit.Mb500 => 500L << 20, _ => null
    };

    public override string ToString() => Name;

    public void Validate()
    {
        if (Name is null || Description is null || Template is null || Folder1 is null || Folder2 is null || Folder3 is null)
            throw new ArgumentException("Повреждены параметры сценария резервирования.");
        if (Id == Guid.Empty) throw new ArgumentException("У сценария резервирования нет идентификатора.");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Введите наименование сценария.");
        if (string.IsNullOrWhiteSpace(Template)) throw new ArgumentException("Укажите шаблон имени файла архива.");
        if (new[] { Name, Description, Template, Folder1, Folder2, Folder3 }.Any(x => x.Any(char.IsControl)))
            throw new ArgumentException("Наименование, шаблон и каталоги сценария не должны содержать переносы строк и управляющие символы.");
        if (!Enum.IsDefined(Format) || !Enum.IsDefined(Compression) || !Enum.IsDefined(Split))
            throw new ArgumentException("Неизвестный формат или параметр архивирования сценария.");
        if (Split == BackupSplit.Custom && VolumeSize is < 1 or > MaxVolumeSize)
            throw new ArgumentException($"Размер тома: от 1 до {MaxVolumeSize} МБ.");
        if (VolumeSize is < 0 or > MaxVolumeSize) throw new ArgumentException($"Размер тома: от 1 до {MaxVolumeSize} МБ.");
        if (KeepCopies is < 0 or > MaxKeepCopies) throw new ArgumentException($"Хранить копий: от 0 (все) до {MaxKeepCopies}.");
    }

    /// <summary>The same scenario apart from its ID and unknown fields; the name and description compare without regard to
    /// repeated spaces (the original's ready ZIP scenario has a double one).</summary>
    public bool SameSettings(BackupScenario other)
    {
        static string Text(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Text(Name) == Text(other.Name) && Text(Description) == Text(other.Description) && Template == other.Template &&
            Format == other.Format && (UseFolder1, Folder1, UseFolder2, Folder2, UseFolder3, Folder3) ==
            (other.UseFolder1, other.Folder1, other.UseFolder2, other.Folder2, other.UseFolder3, other.Folder3) &&
            Compression == other.Compression && Split == other.Split && (Split != BackupSplit.Custom || VolumeSize == other.VolumeSize) &&
            KeepCopies == other.KeepCopies;
    }
}

/// <summary>The backup scenarios of a profile and the default one («Назначить основным»).</summary>
public sealed record BackupScenarioSet
{
    /// <summary>The IDs of the original's three ready scenarios as this program creates them: fixed, so a base can refer to one
    /// before the scenarios are first saved.</summary>
    public static readonly Guid DumpId = new("f66eaf1e-9a16-4ca9-b22d-af0ab14f8dc6");
    public static readonly Guid ConfigurationId = new("0b32073e-9f5f-40a0-978a-4e355bb877ea");
    public static readonly Guid ArchiveId = new("c1673506-af47-409c-8bf8-01384ac4e8c6");

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    public int Version { get; init; } = 1;
    public Guid DefaultId { get; init; }
    public List<BackupScenario> Scenarios { get; init; } = [];

    /// <summary>The scenario «<Использовать сценарий по умолчанию>» means: the default one, else the first; null without any.</summary>
    [JsonIgnore] public BackupScenario? Default => Scenarios.FirstOrDefault(x => x.Id == DefaultId) ?? Scenarios.FirstOrDefault();

    /// <summary>The scenario of a base: its own one when it still exists, else the default one.</summary>
    public BackupScenario? For(Guid? id) => id is { } own && Scenarios.FirstOrDefault(x => x.Id == own) is { } found ? found : Default;

    /// <summary>The scenarios a new profile starts with: the original's ready ones (names, descriptions and template of its
    /// installed .bks), the DT one is the default.</summary>
    public static BackupScenarioSet Original() => new()
    {
        DefaultId = DumpId,
        Scenarios =
        [
            new() { Id = DumpId, Name = "Выгрузка данных информационной базы (*.dt)", Description = "Выгрузка данных информационной базы в DT-формате.", Format = BackupFormat.Dt },
            new() { Id = ConfigurationId, Name = "Выгрузка конфигурации информационной базы (*.cf)", Description = "Выгрузка конфигурации информационной базы в CF-формате", Format = BackupFormat.Cf },
            new() { Id = ArchiveId, Name = "Архивирование данных инф. базы в формате 7Zip", Description = "Только для файлового варианта информационной базы", Format = BackupFormat.Zip }
        ]
    };

    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("Неподдерживаемая версия файла сценариев резервирования.");
        if (Scenarios is null || Scenarios.Any(x => x is null)) throw new InvalidDataException("Повреждён список сценариев резервирования.");
        foreach (var scenario in Scenarios)
        {
            try { scenario.Validate(); }
            catch (ArgumentException ex) { throw new InvalidDataException($"Сценарий резервирования «{scenario.Name}»: {ex.Message}", ex); }
        }
        if (Scenarios.Select(x => x.Id).Distinct().Count() != Scenarios.Count) throw new InvalidDataException("Повторяющиеся идентификаторы сценариев резервирования.");
    }
}

/// <summary>The backup scenarios of a profile: «backup-scenarios.json» next to the settings. Before the first save the original's
/// ready scenarios are given (<see cref="BackupScenarioSet.Original"/>) and nothing is written. Unknown fields stay; a file
/// changed by another window after it was read is not overwritten, a damaged one never.</summary>
public sealed class BackupScenarioStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private byte[]? observed;
    private bool hasObserved;

    public BackupScenarioStore(string settingsPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? throw new ArgumentException("Не задан каталог настроек.");
        PathName = Path.Combine(folder, "backup-scenarios.json");
    }

    public string PathName { get; }

    public BackupScenarioSet Load()
    {
        var bytes = File.Exists(PathName) ? File.ReadAllBytes(PathName) : null;
        var result = bytes is null ? BackupScenarioSet.Original() : Parse(bytes);
        observed = bytes; hasObserved = true;
        return result;
    }

    /// <summary>Whether the scenarios were saved already (a profile that never saved them gets the ready ones).</summary>
    public bool Saved => File.Exists(PathName);

    private static BackupScenarioSet Parse(byte[] bytes)
    {
        BackupScenarioSet result;
        try
        {
            result = JsonSerializer.Deserialize<BackupScenarioSet>(bytes, Options) ?? throw new InvalidDataException("Файл сценариев резервирования пуст.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Файл сценариев резервирования содержит некорректный JSON или неизвестные значения.", ex); }
        result.Validate();
        return result;
    }

    /// <summary>Refuses when the file is not as this store last read or wrote it (another window changed it meanwhile).</summary>
    public void EnsureUnchanged()
    {
        var current = File.Exists(PathName) ? File.ReadAllBytes(PathName) : null;
        if (hasObserved && !Same(current, observed))
            throw new IOException("Сценарии резервирования изменены другим окном программы. Откройте их заново и повторите.");
    }

    public void Save(BackupScenarioSet next)
    {
        next.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        using var guard = FilePersistence.Lock(PathName);
        EnsureUnchanged();
        var current = File.Exists(PathName) ? File.ReadAllBytes(PathName) : null;
        // A damaged file is never overwritten, also when it was not read by this store.
        if (current is not null) Parse(current);
        var written = JsonSerializer.SerializeToUtf8Bytes(next, Options);
        FilePersistence.Replace(FilePersistence.Target(PathName), written, current is null ? null : PathName,
            () => { if (current is not null) FilePersistence.Replace(PathName + ".bak", current, PathName); });
        observed = written; hasObserved = true;
    }

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
