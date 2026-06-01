using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AmazonAdsManager.Api.Services;

public sealed record SponsoredProductsReportFetchResult(
    IReadOnlyList<AdPerformanceDaily> Rows,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, bool> ReportSuccessBySourceType);

public class AmazonSPReportingService
{
    private sealed record ReportSpec(
        string SourceReportType,
        string ReportTypeId,
        string[] GroupBy,
        string[] RequiredColumns,
        string[] OptionalColumns)
    {
        public string[] Columns => RequiredColumns.Concat(OptionalColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        public bool HasOptionalColumns => OptionalColumns.Length > 0;
        public ReportSpec RequiredOnly() => this with { OptionalColumns = [] };
    }

    private sealed record ReportFetchOutcome(
        string SourceReportType,
        IReadOnlyList<AdPerformanceDaily> Rows,
        IReadOnlyList<string> Warnings,
        bool Success);

    private static readonly ReportSpec[] ReportSpecs =
    [
        new("Campaign", "spCampaigns", ["campaign"],
        [
            "date", "campaignId", "campaignName", "impressions", "clicks", "cost", "purchases7d", "sales7d", "unitsSoldClicks7d"
        ],
        [
            "campaignStatus", "campaignBudgetAmount", "campaignBudgetType"
        ]),
        new("Targeting", "spTargeting", ["targeting"],
        [
            "date", "campaignId", "campaignName", "adGroupId", "adGroupName", "keywordId", "keyword", "targeting",
            "keywordType", "matchType", "impressions", "clicks", "cost", "purchases7d", "sales7d", "unitsSoldClicks7d"
        ],
        [
            "adKeywordStatus", "keywordBid", "campaignBudgetAmount", "campaignBudgetType", "campaignStatus"
        ]),
        new("SearchTerm", "spSearchTerm", ["searchTerm"],
        [
            "date", "campaignId", "campaignName", "adGroupId", "adGroupName", "keywordId", "keyword", "targeting",
            "searchTerm", "keywordType", "matchType", "impressions", "clicks", "cost", "purchases7d", "sales7d",
            "unitsSoldClicks7d"
        ],
        [
            "adKeywordStatus", "keywordBid", "campaignBudgetAmount", "campaignBudgetType", "campaignStatus"
        ]),
        new("AdvertisedProduct", "spAdvertisedProduct", ["advertiser"],
        [
            "date", "campaignId", "campaignName", "adGroupId", "adGroupName", "adId", "advertisedAsin", "advertisedSku",
            "impressions", "clicks", "cost", "purchases7d", "sales7d", "unitsSoldClicks7d"
        ],
        [
            "campaignBudgetAmount", "campaignBudgetType", "campaignStatus"
        ]),
        new("PurchasedProduct", "spPurchasedProduct", ["asin"],
        [
            "date", "campaignId", "campaignName", "adGroupId", "adGroupName", "advertisedAsin", "advertisedSku",
            "purchasedAsin", "purchases7d", "sales7d", "unitsSoldClicks7d"
        ],
        [
            "keywordId", "keyword", "targeting", "keywordType", "matchType"
        ])
    ];

    private readonly AmazonAdsAuthService _auth;
    private readonly AmazonAdsOptions _options;
    private readonly IHttpClientFactory _httpFactory;

    public AmazonSPReportingService(AmazonAdsAuthService auth, IOptions<AmazonAdsOptions> options, IHttpClientFactory httpFactory)
    {
        _auth = auth;
        _options = options.Value;
        _httpFactory = httpFactory;
    }

    public async Task<SponsoredProductsReportFetchResult> FetchAsync(
        AmazonAccountConfig account,
        IReadOnlyList<ProductCampaignMapping> mappings,
        DateOnly start,
        DateOnly end,
        CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        var token = await _auth.GetAccessTokenAsync(account);
        var rows = new List<AdPerformanceDaily>();
        var warnings = new List<string>();

        var reportTasks = ReportSpecs
            .Select(spec => FetchReportRowsWithFallbackAsync(http, account, token, spec, mappings, start, end, ct))
            .ToArray();
        var reportResults = await Task.WhenAll(reportTasks);

        foreach (var result in reportResults)
        {
            rows.AddRange(result.Rows);
            warnings.AddRange(result.Warnings);
        }

        if (!rows.Any() && reportResults.All(r => !r.Success))
            throw new InvalidOperationException($"All Amazon Ads reporting imports failed. {warnings.First()}");

        return new SponsoredProductsReportFetchResult(
            rows,
            warnings,
            reportResults.ToDictionary(r => r.SourceReportType, r => r.Success, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<ReportFetchOutcome> FetchReportRowsWithFallbackAsync(
        HttpClient http,
        AmazonAccountConfig account,
        string token,
        ReportSpec spec,
        IReadOnlyList<ProductCampaignMapping> mappings,
        DateOnly start,
        DateOnly end,
        CancellationToken ct)
    {
        try
        {
            var rows = await FetchReportRowsAsync(http, account, token, spec, mappings, start, end, ct);
            return new ReportFetchOutcome(spec.SourceReportType, rows, [], true);
        }
        catch (Exception ex)
        {
            var warnings = new List<string>();
            if (spec.HasOptionalColumns)
            {
                try
                {
                    var rows = await FetchReportRowsAsync(http, account, token, spec.RequiredOnly(), mappings, start, end, ct);
                    warnings.Add($"{spec.SourceReportType} report imported with required columns only because Amazon rejected one or more optional enrichment columns.");
                    return new ReportFetchOutcome(spec.SourceReportType, rows, warnings, true);
                }
                catch (Exception fallbackEx)
                {
                    warnings.Add($"{spec.SourceReportType} required-column report import failed: {fallbackEx.Message}");
                }
            }

            warnings.Add($"{spec.SourceReportType} report import failed: {ex.Message}");
            return new ReportFetchOutcome(spec.SourceReportType, [], warnings, false);
        }
    }

    private async Task<IReadOnlyList<AdPerformanceDaily>> FetchReportRowsAsync(
        HttpClient http,
        AmazonAccountConfig account,
        string token,
        ReportSpec spec,
        IReadOnlyList<ProductCampaignMapping> mappings,
        DateOnly start,
        DateOnly end,
        CancellationToken ct)
    {
        var reportId = await CreateReportAsync(http, account, token, spec, start, end, ct);
        var downloadUrl = await PollUntilCompleteAsync(http, account, token, reportId, TimeSpan.FromMinutes(5), ct);
        return await DownloadAndParseAsync(http, downloadUrl, account.AccountKey, account.ProfileId, mappings, spec, start, ct);
    }

    private async Task<string> CreateReportAsync(HttpClient http, AmazonAccountConfig account, string token, ReportSpec spec, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            startDate = start.ToString("yyyy-MM-dd"),
            endDate = end.ToString("yyyy-MM-dd"),
            configuration = new
            {
                adProduct = "SPONSORED_PRODUCTS",
                groupBy = spec.GroupBy,
                columns = spec.Columns,
                reportTypeId = spec.ReportTypeId,
                timeUnit = "DAILY",
                format = "GZIP_JSON"
            }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{account.BaseUrl}/reporting/reports")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddAuth(req, token, account);

        var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Report creation failed {(int)resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("reportId").GetString()
               ?? throw new InvalidOperationException("Amazon did not return a reportId");
    }

    private async Task<string> PollUntilCompleteAsync(HttpClient http, AmazonAccountConfig account, string token, string reportId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var delayMs = 5_000;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(delayMs, ct);
            delayMs = Math.Min((int)(delayMs * 1.5), 30_000);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{account.BaseUrl}/reporting/reports/{reportId}");
            AddAuth(req, token, account);

            var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Report poll failed {(int)resp.StatusCode}: {raw}");

            using var doc = JsonDocument.Parse(raw);
            var status = doc.RootElement.GetProperty("status").GetString();

            if (status == "COMPLETED")
                return doc.RootElement.GetProperty("url").GetString()
                       ?? throw new InvalidOperationException("No download URL in completed report");

            if (status == "FAILED")
                throw new InvalidOperationException($"Report generation failed: {raw}");
        }

        throw new TimeoutException($"Report {reportId} did not complete within {timeout.TotalMinutes:F0} minutes");
    }

    private static async Task<IReadOnlyList<AdPerformanceDaily>> DownloadAndParseAsync(
        HttpClient http, string url, string accountKey, string profileId,
        IReadOnlyList<ProductCampaignMapping> mappings, ReportSpec spec, DateOnly fallbackDate, CancellationToken ct)
    {
        var campaignMap = mappings
            .GroupBy(m => m.CampaignId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var campaignNameMap = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.CampaignName))
            .GroupBy(m => m.CampaignName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var compressed = await http.GetByteArrayAsync(url, ct);

        string json;
        using (var ms = new MemoryStream(compressed))
        using (var gz = new GZipStream(ms, CompressionMode.Decompress))
        using (var reader = new StreamReader(gz, Encoding.UTF8))
            json = await reader.ReadToEndAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var rows = new List<AdPerformanceDaily>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var campaignId = ReadString(el, "campaignId") ?? "";
            var campaignName = ReadString(el, "campaignName") ?? "";
            campaignMap.TryGetValue(campaignId, out var mapping);
            if (mapping is null && !string.IsNullOrWhiteSpace(campaignName))
                campaignNameMap.TryGetValue(campaignName, out mapping);

            var keywordId = ReadString(el, "keywordId");
            var keywordType = ReadString(el, "keywordType");
            var targeting = ReadString(el, "targeting") ?? ReadString(el, "keyword");
            var searchTerm = ReadString(el, "searchTerm");
            var advertisedAsin = ReadString(el, "advertisedAsin");
            var purchasedAsin = ReadString(el, "purchasedAsin");
            var spend = ReadDecimal(el, "cost");
            var purchases = ReadInt(el, "purchases7d");
            var sales = ReadDecimal(el, "sales7d");
            var clicks = ReadInt(el, "clicks");
            var impressions = ReadInt(el, "impressions");

            rows.Add(new AdPerformanceDaily
            {
                Date = ReadDate(el, fallbackDate),
                SourceReportType = spec.SourceReportType,
                AccountKey = accountKey,
                ProfileId = profileId,
                ProductId = mapping?.ProductId,
                Asin = advertisedAsin,
                CampaignId = campaignId,
                CampaignName = campaignName,
                AdGroupId = ReadString(el, "adGroupId"),
                AdGroupName = ReadString(el, "adGroupName"),
                AdId = ReadString(el, "adId"),
                TargetingText = targeting,
                TargetingType = keywordType,
                MatchType = ReadString(el, "matchType"),
                SearchTerm = searchTerm,
                KeywordId = IsTargetingExpression(keywordType, targeting) ? null : keywordId,
                TargetId = IsTargetingExpression(keywordType, targeting) ? keywordId : null,
                Bid = ReadDecimalOrNull(el, "keywordBid") ?? ReadDecimalOrNull(el, "bid"),
                ServingStatus = ReadString(el, "adKeywordStatus") ?? ReadString(el, "servingStatus"),
                CampaignBudgetAmount = ReadDecimalOrNull(el, "campaignBudgetAmount"),
                CampaignBudgetType = ReadString(el, "campaignBudgetType"),
                CampaignStatus = ReadString(el, "campaignStatus"),
                AdvertisedAsin = advertisedAsin,
                AdvertisedSku = ReadString(el, "advertisedSku"),
                PurchasedAsin = purchasedAsin,
                SearchTermKind = SearchTermKind(searchTerm ?? purchasedAsin),
                Impressions = impressions,
                Clicks = clicks,
                Spend = spend,
                Purchases = purchases,
                Sales = sales,
                UnitsSold = ReadInt(el, "unitsSoldClicks7d", purchases),
                DetailPageViews = ReadInt(el, "detailPageViewsClicks"),
                ROAS = spend > 0 ? decimal.Round(sales / spend, 2) : 0,
                ACOS = sales > 0 ? decimal.Round(spend / sales, 4) : 0,
                CPC = clicks > 0 ? decimal.Round(spend / clicks, 2) : 0,
                CTR = impressions > 0 ? decimal.Round((decimal)clicks / impressions, 4) : 0,
                CVR = clicks > 0 ? decimal.Round((decimal)purchases / clicks, 4) : 0,
                CostPerPurchase = purchases > 0 ? decimal.Round(spend / purchases, 2) : spend,
                PurchaseRate = clicks > 0 ? decimal.Round((decimal)purchases / clicks, 4) : 0
            });
        }

        return rows.AsReadOnly();
    }

    private static string SearchTermKind(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" :
        Regex.IsMatch(value.Trim(), @"^(asin[-=]|B0[A-Z0-9]{8})", RegexOptions.IgnoreCase) ? "ASIN" : "Text";

    private static bool IsTargetingExpression(string? keywordType, string? targeting) =>
        (keywordType?.Contains("target", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (targeting?.StartsWith("asin", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (targeting?.StartsWith("category", StringComparison.OrdinalIgnoreCase) ?? false);

    private static DateOnly ReadDate(JsonElement element, DateOnly fallback)
    {
        var raw = ReadString(element, "date") ?? ReadString(element, "startDate");
        return DateOnly.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    private static int ReadInt(JsonElement element, string name, int fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
                ? parsed
                : int.TryParse(value.ToString(), out parsed) ? parsed : fallback
            : fallback;

    private static decimal ReadDecimal(JsonElement element, string name) => ReadDecimalOrNull(element, name) ?? 0m;

    private static decimal? ReadDecimalOrNull(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var parsed))
            return parsed;
        return decimal.TryParse(value.ToString(), out parsed) ? parsed : null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? JsonValueAsString(value)
            : null;

    private static string? JsonValueAsString(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

    private void AddAuth(HttpRequestMessage req, string token, AmazonAccountConfig account)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Amazon-Advertising-API-ClientId", _options.ClientId);
        req.Headers.Add("Amazon-Advertising-API-Scope", account.ProfileId);
    }
}
