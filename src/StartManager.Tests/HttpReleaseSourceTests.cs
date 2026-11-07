using System.Net;
using System.Security.Cryptography;
using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// The update without gh (user's decision 07.11): GitHubHttpReleaseSource over a substituted HttpMessageHandler, and
// FallbackReleaseSource with a substituted gh.
public sealed class HttpReleaseSourceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-http-" + Guid.NewGuid().ToString("N"));
    private const string Api = "https://api.github.com/repos/owner/repo/releases/latest";
    private const string Url = "https://github.com/owner/repo/releases/download/v0.2.0.21/";

    public HttpReleaseSourceTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    /// <summary>Answers every request with the given function; records the requests.</summary>
    internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return answer(request, cancellationToken);
        }

        public static FakeHandler Of(Func<HttpRequestMessage, HttpResponseMessage> answer) => new((r, _) => Task.FromResult(answer(r)));
    }

    /// <summary>A body that gives its first part, then waits (no data) until the request is cancelled.</summary>
    private sealed class Trickle(byte[] first) : Stream
    {
        private bool given;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!given) { given = true; first.CopyTo(buffer); return first.Length; }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body of count parts of size bytes, each after a pause.</summary>
    private sealed class Drip(int count, int size, int pause) : Stream
    {
        private int given;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (given == count) return 0;
            await Task.Delay(pause, cancellationToken);
            given++;
            buffer.Span[..size].Fill(1);
            return size;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that gives its first part, then breaks as a dropped connection.</summary>
    private sealed class Broken(byte[] first) : MemoryStream(first)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        }
    }

    /// <summary>A body whose length is not told in advance.</summary>
    private sealed class Unsized(byte[] body) : MemoryStream(body)
    {
        public override bool CanSeek => false;
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static HttpResponseMessage Ok(string body) => Ok(Encoding.UTF8.GetBytes(body));
    private static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found) => new(code) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private static ReleaseInfo Release(params (string Name, long Size)[] assets) => new(new(0, 2, 0, 21), "v0.2.0.21", "",
        assets.ToDictionary(x => x.Name, x => x.Size), assets.ToDictionary(x => x.Name, x => Url + x.Name));

    private string[] Left() => Directory.GetFiles(folder).Select(Path.GetFileName).OfType<string>().ToArray();

    [Fact] public void TheApiAnswerIsRead()
    {
        var digest = new string('A', 64);
        var release = GitHubHttpReleaseSource.ParseApi("{\"tag_name\":\"v0.4.0.2\",\"body\":\"notes\",\"assets\":[" +
            "{\"name\":\"a.zip\",\"size\":50266112,\"browser_download_url\":\"" + Url + "a.zip\",\"digest\":\"sha256:" + digest + "\"}," +
            "{\"name\":\"SHA256SUMS.txt\",\"size\":180,\"browser_download_url\":\"" + Url + "SHA256SUMS.txt\",\"digest\":null}," +
            "{\"name\":\"md5.zip\",\"size\":\"1\",\"browser_download_url\":7,\"digest\":\"md5:00\"},{\"name\":\"short.zip\",\"digest\":\"sha256:abc\"}," +
            "{\"name\":\"nothex.zip\",\"digest\":\"sha256:" + new string('g', 64) + "\"},{\"size\":5},{\"name\":\"\"},7]}");
        Assert.Equal(new Version(0, 4, 0, 2), release.Version);
        Assert.Equal("v0.4.0.2", release.Tag);
        Assert.Equal("notes", release.Notes);
        Assert.Equal(new Dictionary<string, long> { ["a.zip"] = 50266112, ["SHA256SUMS.txt"] = 180 }, release.AssetSizes);
        Assert.Equal(new Dictionary<string, string> { ["a.zip"] = Url + "a.zip", ["SHA256SUMS.txt"] = Url + "SHA256SUMS.txt" }, release.AssetUrls);
        Assert.Equal(new Dictionary<string, string> { ["a.zip"] = digest.ToLowerInvariant() }, release.AssetDigests);
        // No assets: still an HTTPS release (its files are asked for over HTTPS and found missing).
        var bare = GitHubHttpReleaseSource.ParseApi("{\"tag_name\":\"v1.0\",\"body\":null}");
        Assert.Empty(bare.AssetUrls!); Assert.Null(bare.AssetSizes); Assert.Null(bare.AssetDigests); Assert.Equal("", bare.Notes);
        Assert.Contains("Непонятный номер", Assert.Throws<InvalidDataException>(() => GitHubHttpReleaseSource.ParseApi("{\"tag_name\":\"1.0\"}")).Message);
        Assert.Throws<InvalidDataException>(() => GitHubHttpReleaseSource.ParseApi("{\"name\":\"v1.0\"}"));
    }

    [Fact] public void LatestAsksTheApiOfTheRepository()
    {
        var handler = FakeHandler.Of(_ => Ok("{\"tag_name\":\"v0.4.0.2\",\"body\":\"\",\"assets\":[]}"));
        var release = new GitHubHttpReleaseSource("owner/repo", handler).Latest();
        Assert.Equal(new Version(0, 4, 0, 2), release.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Api, request.RequestUri!.ToString());
        Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.StartsWith("BaseNavigator/", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.Forbidden, "лимит запросов")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
    [InlineData(HttpStatusCode.InternalServerError, "ошибку HTTP 500")]
    public void AFailedAnswerIsExplained(HttpStatusCode code, string text)
    {
        var source = new GitHubHttpReleaseSource("owner/repo", FakeHandler.Of(_ => new HttpResponseMessage(code)));
        var error = Assert.Throws<InvalidOperationException>(source.Latest);
        Assert.Contains(text, error.Message);
        if (code == HttpStatusCode.NotFound) Assert.Contains("owner/repo", error.Message);
    }

    [Fact] public void NoConnectionAndNoAnswerAreExplained()
    {
        var offline = new GitHubHttpReleaseSource("owner/repo", FakeHandler.Of(_ => throw new HttpRequestException("No such host is known.")));
        Assert.Contains("Нет связи с GitHub по HTTPS: No such host is known.", Assert.Throws<InvalidOperationException>(offline.Latest).Message);
        var silent = new GitHubHttpReleaseSource("owner/repo", new FakeHandler(async (_, cancel) => { await Task.Delay(Timeout.Infinite, cancel); return null!; }))
        { AnswerTimeout = TimeSpan.FromMilliseconds(200) };
        Assert.Contains("не ответил вовремя", Assert.Throws<TimeoutException>(silent.Latest).Message);
    }

    [Fact] public void FilesFollowHttpsRedirectsAndAreWrittenWhole()
    {
        var bytes = RandomNumberGenerator.GetBytes(200_000);
        var handler = FakeHandler.Of(r => r.RequestUri!.Host switch
        {
            "github.com" => Redirect("https://objects.githubusercontent.com/" + r.RequestUri.Segments[^1]),
            _ when r.RequestUri.AbsolutePath == "/a.zip" => Redirect("/b/a.zip", HttpStatusCode.TemporaryRedirect),  // relative
            _ => Ok(r.RequestUri.AbsolutePath == "/b/a.zip" ? bytes : "sums"u8.ToArray()),
        });
        new GitHubHttpReleaseSource("owner/repo", handler).Download(Release(("a.zip", bytes.Length), ("SHA256SUMS.txt", 4)), ["a.zip", "SHA256SUMS.txt"], folder);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder, "a.zip")));
        Assert.Equal("sums", File.ReadAllText(Path.Combine(folder, "SHA256SUMS.txt")));
        Assert.Equal(["SHA256SUMS.txt", "a.zip"], Left().Order(StringComparer.Ordinal));
        Assert.Equal("application/octet-stream", Assert.Single(handler.Requests[0].Headers.Accept).MediaType);
        Assert.Equal("https://objects.githubusercontent.com/b/a.zip", handler.Requests[2].RequestUri!.ToString());
    }

    [Fact] public void AFileOfUnknownSizeIsTakenAsItComesUpToTheCeiling()
    {
        var release = Release(("a.zip", 1)) with { AssetSizes = null };
        Assert.Equal(1L << 30, new GitHubHttpReleaseSource().UnknownSizeLimit);
        new GitHubHttpReleaseSource("owner/repo", FakeHandler.Of(_ => Ok("abc"))).Download(release, ["a.zip"], folder);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(folder, "a.zip")));
        File.Delete(Path.Combine(folder, "a.zip"));
        // Past the ceiling: told by Content-Length at once, or found while reading a body of no told length.
        foreach (var length in new[] { true, false })
        {
            var handler = FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = length ? new ByteArrayContent(new byte[20]) : new StreamContent(new Unsized(new byte[20])) });
            var source = new GitHubHttpReleaseSource("o/r", handler) { UnknownSizeLimit = 10 };
            Assert.Contains("больше, чем указано", Assert.Throws<InvalidDataException>(() => source.Download(release, ["a.zip"], folder)).Message);
            Assert.Empty(Left());
        }
        // At the ceiling exactly: taken.
        new GitHubHttpReleaseSource("o/r", FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Unsized(new byte[10])) }))
        { UnknownSizeLimit = 10 }.Download(release, ["a.zip"], folder);
        Assert.Equal(10, new FileInfo(Path.Combine(folder, "a.zip")).Length);
    }

    [Theory]
    [InlineData("http://objects.githubusercontent.com/a.zip", "не по HTTPS")]
    [InlineData("loop", "слишком много раз")]
    public void ARedirectAwayFromHttpsIsRefused(string to, string text)
    {
        var source = new GitHubHttpReleaseSource("owner/repo", FakeHandler.Of(r => Redirect(to == "loop" ? r.RequestUri!.ToString() : to)));
        var error = Assert.Throws<InvalidDataException>(() => source.Download(Release(("a.zip", 3)), ["a.zip"], folder));
        Assert.Contains(text, error.Message);
        Assert.Empty(Left());
    }

    [Theory]
    [InlineData("http://github.com/owner/repo/a.zip")]
    [InlineData("https://example.com/a.zip")]
    [InlineData("not a url")]
    public void AnAddressOutsideGitHubIsRefused(string url)
    {
        var handler = FakeHandler.Of(_ => Ok("abc"));
        var release = Release(("a.zip", 3)) with { AssetUrls = new Dictionary<string, string> { ["a.zip"] = url } };
        Assert.Contains("https://github.com", Assert.Throws<InvalidDataException>(() => new GitHubHttpReleaseSource("o/r", handler).Download(release, ["a.zip"], folder)).Message);
        Assert.Empty(handler.Requests);
        Assert.Contains("В выпуске нет файла b.zip", Assert.Throws<InvalidDataException>(() => new GitHubHttpReleaseSource("o/r", handler).Download(release, ["b.zip"], folder)).Message);
    }

    [Theory]
    [InlineData(false, 3, "больше, чем указано")]  // the body runs past the size
    [InlineData(true, 3, "больше, чем указано")]   // Content-Length says so at once
    [InlineData(false, 10, "не полностью")]
    public void AFileOfAnotherSizeIsRefused(bool length, long size, string text)
    {
        var handler = FakeHandler.Of(_ =>
        {
            var content = length ? (HttpContent)new ByteArrayContent(new byte[5]) : new StreamContent(new Unsized(new byte[5]));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var error = Assert.Throws<InvalidDataException>(() => new GitHubHttpReleaseSource("o/r", handler).Download(Release(("a.zip", size)), ["a.zip"], folder));
        Assert.Contains(text, error.Message);
        Assert.Empty(Left());
    }

    [Fact] public void ADroppedConnectionIsExplainedAndLeavesNothing()
    {
        var source = new GitHubHttpReleaseSource("o/r", FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Broken(new byte[3])) }));
        Assert.Contains("Нет связи с GitHub по HTTPS", Assert.Throws<InvalidOperationException>(() => source.Download(Release(("a.zip", 10)), ["a.zip"], folder)).Message);
        Assert.Empty(Left());
        var refused = new GitHubHttpReleaseSource("o/r", FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Contains("HTTP 404", Assert.Throws<InvalidOperationException>(() => refused.Download(Release(("a.zip", 10)), ["a.zip"], folder)).Message);
    }

    [Fact] public void CancelMidwayStopsAndLeavesNoFile()
    {
        var source = new GitHubHttpReleaseSource("o/r", FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Trickle(new byte[100])) }));
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(300);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => source.Download(Release(("a.zip", 1000)), ["a.zip"], folder, cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.Empty(Left());
        // Already cancelled: nothing is asked.
        var handler = FakeHandler.Of(_ => Ok("x"));
        Assert.ThrowsAny<OperationCanceledException>(() => new GitHubHttpReleaseSource("o/r", handler).Download(Release(("a.zip", 1)), ["a.zip"], folder, cancel.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact] public void AStalledDownloadEndsASlowOneFinishes()
    {
        HttpResponseMessage Slow(HttpRequestMessage _) => new(HttpStatusCode.OK) { Content = new StreamContent(new Trickle(new byte[100])) };
        var stalled = new GitHubHttpReleaseSource("o/r", FakeHandler.Of(Slow)) { StallTimeout = TimeSpan.FromMilliseconds(300) };
        Assert.Contains("не ответил вовремя", Assert.Throws<TimeoutException>(() => stalled.Download(Release(("a.zip", 1000)), ["a.zip"], folder)).Message);
        Assert.Empty(Left());
        // Slow but alive: a part every 50 ms against a stall limit of 2 s (40 times the pause), 4 s in all — twice the stall
        // limit — it finishes (no limit of the whole download).
        var alive = new GitHubHttpReleaseSource("o/r", FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Drip(80, 10, 50)) }))
        { StallTimeout = TimeSpan.FromSeconds(2) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        alive.Download(Release(("a.zip", 800)), ["a.zip"], folder);
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
        Assert.Equal(800, new FileInfo(Path.Combine(folder, "a.zip")).Length);
    }

    // FallbackReleaseSource. A source that counts its calls and answers as told.
    private sealed class Counted(Func<ReleaseInfo>? latest = null, Action<CancellationToken>? download = null) : IReleaseSource
    {
        public int Latests, Downloads;
        public ReleaseInfo Latest() { Latests++; return (latest ?? throw new InvalidOperationException("no latest"))(); }
        public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory) => Download(release, assets, directory, default);
        public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory, CancellationToken cancel)
        { Downloads++; (download ?? (_ => { }))(cancel); }
    }

    /// <summary>A gh that records its calls; view prints json (null — fails with «boom»), download exits with the code.</summary>
    private string FakeGh(string? json, int download = 0)
    {
        var calls = Path.Combine(folder, "gh-calls.txt");
        var answer = Path.Combine(folder, "gh-answer.json");
        if (json is not null) File.WriteAllText(answer, json);
        var gh = Path.Combine(folder, OperatingSystem.IsWindows() ? "gh.cmd" : "gh");
        File.WriteAllText(gh, OperatingSystem.IsWindows()
            ? $"@echo off\r\n>>\"{calls}\" echo %*\r\nif \"%2\"==\"view\" goto view\r\nexit /b {download}\r\n:view\r\n" +
              (json is null ? "echo boom 1>&2\r\nexit /b 1\r\n" : $"type \"{answer}\"\r\nexit /b 0\r\n")
            : $"#!/bin/sh\necho \"$*\" >> '{calls}'\nif [ \"$2\" = view ]; then " +
              (json is null ? "echo boom >&2; exit 1; fi\n" : $"cat '{answer}'; exit 0; fi\n") + $"exit {download}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(gh, (UnixFileMode)0b111_101_101);
        return gh;
    }

    private string[] GhCalls() => File.Exists(Path.Combine(folder, "gh-calls.txt")) ? File.ReadAllLines(Path.Combine(folder, "gh-calls.txt")) : [];

    private string NoGh => Path.Combine(folder, "no-gh");

    [Fact] public void HttpsFirstGhIsNotCalled()
    {
        var https = new GitHubHttpReleaseSource("owner/repo", FakeHandler.Of(r => r.RequestUri!.ToString() == Api
            ? Ok("{\"tag_name\":\"v0.2.0.21\",\"assets\":[{\"name\":\"a.zip\",\"size\":3,\"browser_download_url\":\"" + Url + "a.zip\"}]}") : Ok("abc")));
        var source = new FallbackReleaseSource(https, new GitHubReleaseSource("owner/repo", FakeGh("{\"tagName\":\"v9.0\"}")));
        var release = source.Latest();
        Assert.Equal(new Version(0, 2, 0, 21), release.Version);
        source.Download(release, ["a.zip"], folder);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(folder, "a.zip")));
        Assert.Empty(GhCalls());
    }

    // Codex audit 07.11 (optional): the release of a check is downloaded by another, fresh source (the offer window makes a new
    // updater): still over HTTPS, gh is not called.
    [Fact] public void AReleaseOfAnEarlierCheckIsDownloadedOverHttpsByAFreshSource()
    {
        HttpResponseMessage Answer(HttpRequestMessage r) => r.RequestUri!.ToString() == Api
            ? Ok("{\"tag_name\":\"v0.2.0.21\",\"assets\":[{\"name\":\"a.zip\",\"size\":3,\"browser_download_url\":\"" + Url + "a.zip\"}]}") : Ok("abc");
        var gh = FakeGh("{\"tagName\":\"v9.0\"}");
        var release = new FallbackReleaseSource(new GitHubHttpReleaseSource("owner/repo", FakeHandler.Of(Answer)), new GitHubReleaseSource("owner/repo", gh)).Latest();
        var handler = FakeHandler.Of(Answer);
        new FallbackReleaseSource(new GitHubHttpReleaseSource("owner/repo", handler), new GitHubReleaseSource("owner/repo", gh)).Download(release, ["a.zip"], folder);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(folder, "a.zip")));
        Assert.Equal(Url + "a.zip", Assert.Single(handler.Requests).RequestUri!.ToString());
        Assert.Empty(GhCalls());
    }

    [Fact] public void AFailedHttpsAnswerFallsBackToGh()
    {
        var https = new GitHubHttpReleaseSource("owner/repo", FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var source = new FallbackReleaseSource(https, new GitHubReleaseSource("owner/repo", FakeGh("{\"tagName\":\"v0.2.0.22\",\"body\":\"\"}")));
        var release = source.Latest();
        Assert.Equal(new Version(0, 2, 0, 22), release.Version);
        Assert.Null(release.AssetUrls);
        // A release of gh is downloaded by gh only.
        source.Download(release, ["a.zip"], folder);
        Assert.Equal(2, GhCalls().Length);
        Assert.StartsWith("release download v0.2.0.22", GhCalls()[1]);
    }

    [Fact] public void BothFailuresAreToldTogether()
    {
        var https = new GitHubHttpReleaseSource("owner/repo", FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var both = Assert.Throws<InvalidOperationException>(new FallbackReleaseSource(https, new GitHubReleaseSource("owner/repo", FakeGh(null))).Latest);
        Assert.StartsWith("Не удалось проверить выпуски по HTTPS: GitHub отклонил запрос без входа (HTTP 429)", both.Message);
        Assert.Contains("Через GitHub CLI (gh) тоже не удалось: GitHub CLI вернул ошибку: boom.", both.Message);
        var missing = Assert.Throws<InvalidOperationException>(new FallbackReleaseSource(https, new GitHubReleaseSource("owner/repo", NoGh)).Latest);
        Assert.EndsWith("Повторите позже. Для закрытого репозитория нужна программа GitHub CLI (gh) с выполненным входом «gh auth login».", missing.Message);
        Assert.Contains("HTTP 429", missing.Message);
    }

    [Fact] public void AFailedHttpsDownloadIsTriedOnceThroughGh()
    {
        var https = new GitHubHttpReleaseSource("owner/repo", FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)));
        new FallbackReleaseSource(https, new GitHubReleaseSource("owner/repo", FakeGh("{}"))).Download(Release(("a.zip", 3)), ["a.zip"], folder);
        Assert.StartsWith("release download v0.2.0.21", Assert.Single(GhCalls()));
        var error = Assert.Throws<InvalidOperationException>(() =>
            new FallbackReleaseSource(https, new GitHubReleaseSource("owner/repo", FakeGh("{}", download: 1))).Download(Release(("a.zip", 3)), ["a.zip"], folder));
        Assert.StartsWith("Не удалось загрузить выпуск по HTTPS: GitHub вернул ошибку HTTP 502", error.Message);
        Assert.Contains("Через GitHub CLI (gh) тоже не удалось", error.Message);
        Assert.False(File.Exists(Path.Combine(folder, "a.zip.part")));
    }

    [Fact] public void TheUsersCancelNeverFallsBack()
    {
        var https = new GitHubHttpReleaseSource("o/r", FakeHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Trickle(new byte[10])) }));
        var gh = new Counted(download: _ => { });
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(300);
        Assert.ThrowsAny<OperationCanceledException>(() => new FallbackReleaseSource(https, gh).Download(Release(("a.zip", 100)), ["a.zip"], folder, cancel.Token));
        Assert.Equal(0, gh.Downloads);
        Assert.Empty(Left());
        // A primary that throws a cancel of its own (not the user's) does not switch either.
        var primary = new Counted(() => throw new OperationCanceledException(), _ => throw new OperationCanceledException());
        Assert.ThrowsAny<OperationCanceledException>(new FallbackReleaseSource(primary, gh).Latest);
        Assert.ThrowsAny<OperationCanceledException>(() => new FallbackReleaseSource(primary, gh).Download(Release(("a.zip", 1)), ["a.zip"], folder));
        Assert.Equal(0, gh.Latests + gh.Downloads);
        // The fallback cancelled by the user: its cancel, not a combined failure.
        var failing = new Counted(download: _ => throw new InvalidOperationException("https"));
        using var late = new CancellationTokenSource();
        var cancelling = new Counted(download: _ => { late.Cancel(); late.Token.ThrowIfCancellationRequested(); });
        Assert.ThrowsAny<OperationCanceledException>(() => new FallbackReleaseSource(failing, cancelling).Download(Release(("a.zip", 1)), ["a.zip"], folder, late.Token));
        Assert.Equal(1, cancelling.Downloads);
    }

    [Fact] public void TheDefaultDownloadPassesNoCancel()
    {
        var gh = new Counted(download: cancel => Assert.False(cancel.CanBeCanceled));
        new FallbackReleaseSource(new Counted(), gh).Download(Release(("a.zip", 1)) with { AssetUrls = null }, ["a.zip"], folder);
        Assert.Equal(1, gh.Downloads);
        var https = new GitHubHttpReleaseSource("o/r", FakeHandler.Of(_ => Ok("abc")));
        https.Download(Release(("a.zip", 3)), ["a.zip"], folder);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(folder, "a.zip")));
    }
}
