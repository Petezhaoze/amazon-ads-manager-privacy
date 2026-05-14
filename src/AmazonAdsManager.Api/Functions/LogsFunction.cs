using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace AmazonAdsManager.Api.Functions;

public class LogsFunction
{
    private readonly CampaignLogRepository _logs;
    private readonly ApiAccessService _access;

    public LogsFunction(CampaignLogRepository logs, ApiAccessService access)
    {
        _logs = logs;
        _access = access;
    }

    [Function("GetLogs")]
    public IActionResult Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logs")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        _ = int.TryParse(req.Query["limit"].ToString(), out var limit);
        if (limit <= 0) limit = 200;

        var entries = _logs.GetByAccount(accountKey, limit);
        return new OkObjectResult(ApiResult<IReadOnlyList<CampaignActionLog>>.Ok(entries));
    }
}
