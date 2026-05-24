IF SCHEMA_ID('dbo') IS NULL
    EXEC('CREATE SCHEMA dbo');
GO

CREATE TABLE dbo.CampaignSnapshot (
    SnapshotDate date NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NOT NULL,
    CampaignName nvarchar(400) NOT NULL,
    AdProduct nvarchar(80) NOT NULL,
    CampaignStatus nvarchar(80) NOT NULL,
    BudgetAmount decimal(18,4) NOT NULL DEFAULT 0,
    BudgetType nvarchar(80) NOT NULL,
    BiddingStrategy nvarchar(120) NOT NULL,
    PortfolioId nvarchar(100) NULL,
    CONSTRAINT PK_CampaignSnapshot PRIMARY KEY (SnapshotDate, AccountKey, CampaignId)
);
GO

CREATE TABLE dbo.AdPerformanceDaily (
    [Date] date NOT NULL,
    SourceReportType nvarchar(40) NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    ProductId nvarchar(100) NULL,
    Asin nvarchar(20) NULL,
    CampaignId nvarchar(100) NOT NULL,
    CampaignName nvarchar(400) NOT NULL,
    AdGroupId nvarchar(100) NULL,
    AdGroupName nvarchar(400) NULL,
    AdId nvarchar(100) NULL,
    TargetingText nvarchar(500) NULL,
    TargetingType nvarchar(100) NULL,
    MatchType nvarchar(100) NULL,
    SearchTerm nvarchar(500) NULL,
    Impressions int NOT NULL DEFAULT 0,
    Clicks int NOT NULL DEFAULT 0,
    Spend decimal(18,4) NOT NULL DEFAULT 0,
    Purchases int NOT NULL DEFAULT 0,
    Sales decimal(18,4) NOT NULL DEFAULT 0,
    UnitsSold int NOT NULL DEFAULT 0,
    DetailPageViews int NOT NULL DEFAULT 0,
    ROAS decimal(18,4) NOT NULL DEFAULT 0,
    ACOS decimal(18,4) NOT NULL DEFAULT 0,
    CPC decimal(18,4) NOT NULL DEFAULT 0,
    CTR decimal(18,6) NOT NULL DEFAULT 0,
    CVR decimal(18,6) NOT NULL DEFAULT 0,
    CostPerPurchase decimal(18,4) NOT NULL DEFAULT 0,
    PurchaseRate decimal(18,6) NOT NULL DEFAULT 0
);
GO
CREATE INDEX IX_AdPerformanceDaily_ProductDate ON dbo.AdPerformanceDaily(AccountKey, ProductId, [Date]);
CREATE INDEX IX_AdPerformanceDaily_SourceSearch ON dbo.AdPerformanceDaily(SourceReportType, SearchTerm);
GO

CREATE TABLE dbo.AmcTrafficHourly (
    [Date] date NOT NULL,
    [Hour] int NOT NULL,
    TimeZone nvarchar(80) NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NOT NULL,
    CampaignName nvarchar(400) NOT NULL,
    AdGroupId nvarchar(100) NULL,
    AdGroupName nvarchar(400) NULL,
    AdProductType nvarchar(100) NOT NULL,
    TargetingText nvarchar(500) NULL,
    MatchType nvarchar(100) NULL,
    CustomerSearchTerm nvarchar(500) NULL,
    Impressions int NOT NULL DEFAULT 0,
    Clicks int NOT NULL DEFAULT 0,
    Spend decimal(18,4) NOT NULL DEFAULT 0
);
GO
CREATE INDEX IX_AmcTrafficHourly_CampaignDate ON dbo.AmcTrafficHourly(AccountKey, CampaignId, [Date], [Hour]);
GO

