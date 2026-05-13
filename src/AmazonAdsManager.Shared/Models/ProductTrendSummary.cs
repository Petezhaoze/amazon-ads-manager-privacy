namespace AmazonAdsManager.Shared.Models;

public class ProductTrendSummary
{
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ASIN { get; set; } = "";
    public string SKU { get; set; } = "";
    public decimal TargetAcos { get; set; }

    public decimal Last7DaysSpend { get; set; }
    public decimal Last7DaysSales { get; set; }
    public decimal Last7DaysAcos { get; set; }
    public int Last7DaysClicks { get; set; }
    public int Last7DaysOrders { get; set; }

    public decimal Previous7DaysSpend { get; set; }
    public decimal Previous7DaysSales { get; set; }
    public decimal Previous7DaysAcos { get; set; }
    public int Previous7DaysClicks { get; set; }
    public int Previous7DaysOrders { get; set; }

    public decimal SpendChangePercent { get; set; }
    public decimal SalesChangePercent { get; set; }
    public decimal AcosChangePercent { get; set; }
    public decimal ConversionRateChangePercent { get; set; }

    public List<string> TrendNotes { get; set; } = new();
    public List<ProductCampaignMapping> LinkedCampaigns { get; set; } = new();
    public bool IsSyntheticMetrics { get; set; }
}
