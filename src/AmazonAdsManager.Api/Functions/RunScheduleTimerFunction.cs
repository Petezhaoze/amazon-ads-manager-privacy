using AmazonAdsManager.Api.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AmazonAdsManager.Api.Functions;

public class RunScheduleTimerFunction(ScheduleRunnerService runner, ILogger<RunScheduleTimerFunction> logger)
{
    [Function("RunScheduleTimer")]
    public async Task Run([TimerTrigger("0 0 * * * *")] TimerInfo timer)
    {
        logger.LogInformation("Hourly schedule timer fired at {Time}", DateTime.UtcNow);
        await runner.RunAsync();
    }
}
