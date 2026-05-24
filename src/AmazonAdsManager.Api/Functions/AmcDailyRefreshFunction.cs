using AmazonAdsManager.Api.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AmazonAdsManager.Api.Functions;

// Daily AMC refresh. Runs once per day to keep dbo.AmcQueryCoverage warm so the
// AI Review path almost never has to wait on AMC.
//
// Strategy:
//   1. For each configured account, delete coverage rows for the last EnsureRecentRefreshDays
//      days. AMC has 1-3 day reporting latency and late-arriving attribution data, so those
//      days are restamped each run.
//   2. Call EnsureWorkflowsAsync over the last EnsureWindowDays days. Already-Queried days
//      are skipped automatically by the coverage table, so the cost is bounded to whatever
//      is genuinely new or stale-reset.
//
// The same call also polls yesterday's still-Pending executions and imports any that have
// finished, so daily ingestion is fully self-driving.
public class AmcDailyRefreshFunction
{
    private const int EnsureWindowDays = 7;
    private const int EnsureRecentRefreshDays = 3;

    private readonly AmazonAccountResolver _accounts;
    private readonly AmcWorkflowService _workflows;
    private readonly AdMetricsRepository _metrics;
    private readonly ILogger<AmcDailyRefreshFunction> _logger;

    public AmcDailyRefreshFunction(
        AmazonAccountResolver accounts,
        AmcWorkflowService workflows,
        AdMetricsRepository metrics,
        ILogger<AmcDailyRefreshFunction> logger)
    {
        _accounts = accounts;
        _workflows = workflows;
        _metrics = metrics;
        _logger = logger;
    }

    [Function("AmcDailyRefresh")]
    public async Task Run([TimerTrigger("0 0 8 * * *")] TimerInfo timer)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var end = today.AddDays(-1);
        var start = end.AddDays(-(EnsureWindowDays - 1));
        var staleStart = end.AddDays(-(EnsureRecentRefreshDays - 1));

        var accountKeys = _accounts.GetSafeList().Select(a => a.AccountKey).ToList();
        _logger.LogInformation(
            "AMC daily refresh starting for {Count} account(s); window {Start}..{End}, stale-reset {StaleStart}..{End}",
            accountKeys.Count, start, end, staleStart);

        foreach (var accountKey in accountKeys)
        {
            try
            {
                _metrics.DeleteAmcCoverage(accountKey, staleStart, end);
                var result = await _workflows.EnsureWorkflowsAsync(accountKey, start, end);

                var imported = result.ImportedRowsByType.Sum(p => p.Value);
                var started = result.StartedExecutionIdsByType.Sum(p => p.Value.Count);
                _logger.LogInformation(
                    "AMC daily refresh {AccountKey}: imported {Imported} rows from completed executions; started {Started} new executions; warnings={Warnings}",
                    accountKey, imported, started, result.Warnings.Count);

                foreach (var warning in result.Warnings)
                    _logger.LogInformation("AMC daily refresh {AccountKey} warning: {Warning}", accountKey, warning);
            }
            catch (AnalyticsDatabaseNotConfiguredException)
            {
                _logger.LogWarning("AMC daily refresh skipped {AccountKey}: AnalyticsDb:ConnectionString is not configured.", accountKey);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AMC daily refresh failed for account {AccountKey}", accountKey);
            }
        }
    }
}
