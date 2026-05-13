using AmazonAdsManager.Shared.Models;

namespace AmazonAdsManager.Api.Services;

public class MockProductReportImportService
{
    private readonly ProductProfileRepository _profiles;
    private readonly ProductCampaignMappingRepository _mappings;
    private readonly ProductMetricRepository _metrics;

    public MockProductReportImportService(
        ProductProfileRepository profiles,
        ProductCampaignMappingRepository mappings,
        ProductMetricRepository metrics)
    {
        _profiles = profiles;
        _mappings = mappings;
        _metrics = metrics;
    }

    public void LoadMockDataForAccount(string accountKey)
    {
        var products = new List<ProductProfile>();
        var campaigns = new List<ProductCampaignMapping>();
        var metricsData = new List<ProductMetric>();

        // Create 2 products per account
        var product1 = new ProductProfile
        {
            Id = $"prod_elite_{accountKey}",
            AccountKey = accountKey,
            DisplayName = "EliteTrace Tracer Pellet Kit",
            ASIN = "B0D4F9K2L1",
            SKU = "ELITE-TRACER-001",
            TargetAcos = 0.25m,
            DefaultDailyBudget = 50,
            IsActive = true,
            Notes = "High-performing product with stable demand"
        };

        var product2 = new ProductProfile
        {
            Id = $"prod_reactive_{accountKey}",
            AccountKey = accountKey,
            DisplayName = "Reactive Clay Pigeon Field Kit",
            ASIN = "B0E2N8R3M9",
            SKU = "CLAY-FIELD-002",
            TargetAcos = 0.30m,
            DefaultDailyBudget = 75,
            IsActive = true,
            Notes = "Seasonal product with variable performance"
        };

        products.AddRange(new[] { product1, product2 });

        // Create campaigns
        var campaigns1 = new List<ProductCampaignMapping>
        {
            new() { AccountKey = accountKey, ProductId = product1.Id, CampaignId = 100001L, CampaignName = "Elite Exact Match", CampaignType = "SP", IsActive = true },
            new() { AccountKey = accountKey, ProductId = product1.Id, CampaignId = 100002L, CampaignName = "Elite Broad", CampaignType = "SP", IsActive = true }
        };

        var campaigns2 = new List<ProductCampaignMapping>
        {
            new() { AccountKey = accountKey, ProductId = product2.Id, CampaignId = 100003L, CampaignName = "Clay Auto", CampaignType = "SP", IsActive = true },
            new() { AccountKey = accountKey, ProductId = product2.Id, CampaignId = 100004L, CampaignName = "Clay Exact", CampaignType = "SP", IsActive = true }
        };

        campaigns.AddRange(campaigns1);
        campaigns.AddRange(campaigns2);

        // Generate 14 days of metrics data
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        for (int d = 13; d >= 0; d--)
        {
            var date = today.AddDays(-d);

            // Product 1 metrics - improving trend
            var spend1 = 40m + (Random.Shared.Next(10, 30));
            var sales1 = 150m + (Random.Shared.Next(20, 80));
            var clicks1 = 150 + Random.Shared.Next(20, 60);
            var orders1 = 3 + Random.Shared.Next(0, 3);

            metricsData.Add(new ProductMetric
            {
                AccountKey = accountKey,
                ProductId = product1.Id,
                ProductName = product1.DisplayName,
                ASIN = product1.ASIN,
                SKU = product1.SKU,
                Date = date,
                Spend = spend1,
                Sales = sales1,
                Clicks = clicks1,
                Impressions = clicks1 * 10 + Random.Shared.Next(500, 1500),
                Orders = orders1,
                AdAttributedUnits = orders1 * 2
            });

            // Product 2 metrics - variable trend
            var spend2 = 60m + (Random.Shared.Next(-20, 40));
            var sales2 = 160m + (Random.Shared.Next(-40, 60));
            var clicks2 = 180 + Random.Shared.Next(-30, 50);
            var orders2 = 2 + Random.Shared.Next(-1, 2);

            metricsData.Add(new ProductMetric
            {
                AccountKey = accountKey,
                ProductId = product2.Id,
                ProductName = product2.DisplayName,
                ASIN = product2.ASIN,
                SKU = product2.SKU,
                Date = date,
                Spend = spend2,
                Sales = sales2,
                Clicks = Math.Max(clicks2, 0),
                Impressions = Math.Max(clicks2, 0) * 10 + Random.Shared.Next(500, 1500),
                Orders = Math.Max(orders2, 0),
                AdAttributedUnits = Math.Max(orders2, 0) * 2
            });
        }

        // Save all data
        foreach (var p in products) _profiles.Upsert(p);
        foreach (var c in campaigns) _mappings.Upsert(c);
        foreach (var m in metricsData) _metrics.Upsert(m);
    }

    public void LoadMockDataForAllAccounts()
    {
        LoadMockDataForAccount("peter");
        LoadMockDataForAccount("dad");
    }
}
