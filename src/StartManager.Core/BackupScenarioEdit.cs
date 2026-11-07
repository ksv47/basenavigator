namespace StartManager.Core;

/// <summary>A problem the window «Сценарий резервирования» shows before saving: the section of the window (0 «Описание сценария»,
/// 1 «Шаблон имени архива», 2 «Место назначения», 3 «Параметры выгрузки»), the message and the field it is about (focused; changing
/// it clears the message).</summary>
public sealed record BackupScenarioProblem(int Section, string Message, BackupScenarioField Field);

/// <summary>The fields of the window «Сценарий резервирования» a problem can be about.</summary>
public enum BackupScenarioField { Name, Description, Template, Folder1, Folder2, Folder3, Format, VolumeSize, KeepCopies }

/// <summary>What the scenario window and the settings section «Сценарии резервирования» do with scenarios
/// (the original's FormBackupScripts, _Unit186.pas:16707-17892, and TsmFormOptions, _Unit205.pas:10613-10960).</summary>
public static class BackupScenarioEdit
{
    /// <summary>The name of a new scenario, as the original's FormInit gives it.</summary>
    public const string NewName = "Новый сценарий";

    /// <summary>The first item of «Сценарий:» of the base's page «Резервное копирование».</summary>
    public const string DefaultChoice = "<Использовать сценарий по умолчанию>";

    /// <summary>A new scenario: the original's name, the defaults of <see cref="BackupScenario"/> (DT, the default template, «1. Каталог»
    /// on and empty — the default folder) and a new ID. The description stays empty: it follows the name.</summary>
    public static BackupScenario New() => new() { Id = Guid.NewGuid(), Name = NewName };

    /// <summary>«Краткое описание» while the name is typed: an empty description, or one that still repeats the name, follows it
    /// (the original fills an empty one with the name, edtScriptNameChange); a description of its own stays.</summary>
    public static string FollowName(string description, string previousName, string name) =>
        description.Length == 0 || description == previousName ? name : description;

    /// <summary>The characters of a file name template the original refuses («Шаблон имени файла содержит некорректные символы!»,
    /// TPath.HasValidFileNameChars): those of Windows on every system, as <see cref="BackupName"/> removes them; each once.</summary>
    public static string InvalidTemplateCharacters(string template) =>
        new([.. template.Where(x => x < ' ' || "\"*/:<>?\\|".Contains(x)).Distinct()]);

    /// <summary>The original's message for a template with such characters, naming them; null when there are none.</summary>
    public static string? TemplateMessage(string template) => InvalidTemplateCharacters(template) is { Length: > 0 } wrong
        ? $"Шаблон имени файла содержит некорректные символы! ( {string.Join(" ", wrong.Select(x => x < ' ' ? "\\x" + ((int)x).ToString("X2", System.Globalization.CultureInfo.InvariantCulture) : x.ToString()))} )"
        : null;

    /// <summary>Whether a folder has a character the original refuses («Каталог содержит некорректные символы!», TPath.HasValidPathChars):
    /// control characters anywhere; on Windows also «"», «&lt;», «&gt;», «|», «*», «?». A Linux folder may have them.</summary>
    public static bool InvalidFolder(string folder, bool windows) =>
        folder.Any(x => x < ' ' || windows && "\"<>|*?".Contains(x));