CREATE TABLE dbo.AmcConversionsHourly (
    ConversionDate date NOT NULL,
    ConversionHour int NOT NULL,
    TimeZone nvarchar(80) NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    ProfileId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NOT NULL,
    CampaignName nvarchar(400) NOT NULL,
    AdGroupId nvarchar(100) NULL,
    AdGroupName nvarchar(400) NULL,
    AdProductType nvarchar(100) NOT NULL,
    TrackedAsin nvarchar(20) NULL,
    ConversionEventType nvarchar(100) NULL,
    Purchases int NOT NULL DEFAULT 0,
    UnitsSold int NOT NULL DEFAULT 0,
    Sales decimal(18,4) NOT NULL DEFAULT 0,
    NewToBrandPurchases int NULL,
    NewToBrandSales decimal(18,4) NULL
);
GO
CREATE INDEX IX_AmcConversionsHourly_CampaignDate ON dbo.AmcConversionsHourly(AccountKey, CampaignId, ConversionDate, ConversionHour);
GO

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
    Purchases int NOT NULL DEFAULT 0,
    Sales decimal(18,4) NOT NULL DEFAULT 0
);
GO
CREATE INDEX IX_AmcAttributionLag_CampaignHours ON dbo.AmcAttributionLag(AccountKey, CampaignId, TrafficDate, TrafficHour, ConversionDate, ConversionHour);
GO

CREATE TABLE dbo.AmcQueryCoverage (
    AccountKey nvarchar(100) NOT NULL,
    ResultType nvarchar(40) NOT NULL,
    [Date] date NOT NULL,
    Status nvarchar(20) NOT NULL,
    WorkflowExecutionId nvarchar(100) NULL,
    UpdatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
    CONSTRAINT PK_AmcQueryCoverage PRIMARY KEY (AccountKey, ResultType, [Date])
);
GO
CREATE INDEX IX_AmcQueryCoverage_Pending ON dbo.AmcQueryCoverage(AccountKey, ResultType, Status, WorkflowExecutionId);
GO

CREATE TABLE dbo.HourlyScorecard (
    AccountKey nvarchar(100) NOT NULL,
    ProductId nvarchar(100) NOT NULL,
    Asin nvarchar(20) NOT NULL,
    DateRangeStart date NOT NULL,
    DateRangeEnd date NOT NULL,
    DayOfWeek nvarchar(20) NOT NULL,
    [Hour] int NOT NULL,
    Impressions int NOT NULL DEFAULT 0,
    Clicks int NOT NULL DEFAULT 0,
    Spend decimal(18,4) NOT NULL DEFAULT 0,
    Purchases int NOT NULL DEFAULT 0,
    Sales decimal(18,4) NOT NULL DEFAULT 0,
    Units int NOT NULL DEFAULT 0,
    ROAS decimal(18,4) NOT NULL DEFAULT 0,
    ACOS decimal(18,4) NOT NULL DEFAULT 0,
    CPC decimal(18,4) NOT NULL DEFAULT 0,
    CTR decimal(18,6) NOT NULL DEFAULT 0,
    CVR decimal(18,6) NOT NULL DEFAULT 0,
    SalesPerDollar decimal(18,4) NOT NULL DEFAULT 0,
    PurchaseShare decimal(18,6) NOT NULL DEFAULT 0,
    SpendShare decimal(18,6) NOT NULL DEFAULT 0,
    EfficiencyScore decimal(18,4) NOT NULL DEFAULT 0,
    RecommendedAction nvarchar(500) NULL,
    CreatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
    CONSTRAINT PK_HourlyScorecard PRIMARY KEY (AccountKey, ProductId, DateRangeStart, DateRangeEnd, DayOfWeek, [Hour])
);
GO

CREATE TABLE dbo.AiRecommendation (
    RecommendationId nvarchar(100) NOT NULL CONSTRAINT PK_AiRecommendation PRIMARY KEY,
    AccountKey nvarchar(100) NOT NULL,
    ProductId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NULL,
    AdGroupId nvarchar(100) NULL,
    RecommendationType nvarchar(80) NOT NULL,
    Title nvarchar(300) NOT NULL,
    CurrentState nvarchar(max) NOT NULL,
    RecommendedState nvarchar(max) NOT NULL,
    Reason nvarchar(max) NOT NULL,
    ExpectedImpact nvarchar(max) NOT NULL,
    Confidence decimal(18,4) NOT NULL,
    SourceDateRangeStart date NOT NULL,
    SourceDateRangeEnd date NOT NULL,
    Status nvarchar(80) NOT NULL,
    CreatedAt datetimeoffset NOT NULL,
    ApprovedAt datetimeoffset NULL,
    IgnoredAt datetimeoffset NULL,
    AppliedAt datetimeoffset NULL
);
GO
CREATE INDEX IX_AiRecommendation_Product ON dbo.AiRecommendation(AccountKey, ProductId, CreatedAt DESC);
GO

