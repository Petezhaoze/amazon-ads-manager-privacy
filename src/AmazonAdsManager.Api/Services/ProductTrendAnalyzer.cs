using AmazonAdsManager.Shared.Models;

namespace AmazonAdsManager.Api.Services;

public class ProductTrendAnalyzer
{
    private readonly ProductProfileRepository _profiles;
    private readonly ProductCampaignMappingRepository _mappings;
    private readonly ProductMetricRepository _metrics;

    public ProductTrendAnalyzer(
        ProductProfileRepository profiles,
        ProductCampaignMappingRepository mappings,
        ProductMetricRepository metrics)
    {
        _profiles = profiles;
        _mappings = mappings;
        _metrics = metrics;
    }

    public ProductTrendSummary AnalyzeProduct(string accountKey, string productId)
    {
        var product = _profiles.GetById(productId);
        if (product is null) throw new InvalidOperationException($"Product {productId} not found");

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var last7Start = today.AddDays(-6);
        var last7End = today;
        var prev7Start = today.AddDays(-13);
        var prev7End = today.AddDays(-7);

        bool isSynthetic = false;
        if (!_metrics.HasAnyMetrics(accountKey, productId))
        {
            GenerateSyntheticMetrics(accountKey, product);
            isSynthetic = true;
        }

        var last7Metrics = _metrics.GetByProductDateRange(accountKey, productId, last7Start, last7End);
        var prev7Metrics = _metrics.GetByProductDateRange(accountKey, productId, prev7Start, prev7End);

        var last7 = AggregateMetrics(last7Metrics);
        var prev7 = AggregateMetrics(prev7Metrics);

        var spendChange = prev7.Spend > 0 ? (last7.Spend - prev7.Spend) / prev7.Spend * 100 : 0;
        var salesChange = prev7.Sales > 0 ? (last7.Sales - prev7.Sales) / prev7.Sales * 100 : 0;
        var acosChange = prev7.Acos > 0 ? (last7.Acos - prev7.Acos) / prev7.Acos * 100 : 0;

        var last7Cvr = last7.Clicks > 0 ? (decimal)last7.Orders / last7.Clicks * 100 : 0;
        var prev7Cvr = prev7.Clicks > 0 ? (decimal)prev7.Orders / prev7.Clicks * 100 : 0;
        var cvrChange = prev7Cvr > 0 ? (last7Cvr - prev7Cvr) / prev7Cvr * 100 : 0;

        var trends = GenerateTrendNotes(product, last7, prev7, (double)acosChange);
        var linkedCampaigns = _mappings.GetByProduct(accountKey, productId).ToList();

        return new ProductTrendSummary
        {
            AccountKey = accountKey,
            ProductId = productId,
            ProductName = product.DisplayName,
            ASIN = product.ASIN,
            SKU = product.SKU,
            TargetAcos = product.TargetAcos,
            Last7DaysSpend = last7.Spend,
            Last7DaysSales = last7.Sales,
            Last7DaysAcos = last7.Acos,
            Last7DaysClicks = last7.Clicks,
            Last7DaysOrders = last7.Orders,
            Previous7DaysSpend = prev7.Spend,
            Previous7DaysSales = prev7.Sales,
            Previous7DaysAcos = prev7.Acos,
            Previous7DaysClicks = prev7.Clicks,
            Previous7DaysOrders = prev7.Orders,
            SpendChangePercent = (decimal)spendChange,
            SalesChangePercent = (decimal)salesChange,
            AcosChangePercent = (decimal)acosChange,
            ConversionRateChangePercent = (decimal)cvrChange,
            TrendNotes = trends,
            LinkedCampaigns = linkedCampaigns,
            IsSyntheticMetrics = isSynthetic
        };
    }

    private void GenerateSyntheticMetrics(string accountKey, ProductProfile product)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var budget = product.DefaultDailyBudget > 0 ? product.DefaultDailyBudget.Value : 50m;
        var rng = new Random(product.Id.GetHashCode());

        for (int d = 13; d >= 0; d--)
        {
            var date = today.AddDays(-d);
            var spend = budget * (0.6m + (decimal)rng.NextDouble() * 0.8m);
            var sales = spend * (product.TargetAcos > 0 ? (0.8m / product.TargetAcos) * (0.7m + (decimal)rng.NextDouble() * 0.6m) : 3m);
            var clicks = (int)(spend * (8 + rng.Next(0, 8)));
            var orders = clicks > 0 ? Math.Max(0, (int)(clicks * (0.01 + rng.NextDouble() * 0.03))) : 0;

            _metrics.Upsert(new ProductMetric
            {
                AccountKey = accountKey,
                ProductId = product.Id,
                ProductName = product.DisplayName,
                ASIN = product.ASIN,
                SKU = product.SKU,
                Date = date,
                Spend = Math.Round(spend, 2),
                Sales = Math.Round(sales, 2),
                Clicks = clicks,
                Impressions = clicks * (10 + rng.Next(0, 15)),
                Orders = orders,
                AdAttributedUnits = orders
            });
        }
    }

    private (decimal Spend, decimal Sales, int Clicks, int Impressions, int Orders, int AdAttributedUnits, decimal Acos) AggregateMetrics(IEnumerable<ProductMetric> metrics)
    {
        var list = metrics.ToList();
        if (list.Count == 0) return (0, 0, 0, 0, 0, 0, 0);

        var spend = list.Sum(m => m.Spend);
        var sales = list.Sum(m => m.Sales);
        var clicks = list.Sum(m => m.Clicks);
        var impressions = list.Sum(m => m.Impressions);
        var orders = list.Sum(m => m.Orders);
        var units = list.Sum(m => m.AdAttributedUnits);
        var acos = spend > 0 ? sales / spend : 0;

        return (spend, sales, clicks, impressions, orders, units, acos);
    }

    private List<string> GenerateTrendNotes(ProductProfile product,
        (decimal, decimal, int, int, int, int, decimal) last7,
        (decimal, decimal, int, int, int, int, decimal) prev7,
        double acosChange)
    {
        var notes = new List<string>();

        if (last7.Item7 > product.TargetAcos * 1.5m)
            notes.Add("ACOS is significantly above target.");

        if (last7.Item1 > prev7.Item1 && last7.Item2 < prev7.Item2)
            notes.Add("Spend increased while sales decreased.");

        if (last7.Item3 >= 20 && last7.Item5 == 0)
            notes.Add("Product received clicks but no orders.");

        if (acosChange < 0)
            notes.Add("ACOS improved compared to previous period.");

        if (last7.Item2 > prev7.Item2 && last7.Item7 < product.TargetAcos)
            notes.Add("Product may be a candidate for increased budget.");

        return notes;
    }
}
