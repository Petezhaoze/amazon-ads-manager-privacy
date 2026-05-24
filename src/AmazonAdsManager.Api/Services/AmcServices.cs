using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace AmazonAdsManager.Api.Services;

public record AmcResultImportRequest(
    string AccountKey,
    string ResultType,
    string? ProfileId = null,
    string? TimeZone = null);

public sealed record AmcEnsureWorkflowsResult(
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, string> SqlByType,
    IReadOnlyDictionary<string, IReadOnlyList<string>> StartedExecutionIdsByType,
    IReadOnlyDictionary<string, int> ImportedRowsByType);

public class AmcWorkflowService
{
    private static readonly string[] HourlyResultTypes = { "traffic-hourly", "conversion-hourly" };

    private readonly IConfiguration _config;
    private readonly AmazonAccountResolver _accounts;
    private readonly AmazonAdsAuthService _auth;
    private readonly AmazonAdsOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AmcResultIngestionService _ingestion;
    private readonly AdMetricsRepository _metrics;
    private readonly ILogger<AmcWorkflowService> _logger;

    public AmcWorkflowService(
        IConfiguration config,
        AmazonAccountResolver accounts,
        AmazonAdsAuthService auth,
        IOptions<AmazonAdsOptions> options,
        IHttpClientFactory httpFactory,
        AmcResultIngestionService ingestion,
        AdMetricsRepository metrics,
        ILogger<AmcWorkflowService> logger)
    {
        _config = config;
        _accounts = accounts;
        _auth = auth;
        _options = options.Value;
        _httpFactory = httpFactory;
        _ingestion = ingestion;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<object> DiscoverAsync(string accountKey)
    {
        var account = _accounts.Resolve(accountKey)
            ?? throw new InvalidOperationException($"Account '{accountKey}' not found.");
        var http = _httpFactory.CreateClient();
        var token = await _auth.GetAccessTokenAsync(account);
        var amc = GetAmcConfiguration(account);

        var accounts = await SendAmcAsync(http, HttpMethod.Get, DiscoveryUrl(amc, "/amc/accounts"), token, amc, account.ProfileId, null, "application/json");

        AmcApiResponse? instances = null;
        if (!string.IsNullOrWhiteSpace(amc.AdvertiserId))
        {
            instances = await SendAmcAsync(http, HttpMethod.Get, DiscoveryUrl(amc, "/amc/instances"), token, amc, account.ProfileId, null, "application/json");
        }

        AmcApiResponse? dataSources = null;
        if (amc.IsConfigured)
            dataSources = await SendAmcAsync(http, HttpMethod.Get, ReportingUrl(amc, "/dataSources"), token, amc, account.ProfileId, null, "application/json");

        return new
        {
            AccountEndpoint = accounts.Status,
            Accounts = accounts.SafeJson,
            AdvertiserId = amc.AdvertiserId,
            MarketplaceId = amc.MarketplaceId,
            InstancesEndpoint = instances?.Status,
            Instances = instances?.SafeJson,
            ConfiguredInstanceId = amc.InstanceId,
            ConfiguredReportingEndpoint = amc.ReportingEndpoint,
            DataSourcesEndpoint = dataSources?.Status,
            DataSources = dataSources?.SafeJson,
            ManualConfigurationWarning = CountArray(accounts.Json, "amcAccounts") == 0 && amc.IsConfigured
                ? "AMC On-Demand instances return zero AMC accounts from /amc/accounts. Continuing with the configured Sponsored Ads entity and AMC instance."
                : null,
            LastRequestDiagnostics = dataSources?.Diagnostics ?? instances?.Diagnostics ?? accounts.Diagnostics
        };
    }

    public async Task<AmcStatusDto> GetStatusAsync(string accountKey)
    {
        var account = _accounts.Resolve(accountKey)
            ?? throw new InvalidOperationException($"Account '{accountKey}' not found.");
        var http = _httpFactory.CreateClient();
        var token = await _auth.GetAccessTokenAsync(account);
        var amc = GetAmcConfiguration(account);

        var accounts = await SendAmcAsync(http, HttpMethod.Get, DiscoveryUrl(amc, "/amc/accounts"), token, amc, account.ProfileId, null, "application/json");
        var amcAccountCount = CountArray(accounts.Json, "amcAccounts");

        AmcApiResponse? instances = null;
        if (!string.IsNullOrWhiteSpace(amc.AdvertiserId))
        {
            instances = await SendAmcAsync(http, HttpMethod.Get, DiscoveryUrl(amc, "/amc/instances"), token, amc, account.ProfileId, null, "application/vnd.amcinstances.v1+json");
            if (string.IsNullOrWhiteSpace(amc.InstanceId) && instances.IsSuccess)
            {
                var discoveredInstance = FirstString(instances.Json, "instanceId");
                if (!string.IsNullOrWhiteSpace(discoveredInstance))
                    amc = amc with
                    {
                        InstanceId = discoveredInstance,
                        ReportingEndpoint = BuildReportingEndpoint(amc.DiscoveryBaseUrl, discoveredInstance)
                    };
            }
        }

        var instanceCount = instances?.IsSuccess == true ? CountArray(instances.Json, "instances") : 0;
        var instanceCreationStatus = instances?.IsSuccess == true ? FirstString(instances.Json, "creationStatus") : null;

        AmcApiResponse? dataSources = null;
        if (amc.IsConfigured)
        {
            dataSources = await SendAmcAsync(http, HttpMethod.Get, ReportingUrl(amc, "/dataSources"), token, amc, account.ProfileId, null, "application/json");
        }

        var isConfigured = amc.IsConfigured;
        var isAuthorized = dataSources?.IsSuccess == true;
        var error = dataSources?.IsSuccess == false
            ? SafeDetails(dataSources.SafeJson)
            : instances?.IsSuccess == false
                ? SafeDetails(instances.SafeJson)
            : accounts.IsSuccess ? null : SafeDetails(accounts.SafeJson);
        var last = dataSources ?? instances ?? accounts;
        var discoveryWarning = amcAccountCount == 0 && isConfigured
            ? "AMC On-Demand instances return zero AMC accounts from /amc/accounts. Continuing with the configured Sponsored Ads entity and AMC instance."
            : null;

        var message = !isConfigured
            ? "AMC is not configured. Add AMC:InstanceId, AMC:AdvertiserId or AMC:EntityId, and AMC:ApiEndpoint."
            : isAuthorized
                ? $"{discoveryWarning ?? "AMC API is authorized and reachable."}"
                : dataSources is not null
                ? $"{discoveryWarning} AMC instance {amc.InstanceId} is configured, but the reporting endpoint returned HTTP {dataSources.Status}. Check that the OAuth token was created by {amc.ExpectedAmazonUserEmail}, the entity header is exactly {amc.AdvertiserId}, and the API client has AMC permission."
                    : instances?.IsSuccess == false
                        ? $"{discoveryWarning} Amazon rejected the AMC instance discovery call, but the configured reporting endpoint will still be used for workflow calls."
                        : $"{discoveryWarning} AMC instance {amc.InstanceId} is configured manually. Reporting authorization has not been confirmed yet.";

        return new AmcStatusDto
        {
            IsConfigured = isConfigured,
            IsAuthorized = isAuthorized,
            IsManuallyConfigured = amc.IsManuallyConfigured,
            AccountKey = accountKey,
            InstanceId = amc.InstanceId,
            AdvertiserId = amc.AdvertiserId,
            MarketplaceId = amc.MarketplaceId,
            ApiEndpoint = amc.ReportingEndpoint,
            ExpectedAmazonUserEmail = amc.ExpectedAmazonUserEmail,
            AccountsHttpStatus = accounts.Status,
            InstancesHttpStatus = instances?.Status,
            DataSourcesHttpStatus = dataSources?.Status,
            AmcAccountCount = amcAccountCount,
            InstanceCount = instanceCount,
            DataSourceCount = dataSources?.IsSuccess == true ? CountArray(dataSources.Json, "dataSources", "dataSourceList") : 0,
            InstanceCreationStatus = instanceCreationStatus,
            DiscoveryWarning = discoveryWarning,
            LastRequestMethod = last.Method,
            LastRequestUrl = last.Url,
            LastRequestStatus = last.Status,
            LastRequestDiagnostics = last.Diagnostics,
            LastResponseBody = last.SafeJson,
            Message = message,
            Error = error
        };
    }

    public async Task<AnalyticsImportResult> RunWorkflowAsync(AnalyticsImportRequest request)
    {
        var account = _accounts.Resolve(request.AccountKey)
            ?? throw new InvalidOperationException($"Account '{request.AccountKey}' not found.");
        var http = _httpFactory.CreateClient();
        var token = await _auth.GetAccessTokenAsync(account);
        var amc = GetAmcConfiguration(account);

        if (string.IsNullOrWhiteSpace(amc.AdvertiserId))
            throw new InvalidOperationException(
                "AMC entity ID is not configured. Set AMC:AdvertiserId or AMC:EntityId to ENTITYF259E0Z05V36.");

        if (string.IsNullOrWhiteSpace(amc.InstanceId) || string.IsNullOrWhiteSpace(amc.ReportingEndpoint))
            throw new InvalidOperationException(
                "AMC instance is not configured. Set AMC:InstanceId to amcjk0ydh5o and AMC:ApiEndpoint to https://advertising-api.amazon.com/amc/reporting/amcjk0ydh5o.");

        var end = request.DateRangeEnd ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var start = request.DateRangeStart ?? end.AddDays(-6);

        var sqlByType = RenderWorkflowSql(start, end);

        var executionIds = new Dictionary<string, string>();
        foreach (var job in DefaultJobs())
        {
            if (!sqlByType.TryGetValue(job.ResultType, out var sql)) continue;
            // Stable workflowId per AMC Agent guidance: PUT updates sqlQuery in place
            // instead of minting a new workflow per SQL edit, which would fragment AMC's
            // per-workflow privacy-execution quota across versions.
            var executionId = await CreateExecutionAsync(http, token, amc, account.ProfileId, job.WorkflowId, sql, start, end);
            executionIds[job.ResultType] = executionId;
        }

        if (!request.WaitForCompletion)
            return new AnalyticsImportResult
            {
                Success = true,
                RowsImported = 0,
                WorkflowExecutionIds = executionIds,
                WorkflowExecutionStatuses = executionIds.ToDictionary(pair => pair.Key, _ => "STARTED"),
                WorkflowSqlByType = sqlByType,
                Summary = $"Started {executionIds.Count} AMC workflow executions for {start:MMM d} - {end:MMM d, yyyy}. Call /api/amc/import-executions after Amazon finishes them."
            };

        var importResult = await ImportExecutionResultsAsync(new AmcExecutionImportRequest
        {
            AccountKey = request.AccountKey,
            TimeZone = "UTC",
            WorkflowExecutionIds = executionIds
        }, waitForCompletion: true);
        importResult.WorkflowSqlByType = sqlByType;
        importResult.Summary = $"Imported {importResult.RowsImported} AMC rows from real AMC workflow executions for {start:MMM d} - {end:MMM d, yyyy}.";
        return importResult;
    }

    public Task<AnalyticsImportResult> ImportExecutionResultsAsync(AmcExecutionImportRequest request) =>
        ImportExecutionResultsAsync(request, waitForCompletion: false);

    private async Task<AnalyticsImportResult> ImportExecutionResultsAsync(AmcExecutionImportRequest request, bool waitForCompletion)
    {
        var account = _accounts.Resolve(request.AccountKey)
            ?? throw new InvalidOperationException($"Account '{request.AccountKey}' not found.");
        if (!request.WorkflowExecutionIds.Any())
            throw new InvalidOperationException("At least one AMC workflow execution ID is required.");

        var http = _httpFactory.CreateClient();
        var token = await _auth.GetAccessTokenAsync(account);
        var amc = GetAmcConfiguration(account);
        var totalRows = 0;
        var byType = new Dictionary<string, int>();
        var statuses = new Dictionary<string, string>();

        foreach (var pair in request.WorkflowExecutionIds)
        {
            var resultType = pair.Key;
            var executionId = pair.Value;
            var status = waitForCompletion
                ? await WaitForExecutionAsync(http, token, amc, account.ProfileId, executionId)
                : await GetExecutionStatusAsync(http, token, amc, account.ProfileId, executionId);

            statuses[resultType] = status;
            if (!IsExecutionReady(status))
                continue;

            var csv = await DownloadExecutionCsvAsync(http, token, amc, account.ProfileId, executionId);
            var result = await _ingestion.ImportCsvAsync(new AmcResultImportRequest(request.AccountKey, resultType, account.ProfileId, request.TimeZone ?? "UTC"), csv);
            totalRows += result.RowsImported;
            foreach (var sourceCount in result.RowsImportedBySourceReportType)
                byType[sourceCount.Key] = byType.GetValueOrDefault(sourceCount.Key) + sourceCount.Value;
        }

        var pending = statuses.Count(status => !IsExecutionReady(status.Value));
        return new AnalyticsImportResult
        {
            Success = pending == 0,
            RowsImported = totalRows,
            RowsImportedBySourceReportType = byType,
            WorkflowExecutionIds = request.WorkflowExecutionIds,
            WorkflowExecutionStatuses = statuses,
            Summary = pending == 0
                ? $"Imported {totalRows} AMC rows from completed workflow executions."
                : $"{pending} AMC workflow execution(s) are not complete yet. Try /api/amc/import-executions again in a few minutes."
        };
    }

    public async Task<AmcEnsureWorkflowsResult> EnsureWorkflowsAsync(string accountKey, DateOnly start, DateOnly end)
    {
        var account = _accounts.Resolve(accountKey)
            ?? throw new InvalidOperationException($"Account '{accountKey}' not found.");
        var http = _httpFactory.CreateClient();
        var token = await _auth.GetAccessTokenAsync(account);
        var amc = GetAmcConfiguration(account);
        if (!amc.IsConfigured)
            throw new InvalidOperationException("AMC is not configured. Set AMC:InstanceId, AMC:AdvertiserId (or AMC:EntityId), and AMC:ApiEndpoint.");

        var sqlByType = RenderWorkflowSql(start, end);
        var warnings = new List<string>();
        var startedByType = new Dictionary<string, IReadOnlyList<string>>();
        var importedByType = new Dictionary<string, int>();

        foreach (var resultType in HourlyResultTypes)
        {
            var importedRows = await ResolvePendingExecutionsAsync(http, token, amc, account, resultType, start, end, warnings);
            if (importedRows > 0)
                importedByType[resultType] = importedRows;

            var coverage = _metrics.GetAmcCoverage(accountKey, resultType, start, end);
            var skipDates = coverage
                .Where(c => c.Status == AmcCoverageStatus.Queried || c.Status == AmcCoverageStatus.Pending)
                .Select(c => c.Date)
                .ToHashSet();
            var segments = AmcCoveragePlanner.ComputeMissingSegments(start, end, skipDates);
            if (!segments.Any()) continue;

            var startedIds = new List<string>();
            if (!sqlByType.TryGetValue(resultType, out var sql))
                continue;

            foreach (var segment in segments)
            {
                try
                {
                    var executionId = await StartWorkflowSegmentAsync(http, token, amc, account.ProfileId, resultType, sql, segment.Start, segment.End);
                    var now = DateTimeOffset.UtcNow;
                    var pendingRows = AmcCoveragePlanner.EnumerateDates(segment.Start, segment.End).Select(d => new AmcQueryCoverageRow
                    {
                        AccountKey = accountKey,
                        ResultType = resultType,
                        Date = d,
                        Status = AmcCoverageStatus.Pending,
                        WorkflowExecutionId = executionId,
                        UpdatedAt = now
                    });
                    _metrics.UpsertAmcCoverage(pendingRows);
                    startedIds.Add(executionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start AMC {ResultType} workflow for {Start}-{End}", resultType, segment.Start, segment.End);
                    warnings.Add($"Failed to start AMC {resultType} workflow for {segment.Start:MMM d} - {segment.End:MMM d, yyyy}: {ex.Message}");
                }
            }

            if (startedIds.Any())
                startedByType[resultType] = startedIds;
        }

        return new AmcEnsureWorkflowsResult(warnings, sqlByType, startedByType, importedByType);
    }

    private async Task<int> ResolvePendingExecutionsAsync(
        HttpClient http, string token, AmcRuntimeConfig amc, AmazonAccountConfig account,
        string resultType, DateOnly start, DateOnly end, List<string> warnings)
    {
        var coverage = _metrics.GetAmcCoverage(account.AccountKey, resultType, start, end);
        var pendingGroups = coverage
            .Where(c => c.Status == AmcCoverageStatus.Pending && !string.IsNullOrWhiteSpace(c.WorkflowExecutionId))
            .GroupBy(c => c.WorkflowExecutionId!)
            .ToList();
        if (!pendingGroups.Any()) return 0;

        var totalImported = 0;
        foreach (var group in pendingGroups)
        {
            var executionId = group.Key;
            string status;
            try
            {
                status = await GetExecutionStatusAsync(http, token, amc, account.ProfileId, executionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AMC execution {ExecutionId} status check failed", executionId);
                MarkCoverage(group, AmcCoverageStatus.Failed, executionId: null);
                warnings.Add($"AMC execution {executionId} status check failed: {ex.Message}. The dates will be retried.");
                continue;
            }

            if (!IsExecutionReady(status))
                continue;

            try
            {
                var csv = await DownloadExecutionCsvAsync(http, token, amc, account.ProfileId, executionId);
                var importResult = await _ingestion.ImportCsvAsync(
                    new AmcResultImportRequest(account.AccountKey, resultType, account.ProfileId, amc.TimeZone),
                    csv);
                totalImported += importResult.RowsImported;
                MarkCoverage(group, AmcCoverageStatus.Queried, executionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AMC execution {ExecutionId} import failed", executionId);
                MarkCoverage(group, AmcCoverageStatus.Failed, executionId: null);
                warnings.Add($"AMC execution {executionId} import failed: {ex.Message}. The dates will be retried.");
            }
        }
        return totalImported;
    }

    private void MarkCoverage(IEnumerable<AmcQueryCoverageRow> rows, string status, string? executionId)
    {
        var now = DateTimeOffset.UtcNow;
        var updates = rows.Select(r => new AmcQueryCoverageRow
        {
            AccountKey = r.AccountKey,
            ResultType = r.ResultType,
            Date = r.Date,
            Status = status,
            WorkflowExecutionId = executionId,
            UpdatedAt = now
        }).ToList();
        _metrics.UpsertAmcCoverage(updates);
    }

    private Task<string> StartWorkflowSegmentAsync(
        HttpClient http, string token, AmcRuntimeConfig amc, string profileId,
        string resultType, string sql, DateOnly segStart, DateOnly segEnd)
    {
        var job = DefaultJobs().First(j => j.ResultType == resultType);
        return CreateExecutionAsync(http, token, amc, profileId, job.WorkflowId, sql, segStart, segEnd);
    }

    private async Task<string> CreateExecutionAsync(HttpClient http, string token, AmcRuntimeConfig amc, string profileId, string label, string sql, DateOnly start, DateOnly end)
    {
        await UpsertWorkflowAsync(http, token, amc, profileId, label, sql);

        var body = JsonSerializer.Serialize(new
        {
            workflowId = label,
            timeWindowType = "EXPLICIT",
            timeWindowStart = $"{start:yyyy-MM-dd}T00:00:00",
            // Half-open interval per AMC convention: end is exclusive. May 18..May 21 inclusive
            // becomes start=2026-05-18T00:00:00, end=2026-05-22T00:00:00.
            timeWindowEnd = $"{end.AddDays(1):yyyy-MM-dd}T00:00:00",
            // Must be the advertiser's IANA TZ (e.g. America/Los_Angeles), not UTC. event_hour /
            // conversion_event_hour columns are in advertiser local time, so UTC boundaries would
            // shift the "day" by 7-8 hours and pull the wrong wall-clock hours for hourly queries.
            timeWindowTimeZone = amc.TimeZone,
            workflowExecutionTimeoutSeconds = 1800
        });
        var response = await SendAmcAsync(http, HttpMethod.Post, ReportingUrl(amc, "/workflowExecutions"), token, amc, profileId, body, "application/json");
        if (!response.IsSuccess)
            throw new InvalidOperationException($"AMC ad-hoc workflow execution '{label}' failed HTTP {response.Status}: {response.SafeJson}\n{response.Diagnostics}");

        var executionId = FirstString(response.Json, "workflowExecutionId");
        if (string.IsNullOrWhiteSpace(executionId))
            throw new InvalidOperationException($"AMC ad-hoc workflow execution '{label}' did not return an execution ID: {response.SafeJson}");
        return executionId;
    }

    private async Task UpsertWorkflowAsync(HttpClient http, string token, AmcRuntimeConfig amc, string profileId, string workflowId, string sql)
    {
        var body = JsonSerializer.Serialize(new
        {
            workflowId,
            sqlQuery = CleanWorkflowSql(sql)
        });

        var put = await SendAmcAsync(http, HttpMethod.Put, ReportingUrl(amc, $"/workflows/{Uri.EscapeDataString(workflowId)}"), token, amc, profileId, body, "application/json");
        if (put.IsSuccess) return;

        if (put.Status is not (404 or 405))
            throw new InvalidOperationException($"AMC workflow update '{workflowId}' failed HTTP {put.Status}: {put.SafeJson}\n{put.Diagnostics}");

        var post = await SendAmcAsync(http, HttpMethod.Post, ReportingUrl(amc, "/workflows"), token, amc, profileId, body, "application/json");
        if (!post.IsSuccess)
            throw new InvalidOperationException($"AMC workflow create '{workflowId}' failed HTTP {post.Status}: {post.SafeJson}\n{post.Diagnostics}");
    }

    private async Task<string> WaitForExecutionAsync(HttpClient http, string token, AmcRuntimeConfig amc, string profileId, string executionId)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            var status = await GetExecutionStatusAsync(http, token, amc, profileId, executionId);
            if (IsExecutionReady(status)) return status;
            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "REJECTED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"AMC workflow execution {executionId} ended with status {status}.");
        }

        throw new TimeoutException($"AMC workflow execution {executionId} did not complete within 20 minutes.");
    }

    private async Task<string> GetExecutionStatusAsync(HttpClient http, string token, AmcRuntimeConfig amc, string profileId, string executionId)
    {
        var response = await SendAmcAsync(http, HttpMethod.Get, ReportingUrl(amc, $"/workflowExecutions/{Uri.EscapeDataString(executionId)}"), token, amc, profileId, null, "application/json");
        if (!response.IsSuccess)
            throw new InvalidOperationException($"AMC workflow execution status failed HTTP {response.Status}: {response.SafeJson}\n{response.Diagnostics}");

        var status = FirstString(response.Json, "status") ?? "UNKNOWN";
        if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "REJECTED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"AMC workflow execution {executionId} ended with status {status}: {response.SafeJson}\n{response.Diagnostics}");

        return status;
    }

    private static bool IsExecutionReady(string status) =>
        string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "AVAILABLE", StringComparison.OrdinalIgnoreCase);

    private async Task<string> DownloadExecutionCsvAsync(HttpClient http, string token, AmcRuntimeConfig amc, string profileId, string executionId)
    {
        var response = await SendAmcAsync(http, HttpMethod.Get, ReportingUrl(amc, $"/workflowExecutions/{Uri.EscapeDataString(executionId)}/downloadUrls"), token, amc, profileId, null, "application/json");
        if (!response.IsSuccess)
            throw new InvalidOperationException($"AMC download URL request failed HTTP {response.Status}: {response.SafeJson}\n{response.Diagnostics}");

        if (response.Json is null)
            throw new InvalidOperationException($"AMC execution {executionId} returned non-JSON download metadata: {response.SafeJson}");

        var urls = ExtractDownloadUrls(response.Json.RootElement).ToList();
        if (!urls.Any())
            throw new InvalidOperationException($"AMC execution {executionId} did not provide result download URLs: {response.SafeJson}");

        var builder = new StringBuilder();
        foreach (var url in urls)
        {
            var bytes = await http.GetByteArrayAsync(url);
            var text = DecodeResultBytes(bytes);
            if (builder.Length == 0)
            {
                builder.Append(text.TrimEnd());
            }
            else
            {
                var lines = text.Split('\n');
                builder.Append('\n');
                builder.Append(string.Join('\n', lines.Skip(1)).TrimEnd());
            }
        }
        return builder.ToString();
    }

    private async Task<AmcApiResponse> SendAmcAsync(HttpClient http, HttpMethod method, string url, string token, AmcRuntimeConfig amc, string profileId, string? jsonBody, string mediaType)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Amazon-Advertising-API-ClientId", _options.ClientId);
        req.Headers.Accept.ParseAdd(mediaType);
        if (!string.IsNullOrWhiteSpace(amc.AdvertiserId))
        {
            req.Headers.TryAddWithoutValidation("Amazon-Advertising-API-AdvertiserId", amc.AdvertiserId);
            req.Headers.TryAddWithoutValidation("Amazon-Advertising-API-EntityId", amc.AdvertiserId);
        }
        if (!string.IsNullOrWhiteSpace(amc.MarketplaceId))
            req.Headers.TryAddWithoutValidation("Amazon-Advertising-API-MarketplaceId", amc.MarketplaceId);
        var adsAccountId = _config["AMC:AdsAccountId"];
        if (!string.IsNullOrWhiteSpace(adsAccountId))
            req.Headers.TryAddWithoutValidation("Amazon-Ads-AccountId", adsAccountId);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, mediaType);

