using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class AmazonSPReportingService
{
    private readonly AmazonAdsAuthService _auth;
    private readonly AmazonAdsOptions _options;
    private readonly IHttpClientFactory _httpFactory;

    public AmazonSPReportingService(AmazonAdsAuthService auth, IOptions<AmazonAdsOptions> options, IHttpClientFactory httpFactory)
    {
        _auth = auth;
        _options = options.Value;
        _httpFactory = httpFactory;
    }

    public async Task<IReadOnlyList<AdPerformanceDaily>> FetchAsync(
        AmazonAccountConfig account,
        IReadOnlyList<ProductCampaignMapping> mappings,
        DateOnly start,
        DateOnly end,
        CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        var token = await _auth.GetAccessTokenAsync(account);
        var reportId = await CreateReportAsync(http, account, token, start, end, ct);
        var downloadUrl = await PollUntilCompleteAsync(http, account, token, reportId, TimeSpan.FromMinutes(5), ct);
        return await DownloadAndParseAsync(http, downloadUrl, account.AccountKey, account.ProfileId, mappings, ct);
    }

    private async Task<string> CreateReportAsync(HttpClient http, AmazonAccountConfig account, string token, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            startDate = start.ToString("yyyy-MM-dd"),
            endDate = end.ToString("yyyy-MM-dd"),
            configuration = new
            {
                adProduct = "SPONSORED_PRODUCTS",
                groupBy = new[] { "targeting" },
                columns = new[] { "date", "campaignId", "campaignName", "adGroupId", "adGroupName", "targeting", "targetingType", "matchType", "impressions", "clicks", "cost", "purchases7d", "sales7d", "unitsSold7d" },
                reportTypeId = "spTargeting",
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
        IReadOnlyList<ProductCampaignMapping> mappings, CancellationToken ct)
    {
        var campaignMap = mappings
            .GroupBy(m => m.CampaignId.ToString(), StringComparer.OrdinalIgnoreCase)
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
            var campaignId = el.TryGetProperty("campaignId", out var cid) ? cid.GetString() ?? "" : "";
            var clicks = el.TryGetProperty("clicks", out var cl) ? cl.GetInt32() : 0;
            var impressions = el.TryGetProperty("impressions", out var imp) ? imp.GetInt32() : 0;
            var spend = el.TryGetProperty("cost", out var cost) ? cost.GetDecimal() : 0m;
            var purchases = el.TryGetProperty("purchases7d", out var pur) ? pur.GetInt32() : 0;
            var sales = el.TryGetProperty("sales7d", out var sal) ? sal.GetDecimal() : 0m;
            var unitsSold = el.TryGetProperty("unitsSold7d", out var us) ? us.GetInt32() : purchases;
            var dateStr = el.TryGetProperty("date", out var d) ? d.GetString() : null;

            if (!DateOnly.TryParse(dateStr, out var date)) continue;

            campaignMap.TryGetValue(campaignId, out var mapping);

            rows.Add(new AdPerformanceDaily
            {
                Date = date,
                SourceReportType = "Targeting",
                AccountKey = accountKey,
                ProfileId = profileId,
                ProductId = mapping?.ProductId,
                Asin = null,
                CampaignId = campaignId,
                CampaignName = el.TryGetProperty("campaignName", out var cn) ? cn.GetString() ?? "" : "",
                AdGroupId = el.TryGetProperty("adGroupId", out var agid) ? agid.GetString() : null,
                AdGroupName = el.TryGetProperty("adGroupName", out var agn) ? agn.GetString() : null,
                TargetingText = el.TryGetProperty("targeting", out var tgt) ? tgt.GetString() : null,
                TargetingType = el.TryGetProperty("targetingType", out var tt) ? tt.GetString() : null,
                MatchType = el.TryGetProperty("matchType", out var mt) ? mt.GetString() : null,
                SearchTerm = null,
                Impressions = impressions,
                Clicks = clicks,
                Spend = spend,
                Purchases = purchases,
                Sales = sales,
                UnitsSold = unitsSold,
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

    private void AddAuth(HttpRequestMessage req, string token, AmazonAccountConfig account)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Amazon-Advertising-API-ClientId", _options.ClientId);
        req.Headers.Add("Amazon-Advertising-API-Scope", account.ProfileId);
    }
}
