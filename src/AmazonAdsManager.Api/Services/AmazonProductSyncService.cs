using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text.Json;

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

        // Deduplicate by ASIN — one ProductProfile per unique ASIN
        var seenAsins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upsertedProducts = 0;
        var upsertedMappings = 0;

        foreach (var ad in productAds)
        {
            var asin = ad.Asin;
            var sku = ad.Sku;

            if (!seenAsins.Add(asin)) continue; // already handled this ASIN

            // Find or create ProductProfile
            var existing = _products.GetByAccount(account.AccountKey)
                .FirstOrDefault(p => string.Equals(p.ASIN, asin, StringComparison.OrdinalIgnoreCase));

            var product = existing ?? new ProductProfile
            {
                AccountKey = account.AccountKey,
                ASIN = asin,
                SKU = sku,
                DisplayName = string.IsNullOrEmpty(sku) ? asin : $"{asin} / {sku}",
                IsActive = true
            };

            // Fetch real title if DisplayName is still the raw ASIN/SKU placeholder
            var needsTitle = product.DisplayName == asin
                || product.DisplayName == $"{asin} / {sku}"
                || string.IsNullOrEmpty(product.DisplayName);

            if (needsTitle)
            {
                var title = await _imageService.GetProductTitleAsync(asin);
                if (!string.IsNullOrEmpty(title))
                    product.DisplayName = title;
            }

            if (existing is null)
            {
                _products.Upsert(product);
                upsertedProducts++;
            }
            else if (needsTitle && !string.IsNullOrEmpty(product.DisplayName))
            {
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
                if (existingMapping is not null) continue;

                _mappings.Upsert(new ProductCampaignMapping
                {
                    Id = mappingId,
                    AccountKey = account.AccountKey,
                    ProductId = product.Id,
                    CampaignId = long.TryParse(linkedAd.CampaignId, out var cid) ? cid : 0,
                    CampaignName = campaign.Name,
                    IsActive = campaign.State is "enabled" or "ENABLED"
                });
                upsertedMappings++;
            }
        }

        return new SyncResult
        {
            ProductsUpserted = upsertedProducts,
            MappingsUpserted = upsertedMappings,
            TotalCampaigns = campaignList.Count,
            TotalProductAds = productAds.Count
        };
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

    private record ProductAdRecord(string AdId, string CampaignId, string AdGroupId, string Asin, string Sku, string State)
    {
        public ProductAdRecord() : this("", "", "", "", "", "") { }
    }
}

