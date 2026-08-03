using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AmazonAdsManager.Api.Services;

public class AmazonProductSyncService
{
    private readonly AmazonAdsAuthService _auth;
    private readonly AmazonCampaignService _campaigns;
    private readonly ProductProfileRepository _products;
    private readonly ProductCampaignMappingRepository _mappings;
    private readonly AmazonProductImageService _imageService;
    private readonly AmazonAdsOptions _options;
    private readonly HttpClient _http;

    public AmazonProductSyncService(
        AmazonAdsAuthService auth,
        AmazonCampaignService campaigns,
        ProductProfileRepository products,
        ProductCampaignMappingRepository mappings,
        AmazonProductImageService imageService,
        IOptions<AmazonAdsOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _auth = auth;
        _campaigns = campaigns;
        _products = products;
        _mappings = mappings;
        _imageService = imageService;
        _options = options.Value;
        _http = httpClientFactory.CreateClient();
    }

    public async Task<SyncResult> SyncAsync(AmazonAccountConfig account)
    {
        var token = await _auth.GetAccessTokenAsync(account);

        var campaignList = await _campaigns.ListCampaignsAsync(account);
        var campaignMap = campaignList.ToDictionary(c => c.CampaignId, c => c);

        var productAds = await FetchProductAdsAsync(account, token);
        var titlesByProductKey = await FetchProductTitlesAsync(
            account,
            token,
            productAds.Select(ad => new ProductIdentity(ad.Asin, ad.Sku)));

        // Deduplicate by ASIN — one ProductProfile per unique ASIN
        var seenAsins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upsertedProducts = 0;
        var upsertedMappings = 0;
        var titlesUpdated = 0;

        foreach (var ad in productAds)
        {
            var asin = ad.Asin;
            var sku = ad.Sku;

            if (!seenAsins.Add(asin)) continue; // already handled this ASIN

            // Find or create ProductProfile
            var existing = _products.GetByAccount(account.AccountKey)
                .FirstOrDefault(p => string.Equals(p.ASIN, asin, StringComparison.OrdinalIgnoreCase));

            var productTitle = GetTitleForProduct(titlesByProductKey, asin, sku);
            var product = existing ?? new ProductProfile
            {
                AccountKey = account.AccountKey,
                ASIN = asin,
                SKU = sku,
                DisplayName = productTitle ?? PlaceholderName(asin, sku),
                IsActive = true
            };

            if (existing is null)
            {
                _products.Upsert(product);
                upsertedProducts++;
            }
            else
            {
                var changed = false;
                if (string.IsNullOrWhiteSpace(product.SKU) && !string.IsNullOrWhiteSpace(sku))
                {
                    product.SKU = sku;
                    changed = true;
                }

                var replacementTitle = productTitle;
                if (!ShouldReplaceProductTitle(product, replacementTitle) &&
                    ShouldCheckProductPageTitle(product.DisplayName, replacementTitle))
                {
                    replacementTitle = await FetchProductPageTitleAsync(product.ASIN);
                }

                if (ShouldReplaceProductTitle(product, replacementTitle))
                {
                    product.DisplayName = CleanProductTitle(replacementTitle)!;
                    changed = true;
                    titlesUpdated++;
                }

                if (changed)
                    _products.Upsert(product);
            }

            // Sync campaign mappings for this ASIN
            var adsForAsin = productAds.Where(a =>
                string.Equals(a.Asin, asin, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var linkedAd in adsForAsin)
            {
                if (!campaignMap.TryGetValue(linkedAd.CampaignId, out var campaign)) continue;

                var mappingId = $"{account.AccountKey}:{product.Id}:{linkedAd.CampaignId}";
                var existingMapping = _mappings.GetById(mappingId);
                var mapping = existingMapping ?? new ProductCampaignMapping
                {
                    Id = mappingId,
                    AccountKey = account.AccountKey,
                    ProductId = product.Id,
                    CampaignId = linkedAd.CampaignId
                };

                mapping.CampaignName = campaign.Name;
                mapping.IsActive = campaign.State is "enabled" or "ENABLED";
                mapping.CampaignStartDate = campaign.StartDate;
                mapping.CampaignEndDate = campaign.EndDate;
                _mappings.Upsert(mapping);
                if (existingMapping is null) upsertedMappings++;
            }
        }

        titlesUpdated += await HydratePlaceholderTitlesAsync(account, token);

        return new SyncResult
        {
            ProductsUpserted = upsertedProducts,
            MappingsUpserted = upsertedMappings,
            TotalCampaigns = campaignList.Count,
            TotalProductAds = productAds.Count,
            TitlesUpdated = titlesUpdated
        };
    }

    public async Task<int> HydratePlaceholderTitlesAsync(AmazonAccountConfig account)
    {
        var token = await _auth.GetAccessTokenAsync(account);
        return await HydratePlaceholderTitlesAsync(account, token);
    }

    private const string SpProductAdV3 = "application/vnd.spproductad.v3+json";

    private async Task<List<ProductAdRecord>> FetchProductAdsAsync(AmazonAccountConfig account, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{account.BaseUrl}/sp/productAds/list");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Amazon-Advertising-API-ClientId", _options.ClientId);
        req.Headers.Add("Amazon-Advertising-API-Scope", account.ProfileId);
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(SpProductAdV3));
        req.Content = new StringContent("{}");
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(SpProductAdV3);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return new List<ProductAdRecord>();

