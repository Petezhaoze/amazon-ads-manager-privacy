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

