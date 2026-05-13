using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AmazonAdsManager.Api.Services;

public class AmazonProductImageService
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AmazonProductImageService(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient();
    }

    public async Task<string?> GetImageUrlAsync(string asin)
    {
        if (_cache.TryGetValue(asin, out var cached)) return cached;

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.amazon.com/dp/{asin}");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            req.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            req.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var raw = await resp.Content.ReadAsStringAsync();
            var html = System.Net.WebUtility.HtmlDecode(raw);

            // Try hiRes image first (high quality product photo)
            var hiRes = Regex.Match(html, @"""hiRes""\s*:\s*""(https://[^""]+\.jpg)""", RegexOptions.IgnoreCase);
            if (hiRes.Success)
            {
                var url = Regex.Replace(hiRes.Groups[1].Value, @"\._[A-Z0-9_,]+_\.", "._SL80_.");
                _cache[asin] = url;
                return url;
            }

            // Fallback: data-a-dynamic-image attribute (main thumbnail)
            var dynMatch = Regex.Match(html, @"data-a-dynamic-image=""(\{[^""]+\})""", RegexOptions.IgnoreCase);
            if (dynMatch.Success)
            {
                var jsonStr = dynMatch.Groups[1].Value;
                var firstUrl = Regex.Match(jsonStr, @"""(https://[^""]+\.jpg)""");
                if (firstUrl.Success)
                {
                    var url = Regex.Replace(firstUrl.Groups[1].Value, @"\._[A-Z0-9_,]+_\.", "._SL80_.");
                    _cache[asin] = url;
                    return url;
                }
            }
        }
        catch { }

        return null;
    }
}
