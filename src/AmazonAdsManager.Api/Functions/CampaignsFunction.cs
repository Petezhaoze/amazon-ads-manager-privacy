using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.Json;

namespace AmazonAdsManager.Api.Functions;

public class CampaignsFunction
{
    private readonly AmazonAccountResolver _resolver;
    private readonly AmazonCampaignService _campaigns;
    private readonly ProductCampaignMappingRepository _mappings;
    private readonly ProductProfileRepository _products;
    private readonly CampaignLogRepository _logs;
    private readonly ApiAccessService _access;

    public CampaignsFunction(AmazonAccountResolver resolver, AmazonCampaignService campaigns,
        ProductCampaignMappingRepository mappings, ProductProfileRepository products,
        CampaignLogRepository logs, ApiAccessService access)
    {
        _resolver = resolver;
        _campaigns = campaigns;
        _mappings = mappings;
        _products = products;
        _logs = logs;
        _access = access;
    }

    [Function("ListCampaigns")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "campaigns")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var account = _resolver.Resolve(accountKey);
        if (account is null)
            return new NotFoundObjectResult(ApiResult.Fail($"Account '{accountKey}' not found"));

        try
        {
            var list = await _campaigns.ListCampaignsAsync(account);

            // Attach ASIN to each campaign from synced mappings (best-effort)
            var mappings = _mappings.GetByAccount(accountKey);
            var productDict = _products.GetByAccount(accountKey)
                .ToDictionary(p => p.Id, p => p.ASIN, StringComparer.OrdinalIgnoreCase);
            var campaignToAsin = mappings
                .GroupBy(m => m.CampaignId)
                .ToDictionary(g => g.Key, g =>
                    productDict.TryGetValue(g.First().ProductId, out var asin) ? asin : "");

            foreach (var c in list)
                if (campaignToAsin.TryGetValue(c.CampaignId, out var asin) && !string.IsNullOrEmpty(asin))
                    c.Asin = asin;

            return new OkObjectResult(ApiResult<List<CampaignDto>>.Ok(list));
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }

    [Function("ToggleCampaign")]
    public async Task<IActionResult> Toggle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "campaigns/toggle")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        CampaignStateUpdateRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<CampaignStateUpdateRequest>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return new BadRequestObjectResult(ApiResult.Fail("Invalid JSON body"));
        }

        if (body is null || string.IsNullOrWhiteSpace(body.AccountKey) ||
            string.IsNullOrWhiteSpace(body.CampaignId) || string.IsNullOrWhiteSpace(body.State))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey, campaignId, and state are required"));

        if (body.State is not "enabled" and not "paused")
            return new BadRequestObjectResult(ApiResult.Fail("state must be 'enabled' or 'paused'"));

        var account = _resolver.Resolve(body.AccountKey);
        if (account is null)
            return new NotFoundObjectResult(ApiResult.Fail($"Account '{body.AccountKey}' not found"));

        try
        {
            var (verifiedState, error) = await _campaigns.UpdateCampaignStateAsync(account, body.CampaignId, body.State);

            _logs.Add(new CampaignActionLog
            {
                AccountKey = body.AccountKey,
                CampaignId = body.CampaignId,
                CampaignName = body.CampaignName ?? body.CampaignId,
                Asin = body.Asin,
                RequestedState = body.State,
                VerifiedState = verifiedState,
                Success = verifiedState is not null,
                ErrorMessage = error,
                Source = "manual"
            });

            if (verifiedState is not null)
                return new OkObjectResult(ApiResult.Ok());

            return new ObjectResult(ApiResult.Fail(error ?? "Amazon rejected the update")) { StatusCode = 502 };
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }
}