CREATE TABLE dbo.AiRecommendationEvidence (
    EvidenceId nvarchar(100) NOT NULL CONSTRAINT PK_AiRecommendationEvidence PRIMARY KEY,
    RecommendationId nvarchar(100) NOT NULL,
    SourceType nvarchar(100) NOT NULL,
    SourceTable nvarchar(100) NOT NULL,
    SourceField nvarchar(100) NOT NULL,
    SourceValue nvarchar(max) NOT NULL,
    MetricName nvarchar(100) NOT NULL,
    MetricValue decimal(18,4) NOT NULL,
    Notes nvarchar(max) NOT NULL
);
GO
CREATE INDEX IX_AiRecommendationEvidence_Recommendation ON dbo.AiRecommendationEvidence(RecommendationId);
GO

CREATE TABLE dbo.RecommendationExperiment (
    ExperimentId nvarchar(100) NOT NULL CONSTRAINT PK_RecommendationExperiment PRIMARY KEY,
    RecommendationId nvarchar(100) NOT NULL,
    ProductId nvarchar(100) NOT NULL,
    CampaignId nvarchar(100) NULL,
    MetricBeforeStart date NOT NULL,
    MetricBeforeEnd date NOT NULL,
    MetricAfterStart date NOT NULL,
    MetricAfterEnd date NOT NULL,
    BaselineSpend decimal(18,4) NOT NULL DEFAULT 0,
    AfterSpend decimal(18,4) NOT NULL DEFAULT 0,
    BaselineSales decimal(18,4) NOT NULL DEFAULT 0,
    AfterSales decimal(18,4) NOT NULL DEFAULT 0,
    BaselineROAS decimal(18,4) NOT NULL DEFAULT 0,
    AfterROAS decimal(18,4) NOT NULL DEFAULT 0,
    BaselineACOS decimal(18,4) NOT NULL DEFAULT 0,
    AfterACOS decimal(18,4) NOT NULL DEFAULT 0,
    BaselinePurchases int NOT NULL DEFAULT 0,
    AfterPurchases int NOT NULL DEFAULT 0,
    Result nvarchar(40) NOT NULL,
    LearningNote nvarchar(max) NOT NULL,
    CreatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
);
GO
CREATE INDEX IX_RecommendationExperiment_Product ON dbo.RecommendationExperiment(ProductId, CreatedAt DESC);
GO

CREATE TABLE dbo.RecommendationApplyAudit (
    ApplyId nvarchar(100) NOT NULL CONSTRAINT PK_RecommendationApplyAudit PRIMARY KEY,
    RecommendationId nvarchar(100) NOT NULL,
    AccountKey nvarchar(100) NOT NULL,
    ProductId nvarchar(100) NOT NULL,
    Status nvarchar(80) NOT NULL,
    CreatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
    ApprovedAt datetimeoffset NULL,
    AppliedAt datetimeoffset NULL,
    ApplyFailedAt datetimeoffset NULL,
    ApplyErrorMessage nvarchar(max) NULL,
    BeforeSnapshotJson nvarchar(max) NOT NULL,
    ProposedChangeJson nvarchar(max) NOT NULL,
    FinalAppliedChangeJson nvarchar(max) NOT NULL,
    AfterSnapshotJson nvarchar(max) NOT NULL,
    AmazonApiRequestJson nvarchar(max) NOT NULL,
    AmazonApiResponseJson nvarchar(max) NOT NULL,
    UserEditedChangeJson nvarchar(max) NOT NULL,
    UserApprovalNotes nvarchar(max) NOT NULL,
    ExperimentId nvarchar(100) NULL,
    DataQualityLabel nvarchar(80) NOT NULL
);
GO
CREATE INDEX IX_RecommendationApplyAudit_Recommendation ON dbo.RecommendationApplyAudit(RecommendationId, CreatedAt DESC);
GO
