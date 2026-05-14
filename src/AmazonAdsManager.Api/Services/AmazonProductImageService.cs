using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AmazonAdsManager.Api.Services;

public class AmazonProductImageService
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _titleCache = new(StringComparer.OrdinalIgnoreCase);

    public AmazonProductImageService(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient("amazon-scraper");
    }

    public async Task<string?> GetImageUrlAsync(string asin)
    {
        if (_imageCache.TryGetValue(asin, out var cached)) return cached;
        await FetchPageAsync(asin);
        return _imageCache.TryGetValue(asin, out var result) ? result : null;
    }

    public async Task<string?> GetProductTitleAsync(string asin)
    {
        if (_titleCache.TryGetValue(asin, out var cached)) return cached;
        await FetchPageAsync(asin);
        return _titleCache.TryGetValue(asin, out var result) ? result : null;
    }

    private async Task FetchPageAsync(string asin)
    {
        if (_imageCache.ContainsKey(asin) && _titleCache.ContainsKey(asin)) return;

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.amazon.com/dp/{asin}");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            req.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            req.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                await FetchSearchPageAsync(asin);
                return;
            }

            var raw = await resp.Content.ReadAsStringAsync();
            var html = System.Net.WebUtility.HtmlDecode(raw);

            // Extract product title
            var titleMatch = Regex.Match(html, @"<span[^>]+id=""productTitle""[^>]*>\s*(.*?)\s*</span>", RegexOptions.Singleline);
            if (titleMatch.Success)
            {
                var full = Regex.Replace(titleMatch.Groups[1].Value, "<[^>]+>", "").Trim();
                _titleCache[asin] = CleanTitle(full);
            }
            else
            {
                var ogTitle = Regex.Match(html, @"<meta[^>]+property=""og:title""[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase);
                if (ogTitle.Success)
                    _titleCache[asin] = CleanTitle(ogTitle.Groups[1].Value);
            }

            // Extract image
            var hiRes = Regex.Match(html, @"""hiRes""\s*:\s*""(https://[^""]+\.jpg)""", RegexOptions.IgnoreCase);
            if (hiRes.Success)
            {
                _imageCache[asin] = Regex.Replace(hiRes.Groups[1].Value, @"\._[A-Z0-9_,]+_\.", "._SL80_.");
                return;
            }

            var dynMatch = Regex.Match(html, @"data-a-dynamic-image=""(\{[^""]+\})""", RegexOptions.IgnoreCase);
            if (dynMatch.Success)
            {
                var firstUrl = Regex.Match(dynMatch.Groups[1].Value, @"""(https://[^""]+\.jpg)""");
                if (firstUrl.Success)
                    _imageCache[asin] = Regex.Replace(firstUrl.Groups[1].Value, @"\._[A-Z0-9_,]+_\.", "._SL80_.");
            }

            if (!_titleCache.ContainsKey(asin))
                await FetchSearchPageAsync(asin);
        }
        catch { }
    }

    private async Task FetchSearchPageAsync(string asin)
    {
        if (_titleCache.ContainsKey(asin) && _imageCache.ContainsKey(asin)) return;

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.amazon.com/s?k={Uri.EscapeDataString(asin)}");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            req.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            req.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;

            var html = System.Net.WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
            var resultMatch = Regex.Match(
                html,
                @"data-component-type=""s-search-result"".*?(?=data-component-type=""s-search-result""|</body>)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!resultMatch.Success) return;

            var resultHtml = resultMatch.Value;
            var titleMatch = Regex.Match(resultHtml, @"<h2[^>]+aria-label=""([^""]+)""", RegexOptions.IgnoreCase);
            if (!titleMatch.Success)
                titleMatch = Regex.Match(resultHtml, @"<img[^>]+class=""s-image""[^>]+alt=""([^""]+)""", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
                _titleCache[asin] = CleanTitle(titleMatch.Groups[1].Value);

            var imageMatch = Regex.Match(resultHtml, @"<img[^>]+class=""s-image""[^>]+src=""(https://[^""]+\.jpg)""", RegexOptions.IgnoreCase);
            if (imageMatch.Success)
                _imageCache[asin] = Regex.Replace(imageMatch.Groups[1].Value, @"\._[A-Z0-9_,]+_\.", "._SL80_.");
        }
        catch { }
    }

    private static string CleanTitle(string full)
    {
        if (string.IsNullOrWhiteSpace(full)) return full;

        full = System.Net.WebUtility.HtmlDecode(full);
        full = Regex.Replace(full, @"\s+", " ").Trim();
        full = Regex.Replace(full, @"^Amazon\.com:\s*", "", RegexOptions.IgnoreCase).Trim();

        var amazonSuffix = full.IndexOf(": Amazon.com", StringComparison.OrdinalIgnoreCase);
        if (amazonSuffix > 0)
            full = full[..amazonSuffix].Trim();

        if (full.Length > 120)
        {
            var cut = full.LastIndexOf(' ', 120);
            full = (cut > 60 ? full[..cut] : full[..120]).Trim() + "...";
        }

        return full;
    }
}
