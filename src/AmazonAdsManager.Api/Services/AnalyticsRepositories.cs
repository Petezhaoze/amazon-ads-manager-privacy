using AmazonAdsManager.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace AmazonAdsManager.Api.Services;

public class AnalyticsDatabaseNotConfiguredException : InvalidOperationException
{
    public AnalyticsDatabaseNotConfiguredException()
        : base("Analytics database is not configured. Add AnalyticsDb:ConnectionString to local.settings.json or Azure Function app settings.")
    {
    }
}

public static class AmcCoverageStatus
{
    public const string Pending = "Pending";
    public const string Queried = "Queried";
    public const string Failed = "Failed";
}

public sealed class AmcQueryCoverageRow
{
    public string AccountKey { get; set; } = "";
    public string ResultType { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Status { get; set; } = AmcCoverageStatus.Pending;
    public string? WorkflowExecutionId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ProductAnalyticsRepository
{
    private readonly ProductProfileRepository _products;
    private readonly ProductCampaignMappingRepository _mappings;

    protected ProductAnalyticsRepository()
    {
        _products = null!;
        _mappings = null!;
    }

    public ProductAnalyticsRepository(ProductProfileRepository products, ProductCampaignMappingRepository mappings)
    {
        _products = products;
        _mappings = mappings;
    }

    public IReadOnlyList<ProductProfile> GetProductsWithCampaigns(string accountKey, bool activeCampaignsOnly = false)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var mappedProductIds = _mappings.GetByAccount(accountKey)
            .Where(m => !activeCampaignsOnly || m.IsCurrentlyRunnable(today))
            .Select(m => m.ProductId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _products.GetByAccount(accountKey)
            .Where(p => mappedProductIds.Contains(p.Id))
            .OrderBy(p => p.DisplayName)
            .ToList()
            .AsReadOnly();
    }

    public virtual IReadOnlyList<ProductCampaignMapping> GetMappings(string accountKey, string productId) =>
        _mappings.GetByProduct(accountKey, productId);

    public virtual ProductProfile? GetProduct(string productId) => _products.GetById(productId);
}

public class AdMetricsRepository
{
    private readonly string? _connectionString;

    public AdMetricsRepository(IConfiguration config)
    {
        _connectionString = config["AnalyticsDb:ConnectionString"];
    }

    public void UpsertCampaignSnapshots(IEnumerable<CampaignSnapshot> snapshots)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var row in snapshots)
        {
            Execute(conn, tx, """
DELETE FROM dbo.CampaignSnapshot
WHERE SnapshotDate = @SnapshotDate AND AccountKey = @AccountKey AND CampaignId = @CampaignId;
INSERT INTO dbo.CampaignSnapshot
(SnapshotDate, AccountKey, ProfileId, CampaignId, CampaignName, AdProduct, CampaignStatus, BudgetAmount, BudgetType, BiddingStrategy, PortfolioId)
VALUES
(@SnapshotDate, @AccountKey, @ProfileId, @CampaignId, @CampaignName, @AdProduct, @CampaignStatus, @BudgetAmount, @BudgetType, @BiddingStrategy, @PortfolioId);
""", AddCampaignSnapshotParams(row));
        }
        tx.Commit();
    }