        var resp = await http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        var response = AmcApiResponse.From(method.Method, url, (int)resp.StatusCode, raw, BuildDiagnostics(method, url, (int)resp.StatusCode, raw, amc, profileId, adsAccountId));
        _logger.LogInformation("AMC API {Method} {Url} returned HTTP {Status}. {Diagnostics}",
            method.Method, url, response.Status, response.Diagnostics);
        return response;
    }

    public Dictionary<string, string> RenderWorkflowSql(DateOnly start, DateOnly end) =>
        DefaultJobs().ToDictionary(job => job.ResultType, job => LoadWorkflowSql(job.SqlFile, start, end));

    private static AmcWorkflowJob[] DefaultJobs() =>
    [
        new("amazon-ads-manager-traffic-hourly", "traffic-hourly", "traffic-hourly.sql"),
        new("amazon-ads-manager-conversion-hourly", "conversion-hourly", "conversion-hourly.sql")
    ];

    private static string LoadWorkflowSql(string fileName, DateOnly start, DateOnly end)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Sql", "amc-workflows", fileName);
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine("src", "AmazonAdsManager.Api", "Sql", "amc-workflows", fileName));
        var sql = File.ReadAllText(path);
        return sql
            .Replace("@start_date", $"TIMESTAMP '{start:yyyy-MM-dd} 00:00:00'")
            .Replace("@end_date", $"TIMESTAMP '{end.AddDays(1):yyyy-MM-dd} 00:00:00'");
    }

    // AMC requires sqlQuery to be a single-line string with comments removed before submission.
    // CAVEAT: the regexes below are not SQL-string-literal-aware. If a future query introduces a
    // string literal containing "--" or "/*" (e.g. WHERE campaign = 'cost--center'), this will
    // silently corrupt the literal. Current workflows have no such literals.
    // Trailing semicolons MUST be stripped: AMC treats `;` as a statement terminator and discards
    // anything after it. A stray trailing `;` produces a SUCCEEDED execution with a header-only
    // CSV instead of a parse error — a silent failure mode we hit in prod.
    internal static string CleanWorkflowSql(string sql)
    {
        var withoutBlockComments = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"--[^\r\n]*", " ");
        var collapsed = Regex.Replace(withoutLineComments, @"\s+", " ").Trim();
        return collapsed.TrimEnd(';', ' ');
    }

    private AmcRuntimeConfig GetAmcConfiguration(AmazonAccountConfig account)
    {
        var instanceId = FirstConfigured("AMC:InstanceId", "amcjk0ydh5o");
        var advertiserId = FirstConfigured("AMC:AdvertiserId", "AMC:EntityId", "ENTITYF259E0Z05V36");
        var marketplaceId = FirstConfigured("AMC:MarketplaceId", "ATVPDKIKX0DER");
        var discoveryBaseUrl = RootUrl(FirstConfigured("AMC:BaseUrl", account.BaseUrl, "https://advertising-api.amazon.com"));
        var endpoint = FirstConfigured("AMC:ApiEndpoint", "AMC:ReportingEndpoint", "");
        if (string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(instanceId))
            endpoint = BuildReportingEndpoint(discoveryBaseUrl, instanceId);

        return new AmcRuntimeConfig(
            InstanceId: instanceId,
            AdvertiserId: advertiserId,
            MarketplaceId: marketplaceId,
            ReportingEndpoint: endpoint.TrimEnd('/'),
            DiscoveryBaseUrl: RootUrl(endpoint, discoveryBaseUrl),
            ExpectedAmazonUserEmail: FirstConfigured("AMC:ExpectedAmazonUserEmail", "peterzeyu1998@gmail.com"),
            // IANA TZ used for the AMC workflow execution's timeWindowStart/End boundaries.
            // Must match the AMC instance's configured timezone so that day boundaries align with
            // how the advertiser thinks of "May 18-21". Defaults to America/Los_Angeles for the
            // ATVPDKIKX0DER (US) marketplace. Override with AMC:TimeZone if the instance differs.
            TimeZone: FirstConfigured("AMC:TimeZone", "America/Los_Angeles"),
            IsManuallyConfigured: true);
    }

    private string FirstConfigured(params string[] keysOrDefaults)
    {
        foreach (var keyOrDefault in keysOrDefaults)
        {
            var value = keyOrDefault.StartsWith("AMC:", StringComparison.OrdinalIgnoreCase)
                ? _config[keyOrDefault]
                : keyOrDefault;
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    private static string BuildReportingEndpoint(string baseUrl, string instanceId) =>
        $"{RootUrl(baseUrl)}/amc/reporting/{Uri.EscapeDataString(instanceId)}";

    private static string DiscoveryUrl(AmcRuntimeConfig amc, string path) =>
        $"{amc.DiscoveryBaseUrl}{path}";

    private static string ReportingUrl(AmcRuntimeConfig amc, string path) =>
        $"{amc.ReportingEndpoint}{path}";

    private static string RootUrl(string url, string fallback = "https://advertising-api.amazon.com")
    {
        if (string.IsNullOrWhiteSpace(url))
            return fallback.TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return fallback.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Host}".TrimEnd('/');
    }

    private string BuildDiagnostics(HttpMethod method, string url, int status, string responseBody, AmcRuntimeConfig amc, string profileId, string? adsAccountId)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "unknown";
        var safeBody = responseBody.Length > 4000 ? responseBody[..4000] : responseBody;
        return string.Join('\n', new[]
        {
            $"AMC request method: {method.Method}",
            $"AMC request URL: {url}",
            $"AMC response status: {status}",
            $"AMC response body: {safeBody}",
            $"Region/host: {host}",
            $"Amazon-Advertising-API-ClientId present: {!string.IsNullOrWhiteSpace(_options.ClientId)}",
            $"Amazon-Advertising-API-AdvertiserId: {Empty(amc.AdvertiserId)}",
            $"Amazon-Advertising-API-EntityId: {Empty(amc.AdvertiserId)}",
            $"Amazon-Advertising-API-MarketplaceId: {Empty(amc.MarketplaceId)}",
            $"Amazon-Advertising-API-Scope: not sent for AMC reporting calls",
            $"Connected Amazon Ads profileId: {Empty(profileId)}",
            $"Amazon-Ads-AccountId: {Empty(adsAccountId)}",
            $"Expected Amazon login user: {Empty(amc.ExpectedAmazonUserEmail)}"
        });
    }

    private static string Empty(string? value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value;

    private static string DecodeResultBytes(byte[] bytes)
    {
        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? FirstString(JsonDocument? doc, params string[] names) =>
        doc is null ? null : FirstString(doc.RootElement, names);

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var value in ValuesForProperty(element, name))
            {
                if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
                if (value.ValueKind == JsonValueKind.Number)
                    return value.ToString();
            }
        }
        return null;
    }

    private static IEnumerable<string> StringsForProperty(JsonElement element, string name) =>
        ValuesForProperty(element, name)
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!)
            .Where(v => !string.IsNullOrWhiteSpace(v));

    private static IEnumerable<string> ExtractDownloadUrls(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            // AMC's /downloadUrls returns BOTH the data file (.csv) and a workflow-metadata
            // sidecar (.json). Only the CSV is parseable data; pulling the JSON would pollute
            // the merged CSV stream.
            if (!string.IsNullOrWhiteSpace(value) &&
                (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) &&
                IsCsvUrl(value))
                yield return value;
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            foreach (var nested in ExtractDownloadUrls(prop.Value))
                yield return nested;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            foreach (var nested in ExtractDownloadUrls(item))
                yield return nested;
        }
    }

    private static bool IsCsvUrl(string url)
    {
        var path = url.Split('?', 2)[0];
        return path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".csv/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<JsonElement> ValuesForProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    yield return prop.Value;
                foreach (var nested in ValuesForProperty(prop.Value, name))
                    yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            foreach (var nested in ValuesForProperty(item, name))
                yield return nested;
        }
    }

    private static int CountArray(JsonDocument? doc, params string[] names)
    {
        if (doc is null) return 0;
        foreach (var name in names)
        {
            foreach (var value in ValuesForProperty(doc.RootElement, name))
            {
                if (value.ValueKind == JsonValueKind.Array)
                    return value.GetArrayLength();
            }
        }
        return 0;
    }

    private static string SafeDetails(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("details", out var details))
                return details.GetString() ?? raw;
            if (doc.RootElement.TryGetProperty("message", out var message))
                return message.GetString() ?? raw;
        }
        catch { }
        return raw;
    }

    private sealed record AmcWorkflowJob(string WorkflowId, string ResultType, string SqlFile);
    private sealed record AmcRuntimeConfig(
        string InstanceId,
        string AdvertiserId,
        string MarketplaceId,
        string ReportingEndpoint,
        string DiscoveryBaseUrl,
        string ExpectedAmazonUserEmail,
        string TimeZone,
        bool IsManuallyConfigured)
    {
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(InstanceId) &&
            !string.IsNullOrWhiteSpace(AdvertiserId) &&
            !string.IsNullOrWhiteSpace(ReportingEndpoint);
    }

    private sealed class AmcApiResponse
    {
        public string Method { get; init; } = "";
        public string Url { get; init; } = "";
        public int Status { get; init; }
        public JsonDocument? Json { get; init; }
        public string SafeJson { get; init; } = "";
        public string Diagnostics { get; init; } = "";
        public bool IsSuccess => Status is >= 200 and <= 299;

        public static AmcApiResponse From(string method, string url, int status, string raw, string diagnostics)
        {
            JsonDocument? json = null;
            try { json = JsonDocument.Parse(raw); } catch { }
            return new AmcApiResponse
            {
                Method = method,
                Url = url,
                Status = status,
                Json = json,
                SafeJson = raw.Length > 4000 ? raw[..4000] : raw,
                Diagnostics = diagnostics
            };
        }
    }
}

