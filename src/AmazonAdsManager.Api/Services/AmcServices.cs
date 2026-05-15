using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;

namespace AmazonAdsManager.Api.Services;

public record AmcResultImportRequest(
    string AccountKey,
    string ResultType,
    string? ProfileId = null,
    string? TimeZone = null);

public class AmcWorkflowService
{
    private readonly IConfiguration _config;
    private readonly AmazonAccountResolver _accounts;
    private readonly AmazonAdsAuthService _auth;
    private readonly AmazonAdsOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AmcResultIngestionService _ingestion;

    public AmcWorkflowService(
        IConfiguration config,
        AmazonAccountResolver accounts,
        AmazonAdsAuthService auth,
        IOptions<AmazonAdsOptions> options,
        IHttpClientFactory httpFactory,
        AmcResultIngestionService ingestion)
    {
        _config = config;
        _accounts = accounts;
        _auth = auth;
        _options = options.Value;
        _httpFactory = httpFactory;
        _ingestion = ingestion;
    }

    public async Task<object> DiscoverAsync(string accountKey)
    {
        var account = _accounts.Resolve(accountKey)
            ?? throw new InvalidOperationException($"Account '{accountKey}' not found.");
        var http = _httpFactory.CreateClient();
        var token = await _auth.GetAccessTokenAsync(account);

        var accounts = await SendAmcAsync(http, HttpMethod.Get, account.BaseUrl, "/amc/accounts", token, null, null, null, "application/vnd.amcaccounts.v1+json");
        var advertiserId = _config["AMC:AdvertiserId"] ?? FirstString(accounts.Json, "accountId", "advertiserId", "id", "entityId");
        var marketplaceId = _config["AMC:MarketplaceId"] ?? "ATVPDKIKX0DER";

        AmcApiResponse? instances = null;
        if (!string.IsNullOrWhiteSpace(advertiserId))
        {
            instances = await SendAmcAsync(http, HttpMethod.Get, account.BaseUrl, "/amc/instances", token, advertiserId, marketplaceId, null, "application/vnd.amcinstances.v1+json");
        }

        return new
        {
            AccountEndpoint = accounts.Status,
            Accounts = accounts.SafeJson,
            AdvertiserId = advertiserId,
            MarketplaceId = marketplaceId,
            InstancesEndpoint = instances?.Status,
            Instances = instances?.SafeJson,
            ConfiguredInstanceId = _config["AMC:InstanceId"]
        };
    }

