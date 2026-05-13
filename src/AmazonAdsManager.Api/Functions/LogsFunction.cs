using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace AmazonAdsManager.Api.Functions;

public class LogsFunction
{
    private readonly CampaignLogRepository _logs;

    public LogsFunction(CampaignLogRepository logs) => _logs = logs;

    [Function("GetLogs")]
    public IActionResult Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logs")] HttpRequest req)
    {
        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        _ = int.TryParse(req.Query["limit"].ToString(), out var limit);
        if (limit <= 0) limit = 200;

        var entries = _logs.GetByAccount(accountKey, limit);
        return new OkObjectResult(ApiResult<IReadOnlyList<CampaignActionLog>>.Ok(entries));
    }
}