public class AmcResultIngestionService
{
    private readonly AdMetricsRepository _metrics;
    private readonly Func<string, AmazonAccountConfig?> _resolveAccount;

    public AmcResultIngestionService(AdMetricsRepository metrics, AmazonAccountResolver accounts)
        : this(metrics, accounts.Resolve)
    {
    }

    internal AmcResultIngestionService(AdMetricsRepository metrics, Func<string, AmazonAccountConfig?> resolveAccount)
    {
        _metrics = metrics;
        _resolveAccount = resolveAccount;
    }

    public async Task<AnalyticsImportResult> ImportResultsAsync(AmcResultImportRequest request, Stream body)
    {
        if (string.IsNullOrWhiteSpace(request.AccountKey))
            throw new InvalidOperationException("accountKey is required for AMC result import.");
        if (string.IsNullOrWhiteSpace(request.ResultType))
            throw new InvalidOperationException("resultType is required. Use traffic-hourly, conversion-hourly, or attribution-lag.");

        var account = _resolveAccount(request.AccountKey)
            ?? throw new InvalidOperationException($"Account '{request.AccountKey}' not found.");
        var profileId = !string.IsNullOrWhiteSpace(request.ProfileId) ? request.ProfileId! : account.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException($"Amazon Ads profileId is missing for account '{request.AccountKey}'.");

        var csv = await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();
        return await ImportCsvAsync(request, csv);
    }