    public async Task<AmcStatusDto> GetStatusAsync(string accountKey)
    {
        var account = _accounts.Resolve(accountKey)
            ?? throw new InvalidOperationException($"Account '{accountKey}' not found.");
        var http = _httpFactory.CreateClient();
        var token = await _auth.GetAccessTokenAsync(account);

        var marketplaceId = _config["AMC:MarketplaceId"] ?? "ATVPDKIKX0DER";
        var configuredAdvertiserId = _config["AMC:AdvertiserId"] ?? "";
        var instanceId = _config["AMC:InstanceId"] ?? "";

        var accounts = await SendAmcAsync(http, HttpMethod.Get, account.BaseUrl, "/amc/accounts", token, null, null, null, "application/vnd.amcaccounts.v1+json");
        var discoveredAdvertiserId = FirstString(accounts.Json, "accountId", "advertiserId", "id", "entityId") ?? "";
        var advertiserId = string.IsNullOrWhiteSpace(configuredAdvertiserId) ? discoveredAdvertiserId : configuredAdvertiserId;
        var amcAccountCount = CountArray(accounts.Json, "amcAccounts");

        AmcApiResponse? dataSources = null;
        if (!string.IsNullOrWhiteSpace(instanceId) && !string.IsNullOrWhiteSpace(advertiserId))
        {
            dataSources = await SendAmcAsync(http, HttpMethod.Get, account.BaseUrl, $"/amc/reporting/{Uri.EscapeDataString(instanceId)}/dataSources", token, advertiserId, marketplaceId, null, "application/vnd.amcdatasources.v1+json");
        }

        var isConfigured = !string.IsNullOrWhiteSpace(instanceId) && !string.IsNullOrWhiteSpace(advertiserId);
        var isAuthorized = dataSources?.IsSuccess == true;
        var error = dataSources?.IsSuccess == false
            ? SafeDetails(dataSources.SafeJson)
            : accounts.IsSuccess ? null : SafeDetails(accounts.SafeJson);

        var message = !isConfigured
            ? "AMC is not configured. Add AMC:InstanceId, AMC:AdvertiserId, and AMC:MarketplaceId."
            : isAuthorized
                ? "AMC API is authorized and reachable."
                : $"AMC is configured for instance {instanceId}, but Amazon API access is not authorized for the current connected account.";

        return new AmcStatusDto
        {
            IsConfigured = isConfigured,
            IsAuthorized = isAuthorized,
            AccountKey = accountKey,
            InstanceId = instanceId,
            AdvertiserId = advertiserId,
            MarketplaceId = marketplaceId,
            AccountsHttpStatus = accounts.Status,
            DataSourcesHttpStatus = dataSources?.Status,
            AmcAccountCount = amcAccountCount,
            DataSourceCount = dataSources?.IsSuccess == true ? CountArray(dataSources.Json, "dataSources", "dataSourceList") : 0,
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

        var marketplaceId = _config["AMC:MarketplaceId"] ?? "ATVPDKIKX0DER";
        var advertiserId = _config["AMC:AdvertiserId"];
        if (string.IsNullOrWhiteSpace(advertiserId))
        {
            var accounts = await SendAmcAsync(http, HttpMethod.Get, account.BaseUrl, "/amc/accounts", token, null, null, null, "application/vnd.amcaccounts.v1+json");
            advertiserId = FirstString(accounts.Json, "accountId", "advertiserId", "id", "entityId");
        }

        if (string.IsNullOrWhiteSpace(advertiserId))
            throw new InvalidOperationException(
                "AMC account/advertiser ID could not be discovered. Add AMC:AdvertiserId and AMC:MarketplaceId to app settings.");

        var instanceId = _config["AMC:InstanceId"];
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            var instances = await SendAmcAsync(http, HttpMethod.Get, account.BaseUrl, "/amc/instances", token, advertiserId, marketplaceId, null, "application/vnd.amcinstances.v1+json");
            instanceId = FirstString(instances.Json, "instanceId");
        }

        if (string.IsNullOrWhiteSpace(instanceId))
            throw new InvalidOperationException(
                "No AMC instance was found. Open Amazon Ads > Measurement & Reporting > Amazon Marketing Cloud and create/enable an AMC instance, or add AMC:InstanceId.");

        var end = request.DateRangeEnd ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var start = request.DateRangeStart ?? end.AddDays(-6);

        var jobs = new[]
        {
            new AmcWorkflowJob("amazon-ads-manager-traffic-hourly", "traffic-hourly", "traffic-hourly.sql"),
            new AmcWorkflowJob("amazon-ads-manager-conversion-hourly", "conversion-hourly", "conversion-hourly.sql"),
            new AmcWorkflowJob("amazon-ads-manager-attribution-lag", "attribution-lag", "attribution-lag.sql")
        };

        var totalRows = 0;
        var byType = new Dictionary<string, int>();
        foreach (var job in jobs)
        {
            var sql = LoadWorkflowSql(job.SqlFile, start, end);
            var executionId = await CreateExecutionAsync(http, account.BaseUrl, token, advertiserId, marketplaceId, instanceId, job.WorkflowId, sql, start, end);
            await WaitForExecutionAsync(http, account.BaseUrl, token, advertiserId, marketplaceId, instanceId, executionId);
            var csv = await DownloadExecutionCsvAsync(http, account.BaseUrl, token, advertiserId, marketplaceId, instanceId, executionId);
            var result = await _ingestion.ImportCsvAsync(new AmcResultImportRequest(request.AccountKey, job.ResultType, account.ProfileId, "UTC"), csv);
            totalRows += result.RowsImported;
            foreach (var pair in result.RowsImportedBySourceReportType)
                byType[pair.Key] = byType.GetValueOrDefault(pair.Key) + pair.Value;
        }

        return new AnalyticsImportResult
        {
            Success = true,
            RowsImported = totalRows,
            RowsImportedBySourceReportType = byType,
            Summary = $"Imported {totalRows} AMC rows from real AMC workflow executions for {start:MMM d} - {end:MMM d, yyyy}."
        };
    }

