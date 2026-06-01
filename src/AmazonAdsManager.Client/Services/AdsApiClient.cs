using AmazonAdsManager.Shared.Models;
using System.Net.Http.Json;

namespace AmazonAdsManager.Client.Services;

public class AdsApiClient
{
    private readonly HttpClient _http;

    public AdsApiClient(HttpClient http) => _http = http;

    public async Task<List<SafeAmazonAccountDto>> GetAccountsAsync()
    {
        var result = await _http.GetFromJsonAsync<ApiResult<List<SafeAmazonAccountDto>>>("accounts");
        return result?.Data ?? new();
    }

    public async Task<List<CampaignDto>> GetCampaignsAsync(string accountKey)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<List<CampaignDto>>>($"campaigns?accountKey={accountKey}");
        return result?.Data ?? new();
    }

    public async Task<bool> ToggleCampaignAsync(string accountKey, string campaignId, string state, string? campaignName = null, string? asin = null)
    {
        var resp = await _http.PostAsJsonAsync("campaigns/toggle",
            new CampaignStateUpdateRequest { AccountKey = accountKey, CampaignId = campaignId, State = state, CampaignName = campaignName, Asin = asin });
        return resp.IsSuccessStatusCode;
    }

    public async Task<List<CampaignSchedule>> GetSchedulesAsync(string accountKey)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<List<CampaignSchedule>>>($"schedules?accountKey={accountKey}");
        return result?.Data ?? new();
    }

    public async Task<CampaignSchedule?> SaveScheduleAsync(CampaignSchedule schedule)
    {
        var resp = await _http.PostAsJsonAsync("schedules", schedule);
        if (!resp.IsSuccessStatusCode) return null;
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<CampaignSchedule>>();
        return result?.Data;
    }

    public async Task<bool> DeleteScheduleAsync(string id)
    {
        var resp = await _http.DeleteAsync($"schedules/{id}");
        return resp.IsSuccessStatusCode;
    }

    // Product APIs
    public async Task<List<ProductProfile>> GetProductsAsync(string accountKey)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<List<ProductProfile>>>($"products?accountKey={accountKey}");
        return result?.Data ?? new();
    }

    public async Task<List<ProductProfile>> GetProductsWithCampaignsAsync(string accountKey, bool activeCampaignsOnly = false)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<ApiResult<List<ProductProfile>>>(
                $"products/with-campaigns?accountKey={Url(accountKey)}&activeCampaignsOnly={activeCampaignsOnly.ToString().ToLowerInvariant()}");
            return result?.Data ?? new();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new();
        }
    }

    public async Task<ProductProfile?> GetProductAsync(string productId)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<ProductProfile>>($"products/{productId}");
        return result?.Data;
    }

    public async Task<ProductProfile?> CreateProductAsync(ProductProfile product)
    {
        var resp = await _http.PostAsJsonAsync("products", product);
        if (!resp.IsSuccessStatusCode) return null;
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<ProductProfile>>();
        return result?.Data;
    }

    public async Task<ProductProfile?> UpdateProductAsync(string productId, ProductProfile product)
    {
        var resp = await _http.PutAsJsonAsync($"products/{productId}", product);
        if (!resp.IsSuccessStatusCode) return null;
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<ProductProfile>>();
        return result?.Data;
    }

    public async Task<List<ProductCampaignMapping>> GetProductCampaignsAsync(string accountKey, string productId)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<List<ProductCampaignMapping>>>($"products/{productId}/campaign-mappings?accountKey={accountKey}");
        return result?.Data ?? new();
    }

    public async Task<ProductAiAnalysisResult?> AnalyzeProductV2Async(
        string accountKey,
        string productId,
        DateOnly? dateRangeStart = null,
        DateOnly? dateRangeEnd = null,
        bool ensureAmcData = false)
    {
        var query = $"accountKey={Url(accountKey)}";
        if (dateRangeStart is not null)
            query += $"&dateRangeStart={dateRangeStart.Value:yyyy-MM-dd}";
        if (dateRangeEnd is not null)
            query += $"&dateRangeEnd={dateRangeEnd.Value:yyyy-MM-dd}";
        if (ensureAmcData)
            query += "&ensureAmcData=true";

        var resp = await _http.PostAsync($"products/{Url(productId)}/analyze-v2?{query}", null);
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<ProductAiAnalysisResult>>();
        if (resp.IsSuccessStatusCode)
            return result?.Data;

        return new ProductAiAnalysisResult
        {
            Success = false,
            IsAiGenerated = false,
            UsedFallback = false,
            Error = result?.Error ?? $"AI analysis failed with HTTP {(int)resp.StatusCode}.",
            ErrorMessage = result?.Error ?? $"AI analysis failed with HTTP {(int)resp.StatusCode}.",
            V2Recommendations = []
        };
    }

    public async Task<AiReviewDataCoverageDto?> GetAiReviewDataCoverageAsync(
        string accountKey,
        string productId,
        DateOnly dateRangeStart,
        DateOnly dateRangeEnd)
    {
        var resp = await _http.GetAsync(
            $"products/{Url(productId)}/ai-review/data-coverage?accountKey={Url(accountKey)}&dateRangeStart={dateRangeStart:yyyy-MM-dd}&dateRangeEnd={dateRangeEnd:yyyy-MM-dd}");
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<AiReviewDataCoverageDto>>();
        if (resp.IsSuccessStatusCode)
            return result?.Data;

        throw new HttpRequestException(result?.Error ?? $"AI Review data coverage failed with HTTP {(int)resp.StatusCode}.", null, resp.StatusCode);
    }

    public async Task<AiReviewRefreshJobDto?> StartAiReviewDataRefreshAsync(
        string accountKey,
        string productId,
        DateOnly dateRangeStart,
        DateOnly dateRangeEnd)
    {
        var resp = await _http.PostAsync(
            $"products/{Url(productId)}/ai-review/refresh?accountKey={Url(accountKey)}&dateRangeStart={dateRangeStart:yyyy-MM-dd}&dateRangeEnd={dateRangeEnd:yyyy-MM-dd}",
            null);
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<AiReviewRefreshJobDto>>();
        if (resp.IsSuccessStatusCode)
            return result?.Data;

        throw new HttpRequestException(result?.Error ?? $"AI Review report refresh failed with HTTP {(int)resp.StatusCode}.", null, resp.StatusCode);
    }

    public async Task<AiReviewRefreshJobDto?> GetAiReviewDataRefreshStatusAsync(
        string accountKey,
        string productId,
        DateOnly dateRangeStart,
        DateOnly dateRangeEnd)
    {
        var resp = await _http.GetAsync(
            $"products/{Url(productId)}/ai-review/refresh-status?accountKey={Url(accountKey)}&dateRangeStart={dateRangeStart:yyyy-MM-dd}&dateRangeEnd={dateRangeEnd:yyyy-MM-dd}");
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<AiReviewRefreshJobDto>>();
        if (resp.IsSuccessStatusCode)
            return result?.Data;

        throw new HttpRequestException(result?.Error ?? $"AI Review refresh status failed with HTTP {(int)resp.StatusCode}.", null, resp.StatusCode);
    }

    public async Task<AnalyticsImportResult?> RunReportImportAsync(
        string accountKey,
        DateOnly? dateRangeStart = null,
        DateOnly? dateRangeEnd = null,
        CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("reports/run-import", new AnalyticsImportRequest
        {
            AccountKey = accountKey,
            DateRangeStart = dateRangeStart,
            DateRangeEnd = dateRangeEnd
        }, ct);
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<AnalyticsImportResult>>(cancellationToken: ct);
        if (resp.IsSuccessStatusCode)
            return result?.Data;

        throw new HttpRequestException(result?.Error ?? $"Report import failed with HTTP {(int)resp.StatusCode}.", null, resp.StatusCode);
    }

    public async Task<AmcHourlyDataStatusDto?> GetAmcHourlyDataStatusAsync(
        string accountKey,
        string productId,
        DateOnly dateRangeStart,
        DateOnly dateRangeEnd)
    {
        var resp = await _http.GetAsync(
            $"products/{Url(productId)}/amc-hourly-status?accountKey={Url(accountKey)}&dateRangeStart={dateRangeStart:yyyy-MM-dd}&dateRangeEnd={dateRangeEnd:yyyy-MM-dd}");
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<AmcHourlyDataStatusDto>>();
        if (resp.IsSuccessStatusCode)
            return result?.Data;

        throw new HttpRequestException(result?.Error ?? $"AMC hourly status failed with HTTP {(int)resp.StatusCode}.", null, resp.StatusCode);
    }

    public async Task<List<AiRecommendationDto>> GetRecommendationsV2Async(string accountKey, string productId)
    {
        var resp = await _http.GetAsync($"products/{Url(productId)}/recommendations-v2?accountKey={Url(accountKey)}");
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<List<AiRecommendationDto>>>();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(result?.Error ?? $"Saved recommendations failed with HTTP {(int)resp.StatusCode}.", null, resp.StatusCode);

        return result?.Data ?? new();
    }

    public async Task<List<HourlyScorecardDto>> GetHourlyScorecardAsync(string accountKey, string productId)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<List<HourlyScorecardDto>>>($"products/{Url(productId)}/scorecard/hourly?accountKey={Url(accountKey)}");
        return result?.Data ?? new();
    }

    public async Task<AmcStatusDto?> GetAmcStatusAsync(string accountKey)
    {
        var resp = await _http.GetAsync($"amc/status?accountKey={Url(accountKey)}");
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<AmcStatusDto>>();
        if (resp.IsSuccessStatusCode)
            return result?.Data;

        return new AmcStatusDto
        {
            IsConfigured = false,
            IsAuthorized = false,
            AccountKey = accountKey,
            Message = "AMC status check failed.",
            Error = result?.Error ?? $"AMC status failed with HTTP {(int)resp.StatusCode}."
        };
    }

    public async Task<AiRuntimeStatusDto?> GetAiRuntimeStatusAsync()
    {
        var resp = await _http.GetAsync("ai/status");
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<AiRuntimeStatusDto>>();
        if (resp.IsSuccessStatusCode)
            return result?.Data;

        return new AiRuntimeStatusDto
        {
            IsConfigured = false,
            Message = result?.Error ?? $"AI status failed with HTTP {(int)resp.StatusCode}."
        };
    }

    public async Task<TechnicalRecommendationDetailsDto?> GetRecommendationTechnicalDetailsAsync(string accountKey, string productId, string recommendationId)
    {
        var resp = await _http.GetAsync(
            $"products/{Url(productId)}/recommendations/{Url(recommendationId)}/technical-details?accountKey={Url(accountKey)}");

        if (!resp.IsSuccessStatusCode)
        {
            var failed = await resp.Content.ReadFromJsonAsync<ApiResult<TechnicalRecommendationDetailsDto>>();
            throw new HttpRequestException(failed?.Error ?? $"Technical details failed with HTTP {(int)resp.StatusCode}.", null, resp.StatusCode);
        }

        var result = await resp.Content.ReadFromJsonAsync<ApiResult<TechnicalRecommendationDetailsDto>>();
        return result?.Data;
    }

    public async Task<List<BeforeAfterComparisonDto>> GetExperimentsAsync(string productId)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<List<BeforeAfterComparisonDto>>>($"products/{productId}/experiments");
        return result?.Data ?? new();
    }

    public async Task<bool> ApproveRecommendationV2Async(string recommendationId)
    {
        var resp = await _http.PostAsync($"recommendations/{recommendationId}/approve-v2", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<RecommendationReviewDto?> GetRecommendationApplyReviewAsync(string accountKey, string productId, string recommendationId)
    {
        var resp = await _http.GetAsync($"products/{Url(productId)}/recommendations/{Url(recommendationId)}/apply-review?accountKey={Url(accountKey)}");
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<RecommendationReviewDto>>();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(result?.Error ?? $"Apply review failed with HTTP {(int)resp.StatusCode}.", null, resp.StatusCode);
        return result?.Data;
    }

    public async Task<ApplyRecommendationResult?> ApplyRecommendationChangesAsync(string recommendationId, ApplyRecommendationRequest request)
    {
        var resp = await _http.PostAsJsonAsync($"recommendations/{Url(recommendationId)}/apply-v2", request);
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<ApplyRecommendationResult>>();
        if (!resp.IsSuccessStatusCode)
            return new ApplyRecommendationResult
            {
                Success = false,
                Status = "ApplyFailed",
                Error = result?.Error ?? $"Apply failed with HTTP {(int)resp.StatusCode}.",
                Message = result?.Error ?? $"Apply failed with HTTP {(int)resp.StatusCode}."
            };
        return result?.Data;
    }

    public async Task<RecommendationAiAnswerDto?> AskRecommendationAiAsync(string recommendationId, RecommendationAiQuestionRequest request)
    {
        var resp = await _http.PostAsJsonAsync($"recommendations/{Url(recommendationId)}/ask-ai", request);
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<RecommendationAiAnswerDto>>();
        if (!resp.IsSuccessStatusCode)
            return new RecommendationAiAnswerDto
            {
                Success = false,
                Error = result?.Error ?? $"AI request failed with HTTP {(int)resp.StatusCode}."
            };
        return result?.Data;
    }

    public async Task<bool> IgnoreRecommendationV2Async(string recommendationId)
    {
        var resp = await _http.PostAsync($"recommendations/{recommendationId}/ignore-v2", null);
        return resp.IsSuccessStatusCode;
    }


    public async Task<bool> EditRecommendationV2Async(string recommendationId, string editedAction)
    {
        var resp = await _http.PostAsJsonAsync($"recommendations/{recommendationId}/edit-v2", editedAction);
        return resp.IsSuccessStatusCode;
    }

    public async Task<string?> GetProductImageUrlAsync(string asin)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<string?>>($"images/product?asin={Uri.EscapeDataString(asin)}");
        return result?.Data;
    }

    public async Task<List<AmazonAdsProfile>> ResolveProfilesAsync(string accountKey)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<List<AmazonAdsProfile>>>($"auth/resolve-profiles?accountKey={Uri.EscapeDataString(accountKey)}");
        return result?.Data ?? new();
    }

    public async Task<bool> UpdateAccountProfileAsync(string accountKey, string profileId)
    {
        var resp = await _http.PostAsJsonAsync("auth/update-profile", new UpdateProfileRequest { AccountKey = accountKey, ProfileId = profileId });
        return resp.IsSuccessStatusCode;
    }

    public async Task<SyncResult?> SyncProductsAsync(string accountKey)
    {
        var resp = await _http.PostAsync($"products/sync?accountKey={accountKey}", null);
        if (!resp.IsSuccessStatusCode) return null;
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<SyncResult>>();
        return result?.Data;
    }

    public async Task<List<CampaignActionLog>> GetLogsAsync(string accountKey, int limit = 200)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<List<CampaignActionLog>>>($"logs?accountKey={accountKey}&limit={limit}");
        return result?.Data ?? new();
    }

    private static string Url(string value) => Uri.EscapeDataString(value);

    // OAuth / account connect
    public async Task<OAuthLoginUrlResponse?> GetLoginUrlAsync()
    {
        var result = await _http.GetFromJsonAsync<ApiResult<OAuthLoginUrlResponse>>("auth/login-url");
        return result?.Data;
    }

    public async Task<OAuthPendingResult?> GetOAuthPendingAsync(string state)
    {
        var result = await _http.GetFromJsonAsync<ApiResult<OAuthPendingResult>>($"auth/pending?state={Uri.EscapeDataString(state)}");
        return result?.Data;
    }

    public async Task<SafeAmazonAccountDto?> SaveAccountAsync(SaveAccountRequest request)
    {
        var resp = await _http.PostAsJsonAsync("auth/save-account", request);
        if (!resp.IsSuccessStatusCode) return null;
        var result = await resp.Content.ReadFromJsonAsync<ApiResult<SafeAmazonAccountDto>>();
        return result?.Data;
    }
}