    public Task<AnalyticsImportResult> ImportCsvAsync(AmcResultImportRequest request, string csv)
    {
        if (string.IsNullOrWhiteSpace(request.AccountKey))
            throw new InvalidOperationException("accountKey is required for AMC result import.");
        if (string.IsNullOrWhiteSpace(request.ResultType))
            throw new InvalidOperationException("resultType is required. Use traffic-hourly, conversion-hourly, or attribution-lag.");

        var account = _resolveAccount(request.AccountKey)
            ?? throw new InvalidOperationException($"Account '{request.AccountKey}' not found.");
        var profileId = !string.IsNullOrWhiteSpace(request.ProfileId) ? request.ProfileId! : account.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException($"Amazon Ads profileId is missing for account '{request.AccountKey}'.");

        if (string.IsNullOrWhiteSpace(csv))
            throw new InvalidOperationException("AMC import body is empty. Upload the CSV result from AMC.");

        var rows = CsvRows.Parse(csv);
        if (!rows.Any())
            throw new InvalidOperationException("AMC import body did not contain any data rows.");

        var normalizedType = NormalizeResultType(request.ResultType);
        var result = normalizedType switch
        {
            "traffic-hourly" => ImportTraffic(rows, request.AccountKey, profileId, request.TimeZone),
            "conversion-hourly" => ImportConversions(rows, request.AccountKey, profileId, request.TimeZone),
            "attribution-lag" => ImportAttributionLag(rows, request.AccountKey, profileId),
            _ => throw new InvalidOperationException("Unsupported AMC resultType. Use traffic-hourly, conversion-hourly, or attribution-lag.")
        };
        return Task.FromResult(result);
    }

