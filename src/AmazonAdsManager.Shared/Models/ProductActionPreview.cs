namespace AmazonAdsManager.Shared.Models;

public class ProductActionPreview
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RecommendationId { get; set; } = "";
    public string ActionType { get; set; } = ""; // UpdateSchedule, ReduceBudget, IncreaseBudget, PauseCampaign, OptimizeKeywords, WatchOnly, NoAction
    public string Summary { get; set; } = "";
    public string Details { get; set; } = "";
    public Dictionary<string, object> Parameters { get; set; } = new();
    public string RiskLevel { get; set; } = "Low"; // Low, Medium, High
    public bool RequiresApproval { get; set; } = true;
}

public class CampaignActionDetails
{
    public long CampaignId { get; set; }
    public string CampaignName { get; set; } = "";
    public string ActionType { get; set; } = ""; // ReduceBudget, IncreaseBudget, PauseCampaign
    public decimal? ProposedBudgetChange { get; set; }
    public decimal? NewDailyBudget { get; set; }
    public string? ProposedState { get; set; } // "paused" or "enabled"
}

public class ScheduleActionDetails
{
    public string ScheduleId { get; set; } = "";
    public string ProposedDayparting { get; set; } = "";
    public string Description { get; set; } = "";
}

public class KeywordActionDetails
{
    public string Suggestion { get; set; } = "";
    public string Type { get; set; } = ""; // NegativeKeyword, BidAdjustment, etc
}
