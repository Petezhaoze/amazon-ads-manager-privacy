using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace AmazonAdsManager.Api.Functions;

public class AccountsFunction
{
    private readonly AmazonAccountResolver _resolver;

    public AccountsFunction(AmazonAccountResolver resolver)
    {
        _resolver = resolver;
    }

    [Function("Accounts")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accounts")] HttpRequest req)
        => new OkObjectResult(ApiResult<IEnumerable<SafeAmazonAccountDto>>.Ok(_resolver.GetSafeList()));
}
