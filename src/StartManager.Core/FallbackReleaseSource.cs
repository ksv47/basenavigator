namespace StartManager.Core;

/// <summary>
/// The primary source (HTTPS) first, the fallback (gh) when it fails (user's decision 07.11): no network, a limit of the API,
/// a private repository. A release that came over HTTPS (it has AssetUrls) is downloaded the same way and, if that fails for
/// any reason but the user's cancel, once more through the fallback; a release of the fallback is downloaded by it only.
/// When both fail, one message tells both reasons, the primary first.
/// </summary>
public sealed class FallbackReleaseSource(IReleaseSource primary, IReleaseSource fallback) : IReleaseSource
{
    public ReleaseInfo Latest()
    {
        try { return primary.Latest(); }
        catch (Exception first) when (first is not OperationCanceledException)
        {
            try { return fallback.Latest(); }
            catch (Exception second) when (second is not OperationCanceledException) { throw Both("Не удалось проверить выпуски", first, second); }
        }
    }

    public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory) => Download(release, assets, directory, default);

    public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory, CancellationToken cancel)
    {
        if (release.AssetUrls is null) { fallback.Download(release, assets, directory, cancel); return; }
        try { primary.Download(release, assets, directory, cancel); }
        // The user's cancel never switches to the fallback.
        catch (Exception first) when (!cancel.IsCancellationRequested && first is not OperationCanceledException)
        {
            try { fallback.Download(release, assets, directory, cancel); }
            catch (Exception second) when (!cancel.IsCancellationRequested && second is not OperationCanceledException)
            { throw Both("Не удалось загрузить выпуск", first, second); }
        }
    }

    /// <summary>The combined failure; a gh that is not installed is not a reason of its own, only what a private repository needs.</summary>
    private static InvalidOperationException Both(string what, Exception first, Exception second) =>
        new($"{what} по HTTPS: {first.Message.TrimEnd('.')}. " + (GitHubReleaseSource.IsMissing(second)
            ? "Для закрытого репозитория нужна программа GitHub CLI (gh) с выполненным входом «gh auth login»."
            : $"Через GitHub CLI (gh) тоже не удалось: {second.Message.TrimEnd('.')}."), new AggregateException(first, second));
}
