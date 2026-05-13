using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace AmazonAdsManager.Api.Functions;

public class LoadMockDataFunction
{
    private readonly MockProductReportImportService _importer;

    public LoadMockDataFunction(MockProductReportImportService importer) => _importer = importer;

    [Function("LoadMockProductData")]
    public IActionResult Load([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/mock-load")] HttpRequest req)
    {
        var accountKey = req.Query["accountKey"].ToString();

        try
        {
            if (string.IsNullOrWhiteSpace(accountKey))
            {
                _importer.LoadMockDataForAllAccounts();
                return new OkObjectResult(ApiResult.Ok("Mock data loaded for all accounts"));
            }
            else
            {
                _importer.LoadMockDataForAccount(accountKey);
                return new OkObjectResult(ApiResult.Ok($"Mock data loaded for account '{accountKey}'"));
            }
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }
}
