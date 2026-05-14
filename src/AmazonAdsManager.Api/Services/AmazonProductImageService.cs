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
            if (!resp.IsSuccessStatusCode) return;

            var raw = await resp.Content.ReadAsStringAsync();
            var html = System.Net.WebUtility.HtmlDecode(raw);

            // Extract product title
            var titleMatch = Regex.Match(html, @"<span[^>]+id=""productTitle""[^>]*>\s*(.*?)\s*</span>", RegexOptions.Singleline);
            if (titleMatch.Success)
            {
                var full = Regex.Replace(titleMatch.Groups[1].Value, "<[^>]+>", "").Trim();
                _titleCache[asin] = SummarizeTitle(full);
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
        }
        catch { }
    }

    private static string SummarizeTitle(string full)
    {
        if (string.IsNullOrWhiteSpace(full)) return full;

        // Split on common separators — take the first meaningful segment
        var separators = new[] { " - ", " | ", ", ", " for ", " with ", " by " };
        foreach (var sep in separators)
        {
            var idx = full.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 10)
            {
                full = full[..idx].Trim();
                break;
            }
        }

        // Cap at 40 chars, break on word boundary
        if (full.Length > 40)
        {
            var cut = full.LastIndexOf(' ', 40);
            full = (cut > 20 ? full[..cut] : full[..40]).Trim() + "…";
        }

        return full;
    }
}
