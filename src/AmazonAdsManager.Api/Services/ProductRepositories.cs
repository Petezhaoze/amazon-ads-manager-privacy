using AmazonAdsManager.Shared.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

// Shared blob helper — one container, each repo owns one blob file
file static class BlobStore
{
    internal static BlobContainerClient? GetContainer(IConfiguration config)
    {
        var connStr = config["AzureWebJobsStorage"];
        if (string.IsNullOrEmpty(connStr) || connStr == "UseDevelopmentStorage=true") return null;
        var container = new BlobContainerClient(connStr, "amazon-ads-manager-data");
        container.CreateIfNotExists();
        return container;
    }

    internal static string? Read(BlobContainerClient? container, string blobName, string localPath)
    {
        if (container is not null)
        {
            var blob = container.GetBlobClient(blobName);
            if (blob.Exists()) return blob.DownloadContent().Value.Content.ToString();
        }
        else if (File.Exists(localPath)) return File.ReadAllText(localPath);
        return null;
    }

    internal static void Write(BlobContainerClient? container, string blobName, string localPath, string json)
    {
        if (container is not null)
        {
            var blob = container.GetBlobClient(blobName);
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            blob.Upload(stream, overwrite: true);
        }
        else File.WriteAllText(localPath, json);
    }

    internal static string LocalPath(string fileName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        var dir = Path.Combine(home, ".amazon-ads-manager");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }
}

public class ProductProfileRepository
{
    private readonly List<ProductProfile> _products = new();
    private readonly BlobContainerClient? _container;
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ProductProfileRepository(IConfiguration config)
    {
        _container = BlobStore.GetContainer(config);
        var json = BlobStore.Read(_container, "products.json", BlobStore.LocalPath("products.json"));
        if (json is not null)
        {
            var loaded = JsonSerializer.Deserialize<List<ProductProfile>>(json, _opts);
            if (loaded is not null) _products.AddRange(loaded);
        }
    }

    private void Save() =>
        BlobStore.Write(_container, "products.json", BlobStore.LocalPath("products.json"),
            JsonSerializer.Serialize(_products, _opts));

