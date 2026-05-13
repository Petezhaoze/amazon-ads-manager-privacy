using AmazonAdsManager.Shared.Models;

namespace AmazonAdsManager.Api.Services;

public class ProductActionPreviewService
{
    private readonly ProductCampaignMappingRepository _campaignMappings;
    private readonly ProductProfileRepository _profiles;

    public ProductActionPreviewService(
        ProductCampaignMappingRepository campaignMappings,
        ProductProfileRepository profiles)
    {
        _campaignMappings = campaignMappings;
        _profiles = profiles;
    }

    public List<ProductActionPreview> GenerateActionPreviews(
        ProductAiRecommendation recommendation,
        ProductTrendSummary trend)
    {
        var previews = new List<ProductActionPreview>();
        var product = _profiles.GetById(trend.ProductId);
        var campaigns = _campaignMappings.GetByProduct(trend.AccountKey, trend.ProductId).ToList();

        return recommendation.RecommendationType switch
        {
            "ReduceBudget" => GenerateReduceBudgetActions(recommendation, trend, product, campaigns),
            "IncreaseBudget" => GenerateIncreaseBudgetActions(recommendation, trend, product, campaigns),
            "PauseOvernight" => GeneratePauseOvernightActions(recommendation, trend, campaigns),
            "AdjustDayparting" => GenerateAdjustDaypartingActions(recommendation, trend),
            "PauseLowPerformer" => GeneratePauseLowPerformerActions(recommendation, trend, campaigns),
            "IncreaseHighPerformerBudget" => GenerateIncreaseHighPerformerActions(recommendation, trend, product, campaigns),
            "AddNegativeKeyword" => GenerateNegativeKeywordActions(recommendation, trend),
            "MoveBudgetToBetterCampaign" => GenerateMoveBudgetActions(recommendation, trend, campaigns),
            "WatchOnly" => GenerateWatchOnlyActions(recommendation, trend),
            _ => new List<ProductActionPreview> { GenerateNoActionPreview(recommendation) }
        };
    }

    private List<ProductActionPreview> GenerateReduceBudgetActions(
        ProductAiRecommendation rec, ProductTrendSummary trend, ProductProfile? product, List<ProductCampaignMapping> campaigns)
    {
        var previews = new List<ProductActionPreview>();

        if (campaigns.Count == 0)
        {
            previews.Add(new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "WatchOnly",
                Summary = "No linked campaigns to modify",
                Details = "This product has no linked campaigns. Manually adjust budgets in Advertising Console.",
                RiskLevel = "Low"
            });
            return previews;
        }

        var budgetReduction = rec.SuggestedBudgetChangePercent ?? -15m;