    /// <summary>The first problem of a scenario as the window would save it, or null. The window shows it in the section it is in and
    /// focuses its field.</summary>
    public static BackupScenarioProblem? Problem(BackupScenario scenario, bool windows)
    {
        if (string.IsNullOrWhiteSpace(scenario.Name)) return new(0, "Введите наименование сценария.", BackupScenarioField.Name);
        if (scenario.Name.Any(char.IsControl)) return new(0, "Наименование сценария не должно содержать управляющие символы.", BackupScenarioField.Name);
        if (scenario.Description.Any(char.IsControl)) return new(0, "Краткое описание не должно содержать переносы строк и управляющие символы.", BackupScenarioField.Description);
        if (string.IsNullOrWhiteSpace(scenario.Template)) return new(1, "Укажите шаблон имени файла архива.", BackupScenarioField.Template);
        if (TemplateMessage(scenario.Template) is { } wrong) return new(1, wrong, BackupScenarioField.Template);
        // «1. Каталог» counts whatever its flag (the default folder takes it, as in the original); «2.» and «3.» only while active, so the
        // text of a disabled one is kept and never blocks saving (Grok audit, part 3).
        // Control characters the model refuses in every folder, active or not: named with their folder, never as the name (Codex recheck, part 3).
        foreach (var (folder, caption, field) in new[] { (scenario.Folder1, "1. Каталог", BackupScenarioField.Folder1),
            (scenario.Folder2, "2. Каталог", BackupScenarioField.Folder2), (scenario.Folder3, "3. Каталог", BackupScenarioField.Folder3) })
            if (folder.Any(char.IsControl)) return new(2, $"{caption}: каталог не должен содержать переносы строк и управляющие символы.", field);
        foreach (var (folder, used, caption, field) in new[] { (scenario.Folder1, true, "1. Каталог", BackupScenarioField.Folder1),
            (scenario.Folder2, scenario.CopiesToFolder2, "2. Каталог", BackupScenarioField.Folder2), (scenario.Folder3, scenario.CopiesToFolder3, "3. Каталог", BackupScenarioField.Folder3) })
        {
            if (!used) continue;
            if (InvalidFolder(folder, windows)) return new(2, $"{caption}: каталог содержит некорректные символы!", field);
            if (field != BackupScenarioField.Folder1 && folder.Trim().Length == 0) return new(2, $"{caption}: укажите каталог или снимите флажок.", field);
        }
        // The archive settings apply to a ZIP only; for DT and CF they are not checked here (Normalize keeps them valid).
        if (scenario.Format == BackupFormat.Zip && scenario.Split == BackupSplit.Custom && scenario.VolumeSize is < 1 or > BackupScenario.MaxVolumeSize)
            return new(3, $"Размер тома: от 1 до {BackupScenario.MaxVolumeSize} МБ.", BackupScenarioField.VolumeSize);
        if (scenario.KeepCopies is < 0 or > BackupScenario.MaxKeepCopies)
            return new(3, $"Хранить копий: от 0 (все) до {BackupScenario.MaxKeepCopies}.", BackupScenarioField.KeepCopies);
        // The model's own check, with the inactive archive settings of a DT/CF left out (Normalize keeps them valid when saving).
        try { Normalize(scenario, scenario with { Compression = BackupCompression.Normal, Split = BackupSplit.None, VolumeSize = 0 }).Validate(); }
        catch (ArgumentException ex) { var field = ModelField(scenario); return new(Section(field), ex.Message, field); }
        return null;
    }

    /// <summary>The field a refusal of the model's own check is about, so it is shown in its section (Codex recheck, part 3).</summary>
    private static BackupScenarioField ModelField(BackupScenario scenario)
    {
        if (scenario.Name is null || scenario.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(scenario.Name)) return BackupScenarioField.Name;
        if (scenario.Description is null || scenario.Description.Any(char.IsControl)) return BackupScenarioField.Description;
        if (scenario.Template is null || scenario.Template.Any(char.IsControl) || string.IsNullOrWhiteSpace(scenario.Template)) return BackupScenarioField.Template;
        if (scenario.Folder1 is null || scenario.Folder1.Any(char.IsControl)) return BackupScenarioField.Folder1;
        if (scenario.Folder2 is null || scenario.Folder2.Any(char.IsControl)) return BackupScenarioField.Folder2;
        if (scenario.Folder3 is null || scenario.Folder3.Any(char.IsControl)) return BackupScenarioField.Folder3;
        if (!Enum.IsDefined(scenario.Format)) return BackupScenarioField.Format;
        if (!Enum.IsDefined(scenario.Compression) || !Enum.IsDefined(scenario.Split) || scenario.VolumeSize is < 0 or > BackupScenario.MaxVolumeSize ||
            scenario.Split == BackupSplit.Custom && scenario.VolumeSize < 1) return BackupScenarioField.VolumeSize;
        if (scenario.KeepCopies is < 0 or > BackupScenario.MaxKeepCopies) return BackupScenarioField.KeepCopies;
        return BackupScenarioField.Name;
    }

    /// <summary>The section of the window a field is in.</summary>
    public static int Section(BackupScenarioField field) => field switch
    {
        BackupScenarioField.Name or BackupScenarioField.Description => 0, BackupScenarioField.Template => 1,
        BackupScenarioField.Folder1 or BackupScenarioField.Folder2 or BackupScenarioField.Folder3 => 2, _ => 3
    };

    /// <summary>Text as the window keeps it: line breaks, tabs and other control characters removed as they are typed or pasted
    /// (the model refuses them in every field; Codex recheck, part 3).</summary>
    public static string WithoutControls(string text) => text.Any(char.IsControl) ? new string([.. text.Where(x => !char.IsControl(x))]) : text;

