namespace AmazonAdsManager.Shared.Models;

public class AdAccount
{
    public string AccountKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Marketplace { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ProductProfileAnalytics
{
    public string ProductId { get; set; } = "";
    public string AccountKey { get; set; } = "";
    public string Asin { get; set; } = "";
    public string Sku { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal TargetAcos { get; set; }
    public decimal GrossMarginPercent { get; set; }
    public decimal? DailyBudgetLimit { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ProductCampaignMappingAnalytics
{
    public string ProductId { get; set; } = "";
    public string AccountKey { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string AdProduct { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class CampaignSnapshot
{
    public DateOnly SnapshotDate { get; set; }
    public string AccountKey { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string AdProduct { get; set; } = "";
    public string CampaignStatus { get; set; } = "";
    public decimal BudgetAmount { get; set; }
    public string BudgetType { get; set; } = "";
    public string BiddingStrategy { get; set; } = "";
    public string? PortfolioId { get; set; }
}

public class AdPerformanceDaily
{
    public DateOnly Date { get; set; }
    public string SourceReportType { get; set; } = "Targeting";
    public string AccountKey { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string? ProductId { get; set; }
    public string? Asin { get; set; }
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string? AdGroupId { get; set; }
    public string? AdGroupName { get; set; }
    public string? AdId { get; set; }
    public string? TargetingText { get; set; }
    public string? TargetingType { get; set; }
    public string? MatchType { get; set; }
    public string? SearchTerm { get; set; }
    public int Impressions { get; set; }
    public int Clicks { get; set; }
    public decimal Spend { get; set; }
    public int Purchases { get; set; }
    public decimal Sales { get; set; }
    public int UnitsSold { get; set; }
    public int DetailPageViews { get; set; }
    public decimal ROAS { get; set; }
    public decimal ACOS { get; set; }
    public decimal CPC { get; set; }
    public decimal CTR { get; set; }
    public decimal CVR { get; set; }
    public decimal CostPerPurchase { get; set; }
    public decimal PurchaseRate { get; set; }
}

public class AmcTrafficHourly
{
    public DateOnly Date { get; set; }
    public int Hour { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string AccountKey { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string? AdGroupId { get; set; }
    public string? AdGroupName { get; set; }
    public string AdProductType { get; set; } = "";
    public string? TargetingText { get; set; }
    public string? MatchType { get; set; }
    public string? CustomerSearchTerm { get; set; }
    public int Impressions { get; set; }
    public int Clicks { get; set; }
    public decimal Spend { get; set; }
}

public class AmcConversionsHourly
{
    public DateOnly ConversionDate { get; set; }
    public int ConversionHour { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string AccountKey { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string? AdGroupId { get; set; }
    public string? AdGroupName { get; set; }
    public string AdProductType { get; set; } = "";
    public string? TrackedAsin { get; set; }
    public string? ConversionEventType { get; set; }
    public int Purchases { get; set; }
    public int UnitsSold { get; set; }
    public decimal Sales { get; set; }
    public int? NewToBrandPurchases { get; set; }
    public decimal? NewToBrandSales { get; set; }
}

public class AmcAttributionLag
{
    public string AccountKey { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string? AdGroupId { get; set; }
    public string? TargetingText { get; set; }
    public string? SearchTerm { get; set; }
    public DateOnly TrafficDate { get; set; }
    public int TrafficHour { get; set; }
    public DateOnly ConversionDate { get; set; }
    public int ConversionHour { get; set; }
    public int HoursToConversion { get; set; }
    public int Purchases { get; set; }
    public decimal Sales { get; set; }
}

public class HourlyScorecard
{
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Asin { get; set; } = "";
    public DateOnly DateRangeStart { get; set; }
    public DateOnly DateRangeEnd { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public int Hour { get; set; }
    public int Impressions { get; set; }
    public int Clicks { get; set; }
    public decimal Spend { get; set; }
    public int Purchases { get; set; }
    public decimal Sales { get; set; }
    public int Units { get; set; }
    public decimal ROAS { get; set; }
    public decimal ACOS { get; set; }
    public decimal CPC { get; set; }
    public decimal CTR { get; set; }
    public decimal CVR { get; set; }
    public decimal SalesPerDollar { get; set; }
    public decimal PurchaseShare { get; set; }
    public decimal SpendShare { get; set; }
    public decimal EfficiencyScore { get; set; }
    public string? RecommendedAction { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AiRecommendation
{
    public string RecommendationId { get; set; } = Guid.NewGuid().ToString();
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string? CampaignId { get; set; }
    public string? AdGroupId { get; set; }
    public string RecommendationType { get; set; } = "";
    public string Title { get; set; } = "";
    public string CurrentState { get; set; } = "";
    public string RecommendedState { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ExpectedImpact { get; set; } = "";
    public decimal Confidence { get; set; }
    public DateOnly SourceDateRangeStart { get; set; }
    public DateOnly SourceDateRangeEnd { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? IgnoredAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}

public class AiRecommendationEvidence
{
    public string EvidenceId { get; set; } = Guid.NewGuid().ToString();
    public string RecommendationId { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceTable { get; set; } = "";
    public string SourceField { get; set; } = "";
    public string SourceValue { get; set; } = "";
    public string MetricName { get; set; } = "";
    public decimal MetricValue { get; set; }
    public string Notes { get; set; } = "";
}

public class RecommendationExperiment
{
    public string ExperimentId { get; set; } = Guid.NewGuid().ToString();
    public string RecommendationId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string? CampaignId { get; set; }
    public DateOnly MetricBeforeStart { get; set; }
    public DateOnly MetricBeforeEnd { get; set; }
    public DateOnly MetricAfterStart { get; set; }
    public DateOnly MetricAfterEnd { get; set; }
    public decimal BaselineSpend { get; set; }
    public decimal AfterSpend { get; set; }
    public decimal BaselineSales { get; set; }
    public decimal AfterSales { get; set; }
    public decimal BaselineROAS { get; set; }
    public decimal AfterROAS { get; set; }
    public decimal BaselineACOS { get; set; }
    public decimal AfterACOS { get; set; }
    public int BaselinePurchases { get; set; }
    public int AfterPurchases { get; set; }
    public string Result { get; set; } = "Inconclusive";
    public string LearningNote { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AiRecommendationDto
{
    public string RecommendationId { get; set; } = "";
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string? CampaignId { get; set; }
    public string RecommendationType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ExpectedImpact { get; set; } = "";
    public decimal Confidence { get; set; }
    public DateOnly SourceDateRangeStart { get; set; }
    public DateOnly SourceDateRangeEnd { get; set; }
    public string Status { get; set; } = "Pending";
}

public class RecommendationSetupDto
{
    public string CampaignName { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string? AdGroupName { get; set; }
    public string? AdGroupId { get; set; }
    public string? TargetId { get; set; }
    public string? KeywordId { get; set; }
    public string? TargetOrSearchTerm { get; set; }
    public string? MatchType { get; set; }
    public string? TargetingType { get; set; }
    public string? CampaignStatus { get; set; }
    public string? AdGroupStatus { get; set; }
    public string? TargetStatus { get; set; }
    public decimal? CurrentBid { get; set; }
    public decimal? DailyBudget { get; set; }
    public string? BudgetType { get; set; }
    public string? BiddingStrategy { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public string? ServingStatus { get; set; }
    public string? NegativeKeywords { get; set; }
    public string DataSource { get; set; } = "";
}

public class RecommendationPerformanceSummaryDto
{
    public DateOnly DateRangeStart { get; set; }
    public DateOnly DateRangeEnd { get; set; }
    public decimal Spend { get; set; }
    public decimal Sales { get; set; }
    public int Orders { get; set; }
    public int Clicks { get; set; }
    public int Impressions { get; set; }
    public decimal? ACOS { get; set; }
    public decimal? ROAS { get; set; }
    public decimal? CPC { get; set; }
    public decimal? CTR { get; set; }
    public decimal? CVR { get; set; }
    public decimal WastedSpend { get; set; }
    public int DaysWithSpendNoSales { get; set; }
}

public class RecommendationProposedChangeDto
{
    public string ActionType { get; set; } = "UnsupportedAction";
    public string FieldName { get; set; } = "";
    public string CurrentValue { get; set; } = "";
    public string ProposedValue { get; set; } = "";
    public string? TargetId { get; set; }
    public string? KeywordId { get; set; }
    public decimal? BidChangePercent { get; set; }
    public decimal? FinalBid { get; set; }
    public decimal? BudgetAmount { get; set; }
    public string? CampaignStatus { get; set; }
    public string? AdGroupStatus { get; set; }
    public string? TargetStatus { get; set; }
    public string? NegativeKeywordText { get; set; }
    public string? NegativeKeywordMatchType { get; set; }
    public string? CampaignName { get; set; }
    public string? AdGroupName { get; set; }
    public string Explanation { get; set; } = "";
    public string RiskLevel { get; set; } = "Medium";
    public bool CanApplyAutomatically { get; set; }
    public bool IsDestructive { get; set; }
    public string ManualActionReason { get; set; } = "";
    public string UserNotes { get; set; } = "";
}

public class RecommendationReviewDto
{
    public AiRecommendationDto Recommendation { get; set; } = new();
    public ProductProfile? Product { get; set; }
    public RecommendationSetupDto CurrentSetup { get; set; } = new();
    public RecommendationProposedChangeDto ProposedChange { get; set; } = new();
    public RecommendationPerformanceSummaryDto Performance { get; set; } = new();
    public List<AiRecommendationEvidenceDto> Evidence { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string DataQualityLabel { get; set; } = "Limited";
    public string AiModel { get; set; } = "";
}

public class ApplyRecommendationRequest
{
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public RecommendationProposedChangeDto ProposedChange { get; set; } = new();
    public string UserNotes { get; set; } = "";
    public bool ConfirmDestructive { get; set; }
}

public class ApplyRecommendationResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public string? AmazonApiRequestJson { get; set; }
    public string? AmazonApiResponseJson { get; set; }
    public string? Error { get; set; }
    public BeforeAfterComparisonDto? Experiment { get; set; }
}

public class RecommendationAiQuestionRequest
{
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Question { get; set; } = "";
    public bool BeginnerChineseMode { get; set; }
    public RecommendationProposedChangeDto? ProposedChange { get; set; }
    public List<RecommendationChatMessageDto> History { get; set; } = new();
}

public class RecommendationAiAnswerDto
{
    public bool Success { get; set; }
    public string Answer { get; set; } = "";
    public string? Error { get; set; }
}

public class RecommendationChatMessageDto
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public class AmazonTargetLookupDto
{
    public string TargetId { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string AdGroupId { get; set; } = "";
    public string State { get; set; } = "";
    public decimal? Bid { get; set; }
    public string ExpressionText { get; set; } = "";
}

public class AmazonKeywordLookupDto
{
    public string KeywordId { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string AdGroupId { get; set; } = "";
    public string State { get; set; } = "";
    public decimal? Bid { get; set; }
    public string KeywordText { get; set; } = "";
    public string MatchType { get; set; } = "";
}

public class RecommendationApplyRecord
{
    public string ApplyId { get; set; } = Guid.NewGuid().ToString();
    public string RecommendationId { get; set; } = "";
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset? ApplyFailedAt { get; set; }
    public string? ApplyErrorMessage { get; set; }
    public string BeforeSnapshotJson { get; set; } = "";
    public string ProposedChangeJson { get; set; } = "";
    public string FinalAppliedChangeJson { get; set; } = "";
    public string AfterSnapshotJson { get; set; } = "";
    public string AmazonApiRequestJson { get; set; } = "";
    public string AmazonApiResponseJson { get; set; } = "";
    public string UserEditedChangeJson { get; set; } = "";
    public string UserApprovalNotes { get; set; } = "";
    public string? ExperimentId { get; set; }
    public string DataQualityLabel { get; set; } = "";
}

public class AiRecommendationEvidenceDto
{
    public string EvidenceId { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceTable { get; set; } = "";
    public string SourceField { get; set; } = "";
    public string SourceValue { get; set; } = "";
    public string MetricName { get; set; } = "";
    public decimal MetricValue { get; set; }
    public string Notes { get; set; } = "";
}

public class HourlyScorecardDto
{
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Asin { get; set; } = "";
    public DateOnly DateRangeStart { get; set; }
    public DateOnly DateRangeEnd { get; set; }
    public string DayOfWeek { get; set; } = "";
    public int Hour { get; set; }
    public int Impressions { get; set; }
    public int Clicks { get; set; }
    public decimal Spend { get; set; }
    public int Purchases { get; set; }
    public decimal Sales { get; set; }
    public int Units { get; set; }
    public decimal ROAS { get; set; }
    public decimal ACOS { get; set; }
    public decimal CPC { get; set; }
    public decimal CTR { get; set; }
    public decimal CVR { get; set; }
    public decimal SalesPerDollar { get; set; }
    public decimal PurchaseShare { get; set; }
    public decimal SpendShare { get; set; }
    public decimal EfficiencyScore { get; set; }
    public string? RecommendedAction { get; set; }
}

public class KeywordPerformanceDto
{
    public string KeywordOrSearchTerm { get; set; } = "";
    public string SourceReportType { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public decimal Spend { get; set; }
    public int Clicks { get; set; }
    public int Impressions { get; set; }
    public decimal Sales { get; set; }
    public int Purchases { get; set; }
    public decimal ROAS { get; set; }
    public decimal ACOS { get; set; }
    public decimal CTR { get; set; }
    public decimal CVR { get; set; }
}

public class BeforeAfterComparisonDto
{
    public string ExperimentId { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string? CampaignId { get; set; }
    public decimal BaselineSpend { get; set; }
    public decimal AfterSpend { get; set; }
    public decimal BaselineSales { get; set; }
    public decimal AfterSales { get; set; }
    public decimal BaselineROAS { get; set; }
    public decimal AfterROAS { get; set; }
    public decimal BaselineACOS { get; set; }
    public decimal AfterACOS { get; set; }
    public int BaselinePurchases { get; set; }
    public int AfterPurchases { get; set; }
    public string Result { get; set; } = "";
    public string LearningNote { get; set; } = "";
}

public class TechnicalRecommendationDetailsDto
{
    public AiRecommendationDto Recommendation { get; set; } = new();
    public List<AiRecommendationEvidenceDto> Evidence { get; set; } = new();
    public List<HourlyScorecardDto> HourlyScorecard { get; set; } = new();
    public List<KeywordPerformanceDto> KeywordPerformance { get; set; } = new();
    public List<BeforeAfterComparisonDto> BeforeAfterComparisons { get; set; } = new();
    public List<ChartSeriesDto> Charts { get; set; } = new();
}

public class ChartSeriesDto
{
    public string Name { get; set; } = "";
    public List<ChartPointDto> Points { get; set; } = new();
}

public class ChartPointDto
{
    public string Label { get; set; } = "";
    public decimal Value { get; set; }
}

public class ProductAiAnalysisResult
{
    public bool Success { get; set; }
    public bool IsAiGenerated { get; set; }
    public bool UsedFallback { get; set; }
    public string? Error { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<AiRecommendationDto> V2Recommendations { get; set; } = new();
    public List<HourlyScorecardDto> HourlyScorecard { get; set; } = new();
    public Dictionary<string, string> AmcWorkflowSqlByType { get; set; } = new();
}

public class AmcHourlyDataStatusDto
{
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public DateOnly DateRangeStart { get; set; }
    public DateOnly DateRangeEnd { get; set; }
    public int MappedCampaignCount { get; set; }
    public int TrafficRows { get; set; }
    public int ConversionRows { get; set; }
    public bool HasTrafficData => TrafficRows > 0;
    public bool HasConversionData => ConversionRows > 0;
    public bool HasAnyData => TrafficRows > 0 || ConversionRows > 0;
    public bool IsMissing => !HasAnyData;
}

public class AmcStatusDto
{
    public bool IsConfigured { get; set; }
    public bool IsAuthorized { get; set; }
    public bool IsManuallyConfigured { get; set; }
    public string AccountKey { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string AdvertiserId { get; set; } = "";
    public string MarketplaceId { get; set; } = "";
    public string ApiEndpoint { get; set; } = "";
    public string ExpectedAmazonUserEmail { get; set; } = "";
    public int AccountsHttpStatus { get; set; }
    public int? InstancesHttpStatus { get; set; }
    public int? DataSourcesHttpStatus { get; set; }
    public int AmcAccountCount { get; set; }
    public int InstanceCount { get; set; }
    public int DataSourceCount { get; set; }
    public string? InstanceCreationStatus { get; set; }
    public string? DiscoveryWarning { get; set; }
    public string? LastRequestMethod { get; set; }
    public string? LastRequestUrl { get; set; }
    public int? LastRequestStatus { get; set; }
    public string? LastRequestDiagnostics { get; set; }
    public string? LastResponseBody { get; set; }
    public string Message { get; set; } = "";
    public string? Error { get; set; }
}

public class AiRuntimeStatusDto
{
    public bool IsConfigured { get; set; }
    public string Model { get; set; } = "";
    public string Message { get; set; } = "";
}

public class AnalyticsImportRequest
{
    public string AccountKey { get; set; } = "";
    public DateOnly? DateRangeStart { get; set; }
    public DateOnly? DateRangeEnd { get; set; }
    public bool WaitForCompletion { get; set; }
    public Dictionary<string, string> WorkflowExecutionIds { get; set; } = new();
}

public class AmcExecutionImportRequest
{
    public string AccountKey { get; set; } = "";
    public string? TimeZone { get; set; }
    public Dictionary<string, string> WorkflowExecutionIds { get; set; } = new();
}

public class AnalyticsImportResult
{
    public bool Success { get; set; } = true;
    public string Summary { get; set; } = "";
    public int RowsImported { get; set; }
    public Dictionary<string, int> RowsImportedBySourceReportType { get; set; } = new();
    public Dictionary<string, string> WorkflowExecutionIds { get; set; } = new();
    public Dictionary<string, string> WorkflowExecutionStatuses { get; set; } = new();
    public Dictionary<string, string> WorkflowSqlByType { get; set; } = new();
}
