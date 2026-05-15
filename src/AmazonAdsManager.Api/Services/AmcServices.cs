using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Configuration;
using System.Globalization;
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

    public AmcWorkflowService(IConfiguration config)
    {
        _config = config;
    }

    public Task<AnalyticsImportResult> RunWorkflowAsync(AnalyticsImportRequest request)
    {
        var instanceId = _config["AMC:InstanceId"];
        var trafficWorkflowId = _config["AMC:WorkflowIds:TrafficHourly"];
        var conversionWorkflowId = _config["AMC:WorkflowIds:ConversionHourly"];
        var lagWorkflowId = _config["AMC:WorkflowIds:AttributionLag"];

        if (string.IsNullOrWhiteSpace(instanceId) ||
            string.IsNullOrWhiteSpace(trafficWorkflowId) ||
            string.IsNullOrWhiteSpace(conversionWorkflowId) ||
            string.IsNullOrWhiteSpace(lagWorkflowId))
        {
            throw new InvalidOperationException(
                "AMC workflow execution is not configured. Add AMC:InstanceId and AMC:WorkflowIds:TrafficHourly, AMC:WorkflowIds:ConversionHourly, AMC:WorkflowIds:AttributionLag. Use the SQL templates in src/AmazonAdsManager.Api/Sql/amc-workflows to create the workflows in AMC.");
        }

        throw new NotImplementedException(
            "AMC API workflow execution is not wired to your AMC instance yet. Export the AMC workflow CSV results and POST them to /api/amc/import-results?resultType=traffic-hourly, conversion-hourly, or attribution-lag.");
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
        if (string.IsNullOrWhiteSpace(csv))
            throw new InvalidOperationException("AMC import body is empty. Upload the CSV result from AMC.");

        var rows = CsvRows.Parse(csv);
        if (!rows.Any())
            throw new InvalidOperationException("AMC import body did not contain any data rows.");

        var normalizedType = NormalizeResultType(request.ResultType);
        return normalizedType switch
        {
            "traffic-hourly" => ImportTraffic(rows, request.AccountKey, profileId, request.TimeZone),
            "conversion-hourly" => ImportConversions(rows, request.AccountKey, profileId, request.TimeZone),
            "attribution-lag" => ImportAttributionLag(rows, request.AccountKey, profileId),
            _ => throw new InvalidOperationException("Unsupported AMC resultType. Use traffic-hourly, conversion-hourly, or attribution-lag.")
        };
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
