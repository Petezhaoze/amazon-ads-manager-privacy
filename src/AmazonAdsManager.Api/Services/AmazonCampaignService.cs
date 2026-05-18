using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmazonAdsManager.Api.Services;

public class AmazonCampaignService
{
    private readonly AmazonAdsAuthService _auth;
    private readonly AmazonAdsOptions _options;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AmazonCampaignService(AmazonAdsAuthService auth, IOptions<AmazonAdsOptions> options, IHttpClientFactory httpClientFactory)
    {
        _auth = auth;
        _options = options.Value;
        _http = httpClientFactory.CreateClient();
    }

    private const string SpCampaignV3 = "application/vnd.spcampaign.v3+json";
    private const string SpProductAdV3 = "application/vnd.spproductad.v3+json";
    private const string SpNegativeKeywordV3 = "application/vnd.spnegativekeyword.v3+json";
    private const string SpTargetingClauseV3 = "application/vnd.sptargetingclause.v3+json";
    private const string SpKeywordV3 = "application/vnd.spkeyword.v3+json";

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

            string? startDate = el.TryGetProperty("startDate", out var sd) ? sd.GetString() : null;
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
                StartDate = startDate,
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

    public async Task<(bool success, string response, string requestJson)> UpdateCampaignBudgetAsync(AmazonAccountConfig account, string campaignId, decimal dailyBudget)
    {
        if (dailyBudget <= 0)
            return (false, "Daily budget must be greater than 0.", "");

        var token = await _auth.GetAccessTokenAsync(account);
        var payload = JsonSerializer.Serialize(new
        {
            campaigns = new[]
            {
                new
                {
                    campaignId,
                    budget = new
                    {
                        budget = dailyBudget,
                        budgetType = "DAILY"
                    }
                }
            }
        }, _jsonOpts);

        var req = BuildRequest(HttpMethod.Put, $"{account.BaseUrl}/sp/campaigns", token, account, SpCampaignV3);
        req.Content = new StringContent(payload, Encoding.UTF8, SpCampaignV3);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode}: {raw}", payload);

        if (AmazonBulkResponseHasError(raw, "campaigns", out var error))
            return (false, error, payload);

