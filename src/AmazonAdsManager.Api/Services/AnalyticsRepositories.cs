using AmazonAdsManager.Shared.Models;

namespace AmazonAdsManager.Api.Services;

public class ProductAnalyticsRepository
{
    private readonly ProductProfileRepository _products;
    private readonly ProductCampaignMappingRepository _mappings;

    public ProductAnalyticsRepository(ProductProfileRepository products, ProductCampaignMappingRepository mappings)
    {
        _products = products;
        _mappings = mappings;
    }

    public IReadOnlyList<ProductProfile> GetProductsWithCampaigns(string accountKey)
    {
        var mappedProductIds = _mappings.GetByAccount(accountKey)
            .Select(m => m.ProductId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _products.GetByAccount(accountKey)
            .Where(p => mappedProductIds.Contains(p.Id))
            .OrderBy(p => p.DisplayName)
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<ProductCampaignMapping> GetMappings(string accountKey, string productId) =>
        _mappings.GetByProduct(accountKey, productId);

    public ProductProfile? GetProduct(string productId) => _products.GetById(productId);
}

public class AdMetricsRepository
{
    private readonly List<AdPerformanceDaily> _daily = new();
    private readonly List<CampaignSnapshot> _campaignSnapshots = new();
    private readonly List<AmcTrafficHourly> _trafficHourly = new();
    private readonly List<AmcConversionsHourly> _conversionsHourly = new();
    private readonly List<AmcAttributionLag> _attributionLag = new();
    private readonly List<HourlyScorecard> _scorecards = new();
    private readonly List<AiRecommendation> _recommendations = new();
    private readonly List<AiRecommendationEvidence> _evidence = new();
    private readonly List<RecommendationExperiment> _experiments = new();
    private readonly object _lock = new();

    public void UpsertCampaignSnapshots(IEnumerable<CampaignSnapshot> snapshots)
    {
        lock (_lock)
        {
            foreach (var snapshot in snapshots)
            {
                _campaignSnapshots.RemoveAll(s =>
                    s.SnapshotDate == snapshot.SnapshotDate &&
                    KeyEquals(s.AccountKey, snapshot.AccountKey) &&
                    KeyEquals(s.CampaignId, snapshot.CampaignId));
                _campaignSnapshots.Add(snapshot);
            }
        }
    }

    public void UpsertDailyMetrics(IEnumerable<AdPerformanceDaily> rows)
    {
        lock (_lock)
        {
            foreach (var row in rows)
            {
                _daily.RemoveAll(d =>
                    d.Date == row.Date &&
                    KeyEquals(d.AccountKey, row.AccountKey) &&
                    KeyEquals(d.CampaignId, row.CampaignId) &&
                    KeyEquals(d.AdGroupId, row.AdGroupId) &&
                    KeyEquals(d.SearchTerm, row.SearchTerm) &&
                    KeyEquals(d.TargetingText, row.TargetingText));
                _daily.Add(row);
            }
        }
    }

    public void UpsertAmcTrafficHourly(IEnumerable<AmcTrafficHourly> rows)
    {
        lock (_lock)
        {
            foreach (var row in rows)
            {
                _trafficHourly.RemoveAll(t =>
                    t.Date == row.Date &&
                    t.Hour == row.Hour &&
                    KeyEquals(t.AccountKey, row.AccountKey) &&
                    KeyEquals(t.CampaignId, row.CampaignId) &&
                    KeyEquals(t.AdGroupId, row.AdGroupId) &&
                    KeyEquals(t.CustomerSearchTerm, row.CustomerSearchTerm));
                _trafficHourly.Add(row);
            }
        }
    }

    public void UpsertAmcConversionsHourly(IEnumerable<AmcConversionsHourly> rows)
    {
        lock (_lock)
        {
            foreach (var row in rows)
            {
                _conversionsHourly.RemoveAll(c =>
                    c.ConversionDate == row.ConversionDate &&
                    c.ConversionHour == row.ConversionHour &&
                    KeyEquals(c.AccountKey, row.AccountKey) &&
                    KeyEquals(c.CampaignId, row.CampaignId) &&
                    KeyEquals(c.AdGroupId, row.AdGroupId) &&
                    KeyEquals(c.TrackedAsin, row.TrackedAsin));
                _conversionsHourly.Add(row);
            }
        }
    }

    public void UpsertAmcAttributionLag(IEnumerable<AmcAttributionLag> rows)
    {
        lock (_lock)
        {
            foreach (var row in rows)
            {
                _attributionLag.RemoveAll(a =>
                    a.TrafficDate == row.TrafficDate &&
                    a.TrafficHour == row.TrafficHour &&
                    a.ConversionDate == row.ConversionDate &&
                    a.ConversionHour == row.ConversionHour &&
                    KeyEquals(a.AccountKey, row.AccountKey) &&
                    KeyEquals(a.CampaignId, row.CampaignId) &&
                    KeyEquals(a.AdGroupId, row.AdGroupId) &&
                    KeyEquals(a.SearchTerm, row.SearchTerm));
                _attributionLag.Add(row);
            }
        }
    }

    public IReadOnlyList<AdPerformanceDaily> GetDailyMetrics(string accountKey, string productId, DateOnly start, DateOnly end)
    {
        lock (_lock)
        {
            return _daily.Where(d =>
                    KeyEquals(d.AccountKey, accountKey) &&
                    KeyEquals(d.ProductId, productId) &&
                    d.Date >= start &&
                    d.Date <= end)
                .ToList()
                .AsReadOnly();
        }
    }

    public IReadOnlyList<AmcTrafficHourly> GetTrafficHourly(string accountKey, IEnumerable<string> campaignIds, DateOnly start, DateOnly end)
    {
        var campaignSet = campaignIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            return _trafficHourly.Where(t =>
                    KeyEquals(t.AccountKey, accountKey) &&
                    campaignSet.Contains(t.CampaignId) &&
                    t.Date >= start &&
                    t.Date <= end)
                .ToList()
                .AsReadOnly();
        }
    }

    public IReadOnlyList<AmcConversionsHourly> GetConversionsHourly(string accountKey, IEnumerable<string> campaignIds, DateOnly start, DateOnly end)
    {
        var campaignSet = campaignIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            return _conversionsHourly.Where(c =>
                    KeyEquals(c.AccountKey, accountKey) &&
                    campaignSet.Contains(c.CampaignId) &&
                    c.ConversionDate >= start &&
                    c.ConversionDate <= end)
                .ToList()
                .AsReadOnly();
        }
    }