    public IReadOnlyList<ProductProfile> GetByAccount(string accountKey) =>
        _products.Where(p => string.Equals(p.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                 .ToList().AsReadOnly();

    public ProductProfile? GetById(string id) => _products.FirstOrDefault(p => p.Id == id);

    public ProductProfile Upsert(ProductProfile product)
    {
        lock (_products)
        {
            var existing = _products.FirstOrDefault(p => p.Id == product.Id);
            if (existing is not null) _products.Remove(existing);
            _products.Add(product);
            Save();
        }
        return product;
    }

    public bool Delete(string id)
    {
        lock (_products)
        {
            var existing = _products.FirstOrDefault(p => p.Id == id);
            if (existing is null) return false;
            _products.Remove(existing);
            Save();
        }
        return true;
    }

    public List<ProductProfile> GetAll() => _products.ToList();
}

public class ProductCampaignMappingRepository
{
    private readonly List<ProductCampaignMapping> _mappings = new();
    private readonly BlobContainerClient? _container;
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ProductCampaignMappingRepository(IConfiguration config)
    {
        _container = BlobStore.GetContainer(config);
        var json = BlobStore.Read(_container, "campaign-mappings.json", BlobStore.LocalPath("campaign_mappings.json"));
        if (json is not null)
        {
            var loaded = JsonSerializer.Deserialize<List<ProductCampaignMapping>>(json, _opts);
            if (loaded is not null) _mappings.AddRange(loaded);
        }
    }

    private void Save() =>
        BlobStore.Write(_container, "campaign-mappings.json", BlobStore.LocalPath("campaign_mappings.json"),
            JsonSerializer.Serialize(_mappings, _opts));

    public IReadOnlyList<ProductCampaignMapping> GetByAccount(string accountKey) =>
        _mappings.Where(m => string.Equals(m.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                 .ToList().AsReadOnly();

    public IReadOnlyList<ProductCampaignMapping> GetByProduct(string accountKey, string productId) =>
        _mappings.Where(m => string.Equals(m.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(m.ProductId, productId, StringComparison.OrdinalIgnoreCase))
                 .ToList().AsReadOnly();

    public ProductCampaignMapping? GetById(string id) => _mappings.FirstOrDefault(m => m.Id == id);

    public ProductCampaignMapping Upsert(ProductCampaignMapping mapping)
    {
        lock (_mappings)
        {
            var existing = _mappings.FirstOrDefault(m => m.Id == mapping.Id);
            if (existing is not null) _mappings.Remove(existing);
            _mappings.Add(mapping);
            Save();
        }
        return mapping;
    }

    public bool Delete(string id)
    {
        lock (_mappings)
        {
            var existing = _mappings.FirstOrDefault(m => m.Id == id);
            if (existing is null) return false;
            _mappings.Remove(existing);
            Save();
        }
        return true;
    }

    public List<ProductCampaignMapping> GetAll() => _mappings.ToList();
}

public class ProductMetricRepository
{
    private readonly List<ProductMetric> _metrics = new();
    private readonly BlobContainerClient? _container;
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ProductMetricRepository(IConfiguration config)
    {
        _container = BlobStore.GetContainer(config);
        var json = BlobStore.Read(_container, "product-metrics.json", BlobStore.LocalPath("product_metrics.json"));
        if (json is not null)
        {
            var loaded = JsonSerializer.Deserialize<List<ProductMetric>>(json, _opts);
            if (loaded is not null) _metrics.AddRange(loaded);
        }
    }

    private void Save() =>
        BlobStore.Write(_container, "product-metrics.json", BlobStore.LocalPath("product_metrics.json"),
            JsonSerializer.Serialize(_metrics, _opts));

    public IReadOnlyList<ProductMetric> GetByProductDateRange(string accountKey, string productId, DateOnly startDate, DateOnly endDate) =>
        _metrics.Where(m => string.Equals(m.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(m.ProductId, productId, StringComparison.OrdinalIgnoreCase) &&
                           m.Date >= startDate && m.Date <= endDate)
                .OrderByDescending(m => m.Date)
                .ToList().AsReadOnly();

    public bool HasAnyMetrics(string accountKey, string productId) =>
        _metrics.Any(m => string.Equals(m.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(m.ProductId, productId, StringComparison.OrdinalIgnoreCase));

    public ProductMetric? GetByProductAndDate(string accountKey, string productId, DateOnly date) =>
        _metrics.FirstOrDefault(m => string.Equals(m.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(m.ProductId, productId, StringComparison.OrdinalIgnoreCase) &&
                                    m.Date == date);

    public ProductMetric Upsert(ProductMetric metric)
    {
        lock (_metrics)
        {
            var existing = GetByProductAndDate(metric.AccountKey, metric.ProductId, metric.Date);
            if (existing is not null) _metrics.Remove(existing);
            _metrics.Add(metric);
            Save();
        }
        return metric;
    }

    public void BulkUpsert(IEnumerable<ProductMetric> metrics)
    {
        foreach (var m in metrics) Upsert(m);
    }

    public List<ProductMetric> GetAll() => _metrics.ToList();
}

public class ProductAiRecommendationRepository
{
    private readonly List<ProductAiRecommendation> _recommendations = new();

    public IReadOnlyList<ProductAiRecommendation> GetByProduct(string accountKey, string productId) =>
        _recommendations.Where(r => string.Equals(r.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(r.ProductId, productId, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(r => r.CreatedAt)
                        .ToList().AsReadOnly();

    public ProductAiRecommendation? GetById(string id) =>
        _recommendations.FirstOrDefault(r => r.Id == id);

    public ProductAiRecommendation Upsert(ProductAiRecommendation recommendation)
    {
        var existing = _recommendations.FirstOrDefault(r => r.Id == recommendation.Id);
        if (existing is not null) _recommendations.Remove(existing);
        _recommendations.Add(recommendation);
        return recommendation;
    }

    public List<ProductAiRecommendation> GetAll() => _recommendations.ToList();
}

public class ProductTrainingExampleRepository
{
    private readonly List<ProductTrainingExample> _examples = new();

    public IReadOnlyList<ProductTrainingExample> GetByProduct(string accountKey, string productId) =>
        _examples.Where(e => string.Equals(e.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(e.ProductId, productId, StringComparison.OrdinalIgnoreCase))
                 .OrderByDescending(e => e.CreatedAt)
                 .ToList().AsReadOnly();

    public ProductTrainingExample Upsert(ProductTrainingExample example)
    {
        var existing = _examples.FirstOrDefault(e => e.Id == example.Id);
        if (existing is not null) _examples.Remove(existing);
        _examples.Add(example);
        return example;
    }

    public void BulkUpsert(IEnumerable<ProductTrainingExample> examples)
    {
        foreach (var e in examples) Upsert(e);
    }

    public List<ProductTrainingExample> GetAll() => _examples.ToList();
}