    private AnalyticsImportResult ImportTraffic(IReadOnlyList<Dictionary<string, string>> rows, string accountKey, string profileId, string? defaultTimeZone)
    {
        AssertDateAndCampaignSurvivedAggregation(rows, "traffic", "traffic_date", "event_date");
        var parsed = rows
            .Where(row => HasAny(row, "date", "traffic_date", "event_date") && HasAny(row, CampaignIdColumns))
            .Select(row => new AmcTrafficHourly
            {
                Date = Date(row, "date", "traffic_date", "event_date"),
                Hour = Int(row, "hour", "traffic_hour", "event_hour"),
                TimeZone = Text(row, "time_zone", "timezone") ?? defaultTimeZone ?? "UTC",
                AccountKey = Text(row, "account_key", "accountkey") ?? accountKey,
                ProfileId = Text(row, "profile_id", "profileid") ?? profileId,
                CampaignId = RequiredText(row, CampaignIdColumns),
                CampaignName = Text(row, "campaign_name", "campaignname") ?? "",
                AdGroupId = Text(row, "ad_group_id", "adgroupid"),
                AdGroupName = Text(row, "ad_group_name", "adgroupname"),
                AdProductType = Text(row, "ad_product_type", "adproducttype", "ad_product") ?? "SPONSORED_PRODUCTS",
                TargetingText = Text(row, "targeting_text", "targeting", "keyword"),
                MatchType = Text(row, "match_type", "matchtype"),
                CustomerSearchTerm = Text(row, "customer_search_term", "search_term", "query"),
                Impressions = Int(row, "impressions"),
                Clicks = Int(row, "clicks"),
                Spend = Decimal(row, "spend", "cost")
            })
            .ToList();

        _metrics.UpsertAmcTrafficHourly(parsed);
        return Result(parsed.Count, "AMCTrafficHourly", $"Imported {parsed.Count} real AMC traffic-hour rows.");
    }

