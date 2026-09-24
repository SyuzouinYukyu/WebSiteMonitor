using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HtmlAgilityPack;

namespace WebSiteMonitor.Core;

public static class ContentHasher
{
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Preview(string value, int max = 4096) => value.Length <= max ? value : value[..max];
}

public static class ContentExtractor
{
    private static readonly Regex WhiteSpace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static ExtractionResult Extract(string source, MonitorMode mode, string? selector = null, string? xpath = null, string? pattern = null)
        => mode switch
        {
            MonitorMode.FullPage => new(NormalizePage(source), mode),
            MonitorMode.Text => new(ExtractText(source), mode),
            MonitorMode.CssSelector => new(ExtractCss(source, selector!), mode),
            MonitorMode.XPath => new(ExtractXPath(source, xpath!), mode),
            MonitorMode.Regex => new(ExtractRegex(source, pattern!), mode),
            _ => throw new ArgumentException("この監視方式はHTML抽出として直接処理できません。")
        };

    public static string NormalizePage(string value) => value.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    public static string NormalizeText(string value) => WhiteSpace.Replace(HtmlEntity.DeEntitize(value), " ").Trim();

    public static void ValidateCssSelector(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException("CSS Selectorを入力してください。");
        try { _ = new HtmlParser().ParseDocument("<html><body></body></html>").QuerySelectorAll(selector).Length; }
        catch (Exception ex) { throw new ArgumentException($"CSS Selectorの構文が正しくありません: {ex.Message}", ex); }
    }

    public static void ValidateXPath(string xpath)
    {
        if (string.IsNullOrWhiteSpace(xpath)) throw new ArgumentException("XPathを入力してください。");
        try { _ = XPathExpression.Compile(xpath); }
        catch (Exception ex) { throw new ArgumentException($"XPathの構文が正しくありません: {ex.Message}", ex); }
    }

    public static void ValidateRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("正規表現を入力してください。");
        try { _ = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout); }
        catch (ArgumentException ex) { throw new ArgumentException($"正規表現の構文が正しくありません: {ex.Message}", ex); }
    }

    public static string ExtractText(string html)
    {
        var doc = Load(html);
        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//noscript|//template") ?? Enumerable.Empty<HtmlNode>()) node.Remove();
        return NormalizeText(doc.DocumentNode.InnerText);
    }

    public static string ExtractCss(string html, string selector)
    {
        ValidateCssSelector(selector);
        var nodes = new HtmlParser().ParseDocument(html).QuerySelectorAll(selector).ToArray();
        if (nodes.Length == 0) throw new InvalidOperationException("CSS Selectorに一致する要素がありません。");
        return string.Join("\n", nodes.Select(n => NormalizeText(n.TextContent)));
    }

    public static string ExtractXPath(string html, string xpath)
    {
        ValidateXPath(xpath);
        var doc = Load(html);
        HtmlNodeCollection? nodes;
        try { nodes = doc.DocumentNode.SelectNodes(xpath); }
        catch (Exception ex) { throw new ArgumentException($"XPathが正しくありません: {ex.Message}", ex); }
        if (nodes is null || nodes.Count == 0) throw new InvalidOperationException("XPathに一致する要素がありません。");
        return string.Join("\n", nodes.Select(n => NormalizeText(n.InnerText)));
    }

    public static string ExtractRegex(string source, string pattern, TimeSpan? timeout = null)
    {
        ValidateRegex(pattern);
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, timeout ?? RegexTimeout);
        var values = new List<string>();
        foreach (Match match in regex.Matches(source)) values.Add(match.Groups.Count > 1 ? match.Groups[1].Value : match.Value);
        if (values.Count == 0) throw new InvalidOperationException("正規表現に一致する内容がありません。");
        return string.Join("\n", values);
    }

    public static string? DetectFeedUrl(string html, Uri pageUri)
    {
        var doc = Load(html);
        foreach (var link in doc.DocumentNode.SelectNodes("//link[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var rel = link.GetAttributeValue("rel", "");
            var type = link.GetAttributeValue("type", "");
            if (!rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("alternate", StringComparer.OrdinalIgnoreCase)) continue;
            if (!type.Contains("rss", StringComparison.OrdinalIgnoreCase) && !type.Contains("atom", StringComparison.OrdinalIgnoreCase) && !type.Contains("xml", StringComparison.OrdinalIgnoreCase)) continue;
            var href = link.GetAttributeValue("href", "");
            if (Uri.TryCreate(pageUri, href, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return uri.AbsoluteUri;
        }
        return null;
    }

    private static HtmlDocument Load(string html)
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true, OptionMaxNestedChildNodes = 1000 };
        doc.LoadHtml(html);
        return doc;
    }
}

public static class FeedParser
{
    public static bool LooksLikeFeed(string? mediaType, string text)
    {
        if (mediaType?.Contains("rss", StringComparison.OrdinalIgnoreCase) == true || mediaType?.Contains("atom", StringComparison.OrdinalIgnoreCase) == true) return true;
        var head = text.AsSpan(0, Math.Min(text.Length, 512)).TrimStart();
        return head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<rss", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<feed", StringComparison.OrdinalIgnoreCase);
    }

    public static string ParseAndNormalize(string xml, int maxItems = 20)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
        using var sr = new StringReader(xml);
        using var reader = XmlReader.Create(sr, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root ?? throw new FormatException("Feed XMLにルート要素がありません。");
        IEnumerable<XElement> entries = root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase)
            ? root.Elements().Where(e => e.Name.LocalName.Equals("entry", StringComparison.OrdinalIgnoreCase))
            : root.Descendants().Where(e => e.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase));
        var normalized = entries.Take(Math.Clamp(maxItems, 1, 100)).Select(NormalizeEntry).Where(s => s.Length > 0).ToArray();
        if (normalized.Length == 0) throw new FormatException("RSS/Atomに項目がありません。");
        return string.Join("\n---\n", normalized);
    }

    private static string NormalizeEntry(XElement entry)
    {
        string First(params string[] names) => entry.Elements().FirstOrDefault(e => names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value.Trim() ?? "";
        var id = First("guid", "id");
        var linkNode = entry.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase));
        var link = linkNode?.Attribute("href")?.Value?.Trim() ?? linkNode?.Value.Trim() ?? "";
        var title = First("title");
        var published = First("pubDate", "published");
        var updated = First("updated", "lastBuildDate", "date");
        var description = First("description", "summary");
        var content = First("content", "encoded");
        var identity = id.Length > 0 ? id : link.Length > 0 ? link : title + "|" + published;
        var compatibilityDate = published.Length > 0 ? published : updated;
        return string.Join("|", new[]
        {
            identity, title, link, compatibilityDate, "updated=" + updated,
            "description=" + description, "content=" + content
        }.Select(ContentExtractor.NormalizeText));
    }
}