    private async Task<string> CreateExecutionAsync(HttpClient http, string baseUrl, string token, string advertiserId, string marketplaceId, string instanceId, string label, string sql, DateOnly start, DateOnly end)
    {
        var body = JsonSerializer.Serialize(new
        {
            sqlQuery = sql,
            dryRun = false,
            timeWindowType = "EXPLICIT",
            timeWindowStart = $"{start:yyyy-MM-dd}T00:00:00",
            timeWindowEnd = $"{end.AddDays(1):yyyy-MM-dd}T00:00:00",
            timeWindowTimeZone = "UTC",
            workflowExecutionTimeoutSeconds = 1800
        });
        var response = await SendAmcAsync(http, HttpMethod.Post, baseUrl, $"/amc/reporting/{Uri.EscapeDataString(instanceId)}/workflowExecutions", token, advertiserId, marketplaceId, body, "application/vnd.amcworkflowexecutions.v1+json");
        if (!response.IsSuccess)
            throw new InvalidOperationException($"AMC ad-hoc workflow execution '{label}' failed HTTP {response.Status}: {response.SafeJson}");

        var executionId = FirstString(response.Json, "workflowExecutionId");
        if (string.IsNullOrWhiteSpace(executionId))
            throw new InvalidOperationException($"AMC ad-hoc workflow execution '{label}' did not return an execution ID: {response.SafeJson}");
        return executionId;
    }

    private async Task WaitForExecutionAsync(HttpClient http, string baseUrl, string token, string advertiserId, string marketplaceId, string instanceId, string executionId)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            var response = await SendAmcAsync(http, HttpMethod.Get, baseUrl, $"/amc/reporting/{Uri.EscapeDataString(instanceId)}/workflowExecutions/{Uri.EscapeDataString(executionId)}", token, advertiserId, marketplaceId, null, "application/vnd.amcworkflowexecutions.v1+json");
            if (!response.IsSuccess)
                throw new InvalidOperationException($"AMC workflow execution status failed HTTP {response.Status}: {response.SafeJson}");

