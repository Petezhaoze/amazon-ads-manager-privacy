using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AmazonAdsManager.Api.Functions;

public class RunScheduleFunction
{
    private readonly ScheduleRunnerService _runner;
    private readonly IConfiguration _config;
    private readonly ILogger<RunScheduleFunction> _logger;
    private readonly ApiAccessService _access;

    public RunScheduleFunction(ScheduleRunnerService runner, IConfiguration config, ILogger<RunScheduleFunction> logger, ApiAccessService access)
    {
        _runner = runner;
        _config = config;
        _logger = logger;
        _access = access;
    }

    [Function("RunSchedule")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "run-schedule")] HttpRequest req)
    {
        var expectedKey = _config["RunnerKey"];
        if (!string.IsNullOrEmpty(expectedKey))
        {
            if (!req.Headers.TryGetValue("x-runner-key", out var provided) || provided != expectedKey)
                return new UnauthorizedResult();
        }
        else
        {
            var unauthorized = _access.RequireAuthorized(req);
            if (unauthorized is not null) return unauthorized;
        }

        try
        {
            await _runner.RunAsync();
            return new OkObjectResult(ApiResult.Ok());
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }
}
