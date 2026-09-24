using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace WebSiteMonitor.Core;

public interface IHttpFetcher
{
    Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? lastModified, CancellationToken cancellationToken);
    Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? lastModified, bool useBrowserCompatibleUserAgent, CancellationToken cancellationToken)
        => FetchAsync(uri, etag, lastModified, cancellationToken);
}

public sealed class SharedHttpFetcher : IHttpFetcher, IDisposable
{
    public const string BrowserCompatibleUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    private static readonly Regex MetaTag = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex CharsetAttribute = new("\\bcharset\\s*=\\s*['\\\"]?\\s*([^\\s'\\\"/>;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex HttpEquivAttribute = new("\\bhttp-equiv\\s*=\\s*['\\\"]?\\s*content-type\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly int _maxBytes;

    static SharedHttpFetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public SharedHttpFetcher(HttpClient? client = null, int maxBytes = 16 * 1024 * 1024)
    {
        _maxBytes = maxBytes;
        if (client is not null) { _client = client; return; }
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            UseProxy = true
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("WebSiteMonitor/1.0.4 (+Windows 11; portable monitor)");
        _ownsClient = true;
    }

    public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? lastModified, CancellationToken cancellationToken)
        => FetchAsync(uri, etag, lastModified, false, cancellationToken);

    public async Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? lastModified, bool useBrowserCompatibleUserAgent, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (useBrowserCompatibleUserAgent) request.Headers.UserAgent.ParseAdd(BrowserCompatibleUserAgent);
            if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            if (DateTimeOffset.TryParse(lastModified, out var modified)) request.Headers.IfModifiedSince = modified;
            try
            {
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified)
                    return new HttpFetchResult(304, null, null, null, etag, lastModified, response.RequestMessage?.RequestUri ?? uri, true);
                if (RetryPolicy.IsTransient((int)response.StatusCode) && attempt < 2)
                {
                    var retryAfter = response.Headers.RetryAfter;
                    await Task.Delay(RetryPolicy.DelayForAttempt(attempt, retryAfter?.Delta, retryAfter?.Date, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                if (length > _maxBytes) throw new InvalidDataException($"レスポンスが上限 {_maxBytes / 1024 / 1024}MB を超えています。");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var memory = new MemoryStream(length is > 0 and <= int.MaxValue ? (int)length.Value : 0);
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (memory.Length + read > _maxBytes) throw new InvalidDataException($"レスポンスが上限 {_maxBytes / 1024 / 1024}MB を超えています。");
                    await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                return new HttpFetchResult((int)response.StatusCode, memory.ToArray(), response.Content.Headers.ContentType?.MediaType,
                    response.Content.Headers.ContentType?.CharSet, response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified?.ToString("R"), response.RequestMessage?.RequestUri ?? uri);
            }
            catch (Exception ex) when (attempt < 2 && (ex is HttpRequestException || ex is TaskCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RetryPolicy.DelayForAttempt(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static string DecodeBody(HttpFetchResult result)
    {
        if (result.Body is null || result.Body.Length == 0) return "";
        var bytes = result.Body;
        var bom = DetectBom(bytes);
        if (bom.Encoding is not null) return bom.Encoding.GetString(bytes, bom.Offset, bytes.Length - bom.Offset);
        var encoding = TryGetEncoding(result.CharacterSet) ?? TryGetMetaEncoding(bytes) ?? Encoding.UTF8;
        return encoding.GetString(bytes);
    }

    private static (Encoding? Encoding, int Offset) DetectBom(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF) return (new UTF32Encoding(true, true), 4);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00) return (new UTF32Encoding(false, true), 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return (new UTF8Encoding(false, true), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return (new UnicodeEncoding(true, true, true), 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (new UnicodeEncoding(false, true, true), 2);
        return (null, 0);
    }

    private static Encoding? TryGetMetaEncoding(byte[] bytes)
    {
        var probeLength = Math.Min(bytes.Length, 16384);
        var probe = Encoding.Latin1.GetString(bytes, 0, probeLength);
        var direct = CharsetAttribute.Match(probe);
        if (direct.Success) return TryGetEncoding(direct.Groups[1].Value);
        foreach (Match tag in MetaTag.Matches(probe))
        {
            if (!HttpEquivAttribute.IsMatch(tag.Value)) continue;
            var charset = CharsetAttribute.Match(tag.Value);
            if (charset.Success) return TryGetEncoding(charset.Groups[1].Value);
        }
        return null;
    }

    private static Encoding? TryGetEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim().Trim('"', ''').ToLowerInvariant();
        if (normalized is "windows-31j" or "windows31j" or "shift_jis" or "shift-jis" or "ms932") return Encoding.GetEncoding(932);
        if (normalized is "euc-jp" or "euc_jp") return Encoding.GetEncoding(51932);
        try { return Encoding.GetEncoding(normalized); }
        catch (ArgumentException) { return null; }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}