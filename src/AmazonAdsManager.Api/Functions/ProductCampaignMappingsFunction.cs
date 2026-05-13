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

    public ProductCampaignMappingsFunction(ProductCampaignMappingRepository repo) => _repo = repo;

    [Function("ListProductCampaigns")]
    public IActionResult List([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}/campaign-mappings")] HttpRequest req, string productId)
    {
        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var mappings = _repo.GetByProduct(accountKey, productId);
        return new OkObjectResult(ApiResult<IReadOnlyList<ProductCampaignMapping>>.Ok(mappings));
    }

    [Function("AddProductCampaign")]
    public async Task<IActionResult> Add([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/{productId}/campaign-mappings")] HttpRequest req, string productId)
    {
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

        if (mapping is null || mapping.CampaignId == 0)
            return new BadRequestObjectResult(ApiResult.Fail("campaignId is required"));

        mapping.ProductId = productId;
        var saved = _repo.Upsert(mapping);
        return new OkObjectResult(ApiResult<ProductCampaignMapping>.Ok(saved));
    }

    [Function("RemoveProductCampaign")]
    public IActionResult Remove([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "products/{productId}/campaign-mappings/{mappingId}")] HttpRequest req, string productId, string mappingId)
    {
        return _repo.Delete(mappingId)
            ? new OkObjectResult(ApiResult.Ok())
            : new NotFoundObjectResult(ApiResult.Fail("Mapping not found"));
    }
}