    private AnalyticsImportResult ImportConversions(IReadOnlyList<Dictionary<string, string>> rows, string accountKey, string profileId, string? defaultTimeZone)
    {
        AssertDateAndCampaignSurvivedAggregation(rows, "conversion", "conversion_date");
        var parsed = rows
            .Where(row => HasAny(row, "conversion_date", "conversiondate", "date") && HasAny(row, CampaignIdColumns))
            .Select(row => new AmcConversionsHourly
            {
                ConversionDate = Date(row, "conversion_date", "conversiondate", "date"),
                ConversionHour = Int(row, "conversion_hour", "conversionhour", "hour"),
                TimeZone = Text(row, "time_zone", "timezone") ?? defaultTimeZone ?? "UTC",
                AccountKey = Text(row, "account_key", "accountkey") ?? accountKey,
                ProfileId = Text(row, "profile_id", "profileid") ?? profileId,
                CampaignId = RequiredText(row, CampaignIdColumns),
                CampaignName = Text(row, "campaign_name", "campaignname") ?? "",
                AdGroupId = Text(row, "ad_group_id", "adgroupid"),
                AdGroupName = Text(row, "ad_group_name", "adgroupname"),
                AdProductType = Text(row, "ad_product_type", "adproducttype", "ad_product") ?? "SPONSORED_PRODUCTS",
                TrackedAsin = Text(row, "tracked_asin", "trackedasin", "asin"),
                ConversionEventType = Text(row, "conversion_event_type", "conversioneventtype", "event_type"),
                Purchases = Int(row, "purchases", "conversions", "orders"),
                UnitsSold = Int(row, "units_sold", "unitssold", "units"),
                Sales = Decimal(row, "sales", "revenue", "total_sales"),
                NewToBrandPurchases = NullableInt(row, "new_to_brand_purchases", "ntb_purchases"),
                NewToBrandSales = NullableDecimal(row, "new_to_brand_sales", "ntb_sales")
            })
            .ToList();

        _metrics.UpsertAmcConversionsHourly(parsed);
        return Result(parsed.Count, "AMCConversionHourly", $"Imported {parsed.Count} real AMC conversion-hour rows.");
    }

