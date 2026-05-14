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
    private readonly IHttpClientFactory _httpFactory;

    public ProductsFunction(ProductProfileRepository repo, AmazonAccountResolver resolver,
        AmazonProductSyncService sync, AmazonProductImageService images, IHttpClientFactory httpFactory)
    {
        _repo = repo;
        _resolver = resolver;
        _sync = sync;
        _images = images;
        _httpFactory = httpFactory;
    }

    [Function("GetProductImageUrl")]
    public async Task<IActionResult> GetImageUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "images/product")] HttpRequest req)
    {
        var asin = req.Query["asin"].ToString();
        if (string.IsNullOrWhiteSpace(asin))
            return new BadRequestObjectResult(ApiResult.Fail("asin is required"));

        var url = await _images.GetImageUrlAsync(asin);
        return new OkObjectResult(ApiResult<string?>.Ok(url));
    }

    [Function("DebugProductTitle")]
    public async Task<IActionResult> DebugTitle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "debug/title/{asin}")] HttpRequest req, string asin)
    {
        // Test the named client directly to diagnose decompression
        var http = _httpFactory.CreateClient("amazon-scraper");
        var rawReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"https://www.amazon.com/dp/{asin}");
        rawReq.Headers.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        rawReq.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        rawReq.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        var resp = await http.SendAsync(rawReq);
        var body = await resp.Content.ReadAsStringAsync();
        var hasTitle = body.Contains("productTitle");
        var isGzip = body.Length > 0 && body[0] == '';

        var serviceTitle = await _images.GetProductTitleAsync(asin);
        return new OkObjectResult(new
        {
            asin,
            httpStatus = (int)resp.StatusCode,
            hasProductTitleInBody = hasTitle,
            isStillGzip = isGzip,
            serviceTitle,
            bodyStart = body.Length > 200 ? body[..200] : body
        });
    }

    [Function("SyncProducts")]
    public async Task<IActionResult> Sync([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/sync")] HttpRequest req)
    {
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
        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var products = _repo.GetByAccount(accountKey);
        return new OkObjectResult(ApiResult<IReadOnlyList<ProductProfile>>.Ok(products));
    }

    [Function("CreateProduct")]
    public async Task<IActionResult> Create([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products")] HttpRequest req)
    {
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
        var product = _repo.GetById(productId);
        if (product is null)
            return new NotFoundObjectResult(ApiResult.Fail($"Product '{productId}' not found"));

        return new OkObjectResult(ApiResult<ProductProfile>.Ok(product));
    }

    [Function("UpdateProduct")]
    public async Task<IActionResult> Update([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "products/{productId}")] HttpRequest req, string productId)
    {
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