    public void ReplaceScorecard(string accountKey, string productId, DateOnly start, DateOnly end, IEnumerable<HourlyScorecard> rows)
    {
        lock (_lock)
        {
            _scorecards.RemoveAll(s =>
                KeyEquals(s.AccountKey, accountKey) &&
                KeyEquals(s.ProductId, productId) &&
                s.DateRangeStart == start &&
                s.DateRangeEnd == end);
            _scorecards.AddRange(rows);
        }
    }

    public IReadOnlyList<HourlyScorecard> GetScorecard(string accountKey, string productId, DateOnly? start = null, DateOnly? end = null)
    {
        lock (_lock)
        {
            return _scorecards.Where(s =>
                    KeyEquals(s.AccountKey, accountKey) &&
                    KeyEquals(s.ProductId, productId) &&
                    (start is null || s.DateRangeStart == start) &&
                    (end is null || s.DateRangeEnd == end))
                .OrderBy(s => s.DayOfWeek)
                .ThenBy(s => s.Hour)
                .ToList()
                .AsReadOnly();
        }
    }

    public AiRecommendation UpsertRecommendation(AiRecommendation recommendation)
    {
        lock (_lock)
        {
            _recommendations.RemoveAll(r => KeyEquals(r.RecommendationId, recommendation.RecommendationId));
            _recommendations.Add(recommendation);
        }
        return recommendation;
    }

    public AiRecommendation? GetRecommendation(string recommendationId)
    {
        lock (_lock)
            return _recommendations.FirstOrDefault(r => KeyEquals(r.RecommendationId, recommendationId));
    }

    public IReadOnlyList<AiRecommendation> GetRecommendations(string accountKey, string productId)
    {
        lock (_lock)
        {
            return _recommendations.Where(r => KeyEquals(r.AccountKey, accountKey) && KeyEquals(r.ProductId, productId))
                .OrderByDescending(r => r.CreatedAt)
                .ToList()
                .AsReadOnly();
        }
    }

    public void ReplaceEvidence(string recommendationId, IEnumerable<AiRecommendationEvidence> rows)
    {
        lock (_lock)
        {
            _evidence.RemoveAll(e => KeyEquals(e.RecommendationId, recommendationId));
            _evidence.AddRange(rows);
        }
    }

    public IReadOnlyList<AiRecommendationEvidence> GetEvidence(string recommendationId)
    {
        lock (_lock)
            return _evidence.Where(e => KeyEquals(e.RecommendationId, recommendationId)).ToList().AsReadOnly();
    }

    public RecommendationExperiment UpsertExperiment(RecommendationExperiment experiment)
    {
        lock (_lock)
        {
            _experiments.RemoveAll(e => KeyEquals(e.ExperimentId, experiment.ExperimentId));
            _experiments.Add(experiment);
        }
        return experiment;
    }

    public IReadOnlyList<RecommendationExperiment> GetExperiments(string productId)
    {
        lock (_lock)
            return _experiments.Where(e => KeyEquals(e.ProductId, productId)).OrderByDescending(e => e.CreatedAt).ToList().AsReadOnly();
    }

    public bool HasAnalyticsRows(string accountKey, string productId)
    {
        lock (_lock)
            return _daily.Any(d => KeyEquals(d.AccountKey, accountKey) && KeyEquals(d.ProductId, productId));
    }

    private static bool KeyEquals(string? left, string? right) =>
        string.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
}
