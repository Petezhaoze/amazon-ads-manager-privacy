using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.Json;

namespace AmazonAdsManager.Api.Functions;

public class ProductCampaignMappingsFunction
{
    private readonly ProductCampaignMappingRepository _repo;
    private readonly ApiAccessService _access;

    public ProductCampaignMappingsFunction(ProductCampaignMappingRepository repo, ApiAccessService access)
    {
        _repo = repo;
        _access = access;
    }

    [Function("ListProductCampaigns")]
    public IActionResult List([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}/campaign-mappings")] HttpRequest req, string productId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var mappings = _repo.GetByProduct(accountKey, productId);
        return new OkObjectResult(ApiResult<IReadOnlyList<ProductCampaignMapping>>.Ok(mappings));
    }

    [Function("AddProductCampaign")]
    public async Task<IActionResult> Add([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/{productId}/campaign-mappings")] HttpRequest req, string productId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        ProductCampaignMapping? mapping;
        try
        {
            mapping = await JsonSerializer.DeserializeAsync<ProductCampaignMapping>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return new BadRequestObjectResult(ApiResult.Fail("Invalid JSON"));
        }

        if (mapping is null || string.IsNullOrWhiteSpace(mapping.CampaignId))
            return new BadRequestObjectResult(ApiResult.Fail("campaignId is required"));

        mapping.ProductId = productId;
        var saved = _repo.Upsert(mapping);
        return new OkObjectResult(ApiResult<ProductCampaignMapping>.Ok(saved));
    }

    [Function("RemoveProductCampaign")]
    public IActionResult Remove([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "products/{productId}/campaign-mappings/{mappingId}")] HttpRequest req, string productId, string mappingId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        return _repo.Delete(mappingId)
            ? new OkObjectResult(ApiResult.Ok())
            : new NotFoundObjectResult(ApiResult.Fail("Mapping not found"));
    }
}
