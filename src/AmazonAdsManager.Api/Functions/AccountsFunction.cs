using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace AmazonAdsManager.Api.Functions;

public class AccountsFunction
{
    private readonly AmazonAccountResolver _resolver;
    private readonly ApiAccessService _access;

    public AccountsFunction(AmazonAccountResolver resolver, ApiAccessService access)
    {
        _resolver = resolver;
        _access = access;
    }

    [Function("Accounts")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accounts")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        return new OkObjectResult(ApiResult<IEnumerable<SafeAmazonAccountDto>>.Ok(_resolver.GetSafeList()));
    }
}