        return (true, raw, payload);
    }

    public async Task<(bool success, string response, string requestJson)> AddNegativeKeywordAsync(
        AmazonAccountConfig account,
        string campaignId,
        string adGroupId,
        string keywordText,
        string matchType)
    {
        if (string.IsNullOrWhiteSpace(campaignId))
            return (false, "Campaign ID is required to create a negative keyword.", "");
        if (string.IsNullOrWhiteSpace(adGroupId))
            return (false, "Ad group ID is required to create a negative keyword.", "");
        if (string.IsNullOrWhiteSpace(keywordText))
            return (false, "Negative keyword text cannot be empty.", "");

        var normalizedMatchType = NormalizeNegativeKeywordMatchType(matchType);
        var token = await _auth.GetAccessTokenAsync(account);
        var payload = JsonSerializer.Serialize(new
        {
            negativeKeywords = new[]
            {
                new
                {
                    campaignId,
                    adGroupId,
                    keywordText = keywordText.Trim(),
                    matchType = normalizedMatchType,
                    state = "ENABLED"
                }
            }
        }, _jsonOpts);

        var req = BuildRequest(HttpMethod.Post, $"{account.BaseUrl}/sp/negativeKeywords", token, account, SpNegativeKeywordV3);
        req.Content = new StringContent(payload, Encoding.UTF8, SpNegativeKeywordV3);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode}: {raw}", payload);

        if (AmazonBulkResponseHasError(raw, "negativeKeywords", out var error))
            return (false, error, payload);

        return (true, raw, payload);
    }

    public async Task<AmazonTargetLookupDto?> FindTargetAsync(AmazonAccountConfig account, string campaignId, string? adGroupId, string? targetingText)
    {
        if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(targetingText))
            return null;

        var token = await _auth.GetAccessTokenAsync(account);
        var payload = JsonSerializer.Serialize(new
        {
            campaignIdFilter = new { include = new[] { campaignId } },
            adGroupIdFilter = string.IsNullOrWhiteSpace(adGroupId) ? null : new { include = new[] { adGroupId } },
            stateFilter = new { include = new[] { "ENABLED", "PAUSED" } }
        }, _jsonOpts);

        var req = BuildRequest(HttpMethod.Post, $"{account.BaseUrl}/sp/targets/list", token, account, SpTargetingClauseV3);
        req.Content = new StringContent(payload, Encoding.UTF8, SpTargetingClauseV3);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Amazon targets API {(int)resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("targetingClauses", out var targets) || targets.ValueKind != JsonValueKind.Array)
            return null;

        var normalizedNeedle = NormalizeTargetText(targetingText);
        AmazonTargetLookupDto? fallback = null;
        foreach (var el in targets.EnumerateArray())
        {
            var expressionText = ExpressionText(el);
            var dto = new AmazonTargetLookupDto
            {
                TargetId = JsonString(el, "targetId"),
                CampaignId = JsonString(el, "campaignId"),
                AdGroupId = JsonString(el, "adGroupId"),
                State = JsonString(el, "state"),
                Bid = JsonDecimal(el, "bid"),
                ExpressionText = expressionText
            };

            if (string.IsNullOrWhiteSpace(dto.TargetId))
                continue;

            fallback ??= dto;
            var normalizedCandidate = NormalizeTargetText(expressionText);
            if (normalizedCandidate.Contains(normalizedNeedle, StringComparison.OrdinalIgnoreCase) ||
                normalizedNeedle.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                return dto;
        }

        return fallback;
    }

    public async Task<AmazonKeywordLookupDto?> FindKeywordAsync(AmazonAccountConfig account, string campaignId, string? adGroupId, string? keywordText, string? matchType)
    {
        if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(keywordText))
            return null;

        var token = await _auth.GetAccessTokenAsync(account);
        var payload = JsonSerializer.Serialize(new
        {
            campaignIdFilter = new { include = new[] { campaignId } },
            adGroupIdFilter = string.IsNullOrWhiteSpace(adGroupId) ? null : new { include = new[] { adGroupId } },
            stateFilter = new { include = new[] { "ENABLED", "PAUSED" } }
        }, _jsonOpts);

        var req = BuildRequest(HttpMethod.Post, $"{account.BaseUrl}/sp/keywords/list", token, account, SpKeywordV3);
        req.Content = new StringContent(payload, Encoding.UTF8, SpKeywordV3);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Amazon keywords API {(int)resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("keywords", out var keywords) || keywords.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var el in keywords.EnumerateArray())
        {
            var dto = new AmazonKeywordLookupDto
            {
                KeywordId = JsonString(el, "keywordId"),
                CampaignId = JsonString(el, "campaignId"),
                AdGroupId = JsonString(el, "adGroupId"),
                State = JsonString(el, "state"),
                Bid = JsonDecimal(el, "bid"),
                KeywordText = JsonString(el, "keywordText"),
                MatchType = JsonString(el, "matchType")
            };

            if (string.IsNullOrWhiteSpace(dto.KeywordId))
                continue;

            var textMatches = string.Equals(dto.KeywordText?.Trim(), keywordText.Trim(), StringComparison.OrdinalIgnoreCase);
            var matchTypeMatches = string.IsNullOrWhiteSpace(matchType) ||
                                   string.Equals(dto.MatchType, matchType, StringComparison.OrdinalIgnoreCase);
            if (textMatches && matchTypeMatches)
                return dto;
        }

        return null;
    }

    public async Task<(bool success, string response, string requestJson)> UpdateTargetBidAsync(AmazonAccountConfig account, string targetId, decimal bid)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return (false, "Target ID is required to update a target bid.", "");
        if (bid <= 0)
            return (false, "Bid must be greater than 0.", "");

        var token = await _auth.GetAccessTokenAsync(account);
        var payload = JsonSerializer.Serialize(new
        {
            targetingClauses = new[] { new { targetId, bid } }
        }, _jsonOpts);

        var req = BuildRequest(HttpMethod.Put, $"{account.BaseUrl}/sp/targets", token, account, SpTargetingClauseV3);
        req.Content = new StringContent(payload, Encoding.UTF8, SpTargetingClauseV3);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode}: {raw}", payload);

        if (AmazonBulkResponseHasError(raw, "targetingClauses", out var error))
            return (false, error, payload);

        return (true, raw, payload);
    }

    public async Task<(bool success, string response, string requestJson)> UpdateKeywordBidAsync(AmazonAccountConfig account, string keywordId, decimal bid)
    {
        if (string.IsNullOrWhiteSpace(keywordId))
            return (false, "Keyword ID is required to update a keyword bid.", "");
        if (bid <= 0)
            return (false, "Bid must be greater than 0.", "");

        var token = await _auth.GetAccessTokenAsync(account);
        var payload = JsonSerializer.Serialize(new
        {
            keywords = new[] { new { keywordId, bid } }
        }, _jsonOpts);

        var req = BuildRequest(HttpMethod.Put, $"{account.BaseUrl}/sp/keywords", token, account, SpKeywordV3);
        req.Content = new StringContent(payload, Encoding.UTF8, SpKeywordV3);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode}: {raw}", payload);

        if (AmazonBulkResponseHasError(raw, "keywords", out var error))
            return (false, error, payload);

        return (true, raw, payload);
    }

    private static string NormalizeNegativeKeywordMatchType(string? matchType) =>
        (matchType ?? "").Trim().ToUpperInvariant() switch
        {
            "NEGATIVE_EXACT" or "EXACT" => "NEGATIVE_EXACT",
            "NEGATIVE_PHRASE" or "PHRASE" => "NEGATIVE_PHRASE",
            _ => "NEGATIVE_EXACT"
        };

    private static bool AmazonBulkResponseHasError(string raw, string envelopeName, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty(envelopeName, out var envelope) &&
                envelope.TryGetProperty("error", out var errors) &&
                errors.ValueKind == JsonValueKind.Array &&
                errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                error = first.TryGetProperty("errorDetails", out var details)
                    ? details.GetString() ?? raw
                    : raw;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string JsonString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var prop) ? prop.ToString() : "";

    private static decimal? JsonDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var value)) return value;
        return decimal.TryParse(prop.ToString(), out var parsed) ? parsed : null;
    }

    private static string ExpressionText(JsonElement el)
    {
        if (!el.TryGetProperty("expression", out var expression) || expression.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var item in expression.EnumerateArray())
        {
            var type = JsonString(item, "type");
            var value = JsonString(item, "value");
            if (!string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(value))
                parts.Add($"{type}={value}");
        }
        return string.Join(" ", parts);
    }

    private static string NormalizeTargetText(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        foreach (var token in new[] { "\"", "'", " ", "_", "-", "asin=", "asinexpanded=", "asin-expanded=", "asin_expanded_from=" })
            text = text.Replace(token, "", StringComparison.OrdinalIgnoreCase);
        return text;
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