            var status = FirstString(response.Json, "status");
            if (string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"AMC workflow execution {executionId} ended with status {status}: {response.SafeJson}");
        }

        throw new TimeoutException($"AMC workflow execution {executionId} did not complete within 20 minutes.");
    }

    private async Task<string> DownloadExecutionCsvAsync(HttpClient http, string baseUrl, string token, string advertiserId, string marketplaceId, string instanceId, string executionId)
    {
        var response = await SendAmcAsync(http, HttpMethod.Get, baseUrl, $"/amc/reporting/{Uri.EscapeDataString(instanceId)}/workflowExecutions/{Uri.EscapeDataString(executionId)}/downloadUrls", token, advertiserId, marketplaceId, null, "application/json");
        if (!response.IsSuccess)
            throw new InvalidOperationException($"AMC download URL request failed HTTP {response.Status}: {response.SafeJson}");

        if (response.Json is null)
            throw new InvalidOperationException($"AMC execution {executionId} returned non-JSON download metadata: {response.SafeJson}");

        var urls = StringsForProperty(response.Json.RootElement, "downloadUrls").ToList();
        if (!urls.Any())
            throw new InvalidOperationException($"AMC execution {executionId} did not provide result download URLs.");

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

    private async Task<AmcApiResponse> SendAmcAsync(HttpClient http, HttpMethod method, string baseUrl, string path, string token, string? advertiserId, string? marketplaceId, string? jsonBody, string mediaType)
    {
        using var req = new HttpRequestMessage(method, $"{baseUrl}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Amazon-Advertising-API-ClientId", _options.ClientId);
        req.Headers.Accept.ParseAdd(mediaType);
        if (!string.IsNullOrWhiteSpace(advertiserId))
        {
            req.Headers.TryAddWithoutValidation("Amazon-Advertising-API-AdvertiserId", advertiserId);
            req.Headers.TryAddWithoutValidation("Amazon-Advertising-API-EntityId", advertiserId);
        }
        if (!string.IsNullOrWhiteSpace(marketplaceId))
            req.Headers.TryAddWithoutValidation("Amazon-Advertising-API-MarketplaceId", marketplaceId);
        var adsAccountId = _config["AMC:AdsAccountId"];
        if (!string.IsNullOrWhiteSpace(adsAccountId))
            req.Headers.TryAddWithoutValidation("Amazon-Ads-AccountId", adsAccountId);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, mediaType);

        var resp = await http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        return AmcApiResponse.From((int)resp.StatusCode, raw);
    }

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

    private sealed class AmcApiResponse
    {
        public int Status { get; init; }
        public JsonDocument? Json { get; init; }
        public string SafeJson { get; init; } = "";
        public bool IsSuccess => Status is >= 200 and <= 299;

        public static AmcApiResponse From(int status, string raw)
        {
            JsonDocument? json = null;
            try { json = JsonDocument.Parse(raw); } catch { }
            return new AmcApiResponse
            {
                Status = status,
                Json = json,
                SafeJson = raw.Length > 4000 ? raw[..4000] : raw
            };
        }
    }
}

public class AmcResultIngestionService
{
    private readonly AdMetricsRepository _metrics;
    private readonly AmazonAccountResolver _accounts;

    public AmcResultIngestionService(AdMetricsRepository metrics, AmazonAccountResolver accounts)
    {
        _metrics = metrics;
        _accounts = accounts;
    }

    public async Task<AnalyticsImportResult> ImportResultsAsync(AmcResultImportRequest request, Stream body)
    {
        if (string.IsNullOrWhiteSpace(request.AccountKey))
            throw new InvalidOperationException("accountKey is required for AMC result import.");
        if (string.IsNullOrWhiteSpace(request.ResultType))
            throw new InvalidOperationException("resultType is required. Use traffic-hourly, conversion-hourly, or attribution-lag.");

        var account = _accounts.Resolve(request.AccountKey)
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

        var account = _accounts.Resolve(request.AccountKey)
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
        var parsed = rows.Select(row => new AmcTrafficHourly
            {
                Date = Date(row, "date", "traffic_date", "event_date"),
                Hour = Int(row, "hour", "traffic_hour", "event_hour"),
                TimeZone = Text(row, "time_zone", "timezone") ?? defaultTimeZone ?? "UTC",
                AccountKey = Text(row, "account_key", "accountkey") ?? accountKey,
                ProfileId = Text(row, "profile_id", "profileid") ?? profileId,
                CampaignId = RequiredText(row, "campaign_id", "campaignid"),
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
        var parsed = rows.Select(row => new AmcConversionsHourly
            {
                ConversionDate = Date(row, "conversion_date", "conversiondate", "date"),
                ConversionHour = Int(row, "conversion_hour", "conversionhour", "hour"),
                TimeZone = Text(row, "time_zone", "timezone") ?? defaultTimeZone ?? "UTC",
                AccountKey = Text(row, "account_key", "accountkey") ?? accountKey,
                ProfileId = Text(row, "profile_id", "profileid") ?? profileId,
                CampaignId = RequiredText(row, "campaign_id", "campaignid"),
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
                CampaignId = RequiredText(row, "campaign_id", "campaignid"),
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

    private static string RequiredText(Dictionary<string, string> row, params string[] names) =>
        Text(row, names) ?? throw new InvalidOperationException($"AMC row is missing required field '{names[0]}'.");

    private static string? Text(Dictionary<string, string> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (row.TryGetValue(NormalizeHeader(name), out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

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
