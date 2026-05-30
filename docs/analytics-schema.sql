-- Azure SQL schema for Amazon Ads Manager normalized analytics.
-- The current app code writes through repository/service abstractions so this
-- schema can be wired in without exposing credentials to Blazor WASM.

CREATE TABLE dbo.AdAccount (
    AccountKey nvarchar(100) NOT NULL PRIMARY KEY,
    DisplayName nvarchar(200) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    Marketplace nvarchar(50) NOT NULL,
    BaseUrl nvarchar(300) NOT NULL,
    CreatedAt datetimeoffset NOT NULL
);

CREATE TABLE dbo.ProductProfileAnalytics (
    ProductId nvarchar(100) NOT NULL PRIMARY KEY,
    AccountKey nvarchar(100) NOT NULL,
    Asin nvarchar(30) NOT NULL,
    Sku nvarchar(120) NOT NULL,
    ProductName nvarchar(500) NOT NULL,
    TargetAcos decimal(18,4) NOT NULL,
    GrossMarginPercent decimal(18,4) NOT NULL,
    DailyBudgetLimit decimal(18,2) NULL,
    CreatedAt datetimeoffset NOT NULL
);

CREATE TABLE dbo.ProductCampaignMappingAnalytics (
    ProductId nvarchar(100) NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NOT NULL,
    CampaignName nvarchar(300) NOT NULL,
    AdProduct nvarchar(100) NOT NULL,
    CreatedAt datetimeoffset NOT NULL,
    CONSTRAINT PK_ProductCampaignMappingAnalytics PRIMARY KEY (AccountKey, ProductId, CampaignId)
);

CREATE TABLE dbo.CampaignSnapshot (
    SnapshotDate date NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NOT NULL,
    CampaignName nvarchar(300) NOT NULL,
    AdProduct nvarchar(100) NOT NULL,
    CampaignStatus nvarchar(50) NOT NULL,
    BudgetAmount decimal(18,2) NOT NULL,
    BudgetType nvarchar(50) NOT NULL,
    BiddingStrategy nvarchar(120) NOT NULL,
    PortfolioId nvarchar(100) NULL,
    CONSTRAINT PK_CampaignSnapshot PRIMARY KEY (SnapshotDate, AccountKey, CampaignId)
);

CREATE TABLE dbo.AdPerformanceDaily (
    [Date] date NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    ProductId nvarchar(100) NULL,
    Asin nvarchar(30) NULL,
    CampaignId nvarchar(100) NOT NULL,
    CampaignName nvarchar(300) NOT NULL,
    AdGroupId nvarchar(100) NULL,
    AdGroupName nvarchar(300) NULL,
    AdId nvarchar(100) NULL,
    TargetingText nvarchar(500) NULL,
    TargetingType nvarchar(100) NULL,
    MatchType nvarchar(100) NULL,
    SearchTerm nvarchar(500) NULL,
    KeywordId nvarchar(100) NULL,
    TargetId nvarchar(100) NULL,
    Bid decimal(18,4) NULL,
    ServingStatus nvarchar(120) NULL,
    CampaignBudgetAmount decimal(18,4) NULL,
    CampaignBudgetType nvarchar(80) NULL,
    CampaignStatus nvarchar(80) NULL,
    AdvertisedAsin nvarchar(20) NULL,
    AdvertisedSku nvarchar(100) NULL,
    PurchasedAsin nvarchar(20) NULL,
    SearchTermKind nvarchar(40) NULL,
    Impressions int NOT NULL,
    Clicks int NOT NULL,
    Spend decimal(18,2) NOT NULL,
    Purchases int NOT NULL,
    Sales decimal(18,2) NOT NULL,
    UnitsSold int NOT NULL,
    DetailPageViews int NOT NULL,
    ROAS decimal(18,4) NOT NULL,
    ACOS decimal(18,4) NOT NULL,
    CPC decimal(18,4) NOT NULL,
    CTR decimal(18,4) NOT NULL,
    CVR decimal(18,4) NOT NULL,
    CostPerPurchase decimal(18,4) NOT NULL,
    PurchaseRate decimal(18,4) NOT NULL
);

CREATE INDEX IX_AdPerformanceDaily_ProductDate ON dbo.AdPerformanceDaily (AccountKey, ProductId, [Date]);

CREATE TABLE dbo.AmcTrafficHourly (
    [Date] date NOT NULL,
    [Hour] int NOT NULL,
    TimeZone nvarchar(100) NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NOT NULL,
    CampaignName nvarchar(300) NOT NULL,
    AdGroupId nvarchar(100) NULL,
    AdGroupName nvarchar(300) NULL,
    AdProductType nvarchar(100) NOT NULL,
    TargetingText nvarchar(500) NULL,
    MatchType nvarchar(100) NULL,
    CustomerSearchTerm nvarchar(500) NULL,
    Impressions int NOT NULL,
    Clicks int NOT NULL,
    Spend decimal(18,2) NOT NULL
);

CREATE INDEX IX_AmcTrafficHourly_CampaignDate ON dbo.AmcTrafficHourly (AccountKey, CampaignId, [Date], [Hour]);

CREATE TABLE dbo.AmcConversionsHourly (
    ConversionDate date NOT NULL,
    ConversionHour int NOT NULL,
    TimeZone nvarchar(100) NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NOT NULL,
    CampaignName nvarchar(300) NOT NULL,
    AdGroupId nvarchar(100) NULL,
    AdGroupName nvarchar(300) NULL,
    AdProductType nvarchar(100) NOT NULL,
    TrackedAsin nvarchar(30) NULL,
    ConversionEventType nvarchar(100) NULL,
    Purchases int NOT NULL,
    UnitsSold int NOT NULL,
    Sales decimal(18,2) NOT NULL,
    NewToBrandPurchases int NULL,
    NewToBrandSales decimal(18,2) NULL
);

