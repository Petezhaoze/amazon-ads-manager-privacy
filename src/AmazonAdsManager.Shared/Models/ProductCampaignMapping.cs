using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmazonAdsManager.Shared.Models;

public class ProductCampaignMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string CampaignType { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string? CampaignStartDate { get; set; }
    public string? CampaignEndDate { get; set; }

    public bool IsCurrentlyRunnable(DateOnly today) =>
        IsActive &&
        !IsAfterEndDate(CampaignEndDate, today) &&
        !IsBeforeStartDate(CampaignStartDate, today);

    private static bool IsAfterEndDate(string? raw, DateOnly today) =>
        TryParseAmazonDate(raw, out var endDate) && endDate < today;

    private static bool IsBeforeStartDate(string? raw, DateOnly today) =>
        TryParseAmazonDate(raw, out var startDate) && startDate > today;

    private static bool TryParseAmazonDate(string? raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (DateOnly.TryParseExact(raw, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date))
            return true;
        return DateOnly.TryParse(raw, out date);
    }
}

public class StringOrNumberJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var longValue)
                ? longValue.ToString(CultureInfo.InvariantCulture)
                : reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to string.")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