        var ads = new List<ProductAdRecord>();
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("productAds", out var arr)) return ads;

        foreach (var el in arr.EnumerateArray())
        {
            ads.Add(new ProductAdRecord
            {
                AdId = el.TryGetProperty("adId", out var aid) ? aid.GetString() ?? "" : "",
                CampaignId = el.TryGetProperty("campaignId", out var cid) ? cid.GetString() ?? "" : "",
                AdGroupId = el.TryGetProperty("adGroupId", out var agid) ? agid.GetString() ?? "" : "",
                Asin = el.TryGetProperty("asin", out var asin) ? asin.GetString() ?? "" : "",
                Sku = el.TryGetProperty("sku", out var sku) ? sku.GetString() ?? "" : "",
                State = el.TryGetProperty("state", out var state) ? state.GetString() ?? "" : ""
            });
        }
        return ads;
    }

    private async Task<int> HydratePlaceholderTitlesAsync(AmazonAccountConfig account, string token)
    {
        var needsTitles = _products.GetByAccount(account.AccountKey)
            .Where(p => IsPlaceholderName(p))
            .ToList();

        if (!needsTitles.Any()) return 0;

        var titles = await FetchProductTitlesAsync(
            account,
            token,
            needsTitles.Select(p => new ProductIdentity(p.ASIN, p.SKU)));

        var titlesUpdated = 0;
        foreach (var product in needsTitles)
        {
            var title = GetTitleForProduct(titles, product.ASIN, product.SKU);
            if (!string.IsNullOrWhiteSpace(title))
            {
                product.DisplayName = title;
                _products.Upsert(product);
                titlesUpdated++;
            }
        }

        var stillNeedsTitles = _products.GetByAccount(account.AccountKey)
            .Where(p => IsPlaceholderName(p))
            .ToList();

        if (!stillNeedsTitles.Any()) return titlesUpdated;

        var sem = new SemaphoreSlim(3);
        await Task.WhenAll(stillNeedsTitles.Select(async p =>
        {
            await sem.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var title = await _imageService.GetProductTitleAsync(p.ASIN).WaitAsync(cts.Token);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    p.DisplayName = title;
                    _products.Upsert(p);
                    Interlocked.Increment(ref titlesUpdated);
                }
            }
            catch { }
            finally { sem.Release(); }
        }));

        return titlesUpdated;
    }

    private async Task<Dictionary<string, string>> FetchProductTitlesAsync(
        AmazonAccountConfig account,
        string token,
        IEnumerable<ProductIdentity> products)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identities = products
            .Where(p => !string.IsNullOrWhiteSpace(p.Asin) || !string.IsNullOrWhiteSpace(p.Sku))
            .Distinct()
            .ToList();

        foreach (var batch in identities.Chunk(50))
        {
            try
            {
                var asins = batch.Select(p => p.Asin).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var skus = batch.Select(p => p.Sku).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (asins.Length == 0 && skus.Length == 0) continue;

                var body = new Dictionary<string, object?>
                {
                    ["pageIndex"] = 0,
                    ["pageSize"] = batch.Length,
                    ["adType"] = "SP",
                    ["checkEligibility"] = false
                };
                if (asins.Length > 0) body["asins"] = asins;
                if (skus.Length > 0) body["skus"] = skus;

                var req = new HttpRequestMessage(HttpMethod.Post, $"{account.BaseUrl}/product/metadata");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("Amazon-Advertising-API-ClientId", _options.ClientId);
                req.Headers.Add("Amazon-Advertising-API-Scope", account.ProfileId);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.productmetadataresponse.v1+json"));
                req.Content = JsonContent.Create(body);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.productmetadatarequest.v1+json");

                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                ExtractProductTitles(doc.RootElement, titles);
            }
            catch { }
        }

        return titles;
    }

    private static void ExtractProductTitles(JsonElement element, Dictionary<string, string> titles)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                AddTitleFromObject(element, titles);
                foreach (var property in element.EnumerateObject())
                    ExtractProductTitles(property.Value, titles);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ExtractProductTitles(item, titles);
                break;
        }
    }

    private static void AddTitleFromObject(JsonElement element, Dictionary<string, string> titles)
    {
        var asin = GetStringProperty(element, "asin", "ASIN", "advertisedAsin", "itemAsin");
        var sku = GetStringProperty(element, "sku", "SKU", "sellerSku", "advertisedSku");
        var title = GetStringProperty(element, "title", "productTitle", "productName", "itemName");
        title ??= GetStringProperty(element, "name");

        title = CleanProductTitle(title);
        if (string.IsNullOrWhiteSpace(title)) return;

        if (!string.IsNullOrWhiteSpace(asin))
            titles[asin] = title;
        if (!string.IsNullOrWhiteSpace(sku))
            titles[sku] = title;
    }

    private static string? GetStringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static string? GetTitleForProduct(Dictionary<string, string> titles, string asin, string sku)
    {
        if (!string.IsNullOrWhiteSpace(asin) && titles.TryGetValue(asin, out var asinTitle))
            return asinTitle;
        if (!string.IsNullOrWhiteSpace(sku) && titles.TryGetValue(sku, out var skuTitle))
            return skuTitle;
        return null;
    }

    private static string PlaceholderName(string asin, string sku) =>
        string.IsNullOrWhiteSpace(sku) ? asin : $"{asin} / {sku}";

    private static bool IsPlaceholderName(ProductProfile p)
    {
        var name = p.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.StartsWith("Sponsored Ad", StringComparison.OrdinalIgnoreCase)) return true;
        // Matches "B0D2B2QSCL" or "B0D2B2QSCL / OJ-5L34-5D4L"
        if (name.Equals(p.ASIN, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(PlaceholderName(p.ASIN, p.SKU), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool ShouldReplaceProductTitle(ProductProfile product, string? productTitle)
    {
        productTitle = CleanProductTitle(productTitle);
        if (string.IsNullOrWhiteSpace(productTitle)) return false;

        var currentTitle = product.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(currentTitle)) return true;
        if (currentTitle.Equals(productTitle, StringComparison.OrdinalIgnoreCase)) return false;
        if (IsPlaceholderName(product)) return true;
        if (IsOverlongTitle(currentTitle) &&
            productTitle.Length < currentTitle.Length &&
            SharesProductFamilyTokens(currentTitle, productTitle))
        {
            return true;
        }
        if (IsTruncatedTitle(currentTitle) &&
            !IsTruncatedTitle(productTitle) &&
            productTitle.Length > currentTitle.Length &&
            SharesProductFamilyTokens(currentTitle, productTitle))
        {
            return true;
        }

        var currentSize = ExtractSizeToken(currentTitle);
        var incomingSize = ExtractSizeToken(productTitle);
        if (currentSize is null || incomingSize is null) return false;
        if (string.Equals(currentSize, incomingSize, StringComparison.OrdinalIgnoreCase)) return false;

        return SharesProductFamilyTokens(currentTitle, productTitle);
    }

    private static string? ExtractSizeToken(string value)
    {
        var match = Regex.Match(
            value,
            @"(?<![A-Za-z0-9])(?<value>\d+(?:\.\d+)?)\s*(?<unit>inch(?:es)?|in\.?|[""”])(?=$|[^A-Za-z0-9])",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        return decimal.TryParse(match.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var size)
            ? size.ToString("0.###", CultureInfo.InvariantCulture)
            : match.Groups["value"].Value;
    }

    private static bool SharesProductFamilyTokens(string currentTitle, string productTitle)
    {
        var currentTokens = DistinctiveTitleTokens(currentTitle);
        if (currentTokens.Count == 0) return false;

        var shared = DistinctiveTitleTokens(productTitle).Count(currentTokens.Contains);
        return shared >= 2;
    }

    private static HashSet<string> DistinctiveTitleTokens(string value)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "inch", "inches", "size", "large", "small", "clear", "adults"
        };

        return Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(token => token.Length >= 3 && !stopWords.Contains(token) && !decimal.TryParse(token, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldCheckProductPageTitle(string currentTitle, string? metadataTitle)
    {
        if (IsTruncatedTitle(currentTitle)) return true;

        var currentSize = ExtractSizeToken(currentTitle);
        if (currentSize is null) return false;

        var metadataSize = ExtractSizeToken(metadataTitle ?? "");
        return metadataSize is null ||
               string.Equals(currentSize, metadataSize, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruncatedTitle(string value) =>
        value.TrimEnd().EndsWith("...", StringComparison.Ordinal);

    private static bool IsOverlongTitle(string value) =>
        value.Length > 60;

    private async Task<string?> FetchProductPageTitleAsync(string asin)
    {
        if (string.IsNullOrWhiteSpace(asin)) return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _imageService.GetProductTitleAsync(asin).WaitAsync(cts.Token);
        }
        catch
        {
            return null;
        }
    }

    private static string? CleanProductTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        title = Regex.Replace(title, "<[^>]+>", "");
        title = System.Net.WebUtility.HtmlDecode(title);
        title = Regex.Replace(title, @"\s+", " ").Trim();
        title = Regex.Replace(title, @"^Amazon\.com:\s*", "", RegexOptions.IgnoreCase).Trim();

        var amazonSuffix = title.IndexOf(": Amazon.com", StringComparison.OrdinalIgnoreCase);
        if (amazonSuffix > 0)
            title = title[..amazonSuffix].Trim();

        return SummarizeProductTitle(title);
    }

    private static string SummarizeProductTitle(string title)
    {
        var separators = new[] { " - ", " | ", ", ", " for ", " with ", " by " };
        foreach (var separator in separators)
        {
            var index = title.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (index > 10)
            {
                title = title[..index].Trim();
                break;
            }
        }

        const int maxLength = 54;
        if (title.Length <= maxLength) return title;

        var cut = title.LastIndexOf(' ', maxLength);
        return (cut > 24 ? title[..cut] : title[..maxLength]).Trim() + "...";
    }

    private record ProductIdentity(string Asin, string Sku);
    private record ProductAdRecord(string AdId, string CampaignId, string AdGroupId, string Asin, string Sku, string State)
    {
        public ProductAdRecord() : this("", "", "", "", "", "") { }
    }
}
