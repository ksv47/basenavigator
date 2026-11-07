using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace StartManager.Core;

/// <summary>
/// Reads releases of a public repository through the GitHub REST API over HTTPS, without gh and without a login (user's
/// decision 07.11). The proxy is the default of HttpClient: the system one on Windows, HTTPS_PROXY on Linux. Only HTTPS is
/// followed (also on redirects); a file never grows past the size the API told.
/// </summary>
/// <param name="handler">The HTTP handler; tests substitute one, the application shares one client per process.</param>
public sealed class GitHubHttpReleaseSource(string repository = GitHubReleaseSource.DefaultRepository, HttpMessageHandler? handler = null) : IReleaseSource
{
    public string Repository { get; } = repository;

    private readonly HttpClient client = handler is null ? Shared : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    // Redirects are followed here, not by the handler, so every hop is checked to be HTTPS.
    private static readonly HttpClient Shared = new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly string Agent = "BaseNavigator/" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0");

    /// <summary>The whole answer of the API.</summary>
    internal TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>No data of a file for this long ends its download. There is no limit of the whole download: a slow but
    /// alive line finishes (user's decision 07.11; the live check took 504 s for 42.6 MB); the user's cancel stops it.</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>The limit of a file whose size the API did not tell (1 GiB; tests lower it).</summary>
    internal long UnknownSizeLimit { get; init; } = 1L << 30;
    private const int MaxRedirects = 5;

    public ReleaseInfo Latest() => Run(async () =>
    {
        using var limit = new CancellationTokenSource(AnswerTimeout);
        try
        {
            using var response = await Send(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), "application/vnd.github+json", limit.Token);
            EnsureSuccess(response);
            return ParseApi(await response.Content.ReadAsStringAsync(limit.Token));
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested) { throw NoAnswer(); }
        catch (HttpRequestException ex) { throw NoConnection(ex); }
    });

    /// <summary>The answer of GET /repos/{owner}/{repo}/releases/latest: tag_name, body and assets (name, size,
    /// browser_download_url, digest «sha256:…»); an asset field that is missing or of another form stays unknown.</summary>
    public static ReleaseInfo ParseApi(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagName) && tagName.ValueKind == JsonValueKind.String ? tagName.GetString() ?? "" : "";
        var version = GitHubReleaseSource.TagVersion(tag);
        Dictionary<string, long>? sizes = null;
        Dictionary<string, string>? urls = null, digests = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") is not { Length: > 0 } name) continue;
                if (asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) && bytes > 0)
                    (sizes ??= new(StringComparer.Ordinal))[name] = bytes;
                if (Text(asset, "browser_download_url") is { Length: > 0 } url) (urls ??= new(StringComparer.Ordinal))[name] = url;
                if (Text(asset, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.Ordinal) && digest.Length == 71 &&
                    digest[7..].All(Uri.IsHexDigit))
                    (digests ??= new(StringComparer.Ordinal))[name] = digest[7..].ToLowerInvariant();
            }
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
        return new(version, tag, notes, sizes, urls ?? new Dictionary<string, string>(StringComparer.Ordinal), digests);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory) => Download(release, assets, directory, default);

    /// <summary>Each file is written to &lt;name&gt;.part and renamed when complete; a failed or cancelled one removes its .part
    /// (the caller removes the folder anyway).</summary>
    public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        foreach (var asset in assets)
        {
            if (release.AssetUrls?.GetValueOrDefault(asset) is not { } url) throw new InvalidDataException($"В выпуске нет файла {asset}.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Адрес файла {asset} не ведёт на https://github.com; загрузка отменена.");
            var target = Path.Combine(directory, asset);
            var part = target + ".part";
            long? size = release.AssetSizes?.GetValueOrDefault(asset) is > 0 and var told ? told : null;
            try
            {
                Run(() => Fetch(uri, part, size, cancel));
                File.Move(part, target, true);
            }
            catch
            {
                try { File.Delete(part); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    private async Task<bool> Fetch(Uri uri, string part, long? size, CancellationToken cancel)
    {
        var name = Path.GetFileNameWithoutExtension(part);
        var limit = size ?? UnknownSizeLimit;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await Send(uri, "application/octet-stream", stall.Token);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength > limit) throw TooLarge(name);
            await using var input = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.Read);
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = await input.ReadAsync(buffer, stall.Token);
                if (read == 0) break;
                received += read;
                if (received > limit) throw TooLarge(name);
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            }
            if (size is { } expected && received != expected)
                throw new InvalidDataException($"Файл {name} загружен не полностью ({received} из {expected} байт). Повторите позже.");
            return true;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested && stall.IsCancellationRequested) { throw NoAnswer(); }
        catch (HttpRequestException ex) { throw NoConnection(ex); }
        catch (HttpIOException ex) { throw NoConnection(ex); }
    }

    private static InvalidDataException TooLarge(string name) =>
        new($"Файл {name} больше, чем указано в выпуске; загрузка прервана.");

    /// <summary>A GET following at most a few redirects, every one of them to HTTPS.</summary>
    private async Task<HttpResponseMessage> Send(Uri uri, string accept, CancellationToken cancel)
    {
        for (var hop = 0; ; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"GitHub перенаправил запрос не по HTTPS ({uri.Scheme}://{uri.Host}); загрузка отменена.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            request.Headers.UserAgent.ParseAdd(Agent);
            if (accept != "application/octet-stream") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location) return response;
            response.Dispose();
            if (hop >= MaxRedirects) throw new InvalidDataException("GitHub перенаправил запрос слишком много раз; загрузка отменена.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.NotFound => $"GitHub не нашёл выпуск в репозитории {Repository} (HTTP 404): репозитория нет, он закрытый или в нём нет выпусков.",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                $"GitHub отклонил запрос без входа (HTTP {code}): вероятно, исчерпан лимит запросов. Повторите позже.",
            _ => $"GitHub вернул ошибку HTTP {code}. Повторите позже.",
        });
    }

    private static TimeoutException NoAnswer() => new("GitHub не ответил вовремя. Проверьте подключение и повторите.");

    private static InvalidOperationException NoConnection(Exception error) =>
        new($"Нет связи с GitHub по HTTPS: {error.Message.TrimEnd('.')}. Проверьте подключение и прокси.", error);

    /// <summary>The asynchronous call made synchronous for IReleaseSource; callers run on a worker thread or the console.</summary>
    private static T Run<T>(Func<Task<T>> call) => Task.Run(call).GetAwaiter().GetResult();
}