    /// <summary>The number fields of the window before they are converted: «Хранить копий» always, «Размер тома» when it applies (a ZIP
    /// split by «Определить»). An empty, fractional or out-of-range value is refused instead of being truncated or clamped into another
    /// policy (Codex and Grok audits, part 3).</summary>
    public static BackupScenarioProblem? NumberProblem(decimal? keepCopies, decimal? volumeSize, bool volumeUsed)
    {
        static bool Whole(decimal? value) => value is { } number && number == decimal.Truncate(number);
        if (!Whole(keepCopies) || keepCopies is < 0 or > BackupScenario.MaxKeepCopies)
            return new(3, $"Хранить копий: укажите целое число от 0 (все) до {BackupScenario.MaxKeepCopies}.", BackupScenarioField.KeepCopies);
        if (volumeUsed && (!Whole(volumeSize) || volumeSize is < 1 or > BackupScenario.MaxVolumeSize))
            return new(3, $"Размер тома: укажите целое число от 1 до {BackupScenario.MaxVolumeSize} МБ.", BackupScenarioField.VolumeSize);
        return null;
    }

    /// <summary>A DT or CF scenario whose inactive archive settings are not valid (a «Определить» size left at 0 before switching from ZIP)
    /// keeps the archive settings it was saved with, so they never block saving it (Codex audit, part 3). A ZIP scenario is as given.</summary>
    public static BackupScenario Normalize(BackupScenario scenario, BackupScenario saved)
    {
        if (scenario.Format == BackupFormat.Zip) return scenario;
        var valid = Enum.IsDefined(scenario.Compression) && Enum.IsDefined(scenario.Split) && scenario.VolumeSize is >= 0 and <= BackupScenario.MaxVolumeSize &&
            (scenario.Split != BackupSplit.Custom || scenario.VolumeSize >= 1);
        return valid ? scenario : scenario with { Compression = saved.Compression, Split = saved.Split, VolumeSize = saved.VolumeSize };
    }

    /// <summary>«Формат:» under the original's list: the mask of the files the scenario makes.</summary>
    public static string FormatText(BackupFormat format) => format switch
    {
        BackupFormat.Dt => "*.DT", BackupFormat.Cf => "*.CF", BackupFormat.Zip => "*.ZIP", _ => "<Неизвестный формат>"
    };

    /// <summary>The set with the scenario added at the end or put in place of the one with its ID; the default stays.</summary>
    public static BackupScenarioSet Put(BackupScenarioSet set, BackupScenario scenario)
    {
        var scenarios = set.Scenarios.ToList();
        var at = scenarios.FindIndex(x => x.Id == scenario.Id);
        // Unknown fields of the stored scenario stay when the window gives one without them.
        if (at >= 0) scenarios[at] = scenario with { Extra = scenario.Extra ?? scenarios[at].Extra };
        else scenarios.Add(scenario);
        return set with { Scenarios = scenarios };
    }

    /// <summary>The set without a scenario. The last one is not removed: a backup needs a scenario. When the default one goes, the
    /// first of the rest becomes the default (as <see cref="BackupScenarioSet.Default"/> would take it anyway), so the mark in the
    /// list and the stored default agree.</summary>
    public static BackupScenarioSet Remove(BackupScenarioSet set, Guid id)
    {
        if (set.Scenarios.All(x => x.Id != id)) throw new InvalidOperationException("Сценарий уже удалён. Нажмите «Обновить».");
        if (set.Scenarios.Count == 1) throw new InvalidOperationException("Нельзя удалить единственный сценарий резервирования: выгрузке нужен хотя бы один.");
        var defaultId = set.Default?.Id;
        var rest = set.Scenarios.Where(x => x.Id != id).ToList();
        return set with { Scenarios = rest, DefaultId = defaultId == id || defaultId is null ? rest[0].Id : defaultId.Value };
    }

    /// <summary>«Назначить основным».</summary>
    public static BackupScenarioSet WithDefault(BackupScenarioSet set, Guid id) =>
        set.Scenarios.Any(x => x.Id == id) ? set with { DefaultId = id } : throw new InvalidOperationException("Сценарий уже удалён. Нажмите «Обновить».");

    /// <summary>The items of «Сценарий:» of the base's page: «&lt;Использовать сценарий по умолчанию&gt;» (null), then the scenarios; a
    /// scenario of the base that is gone keeps its ID under a note, so opening and saving the page does not change it.</summary>
    public static IReadOnlyList<(Guid? Id, string Text)> Choices(IReadOnlyList<BackupScenario> scenarios, Guid? current)
    {
        var result = new List<(Guid? Id, string Text)> { (null, DefaultChoice) };
        result.AddRange(scenarios.Select(x => ((Guid?)x.Id, x.Name)));
        if (current is { } own && scenarios.All(x => x.Id != own)) result.Add((own, "<Сценарий не найден — используется сценарий по умолчанию>"));
        return result;
    }
}