        foreach (var campaign in campaigns)
        {
            // Placeholder for current daily budget (would come from Amazon API)
            var currentBudget = product?.DefaultDailyBudget ?? 50m;
            var newBudget = currentBudget * (1 + budgetReduction / 100);

            previews.Add(new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "ReduceBudget",
                Summary = $"Reduce budget for '{campaign.CampaignName}'",
                Details = $"Campaign: {campaign.CampaignName} (ID: {campaign.CampaignId})\n" +
                         $"Current daily budget: ${currentBudget:F2}\n" +
                         $"Proposed daily budget: ${newBudget:F2}\n" +
                         $"Change: {budgetReduction:+0.0;-0.0}%\n" +
                         $"Reason: {rec.Explanation}",
                Parameters = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign.CampaignId,
                    ["campaignName"] = campaign.CampaignName,
                    ["currentBudget"] = currentBudget,
                    ["newBudget"] = newBudget,
                    ["budgetChangePercent"] = budgetReduction
                },
                RiskLevel = "Low"
            });
        }

        return previews;
    }

    private List<ProductActionPreview> GenerateIncreaseBudgetActions(
        ProductAiRecommendation rec, ProductTrendSummary trend, ProductProfile? product, List<ProductCampaignMapping> campaigns)
    {
        var previews = new List<ProductActionPreview>();

        if (campaigns.Count == 0)
        {
            previews.Add(new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "WatchOnly",
                Summary = "No linked campaigns to modify",
                Details = "This product has no linked campaigns. Manually adjust budgets in Advertising Console.",
                RiskLevel = "Low"
            });
            return previews;
        }

        var budgetIncrease = rec.SuggestedBudgetChangePercent ?? 15m;

        foreach (var campaign in campaigns.Where(c => c.IsActive))
        {
            var currentBudget = product?.DefaultDailyBudget ?? 50m;
            var newBudget = currentBudget * (1 + budgetIncrease / 100);

            previews.Add(new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "IncreaseBudget",
                Summary = $"Increase budget for '{campaign.CampaignName}'",
                Details = $"Campaign: {campaign.CampaignName} (ID: {campaign.CampaignId})\n" +
                         $"Current daily budget: ${currentBudget:F2}\n" +
                         $"Proposed daily budget: ${newBudget:F2}\n" +
                         $"Change: {budgetIncrease:+0.0;-0.0}%\n" +
                         $"Reason: {rec.Explanation}",
                Parameters = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign.CampaignId,
                    ["campaignName"] = campaign.CampaignName,
                    ["currentBudget"] = currentBudget,
                    ["newBudget"] = newBudget,
                    ["budgetChangePercent"] = budgetIncrease
                },
                RiskLevel = "Medium"
            });
        }

        return previews;
    }

    private List<ProductActionPreview> GeneratePauseOvernightActions(
        ProductAiRecommendation rec, ProductTrendSummary trend, List<ProductCampaignMapping> campaigns)
    {
        var previews = new List<ProductActionPreview>();

        if (campaigns.Count == 0)
        {
            previews.Add(new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "WatchOnly",
                Summary = "No linked campaigns to pause",
                Details = "Set up dayparting schedules manually in the Schedules page.",
                RiskLevel = "Low"
            });
            return previews;
        }

        previews.Add(new ProductActionPreview
        {
            RecommendationId = rec.Id,
            ActionType = "UpdateSchedule",
            Summary = "Create/update dayparting schedule to pause overnight",
            Details = $"This will create or update dayparting schedules for {campaigns.Count} linked campaign(s).\n" +
                     $"Campaigns: {string.Join(", ", campaigns.Select(c => c.CampaignName))}\n" +
                     $"Action: Enable campaigns during business hours (e.g., 8am-11pm), pause overnight.\n" +
                     $"You'll be able to edit the exact hours in the Schedules page.",
            Parameters = new Dictionary<string, object>
            {
                ["campaignIds"] = campaigns.Select(c => c.CampaignId).ToList(),
                ["proposedDayparting"] = "08:00-23:00"
            },
            RiskLevel = "Low"
        });

        return previews;
    }

    private List<ProductActionPreview> GenerateAdjustDaypartingActions(
        ProductAiRecommendation rec, ProductTrendSummary trend)
    {
        return new List<ProductActionPreview>
        {
            new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "UpdateSchedule",
                Summary = "Adjust dayparting schedule",
                Details = $"Proposed dayparting: {rec.SuggestedDayparting ?? "Custom hours"}\n" +
                         $"Reason: {rec.Explanation}\n" +
                         $"You can fine-tune the exact hours in the Schedules page before applying.",
                Parameters = new Dictionary<string, object>
                {
                    ["proposedDayparting"] = rec.SuggestedDayparting ?? "Custom"
                },
                RiskLevel = "Low"
            }
        };
    }

    private List<ProductActionPreview> GeneratePauseLowPerformerActions(
        ProductAiRecommendation rec, ProductTrendSummary trend, List<ProductCampaignMapping> campaigns)
    {
        var previews = new List<ProductActionPreview>();

        if (rec.SuggestedCampaignId.HasValue)
        {
            var campaign = campaigns.FirstOrDefault(c => c.CampaignId == rec.SuggestedCampaignId.Value);
            if (campaign is not null)
            {
                previews.Add(new ProductActionPreview
                {
                    RecommendationId = rec.Id,
                    ActionType = "PauseCampaign",
                    Summary = $"Pause campaign '{campaign.CampaignName}'",
                    Details = $"Campaign: {campaign.CampaignName} (ID: {campaign.CampaignId})\n" +
                             $"Reason: {rec.Explanation}\n" +
                             $"This is a destructive action. Consider reducing budget first or adjusting dayparting.",
                    Parameters = new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign.CampaignId,
                        ["campaignName"] = campaign.CampaignName
                    },
                    RiskLevel = "High"
                });
            }
        }
        else
        {
            previews.Add(new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "WatchOnly",
                Summary = "Review low-performing campaigns manually",
                Details = "No specific campaign identified. Review campaigns in the Campaigns page and decide which to pause.",
                RiskLevel = "Low"
            });
        }

        return previews;
    }

    private List<ProductActionPreview> GenerateIncreaseHighPerformerActions(
        ProductAiRecommendation rec, ProductTrendSummary trend, ProductProfile? product, List<ProductCampaignMapping> campaigns)
    {
        return GenerateIncreaseBudgetActions(rec, trend, product, campaigns);
    }

    private List<ProductActionPreview> GenerateNegativeKeywordActions(
        ProductAiRecommendation rec, ProductTrendSummary trend)
    {
        return new List<ProductActionPreview>
        {
            new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "OptimizeKeywords",
                Summary = "Add negative keywords or adjust targeting",
                Details = $"Suggested action: {rec.SuggestedAction}\n" +
                         $"Reason: {rec.Explanation}\n" +
                         $"Implement this manually in the Advertising Console to add negative keywords or targeting refinements.",
                Parameters = new Dictionary<string, object>
                {
                    ["suggestion"] = rec.SuggestedAction
                },
                RiskLevel = "Low"
            }
        };
    }

    private List<ProductActionPreview> GenerateMoveBudgetActions(
        ProductAiRecommendation rec, ProductTrendSummary trend, List<ProductCampaignMapping> campaigns)
    {
        var previews = new List<ProductActionPreview>();

        if (campaigns.Count < 2)
        {
            previews.Add(new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "WatchOnly",
                Summary = "Not enough campaigns to move budget",
                Details = "This product needs at least 2 linked campaigns to move budget between them.",
                RiskLevel = "Low"
            });
        }
        else
        {
            previews.Add(new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "ReduceBudget",
                Summary = $"Reduce budget on lower-performing campaign(s)",
                Details = $"Linked campaigns: {string.Join(", ", campaigns.Select(c => c.CampaignName))}\n" +
                         $"Consider reducing budget on underperformers and increasing on high-performers.\n" +
                         $"Review each campaign's ACOS in the Campaigns page first.",
                Parameters = new Dictionary<string, object>
                {
                    ["campaignCount"] = campaigns.Count,
                    ["campaigns"] = campaigns.Select(c => c.CampaignName).ToList()
                },
                RiskLevel = "Medium"
            });
        }

        return previews;
    }

    private List<ProductActionPreview> GenerateWatchOnlyActions(
        ProductAiRecommendation rec, ProductTrendSummary trend)
    {
        return new List<ProductActionPreview>
        {
            new ProductActionPreview
            {
                RecommendationId = rec.Id,
                ActionType = "WatchOnly",
                Summary = "Monitor product performance",
                Details = $"Recommendation: {rec.Explanation}\n" +
                         $"Action: Continue monitoring this product's metrics.\n" +
                         $"Check back in 7 days to see if trends change.",
                Parameters = new Dictionary<string, object>(),
                RiskLevel = "Low"
            }
        };
    }

    private ProductActionPreview GenerateNoActionPreview(ProductAiRecommendation rec)
    {
        return new ProductActionPreview
        {
            RecommendationId = rec.Id,
            ActionType = "NoAction",
            Summary = "No action available",
            Details = "This recommendation does not map to a specific action.",
            RiskLevel = "Low"
        };
    }
}
