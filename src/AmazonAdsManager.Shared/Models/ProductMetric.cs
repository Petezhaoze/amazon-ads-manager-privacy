namespace AmazonAdsManager.Shared.Models;

public class ProductMetric
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ASIN { get; set; } = "";
    public string SKU { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal Spend { get; set; }
    public decimal Sales { get; set; }
    public int Clicks { get; set; }
    public int Impressions { get; set; }
    public int Orders { get; set; }
    public int AdAttributedUnits { get; set; }

    public decimal Acos => Spend > 0 ? Sales / Spend : 0;
    public decimal Roas => Sales > 0 ? Sales / Spend : 0;
    public decimal Cpc => Clicks > 0 ? Spend / Clicks : 0;
    public decimal Ctr => Impressions > 0 ? (decimal)Clicks / Impressions * 100 : 0;
    public decimal Cvr => Clicks > 0 ? (decimal)Orders / Clicks * 100 : 0;
}
