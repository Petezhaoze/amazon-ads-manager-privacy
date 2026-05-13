using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class AmazonCampaignService
{
    private readonly AmazonAdsAuthService _auth;
    private readonly AmazonAdsOptions _options;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AmazonCampaignService(AmazonAdsAuthService auth, IOptions<AmazonAdsOptions> options, IHttpClientFactory httpClientFactory)
    {
        _auth = auth;
        _options = options.Value;
        _http = httpClientFactory.CreateClient();
    }

    private const string SpCampaignV3 = "application/vnd.spcampaign.v3+json";
    private const string SpProductAdV3 = "application/vnd.spproductad.v3+json";

    public async Task<List<CampaignDto>> ListCampaignsAsync(AmazonAccountConfig account)
    {
        var token = await _auth.GetAccessTokenAsync(account);
        var req = BuildRequest(HttpMethod.Post, $"{account.BaseUrl}/sp/campaigns/list", token, account, SpCampaignV3);
        // extendedData: true asks Amazon to include serving/delivery status fields
        var body = JsonSerializer.Serialize(new { extendedData = true });
        req.Content = new StringContent(body);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(SpCampaignV3);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Amazon campaigns API {(int)resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var campaigns = new List<CampaignDto>();
        if (!doc.RootElement.TryGetProperty("campaigns", out var arr)) return campaigns;

        foreach (var el in arr.EnumerateArray())
        {
            var state = el.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            if (state == "ARCHIVED") continue;

            decimal dailyBudget = 0;
            string budgetType = "";
            if (el.TryGetProperty("budget", out var budget))
            {
                dailyBudget = budget.TryGetProperty("budget", out var b) ? b.GetDecimal() : 0;
                budgetType = budget.TryGetProperty("budgetType", out var bt) ? bt.GetString() ?? "" : "";
            }

            string? endDate = el.TryGetProperty("endDate", out var ed) ? ed.GetString() : null;

            // Try top-level servingStatus first, then nested extendedData
            string? servingStatus = null;
            if (el.TryGetProperty("servingStatus", out var ss0))
                servingStatus = ss0.GetString();
            else if (el.TryGetProperty("extendedData", out var ext))
            {
                if (ext.TryGetProperty("servingStatus", out var ss1))
                    servingStatus = ss1.GetString();
                else if (ext.TryGetProperty("deliveryStatus", out var ss2))
                    servingStatus = ss2.GetString();
            }

            campaigns.Add(new CampaignDto
            {
                CampaignId = el.GetProperty("campaignId").GetString() ?? "",
                Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                State = state.ToLowerInvariant(),
                BudgetType = budgetType.ToLowerInvariant(),
                DailyBudget = dailyBudget,
                EndDate = endDate,
                ServingStatus = servingStatus
            });
        }
        return campaigns;
    }

    // Returns (verifiedState, errorMessage): verifiedState is non-null on success, null on failure
    public async Task<(string? verifiedState, string? error)> UpdateCampaignStateAsync(AmazonAccountConfig account, string campaignId, string state)
    {
        var token = await _auth.GetAccessTokenAsync(account);
        var payload = JsonSerializer.Serialize(new
        {
            campaigns = new[] { new { campaignId, state = state.ToUpperInvariant() } }
        });

        var req = BuildRequest(HttpMethod.Put, $"{account.BaseUrl}/sp/campaigns", token, account, SpCampaignV3);
        req.Content = new StringContent(payload);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(SpCampaignV3);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return (null, $"HTTP {(int)resp.StatusCode}: {raw}");

        // Parse Amazon's response: { "campaigns": { "success": [...], "error": [...] } }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("campaigns", out var campaigns))
            {
                if (campaigns.TryGetProperty("error", out var errors) && errors.GetArrayLength() > 0)
                {
                    var errMsg = errors[0].TryGetProperty("errorDetails", out var ed) ? ed.GetString() : "Amazon rejected the update";
                    return (null, errMsg ?? "Amazon rejected the update");
                }
                if (campaigns.TryGetProperty("success", out var successes) && successes.GetArrayLength() > 0)
                    return (state.ToLowerInvariant(), null);
            }
        }
        catch { }

        // If response body is empty or unparseable but HTTP was 200, treat as success
        return (state.ToLowerInvariant(), null);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, string token, AmazonAccountConfig account, string mediaType = "application/json")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Amazon-Advertising-API-ClientId", _options.ClientId);
        req.Headers.Add("Amazon-Advertising-API-Scope", account.ProfileId);
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(mediaType));
        return req;
    }
}
