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

// Core infrastructure
builder.Services.AddSingleton<AmazonAccountResolver>();
builder.Services.AddSingleton<ApiAccessService>();
builder.Services.AddSingleton<OAuthService>();
builder.Services.AddSingleton<AmazonAdsAuthService>();

// Campaign management
builder.Services.AddSingleton<AmazonCampaignService>();
builder.Services.AddSingleton<ScheduleRepository>();
builder.Services.AddSingleton<CampaignLogRepository>();
builder.Services.AddSingleton<ScheduleRunnerService>();

// Product management
builder.Services.AddSingleton<ProductProfileRepository>();
builder.Services.AddSingleton<ProductCampaignMappingRepository>();
builder.Services.AddSingleton<AmazonProductSyncService>();
builder.Services.AddSingleton<AmazonProductImageService>();

// AI client — OpenAI (requires OpenAI:ApiKey in app settings)
builder.Services.AddSingleton<IAiClient, OpenAiClient>();

// Analytics: real Amazon Ads Reporting API + scorecard + recommendations
builder.Services.AddSingleton<AmazonSPReportingService>();
builder.Services.AddSingleton<AmazonAdsReportService>();
builder.Services.AddSingleton<AmcWorkflowService>();
builder.Services.AddSingleton<AmcResultIngestionService>();
builder.Services.AddSingleton<ProductAnalyticsRepository>();
builder.Services.AddSingleton<AdMetricsRepository>();
builder.Services.AddSingleton<HourlyScorecardService>();
builder.Services.AddSingleton<AiRecommendationPromptBuilder>();
builder.Services.AddSingleton<AiRecommendationEvidenceService>();
builder.Services.AddSingleton<RecommendationExperimentService>();
builder.Services.AddSingleton<ProductAiRecommendationServiceV2>();
builder.Services.AddSingleton<AiReviewDataRefreshService>();
builder.Services.AddSingleton<RecommendationApplyService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Build().Run();
