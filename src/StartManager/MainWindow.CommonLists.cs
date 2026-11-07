using StartManager.Core;

namespace StartManager;

// The original's «Общие списки баз»: the bases of the common lists of 1CEStart are shown with the open list, started and viewed,
// never changed here (decision of the user 11.10.2026). Favorites and the history of such a base are kept under its own list.
public sealed partial class MainWindow
{
    private CommonLoad commonLists = CommonLoad.Empty;

    // A base the open list has got since the load (an import, an added base) wins over a common one with the same ID.
    private IReadOnlyList<CommonBase> CommonShown()
    {
        if (commonLists.Bases.Count == 0) return [];
        var own = library.Bases.Select(x => x.Id).ToHashSet();
        return [.. commonLists.Bases.Where(x => !own.Contains(x.Entry.Id))];
    }

    // The bases of the tree: those of the open list, then those of the common lists.
    private IEnumerable<BaseEntry> ShownBases() => library.Bases.Concat(CommonShown().Select(x => x.Entry));

    // By the object itself: a record of the open list may be equal to a common one field by field.
    private CommonBase? CommonOf(BaseEntry? entry) =>
        entry is null ? null : commonLists.Bases.FirstOrDefault(x => ReferenceEquals(x.Entry, entry));

    private static string CommonReadOnly(CommonBase common) =>
        $"База из общего списка {common.ListPath}: её можно запускать и смотреть, а изменить — только в самом списке.";

    // A change of a common base: the reason goes to the status line and nothing opens.
    private bool CommonLocked(BaseEntry? entry)
    {
        if (CommonOf(entry) is not { } common) return false;
        status.Text = CommonReadOnly(common); return true;
    }

    // A group only common bases are in: the open list has no such group to rename or remove.
    private bool CommonGroupLocked(string group)
    {
        if (group.Length == 0 || Groups.All(library).Contains(group, StringComparer.OrdinalIgnoreCase)) return false;
        status.Text = $"Группа «{group}» есть только в общих списках: изменить её можно в самих списках."; return true;
    }

    // Favorites and history are kept per list: a common base under the source of its own file.
    private string SourceOf(BaseEntry entry) =>
        CommonOf(entry) is { } common ? ActivityStore.Source(store.PathName, common.ListPath) : ActivitySource;

    private static CommonLoad LoadCommonLists(Library loaded, string settingsPath) =>
        loaded.UseCommonLists ? CommonLists.Load(CommonLists.Discover(), loaded.SkipReadOnlyCommonLists, loaded, settingsPath) : CommonLoad.Empty;

    /// <summary>«Дополнительные параметры» of a base of a common list: the list is never written, the string is kept in the settings of
    /// the program under the source of that list (<see cref="CommonLists.WithParameters"/>; null: the string of the list applies).
    /// Returns the error or null.</summary>
    private async Task<string?> SaveCommonParameters(CommonBase common, string? parameters)
    {
        if (loadFailed || reading || closed) return "Список занят или не загружен.";
        Library next;
        try { next = CommonLists.WithParameters(library, store.PathName, common, parameters); }
        catch (InvalidOperationException ex) { return ex.Message; }
        if (ReferenceEquals(next, library)) return null;
        var failedBefore = loadFailed;
        BeginWriting("Сохранение локальных настроек…");
        try
        {
            var settings = store; var profile = ProfileToSave(next);
            await Task.Run(() => settings.Save(profile));
            library = next;
            var updated = common with { Entry = common.Entry with { AdditionalParameters = parameters } };
            commonLists = commonLists with { Bases = [.. commonLists.Bases.Select(x => ReferenceEquals(x, common) ? updated : x)] };
            AppLog.Info("common.parameters.saved");
            status.Text = "Дополнительные параметры базы общего списка сохранены в настройках программы.";
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("common.parameters.save.failed", ex);
            return "Не удалось сохранить: " + LegacyImport.ErrorMessage(ex);
        }
        finally { EndWriting(failedBefore); }
    }

    // A base by the source and ID of a history record: the open list, or a common list by the key of its file. A common record is
    // found also when the open list has a base with its ID (the tree then shows that one): the record names its own list.
    private BaseEntry? BaseOf(string source, Guid id) => source == ActivitySource ? library.Bases.FirstOrDefault(x => x.Id == id)
        : commonLists.Bases.FirstOrDefault(x => x.Entry.Id == id && ActivityStore.Source(store.PathName, x.ListPath) == source)?.Entry;

    // The keys of the lists whose launches the history shows: the open list and the common lists loaded now.
    private HashSet<string> HistorySources() =>
        [ActivitySource, .. commonLists.Lists.Where(x => x.Problem is null).Select(x => ActivityStore.Source(store.PathName, x.Path))];

    private string CommonDescription()
    {
        if (!library.UseCommonLists) return "";
        var shown = CommonShown().Count;
        var failed = commonLists.Lists.Count(x => x.Problem is not null);
        return $" · общие списки: {commonLists.Lists.Count(x => x.Problem is null)}, баз: {shown}" + (failed > 0 ? $", не загружено: {failed}" : "");
    }
}