    public void UpsertDailyMetrics(IEnumerable<AdPerformanceDaily> rows)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var row in rows)
        {
            Execute(conn, tx, """
DELETE FROM dbo.AdPerformanceDaily
WHERE [Date] = @Date
  AND SourceReportType = @SourceReportType
  AND AccountKey = @AccountKey
  AND CampaignId = @CampaignId
  AND ISNULL(AdGroupId, '') = ISNULL(@AdGroupId, '')
  AND ISNULL(AdId, '') = ISNULL(@AdId, '')
  AND ISNULL(TargetingText, '') = ISNULL(@TargetingText, '')
  AND ISNULL(SearchTerm, '') = ISNULL(@SearchTerm, '');
INSERT INTO dbo.AdPerformanceDaily
([Date], SourceReportType, AccountKey, ProfileId, ProductId, Asin, CampaignId, CampaignName, AdGroupId, AdGroupName, AdId, TargetingText, TargetingType, MatchType, SearchTerm,
 Impressions, Clicks, Spend, Purchases, Sales, UnitsSold, DetailPageViews, ROAS, ACOS, CPC, CTR, CVR, CostPerPurchase, PurchaseRate)
VALUES
(@Date, @SourceReportType, @AccountKey, @ProfileId, @ProductId, @Asin, @CampaignId, @CampaignName, @AdGroupId, @AdGroupName, @AdId, @TargetingText, @TargetingType, @MatchType, @SearchTerm,
 @Impressions, @Clicks, @Spend, @Purchases, @Sales, @UnitsSold, @DetailPageViews, @ROAS, @ACOS, @CPC, @CTR, @CVR, @CostPerPurchase, @PurchaseRate);
""", AddDailyParams(row));
        }
        tx.Commit();
    }

    public virtual void UpsertAmcTrafficHourly(IEnumerable<AmcTrafficHourly> rows)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var row in rows)
        {
            Execute(conn, tx, """
DELETE FROM dbo.AmcTrafficHourly
WHERE [Date] = @Date AND [Hour] = @Hour AND AccountKey = @AccountKey AND CampaignId = @CampaignId
  AND ISNULL(AdGroupId, '') = ISNULL(@AdGroupId, '') AND ISNULL(CustomerSearchTerm, '') = ISNULL(@CustomerSearchTerm, '');
INSERT INTO dbo.AmcTrafficHourly
([Date], [Hour], TimeZone, AccountKey, ProfileId, CampaignId, CampaignName, AdGroupId, AdGroupName, AdProductType, TargetingText, MatchType, CustomerSearchTerm, Impressions, Clicks, Spend)
VALUES
(@Date, @Hour, @TimeZone, @AccountKey, @ProfileId, @CampaignId, @CampaignName, @AdGroupId, @AdGroupName, @AdProductType, @TargetingText, @MatchType, @CustomerSearchTerm, @Impressions, @Clicks, @Spend);
""", AddAmcTrafficParams(row));
        }
        tx.Commit();
    }

    public virtual void UpsertAmcConversionsHourly(IEnumerable<AmcConversionsHourly> rows)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var row in rows)
        {
            Execute(conn, tx, """
DELETE FROM dbo.AmcConversionsHourly
WHERE ConversionDate = @ConversionDate AND ConversionHour = @ConversionHour AND AccountKey = @AccountKey AND CampaignId = @CampaignId
  AND ISNULL(AdGroupId, '') = ISNULL(@AdGroupId, '') AND ISNULL(TrackedAsin, '') = ISNULL(@TrackedAsin, '') AND ISNULL(ConversionEventType, '') = ISNULL(@ConversionEventType, '');
INSERT INTO dbo.AmcConversionsHourly
(ConversionDate, ConversionHour, TimeZone, AccountKey, ProfileId, CampaignId, CampaignName, AdGroupId, AdGroupName, AdProductType, TrackedAsin, ConversionEventType, Purchases, UnitsSold, Sales, NewToBrandPurchases, NewToBrandSales)
VALUES
(@ConversionDate, @ConversionHour, @TimeZone, @AccountKey, @ProfileId, @CampaignId, @CampaignName, @AdGroupId, @AdGroupName, @AdProductType, @TrackedAsin, @ConversionEventType, @Purchases, @UnitsSold, @Sales, @NewToBrandPurchases, @NewToBrandSales);
""", AddAmcConversionParams(row));
        }
        tx.Commit();
    }

    public virtual void UpsertAmcAttributionLag(IEnumerable<AmcAttributionLag> rows)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var row in rows)
        {
            Execute(conn, tx, """
DELETE FROM dbo.AmcAttributionLag
WHERE AccountKey = @AccountKey AND CampaignId = @CampaignId
  AND ISNULL(AdGroupId, '') = ISNULL(@AdGroupId, '') AND ISNULL(TargetingText, '') = ISNULL(@TargetingText, '') AND ISNULL(SearchTerm, '') = ISNULL(@SearchTerm, '')
  AND TrafficDate = @TrafficDate AND TrafficHour = @TrafficHour AND ConversionDate = @ConversionDate AND ConversionHour = @ConversionHour;
INSERT INTO dbo.AmcAttributionLag
(AccountKey, ProfileId, CampaignId, AdGroupId, TargetingText, SearchTerm, TrafficDate, TrafficHour, ConversionDate, ConversionHour, HoursToConversion, Purchases, Sales)
VALUES
(@AccountKey, @ProfileId, @CampaignId, @AdGroupId, @TargetingText, @SearchTerm, @TrafficDate, @TrafficHour, @ConversionDate, @ConversionHour, @HoursToConversion, @Purchases, @Sales);
""", AddAttributionParams(row));
        }
        tx.Commit();
    }

    public virtual IReadOnlyList<AdPerformanceDaily> GetDailyMetrics(string accountKey, string productId, DateOnly start, DateOnly end)
        => GetDailyMetrics(accountKey, productId, Array.Empty<string>(), start, end);

    public virtual IReadOnlyList<AdPerformanceDaily> GetDailyMetrics(string accountKey, string productId, IEnumerable<string> campaignIds, DateOnly start, DateOnly end)
    {
        var ids = campaignIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var campaignFilter = ids.Any()
            ? $" OR CampaignId IN ({InClause("cid", ids.Count)})"
            : "";

        using var conn = OpenConnection();
        using var cmd = CreateCommand(conn, $"""
SELECT * FROM dbo.AdPerformanceDaily
WHERE AccountKey = @AccountKey
  AND [Date] >= @Start
  AND [Date] <= @End
  AND (ProductId = @ProductId{campaignFilter})
ORDER BY [Date], CampaignName, TargetingText, SearchTerm;
""", null);
        cmd.Parameters.AddWithValue("@AccountKey", accountKey);
        cmd.Parameters.AddWithValue("@ProductId", productId);
        cmd.Parameters.AddWithValue("@Start", ToDateTime(start));
        cmd.Parameters.AddWithValue("@End", ToDateTime(end));
        AddInParams(cmd, "cid", ids);
        using var reader = cmd.ExecuteReader();
        var rows = new List<AdPerformanceDaily>();
        while (reader.Read()) rows.Add(ReadDaily(reader));
        return rows.AsReadOnly();
    }

    public virtual IReadOnlyList<AmcTrafficHourly> GetTrafficHourly(string accountKey, IEnumerable<string> campaignIds, DateOnly start, DateOnly end)
    {
        var ids = campaignIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!ids.Any()) return Array.Empty<AmcTrafficHourly>();
        using var conn = OpenConnection();
        using var cmd = CreateCommand(conn, $"""
SELECT * FROM dbo.AmcTrafficHourly
WHERE AccountKey = @AccountKey AND [Date] >= @Start AND [Date] <= @End AND CampaignId IN ({InClause("cid", ids.Count)})
ORDER BY [Date], [Hour];
""", null);
        cmd.Parameters.AddWithValue("@AccountKey", accountKey);
        cmd.Parameters.AddWithValue("@Start", ToDateTime(start));
        cmd.Parameters.AddWithValue("@End", ToDateTime(end));
        AddInParams(cmd, "cid", ids);
        using var reader = cmd.ExecuteReader();
        var rows = new List<AmcTrafficHourly>();
        while (reader.Read()) rows.Add(ReadTraffic(reader));
        return rows.AsReadOnly();
    }

    public virtual IReadOnlyList<AmcConversionsHourly> GetConversionsHourly(string accountKey, IEnumerable<string> campaignIds, DateOnly start, DateOnly end)
    {
        var ids = campaignIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!ids.Any()) return Array.Empty<AmcConversionsHourly>();
        using var conn = OpenConnection();
        using var cmd = CreateCommand(conn, $"""
SELECT * FROM dbo.AmcConversionsHourly
WHERE AccountKey = @AccountKey AND ConversionDate >= @Start AND ConversionDate <= @End AND CampaignId IN ({InClause("cid", ids.Count)})
ORDER BY ConversionDate, ConversionHour;
""", null);
        cmd.Parameters.AddWithValue("@AccountKey", accountKey);
        cmd.Parameters.AddWithValue("@Start", ToDateTime(start));
        cmd.Parameters.AddWithValue("@End", ToDateTime(end));
        AddInParams(cmd, "cid", ids);
        using var reader = cmd.ExecuteReader();
        var rows = new List<AmcConversionsHourly>();
        while (reader.Read()) rows.Add(ReadConversion(reader));
        return rows.AsReadOnly();
    }

    public virtual void ReplaceScorecard(string accountKey, string productId, DateOnly start, DateOnly end, IEnumerable<HourlyScorecard> rows)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        Execute(conn, tx, """
DELETE FROM dbo.HourlyScorecard
WHERE AccountKey = @AccountKey AND ProductId = @ProductId AND DateRangeStart = @Start AND DateRangeEnd = @End;
""", cmd =>
        {
            cmd.Parameters.AddWithValue("@AccountKey", accountKey);
            cmd.Parameters.AddWithValue("@ProductId", productId);
            cmd.Parameters.AddWithValue("@Start", ToDateTime(start));
            cmd.Parameters.AddWithValue("@End", ToDateTime(end));
        });

        foreach (var row in rows)
        {
            Execute(conn, tx, """
INSERT INTO dbo.HourlyScorecard
(AccountKey, ProductId, Asin, DateRangeStart, DateRangeEnd, DayOfWeek, [Hour], Impressions, Clicks, Spend, Purchases, Sales, Units, ROAS, ACOS, CPC, CTR, CVR, SalesPerDollar, PurchaseShare, SpendShare, EfficiencyScore, RecommendedAction, CreatedAt)
VALUES
(@AccountKey, @ProductId, @Asin, @DateRangeStart, @DateRangeEnd, @DayOfWeek, @Hour, @Impressions, @Clicks, @Spend, @Purchases, @Sales, @Units, @ROAS, @ACOS, @CPC, @CTR, @CVR, @SalesPerDollar, @PurchaseShare, @SpendShare, @EfficiencyScore, @RecommendedAction, @CreatedAt);
""", AddScorecardParams(row));
        }
        tx.Commit();
    }

    public IReadOnlyList<HourlyScorecard> GetScorecard(string accountKey, string productId, DateOnly? start = null, DateOnly? end = null)
    {
        using var conn = OpenConnection();
        using var cmd = CreateCommand(conn, """
SELECT * FROM dbo.HourlyScorecard
WHERE AccountKey = @AccountKey AND ProductId = @ProductId
  AND (@Start IS NULL OR DateRangeStart = @Start)
  AND (@End IS NULL OR DateRangeEnd = @End)
ORDER BY DayOfWeek, [Hour];
""", null);
        cmd.Parameters.AddWithValue("@AccountKey", accountKey);
        cmd.Parameters.AddWithValue("@ProductId", productId);
        cmd.Parameters.Add("@Start", SqlDbType.Date).Value = start is null ? DBNull.Value : ToDateTime(start.Value);
        cmd.Parameters.Add("@End", SqlDbType.Date).Value = end is null ? DBNull.Value : ToDateTime(end.Value);
        using var reader = cmd.ExecuteReader();
        var rows = new List<HourlyScorecard>();
        while (reader.Read()) rows.Add(ReadScorecard(reader));
        return rows.AsReadOnly();
    }

    public AiRecommendation UpsertRecommendation(AiRecommendation row)
    {
        using var conn = OpenConnection();
        Execute(conn, null, """
DELETE FROM dbo.AiRecommendation WHERE RecommendationId = @RecommendationId;
INSERT INTO dbo.AiRecommendation
(RecommendationId, AccountKey, ProductId, CampaignId, AdGroupId, RecommendationType, Title, CurrentState, RecommendedState, Reason, ExpectedImpact, Confidence, SourceDateRangeStart, SourceDateRangeEnd, Status, CreatedAt, ApprovedAt, IgnoredAt, AppliedAt)
VALUES
(@RecommendationId, @AccountKey, @ProductId, @CampaignId, @AdGroupId, @RecommendationType, @Title, @CurrentState, @RecommendedState, @Reason, @ExpectedImpact, @Confidence, @SourceDateRangeStart, @SourceDateRangeEnd, @Status, @CreatedAt, @ApprovedAt, @IgnoredAt, @AppliedAt);
""", AddRecommendationParams(row));
        return row;
    }

    public AiRecommendation? GetRecommendation(string recommendationId)
    {
        using var conn = OpenConnection();
        using var cmd = CreateCommand(conn, "SELECT * FROM dbo.AiRecommendation WHERE RecommendationId = @RecommendationId;", null);
        cmd.Parameters.AddWithValue("@RecommendationId", recommendationId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecommendation(reader) : null;
    }

    public IReadOnlyList<AiRecommendation> GetRecommendations(string accountKey, string productId)
    {
        using var conn = OpenConnection();
        using var cmd = CreateCommand(conn, """
SELECT * FROM dbo.AiRecommendation
WHERE AccountKey = @AccountKey AND ProductId = @ProductId
ORDER BY CreatedAt DESC;
""", null);
        cmd.Parameters.AddWithValue("@AccountKey", accountKey);
        cmd.Parameters.AddWithValue("@ProductId", productId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<AiRecommendation>();
        while (reader.Read()) rows.Add(ReadRecommendation(reader));
        return rows.AsReadOnly();
    }

    public void ReplaceEvidence(string recommendationId, IEnumerable<AiRecommendationEvidence> rows)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        Execute(conn, tx, "DELETE FROM dbo.AiRecommendationEvidence WHERE RecommendationId = @RecommendationId;", cmd =>
            cmd.Parameters.AddWithValue("@RecommendationId", recommendationId));
        foreach (var row in rows)
        {
            Execute(conn, tx, """
INSERT INTO dbo.AiRecommendationEvidence
(EvidenceId, RecommendationId, SourceType, SourceTable, SourceField, SourceValue, MetricName, MetricValue, Notes)
VALUES
(@EvidenceId, @RecommendationId, @SourceType, @SourceTable, @SourceField, @SourceValue, @MetricName, @MetricValue, @Notes);
""", AddEvidenceParams(row));
        }
        tx.Commit();
    }

    public IReadOnlyList<AiRecommendationEvidence> GetEvidence(string recommendationId)
    {
        using var conn = OpenConnection();
        using var cmd = CreateCommand(conn, "SELECT * FROM dbo.AiRecommendationEvidence WHERE RecommendationId = @RecommendationId ORDER BY SourceType, MetricName;", null);
        cmd.Parameters.AddWithValue("@RecommendationId", recommendationId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<AiRecommendationEvidence>();
        while (reader.Read()) rows.Add(ReadEvidence(reader));
        return rows.AsReadOnly();
    }

    public RecommendationExperiment UpsertExperiment(RecommendationExperiment row)
    {
        using var conn = OpenConnection();
        Execute(conn, null, """
DELETE FROM dbo.RecommendationExperiment WHERE ExperimentId = @ExperimentId;
INSERT INTO dbo.RecommendationExperiment
(ExperimentId, RecommendationId, ProductId, CampaignId, MetricBeforeStart, MetricBeforeEnd, MetricAfterStart, MetricAfterEnd, BaselineSpend, AfterSpend, BaselineSales, AfterSales, BaselineROAS, AfterROAS, BaselineACOS, AfterACOS, BaselinePurchases, AfterPurchases, Result, LearningNote, CreatedAt)
VALUES
(@ExperimentId, @RecommendationId, @ProductId, @CampaignId, @MetricBeforeStart, @MetricBeforeEnd, @MetricAfterStart, @MetricAfterEnd, @BaselineSpend, @AfterSpend, @BaselineSales, @AfterSales, @BaselineROAS, @AfterROAS, @BaselineACOS, @AfterACOS, @BaselinePurchases, @AfterPurchases, @Result, @LearningNote, @CreatedAt);
""", AddExperimentParams(row));
        return row;
    }

    public IReadOnlyList<RecommendationExperiment> GetExperiments(string productId)
    {
        using var conn = OpenConnection();
        using var cmd = CreateCommand(conn, "SELECT * FROM dbo.RecommendationExperiment WHERE ProductId = @ProductId ORDER BY CreatedAt DESC;", null);
        cmd.Parameters.AddWithValue("@ProductId", productId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<RecommendationExperiment>();
        while (reader.Read()) rows.Add(ReadExperiment(reader));
        return rows.AsReadOnly();
    }

    public void UpsertRecommendationApplyRecord(RecommendationApplyRecord row)
    {
        using var conn = OpenConnection();
        EnsureRecommendationApplyTable(conn);
        Execute(conn, null, """
DELETE FROM dbo.RecommendationApplyAudit WHERE ApplyId = @ApplyId;
INSERT INTO dbo.RecommendationApplyAudit
(ApplyId, RecommendationId, AccountKey, ProductId, Status, CreatedAt, ApprovedAt, AppliedAt, ApplyFailedAt, ApplyErrorMessage,
 BeforeSnapshotJson, ProposedChangeJson, FinalAppliedChangeJson, AfterSnapshotJson, AmazonApiRequestJson, AmazonApiResponseJson,
 UserEditedChangeJson, UserApprovalNotes, ExperimentId, DataQualityLabel)
VALUES
(@ApplyId, @RecommendationId, @AccountKey, @ProductId, @Status, @CreatedAt, @ApprovedAt, @AppliedAt, @ApplyFailedAt, @ApplyErrorMessage,
 @BeforeSnapshotJson, @ProposedChangeJson, @FinalAppliedChangeJson, @AfterSnapshotJson, @AmazonApiRequestJson, @AmazonApiResponseJson,
 @UserEditedChangeJson, @UserApprovalNotes, @ExperimentId, @DataQualityLabel);
""", AddApplyRecordParams(row));
    }

    public bool HasAnalyticsRows(string accountKey, string productId)
    {
        using var conn = OpenConnection();
        using var cmd = CreateCommand(conn, "SELECT TOP (1) 1 FROM dbo.AdPerformanceDaily WHERE AccountKey = @AccountKey AND ProductId = @ProductId;", null);
        cmd.Parameters.AddWithValue("@AccountKey", accountKey);
        cmd.Parameters.AddWithValue("@ProductId", productId);
        return cmd.ExecuteScalar() is not null;
    }

    public virtual IReadOnlyList<AmcQueryCoverageRow> GetAmcCoverage(string accountKey, string resultType, DateOnly start, DateOnly end)
    {
        using var conn = OpenConnection();
        EnsureAmcQueryCoverageTable(conn);
        using var cmd = CreateCommand(conn, """
SELECT AccountKey, ResultType, [Date], Status, WorkflowExecutionId, UpdatedAt
FROM dbo.AmcQueryCoverage
WHERE AccountKey = @AccountKey AND ResultType = @ResultType AND [Date] >= @Start AND [Date] <= @End;
""", null);
        cmd.Parameters.AddWithValue("@AccountKey", accountKey);
        cmd.Parameters.AddWithValue("@ResultType", resultType);
        cmd.Parameters.AddWithValue("@Start", ToDateTime(start));
        cmd.Parameters.AddWithValue("@End", ToDateTime(end));
        using var reader = cmd.ExecuteReader();
        var rows = new List<AmcQueryCoverageRow>();
        while (reader.Read()) rows.Add(ReadCoverage(reader));
        return rows.AsReadOnly();
    }

    public virtual void DeleteAmcCoverage(string accountKey, DateOnly start, DateOnly end)
    {
        if (start > end) return;
        using var conn = OpenConnection();
        EnsureAmcQueryCoverageTable(conn);
        Execute(conn, null, """
DELETE FROM dbo.AmcQueryCoverage
WHERE AccountKey = @AccountKey AND [Date] >= @Start AND [Date] <= @End;
""", cmd =>
        {
            cmd.Parameters.AddWithValue("@AccountKey", accountKey);
            cmd.Parameters.AddWithValue("@Start", ToDateTime(start));
            cmd.Parameters.AddWithValue("@End", ToDateTime(end));
        });
    }

    public virtual void UpsertAmcCoverage(IEnumerable<AmcQueryCoverageRow> rows)
    {
        var list = rows.ToList();
        if (!list.Any()) return;
        using var conn = OpenConnection();
        EnsureAmcQueryCoverageTable(conn);
        using var tx = conn.BeginTransaction();
        foreach (var row in list)
        {
            Execute(conn, tx, """
DELETE FROM dbo.AmcQueryCoverage
WHERE AccountKey = @AccountKey AND ResultType = @ResultType AND [Date] = @Date;
INSERT INTO dbo.AmcQueryCoverage
(AccountKey, ResultType, [Date], Status, WorkflowExecutionId, UpdatedAt)
VALUES
(@AccountKey, @ResultType, @Date, @Status, @WorkflowExecutionId, @UpdatedAt);
""", cmd =>
            {
                cmd.Parameters.AddWithValue("@AccountKey", row.AccountKey);
                cmd.Parameters.AddWithValue("@ResultType", row.ResultType);
                cmd.Parameters.AddWithValue("@Date", ToDateTime(row.Date));
                cmd.Parameters.AddWithValue("@Status", row.Status);
                cmd.Parameters.AddWithValue("@WorkflowExecutionId", Db(row.WorkflowExecutionId));
                cmd.Parameters.AddWithValue("@UpdatedAt", row.UpdatedAt);
            });
        }
        tx.Commit();
    }

    private static void EnsureAmcQueryCoverageTable(SqlConnection conn)
    {
        Execute(conn, null, """
IF OBJECT_ID('dbo.AmcQueryCoverage', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AmcQueryCoverage (
        AccountKey nvarchar(100) NOT NULL,
        ResultType nvarchar(40) NOT NULL,
        [Date] date NOT NULL,
        Status nvarchar(20) NOT NULL,
        WorkflowExecutionId nvarchar(100) NULL,
        UpdatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
        CONSTRAINT PK_AmcQueryCoverage PRIMARY KEY (AccountKey, ResultType, [Date])
    );
    CREATE INDEX IX_AmcQueryCoverage_Pending ON dbo.AmcQueryCoverage(AccountKey, ResultType, Status, WorkflowExecutionId);
END
""", _ => { });
    }

    private static AmcQueryCoverageRow ReadCoverage(SqlDataReader r) => new()
    {
        AccountKey = r.GetString(r.GetOrdinal("AccountKey")),
        ResultType = r.GetString(r.GetOrdinal("ResultType")),
        Date = ToDateOnly(r, "Date"),
        Status = r.GetString(r.GetOrdinal("Status")),
        WorkflowExecutionId = GetNullableString(r, "WorkflowExecutionId"),
        UpdatedAt = r.GetDateTimeOffset(r.GetOrdinal("UpdatedAt"))
    };

    private static void EnsureRecommendationApplyTable(SqlConnection conn)
    {
        Execute(conn, null, """
IF OBJECT_ID('dbo.RecommendationApplyAudit', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.RecommendationApplyAudit (
        ApplyId nvarchar(100) NOT NULL CONSTRAINT PK_RecommendationApplyAudit PRIMARY KEY,
        RecommendationId nvarchar(100) NOT NULL,
        AccountKey nvarchar(100) NOT NULL,
        ProductId nvarchar(100) NOT NULL,
        Status nvarchar(80) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
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
    CREATE INDEX IX_RecommendationApplyAudit_Recommendation ON dbo.RecommendationApplyAudit(RecommendationId, CreatedAt DESC);
END
""", _ => { });
    }

    private SqlConnection OpenConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new AnalyticsDatabaseNotConfiguredException();
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static SqlCommand CreateCommand(SqlConnection conn, string sql, SqlTransaction? tx)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static void Execute(SqlConnection conn, SqlTransaction? tx, string sql, Action<SqlCommand> addParams)
    {
        using var cmd = CreateCommand(conn, sql, tx);
        addParams(cmd);
        cmd.ExecuteNonQuery();
    }

    private static object Db(object? value) => value ?? DBNull.Value;
    private static DateTime ToDateTime(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);
    private static DateOnly ToDateOnly(SqlDataReader r, string name) => DateOnly.FromDateTime(r.GetDateTime(r.GetOrdinal(name)));
    private static string? GetNullableString(SqlDataReader r, string name) => r.IsDBNull(r.GetOrdinal(name)) ? null : r.GetString(r.GetOrdinal(name));
    private static DateTimeOffset? GetNullableDateTimeOffset(SqlDataReader r, string name) => r.IsDBNull(r.GetOrdinal(name)) ? null : r.GetDateTimeOffset(r.GetOrdinal(name));
    private static string InClause(string cmdPrefix, int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"@{cmdPrefix}{i}"));
    private static void AddInParams(SqlCommand cmd, string prefix, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
            cmd.Parameters.AddWithValue($"@{prefix}{i}", values[i]);
    }

    private static Action<SqlCommand> AddDailyParams(AdPerformanceDaily row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@Date", ToDateTime(row.Date));
        cmd.Parameters.AddWithValue("@SourceReportType", string.IsNullOrWhiteSpace(row.SourceReportType) ? "Targeting" : row.SourceReportType);
        cmd.Parameters.AddWithValue("@AccountKey", row.AccountKey);
        cmd.Parameters.AddWithValue("@ProfileId", row.ProfileId);
        cmd.Parameters.AddWithValue("@ProductId", Db(row.ProductId));
        cmd.Parameters.AddWithValue("@Asin", Db(row.Asin));
        cmd.Parameters.AddWithValue("@CampaignId", row.CampaignId);
        cmd.Parameters.AddWithValue("@CampaignName", row.CampaignName);
        cmd.Parameters.AddWithValue("@AdGroupId", Db(row.AdGroupId));
        cmd.Parameters.AddWithValue("@AdGroupName", Db(row.AdGroupName));
        cmd.Parameters.AddWithValue("@AdId", Db(row.AdId));
        cmd.Parameters.AddWithValue("@TargetingText", Db(row.TargetingText));
        cmd.Parameters.AddWithValue("@TargetingType", Db(row.TargetingType));
        cmd.Parameters.AddWithValue("@MatchType", Db(row.MatchType));
        cmd.Parameters.AddWithValue("@SearchTerm", Db(row.SearchTerm));
        cmd.Parameters.AddWithValue("@Impressions", row.Impressions);
        cmd.Parameters.AddWithValue("@Clicks", row.Clicks);
        cmd.Parameters.AddWithValue("@Spend", row.Spend);
        cmd.Parameters.AddWithValue("@Purchases", row.Purchases);
        cmd.Parameters.AddWithValue("@Sales", row.Sales);
        cmd.Parameters.AddWithValue("@UnitsSold", row.UnitsSold);
        cmd.Parameters.AddWithValue("@DetailPageViews", row.DetailPageViews);
        cmd.Parameters.AddWithValue("@ROAS", row.ROAS);
        cmd.Parameters.AddWithValue("@ACOS", row.ACOS);
        cmd.Parameters.AddWithValue("@CPC", row.CPC);
        cmd.Parameters.AddWithValue("@CTR", row.CTR);
        cmd.Parameters.AddWithValue("@CVR", row.CVR);
        cmd.Parameters.AddWithValue("@CostPerPurchase", row.CostPerPurchase);
        cmd.Parameters.AddWithValue("@PurchaseRate", row.PurchaseRate);
    };

    private static Action<SqlCommand> AddCampaignSnapshotParams(CampaignSnapshot row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@SnapshotDate", ToDateTime(row.SnapshotDate));
        cmd.Parameters.AddWithValue("@AccountKey", row.AccountKey);
        cmd.Parameters.AddWithValue("@ProfileId", row.ProfileId);
        cmd.Parameters.AddWithValue("@CampaignId", row.CampaignId);
        cmd.Parameters.AddWithValue("@CampaignName", row.CampaignName);
        cmd.Parameters.AddWithValue("@AdProduct", row.AdProduct);
        cmd.Parameters.AddWithValue("@CampaignStatus", row.CampaignStatus);
        cmd.Parameters.AddWithValue("@BudgetAmount", row.BudgetAmount);
        cmd.Parameters.AddWithValue("@BudgetType", row.BudgetType);
        cmd.Parameters.AddWithValue("@BiddingStrategy", row.BiddingStrategy);
        cmd.Parameters.AddWithValue("@PortfolioId", Db(row.PortfolioId));
    };

    private static Action<SqlCommand> AddScorecardParams(HourlyScorecard row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@AccountKey", row.AccountKey);
        cmd.Parameters.AddWithValue("@ProductId", row.ProductId);
        cmd.Parameters.AddWithValue("@Asin", row.Asin);
        cmd.Parameters.AddWithValue("@DateRangeStart", ToDateTime(row.DateRangeStart));
        cmd.Parameters.AddWithValue("@DateRangeEnd", ToDateTime(row.DateRangeEnd));
        cmd.Parameters.AddWithValue("@DayOfWeek", row.DayOfWeek.ToString());
        cmd.Parameters.AddWithValue("@Hour", row.Hour);
        cmd.Parameters.AddWithValue("@Impressions", row.Impressions);
        cmd.Parameters.AddWithValue("@Clicks", row.Clicks);
        cmd.Parameters.AddWithValue("@Spend", row.Spend);
        cmd.Parameters.AddWithValue("@Purchases", row.Purchases);
        cmd.Parameters.AddWithValue("@Sales", row.Sales);
        cmd.Parameters.AddWithValue("@Units", row.Units);
        cmd.Parameters.AddWithValue("@ROAS", row.ROAS);
        cmd.Parameters.AddWithValue("@ACOS", row.ACOS);
        cmd.Parameters.AddWithValue("@CPC", row.CPC);
        cmd.Parameters.AddWithValue("@CTR", row.CTR);
        cmd.Parameters.AddWithValue("@CVR", row.CVR);
        cmd.Parameters.AddWithValue("@SalesPerDollar", row.SalesPerDollar);
        cmd.Parameters.AddWithValue("@PurchaseShare", row.PurchaseShare);
        cmd.Parameters.AddWithValue("@SpendShare", row.SpendShare);
        cmd.Parameters.AddWithValue("@EfficiencyScore", row.EfficiencyScore);
        cmd.Parameters.AddWithValue("@RecommendedAction", Db(row.RecommendedAction));
        cmd.Parameters.AddWithValue("@CreatedAt", row.CreatedAt);
    };

    private static Action<SqlCommand> AddRecommendationParams(AiRecommendation row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@RecommendationId", row.RecommendationId);
        cmd.Parameters.AddWithValue("@AccountKey", row.AccountKey);
        cmd.Parameters.AddWithValue("@ProductId", row.ProductId);
        cmd.Parameters.AddWithValue("@CampaignId", Db(row.CampaignId));
        cmd.Parameters.AddWithValue("@AdGroupId", Db(row.AdGroupId));
        cmd.Parameters.AddWithValue("@RecommendationType", row.RecommendationType);
        cmd.Parameters.AddWithValue("@Title", row.Title);
        cmd.Parameters.AddWithValue("@CurrentState", row.CurrentState);
        cmd.Parameters.AddWithValue("@RecommendedState", row.RecommendedState);
        cmd.Parameters.AddWithValue("@Reason", row.Reason);
        cmd.Parameters.AddWithValue("@ExpectedImpact", row.ExpectedImpact);
        cmd.Parameters.AddWithValue("@Confidence", row.Confidence);
        cmd.Parameters.AddWithValue("@SourceDateRangeStart", ToDateTime(row.SourceDateRangeStart));
        cmd.Parameters.AddWithValue("@SourceDateRangeEnd", ToDateTime(row.SourceDateRangeEnd));
        cmd.Parameters.AddWithValue("@Status", row.Status);
        cmd.Parameters.AddWithValue("@CreatedAt", row.CreatedAt);
        cmd.Parameters.AddWithValue("@ApprovedAt", Db(row.ApprovedAt));
        cmd.Parameters.AddWithValue("@IgnoredAt", Db(row.IgnoredAt));
        cmd.Parameters.AddWithValue("@AppliedAt", Db(row.AppliedAt));
    };

    private static Action<SqlCommand> AddEvidenceParams(AiRecommendationEvidence row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@EvidenceId", row.EvidenceId);
        cmd.Parameters.AddWithValue("@RecommendationId", row.RecommendationId);
        cmd.Parameters.AddWithValue("@SourceType", row.SourceType);
        cmd.Parameters.AddWithValue("@SourceTable", row.SourceTable);
        cmd.Parameters.AddWithValue("@SourceField", row.SourceField);
        cmd.Parameters.AddWithValue("@SourceValue", row.SourceValue);
        cmd.Parameters.AddWithValue("@MetricName", row.MetricName);
        cmd.Parameters.AddWithValue("@MetricValue", row.MetricValue);
        cmd.Parameters.AddWithValue("@Notes", row.Notes);
    };

    private static Action<SqlCommand> AddExperimentParams(RecommendationExperiment row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@ExperimentId", row.ExperimentId);
        cmd.Parameters.AddWithValue("@RecommendationId", row.RecommendationId);
        cmd.Parameters.AddWithValue("@ProductId", row.ProductId);
        cmd.Parameters.AddWithValue("@CampaignId", Db(row.CampaignId));
        cmd.Parameters.AddWithValue("@MetricBeforeStart", ToDateTime(row.MetricBeforeStart));
        cmd.Parameters.AddWithValue("@MetricBeforeEnd", ToDateTime(row.MetricBeforeEnd));
        cmd.Parameters.AddWithValue("@MetricAfterStart", ToDateTime(row.MetricAfterStart));
        cmd.Parameters.AddWithValue("@MetricAfterEnd", ToDateTime(row.MetricAfterEnd));
        cmd.Parameters.AddWithValue("@BaselineSpend", row.BaselineSpend);
        cmd.Parameters.AddWithValue("@AfterSpend", row.AfterSpend);
        cmd.Parameters.AddWithValue("@BaselineSales", row.BaselineSales);
        cmd.Parameters.AddWithValue("@AfterSales", row.AfterSales);
        cmd.Parameters.AddWithValue("@BaselineROAS", row.BaselineROAS);
        cmd.Parameters.AddWithValue("@AfterROAS", row.AfterROAS);
        cmd.Parameters.AddWithValue("@BaselineACOS", row.BaselineACOS);
        cmd.Parameters.AddWithValue("@AfterACOS", row.AfterACOS);
        cmd.Parameters.AddWithValue("@BaselinePurchases", row.BaselinePurchases);
        cmd.Parameters.AddWithValue("@AfterPurchases", row.AfterPurchases);
        cmd.Parameters.AddWithValue("@Result", row.Result);
        cmd.Parameters.AddWithValue("@LearningNote", row.LearningNote);
        cmd.Parameters.AddWithValue("@CreatedAt", row.CreatedAt);
    };

    private static Action<SqlCommand> AddApplyRecordParams(RecommendationApplyRecord row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@ApplyId", row.ApplyId);
        cmd.Parameters.AddWithValue("@RecommendationId", row.RecommendationId);
        cmd.Parameters.AddWithValue("@AccountKey", row.AccountKey);
        cmd.Parameters.AddWithValue("@ProductId", row.ProductId);
        cmd.Parameters.AddWithValue("@Status", row.Status);
        cmd.Parameters.AddWithValue("@CreatedAt", row.CreatedAt);
        cmd.Parameters.AddWithValue("@ApprovedAt", Db(row.ApprovedAt));
        cmd.Parameters.AddWithValue("@AppliedAt", Db(row.AppliedAt));
        cmd.Parameters.AddWithValue("@ApplyFailedAt", Db(row.ApplyFailedAt));
        cmd.Parameters.AddWithValue("@ApplyErrorMessage", Db(row.ApplyErrorMessage));
        cmd.Parameters.AddWithValue("@BeforeSnapshotJson", row.BeforeSnapshotJson);
        cmd.Parameters.AddWithValue("@ProposedChangeJson", row.ProposedChangeJson);
        cmd.Parameters.AddWithValue("@FinalAppliedChangeJson", row.FinalAppliedChangeJson);
        cmd.Parameters.AddWithValue("@AfterSnapshotJson", row.AfterSnapshotJson);
        cmd.Parameters.AddWithValue("@AmazonApiRequestJson", row.AmazonApiRequestJson);
        cmd.Parameters.AddWithValue("@AmazonApiResponseJson", row.AmazonApiResponseJson);
        cmd.Parameters.AddWithValue("@UserEditedChangeJson", row.UserEditedChangeJson);
        cmd.Parameters.AddWithValue("@UserApprovalNotes", row.UserApprovalNotes);
        cmd.Parameters.AddWithValue("@ExperimentId", Db(row.ExperimentId));
        cmd.Parameters.AddWithValue("@DataQualityLabel", row.DataQualityLabel);
    };

    private static Action<SqlCommand> AddAmcTrafficParams(AmcTrafficHourly row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@Date", ToDateTime(row.Date));
        cmd.Parameters.AddWithValue("@Hour", row.Hour);
        cmd.Parameters.AddWithValue("@TimeZone", row.TimeZone);
        cmd.Parameters.AddWithValue("@AccountKey", row.AccountKey);
        cmd.Parameters.AddWithValue("@ProfileId", row.ProfileId);
        cmd.Parameters.AddWithValue("@CampaignId", row.CampaignId);
        cmd.Parameters.AddWithValue("@CampaignName", row.CampaignName);
        cmd.Parameters.AddWithValue("@AdGroupId", Db(row.AdGroupId));
        cmd.Parameters.AddWithValue("@AdGroupName", Db(row.AdGroupName));
        cmd.Parameters.AddWithValue("@AdProductType", row.AdProductType);
        cmd.Parameters.AddWithValue("@TargetingText", Db(row.TargetingText));
        cmd.Parameters.AddWithValue("@MatchType", Db(row.MatchType));
        cmd.Parameters.AddWithValue("@CustomerSearchTerm", Db(row.CustomerSearchTerm));
        cmd.Parameters.AddWithValue("@Impressions", row.Impressions);
        cmd.Parameters.AddWithValue("@Clicks", row.Clicks);
        cmd.Parameters.AddWithValue("@Spend", row.Spend);
    };

    private static Action<SqlCommand> AddAmcConversionParams(AmcConversionsHourly row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@ConversionDate", ToDateTime(row.ConversionDate));
        cmd.Parameters.AddWithValue("@ConversionHour", row.ConversionHour);
        cmd.Parameters.AddWithValue("@TimeZone", row.TimeZone);
        cmd.Parameters.AddWithValue("@AccountKey", row.AccountKey);
        cmd.Parameters.AddWithValue("@ProfileId", row.ProfileId);
        cmd.Parameters.AddWithValue("@CampaignId", row.CampaignId);
        cmd.Parameters.AddWithValue("@CampaignName", row.CampaignName);
        cmd.Parameters.AddWithValue("@AdGroupId", Db(row.AdGroupId));
        cmd.Parameters.AddWithValue("@AdGroupName", Db(row.AdGroupName));
        cmd.Parameters.AddWithValue("@AdProductType", row.AdProductType);
        cmd.Parameters.AddWithValue("@TrackedAsin", Db(row.TrackedAsin));
        cmd.Parameters.AddWithValue("@ConversionEventType", Db(row.ConversionEventType));
        cmd.Parameters.AddWithValue("@Purchases", row.Purchases);
        cmd.Parameters.AddWithValue("@UnitsSold", row.UnitsSold);
        cmd.Parameters.AddWithValue("@Sales", row.Sales);
        cmd.Parameters.AddWithValue("@NewToBrandPurchases", Db(row.NewToBrandPurchases));
        cmd.Parameters.AddWithValue("@NewToBrandSales", Db(row.NewToBrandSales));
    };

    private static Action<SqlCommand> AddAttributionParams(AmcAttributionLag row) => cmd =>
    {
        cmd.Parameters.AddWithValue("@AccountKey", row.AccountKey);
        cmd.Parameters.AddWithValue("@ProfileId", row.ProfileId);
        cmd.Parameters.AddWithValue("@CampaignId", row.CampaignId);
        cmd.Parameters.AddWithValue("@AdGroupId", Db(row.AdGroupId));
        cmd.Parameters.AddWithValue("@TargetingText", Db(row.TargetingText));
        cmd.Parameters.AddWithValue("@SearchTerm", Db(row.SearchTerm));
        cmd.Parameters.AddWithValue("@TrafficDate", ToDateTime(row.TrafficDate));
        cmd.Parameters.AddWithValue("@TrafficHour", row.TrafficHour);
        cmd.Parameters.AddWithValue("@ConversionDate", ToDateTime(row.ConversionDate));
        cmd.Parameters.AddWithValue("@ConversionHour", row.ConversionHour);
        cmd.Parameters.AddWithValue("@HoursToConversion", row.HoursToConversion);
        cmd.Parameters.AddWithValue("@Purchases", row.Purchases);
        cmd.Parameters.AddWithValue("@Sales", row.Sales);
    };

    private static AdPerformanceDaily ReadDaily(SqlDataReader r) => new()
    {
        Date = ToDateOnly(r, "Date"),
        SourceReportType = r.GetString(r.GetOrdinal("SourceReportType")),
        AccountKey = r.GetString(r.GetOrdinal("AccountKey")),
        ProfileId = r.GetString(r.GetOrdinal("ProfileId")),
        ProductId = GetNullableString(r, "ProductId"),
        Asin = GetNullableString(r, "Asin"),
        CampaignId = r.GetString(r.GetOrdinal("CampaignId")),
        CampaignName = r.GetString(r.GetOrdinal("CampaignName")),
        AdGroupId = GetNullableString(r, "AdGroupId"),
        AdGroupName = GetNullableString(r, "AdGroupName"),
        AdId = GetNullableString(r, "AdId"),
        TargetingText = GetNullableString(r, "TargetingText"),
        TargetingType = GetNullableString(r, "TargetingType"),
        MatchType = GetNullableString(r, "MatchType"),
        SearchTerm = GetNullableString(r, "SearchTerm"),
        Impressions = r.GetInt32(r.GetOrdinal("Impressions")),
        Clicks = r.GetInt32(r.GetOrdinal("Clicks")),
        Spend = r.GetDecimal(r.GetOrdinal("Spend")),
        Purchases = r.GetInt32(r.GetOrdinal("Purchases")),
        Sales = r.GetDecimal(r.GetOrdinal("Sales")),
        UnitsSold = r.GetInt32(r.GetOrdinal("UnitsSold")),
        DetailPageViews = r.GetInt32(r.GetOrdinal("DetailPageViews")),
        ROAS = r.GetDecimal(r.GetOrdinal("ROAS")),
        ACOS = r.GetDecimal(r.GetOrdinal("ACOS")),
        CPC = r.GetDecimal(r.GetOrdinal("CPC")),
        CTR = r.GetDecimal(r.GetOrdinal("CTR")),
        CVR = r.GetDecimal(r.GetOrdinal("CVR")),
        CostPerPurchase = r.GetDecimal(r.GetOrdinal("CostPerPurchase")),
        PurchaseRate = r.GetDecimal(r.GetOrdinal("PurchaseRate"))
    };

    private static HourlyScorecard ReadScorecard(SqlDataReader r) => new()
    {
        AccountKey = r.GetString(r.GetOrdinal("AccountKey")),
        ProductId = r.GetString(r.GetOrdinal("ProductId")),
        Asin = r.GetString(r.GetOrdinal("Asin")),
        DateRangeStart = ToDateOnly(r, "DateRangeStart"),
        DateRangeEnd = ToDateOnly(r, "DateRangeEnd"),
        DayOfWeek = Enum.TryParse<DayOfWeek>(r.GetString(r.GetOrdinal("DayOfWeek")), out var dow) ? dow : DayOfWeek.Sunday,
        Hour = r.GetInt32(r.GetOrdinal("Hour")),
        Impressions = r.GetInt32(r.GetOrdinal("Impressions")),
        Clicks = r.GetInt32(r.GetOrdinal("Clicks")),
        Spend = r.GetDecimal(r.GetOrdinal("Spend")),
        Purchases = r.GetInt32(r.GetOrdinal("Purchases")),
        Sales = r.GetDecimal(r.GetOrdinal("Sales")),
        Units = r.GetInt32(r.GetOrdinal("Units")),
        ROAS = r.GetDecimal(r.GetOrdinal("ROAS")),
        ACOS = r.GetDecimal(r.GetOrdinal("ACOS")),
        CPC = r.GetDecimal(r.GetOrdinal("CPC")),
        CTR = r.GetDecimal(r.GetOrdinal("CTR")),
        CVR = r.GetDecimal(r.GetOrdinal("CVR")),
        SalesPerDollar = r.GetDecimal(r.GetOrdinal("SalesPerDollar")),
        PurchaseShare = r.GetDecimal(r.GetOrdinal("PurchaseShare")),
        SpendShare = r.GetDecimal(r.GetOrdinal("SpendShare")),
        EfficiencyScore = r.GetDecimal(r.GetOrdinal("EfficiencyScore")),
        RecommendedAction = GetNullableString(r, "RecommendedAction"),
        CreatedAt = r.GetDateTimeOffset(r.GetOrdinal("CreatedAt"))
    };

    private static AiRecommendation ReadRecommendation(SqlDataReader r) => new()
    {
        RecommendationId = r.GetString(r.GetOrdinal("RecommendationId")),
        AccountKey = r.GetString(r.GetOrdinal("AccountKey")),
        ProductId = r.GetString(r.GetOrdinal("ProductId")),
        CampaignId = GetNullableString(r, "CampaignId"),
        AdGroupId = GetNullableString(r, "AdGroupId"),
        RecommendationType = r.GetString(r.GetOrdinal("RecommendationType")),
        Title = r.GetString(r.GetOrdinal("Title")),
        CurrentState = r.GetString(r.GetOrdinal("CurrentState")),
        RecommendedState = r.GetString(r.GetOrdinal("RecommendedState")),
        Reason = r.GetString(r.GetOrdinal("Reason")),
        ExpectedImpact = r.GetString(r.GetOrdinal("ExpectedImpact")),
        Confidence = r.GetDecimal(r.GetOrdinal("Confidence")),
        SourceDateRangeStart = ToDateOnly(r, "SourceDateRangeStart"),
        SourceDateRangeEnd = ToDateOnly(r, "SourceDateRangeEnd"),
        Status = r.GetString(r.GetOrdinal("Status")),
        CreatedAt = r.GetDateTimeOffset(r.GetOrdinal("CreatedAt")),
        ApprovedAt = GetNullableDateTimeOffset(r, "ApprovedAt"),
        IgnoredAt = GetNullableDateTimeOffset(r, "IgnoredAt"),
        AppliedAt = GetNullableDateTimeOffset(r, "AppliedAt")
    };

    private static AiRecommendationEvidence ReadEvidence(SqlDataReader r) => new()
    {
        EvidenceId = r.GetString(r.GetOrdinal("EvidenceId")),
        RecommendationId = r.GetString(r.GetOrdinal("RecommendationId")),
        SourceType = r.GetString(r.GetOrdinal("SourceType")),
        SourceTable = r.GetString(r.GetOrdinal("SourceTable")),
        SourceField = r.GetString(r.GetOrdinal("SourceField")),
        SourceValue = r.GetString(r.GetOrdinal("SourceValue")),
        MetricName = r.GetString(r.GetOrdinal("MetricName")),
        MetricValue = r.GetDecimal(r.GetOrdinal("MetricValue")),
        Notes = r.GetString(r.GetOrdinal("Notes"))
    };

    private static RecommendationExperiment ReadExperiment(SqlDataReader r) => new()
    {
        ExperimentId = r.GetString(r.GetOrdinal("ExperimentId")),
        RecommendationId = r.GetString(r.GetOrdinal("RecommendationId")),
        ProductId = r.GetString(r.GetOrdinal("ProductId")),
        CampaignId = GetNullableString(r, "CampaignId"),
        MetricBeforeStart = ToDateOnly(r, "MetricBeforeStart"),
        MetricBeforeEnd = ToDateOnly(r, "MetricBeforeEnd"),
        MetricAfterStart = ToDateOnly(r, "MetricAfterStart"),
        MetricAfterEnd = ToDateOnly(r, "MetricAfterEnd"),
        BaselineSpend = r.GetDecimal(r.GetOrdinal("BaselineSpend")),
        AfterSpend = r.GetDecimal(r.GetOrdinal("AfterSpend")),
        BaselineSales = r.GetDecimal(r.GetOrdinal("BaselineSales")),
        AfterSales = r.GetDecimal(r.GetOrdinal("AfterSales")),
        BaselineROAS = r.GetDecimal(r.GetOrdinal("BaselineROAS")),
        AfterROAS = r.GetDecimal(r.GetOrdinal("AfterROAS")),
        BaselineACOS = r.GetDecimal(r.GetOrdinal("BaselineACOS")),
        AfterACOS = r.GetDecimal(r.GetOrdinal("AfterACOS")),
        BaselinePurchases = r.GetInt32(r.GetOrdinal("BaselinePurchases")),
        AfterPurchases = r.GetInt32(r.GetOrdinal("AfterPurchases")),
        Result = r.GetString(r.GetOrdinal("Result")),
        LearningNote = r.GetString(r.GetOrdinal("LearningNote")),
        CreatedAt = r.GetDateTimeOffset(r.GetOrdinal("CreatedAt"))
    };

    private static AmcTrafficHourly ReadTraffic(SqlDataReader r) => new()
    {
        Date = ToDateOnly(r, "Date"),
        Hour = r.GetInt32(r.GetOrdinal("Hour")),
        TimeZone = r.GetString(r.GetOrdinal("TimeZone")),
        AccountKey = r.GetString(r.GetOrdinal("AccountKey")),
        ProfileId = r.GetString(r.GetOrdinal("ProfileId")),
        CampaignId = r.GetString(r.GetOrdinal("CampaignId")),
        CampaignName = r.GetString(r.GetOrdinal("CampaignName")),
        AdGroupId = GetNullableString(r, "AdGroupId"),
        AdGroupName = GetNullableString(r, "AdGroupName"),
        AdProductType = r.GetString(r.GetOrdinal("AdProductType")),
        TargetingText = GetNullableString(r, "TargetingText"),
        MatchType = GetNullableString(r, "MatchType"),
        CustomerSearchTerm = GetNullableString(r, "CustomerSearchTerm"),
        Impressions = r.GetInt32(r.GetOrdinal("Impressions")),
        Clicks = r.GetInt32(r.GetOrdinal("Clicks")),
        Spend = r.GetDecimal(r.GetOrdinal("Spend"))
    };

    private static AmcConversionsHourly ReadConversion(SqlDataReader r) => new()
    {
        ConversionDate = ToDateOnly(r, "ConversionDate"),
        ConversionHour = r.GetInt32(r.GetOrdinal("ConversionHour")),
        TimeZone = r.GetString(r.GetOrdinal("TimeZone")),
        AccountKey = r.GetString(r.GetOrdinal("AccountKey")),
        ProfileId = r.GetString(r.GetOrdinal("ProfileId")),
        CampaignId = r.GetString(r.GetOrdinal("CampaignId")),
        CampaignName = r.GetString(r.GetOrdinal("CampaignName")),
        AdGroupId = GetNullableString(r, "AdGroupId"),
        AdGroupName = GetNullableString(r, "AdGroupName"),
        AdProductType = r.GetString(r.GetOrdinal("AdProductType")),
        TrackedAsin = GetNullableString(r, "TrackedAsin"),
        ConversionEventType = GetNullableString(r, "ConversionEventType"),
        Purchases = r.GetInt32(r.GetOrdinal("Purchases")),
        UnitsSold = r.GetInt32(r.GetOrdinal("UnitsSold")),
        Sales = r.GetDecimal(r.GetOrdinal("Sales")),
        NewToBrandPurchases = r.IsDBNull(r.GetOrdinal("NewToBrandPurchases")) ? null : r.GetInt32(r.GetOrdinal("NewToBrandPurchases")),
        NewToBrandSales = r.IsDBNull(r.GetOrdinal("NewToBrandSales")) ? null : r.GetDecimal(r.GetOrdinal("NewToBrandSales"))
    };
}