    private AnalyticsImportResult ImportAttributionLag(IReadOnlyList<Dictionary<string, string>> rows, string accountKey, string profileId)
    {
        var parsed = rows.Select(row => new AmcAttributionLag
            {
                AccountKey = Text(row, "account_key", "accountkey") ?? accountKey,
                ProfileId = Text(row, "profile_id", "profileid") ?? profileId,
                CampaignId = RequiredText(row, CampaignIdColumns),
                AdGroupId = Text(row, "ad_group_id", "adgroupid"),
                TargetingText = Text(row, "targeting_text", "targeting", "keyword"),
                SearchTerm = Text(row, "search_term", "customer_search_term", "query"),
                TrafficDate = Date(row, "traffic_date", "trafficdate"),
                TrafficHour = Int(row, "traffic_hour", "traffichour"),
                ConversionDate = Date(row, "conversion_date", "conversiondate"),
                ConversionHour = Int(row, "conversion_hour", "conversionhour"),
                HoursToConversion = Int(row, "hours_to_conversion", "hourstoconversion", "lag_hours"),
                Purchases = Int(row, "purchases", "conversions", "orders"),
                Sales = Decimal(row, "sales", "revenue", "total_sales")
            })
            .ToList();

        _metrics.UpsertAmcAttributionLag(parsed);
        return Result(parsed.Count, "AMCAttributionLag", $"Imported {parsed.Count} real AMC attribution-lag rows.");
    }