CREATE INDEX IX_AmcConversionsHourly_CampaignDate ON dbo.AmcConversionsHourly (AccountKey, CampaignId, ConversionDate, ConversionHour);

CREATE TABLE dbo.AmcAttributionLag (
    AccountKey nvarchar(100) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NOT NULL,
    AdGroupId nvarchar(100) NULL,
    TargetingText nvarchar(500) NULL,
    SearchTerm nvarchar(500) NULL,
    TrafficDate date NOT NULL,
    TrafficHour int NOT NULL,
    ConversionDate date NOT NULL,
    ConversionHour int NOT NULL,
    HoursToConversion int NOT NULL,
    Purchases int NOT NULL,
    Sales decimal(18,2) NOT NULL
);

CREATE TABLE dbo.HourlyScorecard (
    AccountKey nvarchar(100) NOT NULL,
    ProductId nvarchar(100) NOT NULL,
    Asin nvarchar(30) NOT NULL,
    DateRangeStart date NOT NULL,
    DateRangeEnd date NOT NULL,
    DayOfWeek int NOT NULL,
    [Hour] int NOT NULL,
    Impressions int NOT NULL,
    Clicks int NOT NULL,
    Spend decimal(18,2) NOT NULL,
    Purchases int NOT NULL,
    Sales decimal(18,2) NOT NULL,
    Units int NOT NULL,
    ROAS decimal(18,4) NOT NULL,
    ACOS decimal(18,4) NOT NULL,
    CPC decimal(18,4) NOT NULL,
    CTR decimal(18,4) NOT NULL,
    CVR decimal(18,4) NOT NULL,
    SalesPerDollar decimal(18,4) NOT NULL,
    PurchaseShare decimal(18,4) NOT NULL,
    SpendShare decimal(18,4) NOT NULL,
    EfficiencyScore decimal(18,2) NOT NULL,
    RecommendedAction nvarchar(300) NULL,
    CreatedAt datetimeoffset NOT NULL,
    CONSTRAINT PK_HourlyScorecard PRIMARY KEY (AccountKey, ProductId, DateRangeStart, DateRangeEnd, DayOfWeek, [Hour])
);

CREATE TABLE dbo.AiRecommendation (
    RecommendationId nvarchar(100) NOT NULL PRIMARY KEY,
    AccountKey nvarchar(100) NOT NULL,
    ProductId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NULL,
    AdGroupId nvarchar(100) NULL,
    RecommendationType nvarchar(100) NOT NULL,
    CurrentState nvarchar(max) NOT NULL,
    RecommendedState nvarchar(max) NOT NULL,
    Reason nvarchar(max) NOT NULL,
    ExpectedImpact nvarchar(max) NOT NULL,
    ActionKey nvarchar(300) NOT NULL DEFAULT '',
    SellerCentralArea nvarchar(120) NOT NULL DEFAULT '',
    ObjectLabel nvarchar(500) NOT NULL DEFAULT '',
    FieldName nvarchar(160) NOT NULL DEFAULT '',
    CurrentValue nvarchar(500) NOT NULL DEFAULT '',
    RecommendedValue nvarchar(500) NOT NULL DEFAULT '',
    DataQualityLabel nvarchar(80) NOT NULL DEFAULT 'Good',
    DataQualityMessage nvarchar(500) NOT NULL DEFAULT '',
    MetricFactsJson nvarchar(max) NOT NULL DEFAULT '[]',
    CanApplyAutomatically bit NOT NULL DEFAULT 0,
    BlockedReason nvarchar(500) NOT NULL DEFAULT '',
    Confidence decimal(18,4) NOT NULL,
    SourceDateRangeStart date NOT NULL,
    SourceDateRangeEnd date NOT NULL,
    Status nvarchar(50) NOT NULL,
    CreatedAt datetimeoffset NOT NULL,
    ApprovedAt datetimeoffset NULL,
    IgnoredAt datetimeoffset NULL,
    AppliedAt datetimeoffset NULL
);

CREATE TABLE dbo.AiRecommendationEvidence (
    EvidenceId nvarchar(100) NOT NULL PRIMARY KEY,
    RecommendationId nvarchar(100) NOT NULL,
    SourceType nvarchar(100) NOT NULL,
    SourceTable nvarchar(100) NOT NULL,
    SourceField nvarchar(100) NOT NULL,
    SourceValue nvarchar(max) NOT NULL,
    MetricName nvarchar(100) NOT NULL,
    MetricValue decimal(18,4) NOT NULL,
    Notes nvarchar(max) NOT NULL
);

CREATE TABLE dbo.RecommendationExperiment (
    ExperimentId nvarchar(100) NOT NULL PRIMARY KEY,
    RecommendationId nvarchar(100) NOT NULL,
    ProductId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NULL,
    MetricBeforeStart date NOT NULL,
    MetricBeforeEnd date NOT NULL,
    MetricAfterStart date NOT NULL,
    MetricAfterEnd date NOT NULL,
    BaselineSpend decimal(18,2) NOT NULL,
    AfterSpend decimal(18,2) NOT NULL,
    BaselineSales decimal(18,2) NOT NULL,
    AfterSales decimal(18,2) NOT NULL,
    BaselineROAS decimal(18,4) NOT NULL,
    AfterROAS decimal(18,4) NOT NULL,
    BaselineACOS decimal(18,4) NOT NULL,
    AfterACOS decimal(18,4) NOT NULL,
    BaselinePurchases int NOT NULL,
    AfterPurchases int NOT NULL,
    Result nvarchar(50) NOT NULL,
    LearningNote nvarchar(max) NOT NULL,
    CreatedAt datetimeoffset NOT NULL
);
