using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Bind AmazonAds config section
builder.Services.Configure<AmazonAdsOptions>(options =>
{
    options.ClientId = builder.Configuration["AmazonAds:ClientId"] ?? "";
    options.ClientSecret = builder.Configuration["AmazonAds:ClientSecret"] ?? "";

    var accounts = new List<AmazonAccountConfig>();
    var i = 0;
    while (true)
    {
        var key = builder.Configuration[$"AmazonAds:Accounts:{i}:AccountKey"];
        if (string.IsNullOrEmpty(key)) break;
        accounts.Add(new AmazonAccountConfig
        {
            AccountKey = key,
            DisplayName = builder.Configuration[$"AmazonAds:Accounts:{i}:DisplayName"] ?? key,
            RefreshToken = builder.Configuration[$"AmazonAds:Accounts:{i}:RefreshToken"] ?? "",
            ProfileId = builder.Configuration[$"AmazonAds:Accounts:{i}:ProfileId"] ?? "",
            BaseUrl = builder.Configuration[$"AmazonAds:Accounts:{i}:BaseUrl"] ?? "https://advertising-api.amazon.com"
        });
        i++;
    }
    options.Accounts = accounts;
});

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("amazon-scraper")
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip
            | System.Net.DecompressionMethods.Deflate
            | System.Net.DecompressionMethods.Brotli
    });
builder.Services.AddSingleton<AmazonAccountResolver>();
builder.Services.AddSingleton<OAuthService>();
builder.Services.AddSingleton<AmazonAdsAuthService>();
builder.Services.AddSingleton<AmazonCampaignService>();
builder.Services.AddSingleton<ScheduleRepository>();
builder.Services.AddSingleton<CampaignLogRepository>();
builder.Services.AddSingleton<ScheduleRunnerService>();

// Product services
builder.Services.AddSingleton<ProductProfileRepository>();
builder.Services.AddSingleton<ProductCampaignMappingRepository>();
builder.Services.AddSingleton<ProductMetricRepository>();
builder.Services.AddSingleton<ProductAiRecommendationRepository>();
builder.Services.AddSingleton<ProductTrainingExampleRepository>();
builder.Services.AddSingleton<ProductTrendAnalyzer>();
builder.Services.AddSingleton<IAiClient, MockAiClient>();
builder.Services.AddSingleton<ProductAiRecommendationService>();
builder.Services.AddSingleton<ProductRecommendationDecisionService>();
builder.Services.AddSingleton<ProductTrainingDataExportService>();
builder.Services.AddSingleton<MockProductReportImportService>();
builder.Services.AddSingleton<AmazonProductSyncService>();
builder.Services.AddSingleton<ProductActionPreviewService>();
builder.Services.AddSingleton<AmazonProductImageService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Build().Run();
