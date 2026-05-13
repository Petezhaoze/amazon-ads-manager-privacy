using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AmazonAdsManager.Api.Services;

public class ScheduleRunnerService
{
    private readonly ScheduleRepository _repo;
    private readonly AmazonAccountResolver _resolver;
    private readonly AmazonCampaignService _campaigns;
    private readonly CampaignLogRepository _logs;
    private readonly ILogger<ScheduleRunnerService> _logger;

    public ScheduleRunnerService(
        ScheduleRepository repo,
        AmazonAccountResolver resolver,
        AmazonCampaignService campaigns,
        CampaignLogRepository logs,
        ILogger<ScheduleRunnerService> logger)
    {
        _repo = repo;
        _resolver = resolver;
        _campaigns = campaigns;
        _logs = logs;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var schedules = _repo.GetAll();
        _logger.LogInformation("Running schedule check for {Count} schedule(s)", schedules.Count);

        foreach (var schedule in schedules)
        {
            try
            {
                var account = _resolver.Resolve(schedule.AccountKey);
                if (account is null)
                {
                    _logger.LogWarning("Account {Key} not found for schedule {Id}", schedule.AccountKey, schedule.Id);
                    continue;
                }

                var tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                var dayIndex = (int)localNow.DayOfWeek;
                var hourIndex = localNow.Hour;

                var shouldBePaused = schedule.PauseHours[dayIndex][hourIndex];
                var desiredState = shouldBePaused ? "paused" : "enabled";

                if (desiredState == schedule.LastKnownState)
                {
                    _logger.LogDebug("Campaign {Id} already in state {State}", schedule.CampaignId, desiredState);
                    continue;
                }

                var (verifiedState, error) = await _campaigns.UpdateCampaignStateAsync(account, schedule.CampaignId, desiredState);

                _logs.Add(new CampaignActionLog
                {
                    AccountKey = schedule.AccountKey,
                    CampaignId = schedule.CampaignId,
                    CampaignName = schedule.CampaignName,
                    Asin = schedule.Asin,
                    RequestedState = desiredState,
                    VerifiedState = verifiedState,
                    Success = verifiedState is not null,
                    ErrorMessage = error,
                    Source = "scheduler"
                });

                if (verifiedState is not null)
                {
                    schedule.LastKnownState = verifiedState;
                    _repo.Upsert(schedule);
                    _logger.LogInformation("Campaign {Name} ({Id}) set to {State}", schedule.CampaignName, schedule.CampaignId, verifiedState);
                }
                else
                {
                    _logger.LogError("Failed to set campaign {Name} ({Id}) to {State}: {Error}", schedule.CampaignName, schedule.CampaignId, desiredState, error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing schedule {Id}", schedule.Id);
            }
        }
    }
}