    private static AnalyticsImportResult Result(int count, string source, string summary) => new()
    {
        Success = true,
        RowsImported = count,
        RowsImportedBySourceReportType = new Dictionary<string, int> { [source] = count },
        Summary = summary
    };

    private static string NormalizeResultType(string raw) =>
        raw.Trim().ToLowerInvariant().Replace("_", "-");

    private static readonly string[] CampaignIdColumns =
    [
        "campaign_id_string",
        "campaignidstring",
        "campaign_id",
        "campaignid"
    ];

    // Detect AMC output-suppression bugs. If AMC returned data rows but every row has an empty
    // date AND/OR empty campaign id, the parser would silently drop them all -> 0 rows imported,
    // coverage marked Queried, user confused. Surface a clear exception instead so the coverage
    // row goes to Failed and the warning bubbles up.
    //
    // Known causes (AMC Agent, May 2026):
    //   1. An Internal-classified column (e.g. `campaign`) appears in the final SELECT. AMC
    //      silently NULLs the output instead of erroring. Fix: drop the Internal column; pull the
    //      label via the Amazon Ads API using campaign_id_string as the key.
    //   2. Trailing `;` in the submitted SQL terminates the statement; AMC returns header-only.
    //      Fix: CleanWorkflowSql strips it. Verify SQL still has the SELECT after submission.
    internal static void AssertDateAndCampaignSurvivedAggregation(IReadOnlyList<Dictionary<string, string>> rows, string label, params string[] dateColumns)
    {
        if (rows.Count == 0) return;
        var withDate = rows.Count(r => HasAny(r, dateColumns));
        var withCampaign = rows.Count(r => HasAny(r, CampaignIdColumns));
        if (withDate == 0)
            throw new InvalidOperationException(
                $"AMC {label} CSV contained {rows.Count} rows but the date column ({string.Join("/", dateColumns)}) was empty on ALL of them. " +
                $"Likely causes: (a) an Internal-classified column such as `campaign` is in the final SELECT (AMC silently NULLs output); " +
                $"(b) a trailing semicolon (`;`) in the submitted SQL terminated the statement. Inspect the workflow SQL and retry.");
        if (withCampaign == 0)
            throw new InvalidOperationException(
                $"AMC {label} CSV contained {rows.Count} rows but campaign_id was empty on ALL of them. " +
                $"Verify the SELECT uses `campaign_id_string AS campaign_id` and that no Internal column was added to the final SELECT.");
    }

    private static string RequiredText(Dictionary<string, string> row, params string[] names) =>
        Text(row, names) ?? throw new InvalidOperationException(
            $"AMC row is missing required field '{names[0]}'. Available fields: {string.Join(", ", row.Keys.OrderBy(k => k))}.");

    private static string? Text(Dictionary<string, string> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (row.TryGetValue(NormalizeHeader(name), out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    private static bool HasAny(Dictionary<string, string> row, params string[] names) =>
        names.Any(name => row.TryGetValue(NormalizeHeader(name), out var value) && !string.IsNullOrWhiteSpace(value));

    private static DateOnly Date(Dictionary<string, string> row, params string[] names)
    {
        var raw = RequiredText(row, names);
        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return DateOnly.FromDateTime(dto.UtcDateTime);
        throw new InvalidOperationException($"AMC value '{raw}' is not a valid date for '{names[0]}'.");
    }

    private static int Int(Dictionary<string, string> row, params string[] names)
    {
        var raw = Text(row, names);
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
            return (int)decimalValue;
        throw new InvalidOperationException($"AMC value '{raw}' is not a valid integer for '{names[0]}'.");
    }

    private static int? NullableInt(Dictionary<string, string> row, params string[] names)
    {
        var raw = Text(row, names);
        return string.IsNullOrWhiteSpace(raw) ? null : Int(row, names);
    }

    private static decimal Decimal(Dictionary<string, string> row, params string[] names)
    {
        var raw = Text(row, names);
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new InvalidOperationException($"AMC value '{raw}' is not a valid decimal for '{names[0]}'.");
    }

    private static decimal? NullableDecimal(Dictionary<string, string> row, params string[] names)
    {
        var raw = Text(row, names);
        return string.IsNullOrWhiteSpace(raw) ? null : Decimal(row, names);
    }

    private static string NormalizeHeader(string header)
    {
        var chars = header
            .Trim()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant);
        return string.Concat(chars);
    }

    private static class CsvRows
    {
        public static IReadOnlyList<Dictionary<string, string>> Parse(string csv)
        {
            var records = ReadRecords(csv).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
            if (records.Count < 2) return Array.Empty<Dictionary<string, string>>();

            var headers = records[0].Select(NormalizeHeader).ToList();
            return records.Skip(1)
                .Select(record =>
                {
                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < headers.Count && i < record.Count; i++)
                        row[headers[i]] = record[i];
                    return row;
                })
                .ToList();
        }

        private static List<List<string>> ReadRecords(string csv)
        {
            var records = new List<List<string>>();
            var record = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < csv.Length; i++)
            {
                var ch = csv[i];
                if (inQuotes)
                {
                    if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else if (ch == '"')
                    {
                        inQuotes = false;
                    }
                    else
                    {
                        field.Append(ch);
                    }
                    continue;
                }

                if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    record.Add(field.ToString());
                    field.Clear();
                }
                else if (ch == '\n')
                {
                    record.Add(field.ToString().TrimEnd('\r'));
                    records.Add(record);
                    record = new List<string>();
                    field.Clear();
                }
                else
                {
                    field.Append(ch);
                }
            }

            record.Add(field.ToString().TrimEnd('\r'));
            records.Add(record);
            return records;
        }
    }
}
