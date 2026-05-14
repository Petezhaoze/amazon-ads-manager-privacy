using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.Json;

namespace AmazonAdsManager.Api.Functions;

public class ProductsFunction
{
    private readonly ProductProfileRepository _repo;
    private readonly AmazonAccountResolver _resolver;
    private readonly AmazonProductSyncService _sync;
    private readonly AmazonProductImageService _images;
    private readonly ApiAccessService _access;

    public ProductsFunction(ProductProfileRepository repo, AmazonAccountResolver resolver,
        AmazonProductSyncService sync, AmazonProductImageService images, ApiAccessService access)
    {
        _repo = repo;
        _resolver = resolver;
        _sync = sync;
        _images = images;
        _access = access;
    }

    [Function("GetProductImageUrl")]
    public async Task<IActionResult> GetImageUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "images/product")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var asin = req.Query["asin"].ToString();
        if (string.IsNullOrWhiteSpace(asin))
            return new BadRequestObjectResult(ApiResult.Fail("asin is required"));

        var url = await _images.GetImageUrlAsync(asin);
        return new OkObjectResult(ApiResult<string?>.Ok(url));
    }

    [Function("SyncProducts")]
    public async Task<IActionResult> Sync([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/sync")] HttpRequest req)
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
            var result = await _sync.SyncAsync(account);
            return new OkObjectResult(ApiResult<SyncResult>.Ok(result));
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }

    [Function("ListProducts")]
    public IActionResult List([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var products = _repo.GetByAccount(accountKey);
        return new OkObjectResult(ApiResult<IReadOnlyList<ProductProfile>>.Ok(products));
    }

    [Function("CreateProduct")]
    public async Task<IActionResult> Create([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        ProductProfile? product;
        try
        {
            product = await JsonSerializer.DeserializeAsync<ProductProfile>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return new BadRequestObjectResult(ApiResult.Fail("Invalid JSON"));
        }

        if (product is null || string.IsNullOrWhiteSpace(product.AccountKey) || string.IsNullOrWhiteSpace(product.ASIN))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey and ASIN are required"));

        var saved = _repo.Upsert(product);
        return new OkObjectResult(ApiResult<ProductProfile>.Ok(saved));
    }

    [Function("GetProduct")]
    public IActionResult Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}")] HttpRequest req, string productId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var product = _repo.GetById(productId);
        if (product is null)
            return new NotFoundObjectResult(ApiResult.Fail($"Product '{productId}' not found"));

        return new OkObjectResult(ApiResult<ProductProfile>.Ok(product));
    }

    [Function("UpdateProduct")]
    public async Task<IActionResult> Update([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "products/{productId}")] HttpRequest req, string productId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var existing = _repo.GetById(productId);
        if (existing is null)
            return new NotFoundObjectResult(ApiResult.Fail("Product not found"));

        ProductProfile? product;
        try
        {
            product = await JsonSerializer.DeserializeAsync<ProductProfile>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return new BadRequestObjectResult(ApiResult.Fail("Invalid JSON"));
        }

        if (product is null) return new BadRequestObjectResult(ApiResult.Fail("Invalid product"));
        product.Id = productId;
        var saved = _repo.Upsert(product);
        return new OkObjectResult(ApiResult<ProductProfile>.Ok(saved));
    }
}
