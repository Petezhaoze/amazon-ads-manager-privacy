namespace AmazonAdsManager.Shared.Models;

public class CampaignDto
{
    public string CampaignId { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string BudgetType { get; set; } = "";
    public decimal DailyBudget { get; set; }
    public string? Asin { get; set; }
    public string? EndDate { get; set; }
    public string? ServingStatus { get; set; }

    public bool IsEnded
    {
        get
        {
            if (string.IsNullOrEmpty(EndDate)) return false;
            // Amazon returns dates as YYYYMMDD (e.g. "20260101")
            if (DateOnly.TryParseExact(EndDate, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d1))
                return d1 < DateOnly.FromDateTime(DateTime.UtcNow);
            // Fallback for ISO format "2026-01-01"
            if (DateOnly.TryParse(EndDate, out var d2))
                return d2 < DateOnly.FromDateTime(DateTime.UtcNow);
            return false;
        }
    }
}
