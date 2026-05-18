using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class RecommendationApplyService
{
    private readonly AdMetricsRepository _metrics;
    private readonly ProductAnalyticsRepository _products;
    private readonly AmazonAccountResolver _accounts;
    private readonly AmazonCampaignService _campaigns;
    private readonly RecommendationExperimentService _experiments;
    private readonly IAiClient _ai;
    private readonly IConfiguration _config;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public RecommendationApplyService(
        AdMetricsRepository metrics,
        ProductAnalyticsRepository products,
        AmazonAccountResolver accounts,
        AmazonCampaignService campaigns,
        RecommendationExperimentService experiments,
        IAiClient ai,
        IConfiguration config)
    {
        _metrics = metrics;
        _products = products;
        _accounts = accounts;
        _campaigns = campaigns;
        _experiments = experiments;
        _ai = ai;
        _config = config;
    }

    public async Task<RecommendationReviewDto> BuildReviewAsync(string accountKey, string productId, string recommendationId)
    {
        var rec = GetRecommendation(accountKey, productId, recommendationId);
        if (!string.Equals(rec.Status, "Applied", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rec.Status, "Ignored", StringComparison.OrdinalIgnoreCase))
        {
            rec.Status = "Review";
            _metrics.UpsertRecommendation(rec);
        }

        var product = _products.GetProduct(productId);
        var performanceRows = _metrics.GetDailyMetrics(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd);
        var performance = BuildPerformance(performanceRows, rec.SourceDateRangeStart, rec.SourceDateRangeEnd);
        var setup = await BuildCurrentSetupAsync(accountKey, rec, performanceRows);
        var proposed = BuildProposedChange(rec, setup, performanceRows);
        var evidence = _metrics.GetEvidence(rec.RecommendationId).Select(AnalyticsMappers.ToDto).ToList();
        var warnings = new List<string>();

        if (!proposed.CanApplyAutomatically)
            warnings.Add(proposed.ManualActionReason);
        if (performanceRows.Count == 0)
            warnings.Add("No stored reporting rows are available for this product/date range. Review carefully before applying.");
        if (proposed.IsDestructive)
            warnings.Add("This change can reduce or stop ad delivery. You must confirm before applying.");

        return new RecommendationReviewDto
        {
            Recommendation = AnalyticsMappers.ToDto(rec),
            Product = product,
            CurrentSetup = setup,
            ProposedChange = proposed,
            Performance = performance,
            Evidence = evidence,
            Warnings = warnings.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList(),
            DataQualityLabel = performanceRows.Any() ? "Good" : "Limited",
            AiModel = _config["OpenAI:Model"] ?? ""
        };
    }

    public async Task<ApplyRecommendationResult> ApplyAsync(string recommendationId, ApplyRecommendationRequest request)
    {
        var rec = GetRecommendation(request.AccountKey, request.ProductId, recommendationId);
        var review = await BuildReviewAsync(request.AccountKey, request.ProductId, recommendationId);
        var proposed = request.ProposedChange;
        var validation = Validate(proposed, request.ConfirmDestructive);
        if (validation is not null)
            return ApplyFailed(rec, request, review, validation);

        if (!proposed.CanApplyAutomatically)
            return ApplyFailed(rec, request, review, proposed.ManualActionReason);

        var account = _accounts.Resolve(request.AccountKey)
            ?? throw new InvalidOperationException($"Account '{request.AccountKey}' not found.");

        var approvedAt = DateTimeOffset.UtcNow;
        rec.Status = "Applying";
        rec.ApprovedAt ??= approvedAt;
        _metrics.UpsertRecommendation(rec);

        (bool success, string response, string apiRequest) apiResult;
        try
        {
            apiResult = proposed.ActionType switch
            {
                "PauseCampaign" => await ApplyCampaignState(account, rec.CampaignId, "paused"),
                "EnableCampaign" => await ApplyCampaignState(account, rec.CampaignId, "enabled"),
                "UpdateCampaignBudget" => await _campaigns.UpdateCampaignBudgetAsync(account, rec.CampaignId ?? "", proposed.BudgetAmount ?? 0),
                "UpdateTargetBid" => await _campaigns.UpdateTargetBidAsync(account, proposed.TargetId ?? review.CurrentSetup.TargetId ?? "", proposed.FinalBid ?? 0),
                "UpdateKeywordBid" => await _campaigns.UpdateKeywordBidAsync(account, proposed.KeywordId ?? review.CurrentSetup.KeywordId ?? "", proposed.FinalBid ?? 0),
                "AddNegativeKeyword" => await _campaigns.AddNegativeKeywordAsync(
                    account,
                    rec.CampaignId ?? "",
                    review.CurrentSetup.AdGroupId ?? "",
                    proposed.NegativeKeywordText ?? "",
                    proposed.NegativeKeywordMatchType ?? "NEGATIVE_EXACT"),
                _ => (false, $"Automatic apply is not supported for action type '{proposed.ActionType}'.", "")
            };
        }
        catch (Exception ex)
        {
            apiResult = (false, ex.Message, "");
        }

        if (!apiResult.success)
            return ApplyFailed(rec, request, review, apiResult.response, apiResult.apiRequest);

        var appliedAt = DateTimeOffset.UtcNow;
        rec.Status = "Applied";
        rec.AppliedAt = appliedAt;
        rec.RecommendedState = JsonSerializer.Serialize(proposed, JsonOptions);
        rec.CurrentState = JsonSerializer.Serialize(review.CurrentSetup, JsonOptions);
        _metrics.UpsertRecommendation(rec);

        var experiment = _experiments.CompareAndSave(rec);
        var after = await TryBuildCurrentSetupAsync(request.AccountKey, rec);
        var record = new RecommendationApplyRecord
        {
            RecommendationId = rec.RecommendationId,
            AccountKey = rec.AccountKey,
            ProductId = rec.ProductId,
            Status = "Applied",
            ApprovedAt = rec.ApprovedAt,
            AppliedAt = rec.AppliedAt,
            BeforeSnapshotJson = JsonSerializer.Serialize(review.CurrentSetup, JsonOptions),
            ProposedChangeJson = JsonSerializer.Serialize(review.ProposedChange, JsonOptions),
            FinalAppliedChangeJson = JsonSerializer.Serialize(proposed, JsonOptions),
            AfterSnapshotJson = JsonSerializer.Serialize(after ?? review.CurrentSetup, JsonOptions),
            AmazonApiRequestJson = apiResult.apiRequest,
            AmazonApiResponseJson = apiResult.response,
            UserEditedChangeJson = JsonSerializer.Serialize(proposed, JsonOptions),
            UserApprovalNotes = request.UserNotes,
            ExperimentId = experiment.ExperimentId,
            DataQualityLabel = review.DataQualityLabel
        };
        _metrics.UpsertRecommendationApplyRecord(record);

        return new ApplyRecommendationResult
        {
            Success = true,
            Status = "Applied",
            Message = "Changes were applied to Amazon Ads successfully. Before/after tracking has started.",
            ApprovedAt = rec.ApprovedAt,
            AppliedAt = appliedAt,
            AmazonApiRequestJson = apiResult.apiRequest,
            AmazonApiResponseJson = apiResult.response,
            Experiment = AnalyticsMappers.ToDto(experiment)
        };
    }

    public async Task<RecommendationAiAnswerDto> AskAsync(string recommendationId, RecommendationAiQuestionRequest request)
    {
        try
        {
            var review = await BuildReviewAsync(request.AccountKey, request.ProductId, recommendationId);
            if (request.ProposedChange is not null)
                review.ProposedChange = request.ProposedChange;

            var system = request.BeginnerChineseMode
                ? "You explain Amazon Ads recommendations in simple Chinese for a complete beginner. Keep it short, practical, and non-redundant. Do not list definitions for every technical term. Only explain a technical term briefly if it is necessary to the action."
                : "You are an Amazon Ads expert. Answer only about the selected recommendation and the provided context. Be practical, concise, and do not invent unavailable data.";

            var user = request.BeginnerChineseMode
                ? BuildChinesePrompt(review, request.Question, request.History)
                : BuildChatPrompt(review, request.Question, request.History);

            var answer = await _ai.CompleteAsync(system, user);
            return new RecommendationAiAnswerDto { Success = true, Answer = answer };
        }
        catch (Exception ex)
        {
            return new RecommendationAiAnswerDto
            {
                Success = false,
                Error = ex.Message,
                Answer = ""
            };
        }
    }

    private AiRecommendation GetRecommendation(string accountKey, string productId, string recommendationId)
    {
        var rec = _metrics.GetRecommendation(recommendationId)
            ?? throw new InvalidOperationException("Recommendation not found.");
        if (!string.Equals(rec.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rec.ProductId, productId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recommendation does not belong to this account/product.");
        return rec;
    }

    private async Task<RecommendationSetupDto> BuildCurrentSetupAsync(string accountKey, AiRecommendation rec, IReadOnlyList<AdPerformanceDaily> rows)
    {
        var setup = SetupFromRows(rec, rows);
        var live = await TryLiveCampaignAsync(accountKey, rec.CampaignId);
        if (live is not null)
        {
            setup.CampaignName = live.Name;
            setup.CampaignId = live.CampaignId;
            setup.CampaignStatus = live.State;
            setup.DailyBudget = live.DailyBudget;
            setup.BudgetType = live.BudgetType;
            setup.StartDate = live.StartDate;
            setup.EndDate = live.EndDate;
            setup.ServingStatus = live.ServingStatus;
            setup.DataSource = "Live Amazon Ads campaign API plus stored reporting metrics";
        }

        await EnrichTargetOrKeywordAsync(accountKey, setup, rows);
        return setup;
    }

    private async Task EnrichTargetOrKeywordAsync(string accountKey, RecommendationSetupDto setup, IReadOnlyList<AdPerformanceDaily> rows)
    {
        if (string.IsNullOrWhiteSpace(setup.CampaignId) || string.IsNullOrWhiteSpace(setup.TargetOrSearchTerm))
            return;

        try
        {
            var account = _accounts.Resolve(accountKey);
            if (account is null) return;

            var sourceType = rows.FirstOrDefault(r =>
                string.Equals(r.CampaignId, setup.CampaignId, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(r.TargetingText, setup.TargetOrSearchTerm, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(r.SearchTerm, setup.TargetOrSearchTerm, StringComparison.OrdinalIgnoreCase)))?.SourceReportType ?? "";

            var shouldTryTarget = !sourceType.Equals("Keyword", StringComparison.OrdinalIgnoreCase) &&
                                  !sourceType.Equals("SearchTerm", StringComparison.OrdinalIgnoreCase);
            if (shouldTryTarget)
            {
                var target = await _campaigns.FindTargetAsync(account, setup.CampaignId, setup.AdGroupId, setup.TargetOrSearchTerm);
                if (target is not null)
                {
                    setup.TargetId = target.TargetId;
                    setup.CurrentBid = target.Bid;
                    setup.TargetStatus = target.State;
                    setup.TargetOrSearchTerm = string.IsNullOrWhiteSpace(target.ExpressionText) ? setup.TargetOrSearchTerm : target.ExpressionText;
                    setup.DataSource += " + live target lookup";
                    return;
                }
            }

            var keyword = await _campaigns.FindKeywordAsync(account, setup.CampaignId, setup.AdGroupId, setup.TargetOrSearchTerm, setup.MatchType);
            if (keyword is not null)
            {
                setup.KeywordId = keyword.KeywordId;
                setup.CurrentBid = keyword.Bid;
                setup.TargetStatus = keyword.State;
                setup.MatchType = string.IsNullOrWhiteSpace(keyword.MatchType) ? setup.MatchType : keyword.MatchType;
                setup.DataSource += " + live keyword lookup";
            }
        }
        catch
        {
            // Keep the review usable even when the optional live target/keyword lookup fails.
        }
    }

    private async Task<RecommendationSetupDto?> TryBuildCurrentSetupAsync(string accountKey, AiRecommendation rec)
    {
        var rows = _metrics.GetDailyMetrics(accountKey, rec.ProductId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd);
        return await BuildCurrentSetupAsync(accountKey, rec, rows);
    }

    private async Task<CampaignDto?> TryLiveCampaignAsync(string accountKey, string? campaignId)
    {
        if (string.IsNullOrWhiteSpace(campaignId)) return null;
        try
        {
            var account = _accounts.Resolve(accountKey);
            if (account is null) return null;
            return (await _campaigns.ListCampaignsAsync(account))
                .FirstOrDefault(c => string.Equals(c.CampaignId, campaignId, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static RecommendationSetupDto SetupFromRows(AiRecommendation rec, IReadOnlyList<AdPerformanceDaily> rows)
    {
        var best = rows
            .Where(r => string.IsNullOrWhiteSpace(rec.CampaignId) || string.Equals(r.CampaignId, rec.CampaignId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Spend)
            .FirstOrDefault() ?? rows.OrderByDescending(r => r.Spend).FirstOrDefault();

        return new RecommendationSetupDto
        {
            CampaignId = rec.CampaignId ?? best?.CampaignId ?? "",
            CampaignName = best?.CampaignName ?? "",
            AdGroupId = rec.AdGroupId ?? best?.AdGroupId,
            AdGroupName = best?.AdGroupName,
            TargetOrSearchTerm = !string.IsNullOrWhiteSpace(best?.SearchTerm) ? best.SearchTerm : best?.TargetingText,
            MatchType = best?.MatchType,
            TargetingType = best?.TargetingType,
            CampaignStatus = "Unknown",
            DataSource = best is null ? "Recommendation only; no stored metric row found" : "Stored Amazon Ads reporting metrics"
        };
    }

    private static RecommendationPerformanceSummaryDto BuildPerformance(IReadOnlyList<AdPerformanceDaily> rows, DateOnly start, DateOnly end)
    {
        var spend = rows.Sum(r => r.Spend);
        var sales = rows.Sum(r => r.Sales);
        var clicks = rows.Sum(r => r.Clicks);
        var impressions = rows.Sum(r => r.Impressions);
        var purchases = rows.Sum(r => r.Purchases);
        return new RecommendationPerformanceSummaryDto
        {
            DateRangeStart = start,
            DateRangeEnd = end,
            Spend = decimal.Round(spend, 2),
            Sales = decimal.Round(sales, 2),
            Orders = purchases,
            Clicks = clicks,
            Impressions = impressions,
            ACOS = sales > 0 ? decimal.Round(spend / sales, 4) : null,
            ROAS = spend > 0 ? decimal.Round(sales / spend, 2) : null,
            CPC = clicks > 0 ? decimal.Round(spend / clicks, 2) : null,
            CTR = impressions > 0 ? decimal.Round((decimal)clicks / impressions, 4) : null,
            CVR = clicks > 0 ? decimal.Round((decimal)purchases / clicks, 4) : null,
            WastedSpend = decimal.Round(rows.Where(r => r.Spend > 0 && r.Sales <= 0).Sum(r => r.Spend), 2),
            DaysWithSpendNoSales = rows.Where(r => r.Spend > 0 && r.Sales <= 0).Select(r => r.Date).Distinct().Count()
        };
    }

    private RecommendationProposedChangeDto BuildProposedChange(AiRecommendation rec, RecommendationSetupDto setup, IReadOnlyList<AdPerformanceDaily> rows)
    {
        var text = $"{rec.RecommendationType} {rec.Title} {rec.RecommendedState}".ToLowerInvariant();
        if (text.Contains("pause campaign") || text.Contains("turn off campaign"))
        {
            return new RecommendationProposedChangeDto
            {
                ActionType = "PauseCampaign",
                FieldName = "Campaign status",
                CurrentValue = setup.CampaignStatus ?? "Unknown",
                ProposedValue = "Paused",
                CampaignStatus = "paused",
                Explanation = rec.Reason,
                RiskLevel = "High",
                IsDestructive = true,
                CanApplyAutomatically = !string.IsNullOrWhiteSpace(rec.CampaignId),
                ManualActionReason = string.IsNullOrWhiteSpace(rec.CampaignId) ? "Campaign ID is missing, so the app cannot pause this campaign automatically." : ""
            };
        }

        if (text.Contains("enable campaign") || text.Contains("resume campaign"))
        {
            return new RecommendationProposedChangeDto
            {
                ActionType = "EnableCampaign",
                FieldName = "Campaign status",
                CurrentValue = setup.CampaignStatus ?? "Unknown",
                ProposedValue = "Enabled",
                CampaignStatus = "enabled",
                Explanation = rec.Reason,
                RiskLevel = "Medium",
                CanApplyAutomatically = !string.IsNullOrWhiteSpace(rec.CampaignId),
                ManualActionReason = string.IsNullOrWhiteSpace(rec.CampaignId) ? "Campaign ID is missing, so the app cannot enable this campaign automatically." : ""
            };
        }

        if (rec.RecommendationType.Contains("Negative", StringComparison.OrdinalIgnoreCase))
        {
            var source = rows
                .Where(r => string.Equals(r.SourceReportType, "SearchTerm", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.SearchTerm))
                .OrderByDescending(r => r.Spend)
                .FirstOrDefault();
            return new RecommendationProposedChangeDto
            {
                ActionType = "AddNegativeKeyword",
                FieldName = "Negative keyword",
                CurrentValue = "Not added",
                ProposedValue = source?.SearchTerm ?? setup.TargetOrSearchTerm ?? "",
                NegativeKeywordText = source?.SearchTerm ?? setup.TargetOrSearchTerm,
                NegativeKeywordMatchType = "NEGATIVE_EXACT",
                Explanation = rec.Reason,
                RiskLevel = "Medium",
                IsDestructive = true,
                CanApplyAutomatically = source is not null && !string.IsNullOrWhiteSpace(source.AdGroupId),
                ManualActionReason = source is null
                    ? "This recommendation does not have a Search Term report row, so the app cannot safely create a negative keyword automatically."
                    : string.IsNullOrWhiteSpace(source.AdGroupId)
                        ? "The search term row does not include an ad group ID, so the app cannot safely create a negative keyword automatically."
                        : ""
            };
        }

        if (rec.RecommendationType.Contains("Bid", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bid") ||
            text.Contains("lower bids") ||
            text.Contains("increase bids"))
        {
            var bidChange = IsBidIncreaseText(text) ? 15m : -20m;
            var currentBid = setup.CurrentBid;
            decimal? finalBid = currentBid is > 0
                ? decimal.Round(Math.Max(0.02m, currentBid.Value * (1 + bidChange / 100m)), 2)
                : null;
            var isTarget = !string.IsNullOrWhiteSpace(setup.TargetId);
            var isKeyword = !string.IsNullOrWhiteSpace(setup.KeywordId);
            var actionType = isTarget ? "UpdateTargetBid" : isKeyword ? "UpdateKeywordBid" : "UnsupportedAction";
            var objectLabel = isTarget ? "target" : isKeyword ? "keyword" : "target/keyword";

            return new RecommendationProposedChangeDto
            {
                ActionType = actionType,
                FieldName = $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(objectLabel)} bid",
                CurrentValue = currentBid.HasValue ? currentBid.Value.ToString("C", CultureInfo.GetCultureInfo("en-US")) : "Live bid not found",
                ProposedValue = finalBid.HasValue ? finalBid.Value.ToString("C", CultureInfo.GetCultureInfo("en-US")) : rec.RecommendedState,
                TargetId = setup.TargetId,
                KeywordId = setup.KeywordId,
                BidChangePercent = bidChange,
                FinalBid = finalBid,
                Explanation = rec.Reason,
                RiskLevel = bidChange < 0 ? "Medium" : "Low",
                CanApplyAutomatically = finalBid.HasValue && (isTarget || isKeyword),
                ManualActionReason = finalBid.HasValue && (isTarget || isKeyword)
                    ? ""
                    : "Amazon did not return a matching live target/keyword ID and bid for this recommendation, so the app cannot safely apply this bid change automatically."
            };
        }

        if (rec.RecommendationType.Contains("Budget", StringComparison.OrdinalIgnoreCase) && setup.DailyBudget is > 0)
        {
            var multiplier = text.Contains("reduce") || text.Contains("lower") ? 0.9m : 1.1m;
            var proposedBudget = decimal.Round(setup.DailyBudget.Value * multiplier, 2);
            return new RecommendationProposedChangeDto
            {
                ActionType = "UpdateCampaignBudget",
                FieldName = "Daily budget",
                CurrentValue = setup.DailyBudget.Value.ToString("C", CultureInfo.GetCultureInfo("en-US")),
                ProposedValue = proposedBudget.ToString("C", CultureInfo.GetCultureInfo("en-US")),
                BudgetAmount = proposedBudget,
                Explanation = rec.Reason,
                RiskLevel = proposedBudget < setup.DailyBudget.Value ? "Medium" : "Low",
                CanApplyAutomatically = !string.IsNullOrWhiteSpace(rec.CampaignId),
                ManualActionReason = string.IsNullOrWhiteSpace(rec.CampaignId) ? "Campaign ID is missing, so the app cannot update campaign budget automatically." : ""
            };
        }

        var unsupportedReason = rec.RecommendationType.Contains("CampaignStructure", StringComparison.OrdinalIgnoreCase)
            ? "Campaign structure changes require creating or moving Amazon Ads objects. This app will not auto-apply that until the exact source and destination objects are available."
            : "This recommendation cannot be applied automatically yet because the required live Amazon Ads object IDs are not available.";

        return new RecommendationProposedChangeDto
        {
            ActionType = "UnsupportedAction",
            FieldName = "Suggested action",
            CurrentValue = setup.TargetOrSearchTerm ?? setup.CampaignName,
            ProposedValue = rec.RecommendedState,
            Explanation = rec.Reason,
            RiskLevel = "Medium",
            CanApplyAutomatically = false,
            ManualActionReason = unsupportedReason
        };
    }

    private static bool IsBidIncreaseText(string text) =>
        text.Contains("increase", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("raise", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("scale", StringComparison.OrdinalIgnoreCase);

    private static string? Validate(RecommendationProposedChangeDto change, bool confirmDestructive)
    {
        if (change.IsDestructive && !confirmDestructive)
            return "This change can pause or restrict ad delivery. Check the confirmation box before applying.";
        if (change.ActionType == "UpdateCampaignBudget" && (change.BudgetAmount is null or <= 0))
            return "Budget must be greater than 0.";
        if (change.FinalBid is <= 0)
            return "Final bid must be greater than 0.";
        if (Math.Abs(change.BidChangePercent ?? 0) > 60)
            return "Bid change is very large. Reduce it below 60% or apply manually.";
        if (change.ActionType == "AddNegativeKeyword" && string.IsNullOrWhiteSpace(change.NegativeKeywordText))
            return "Negative keyword text cannot be empty.";
        if ((change.ActionType == "PauseCampaign" || change.ActionType == "EnableCampaign") &&
            !new[] { "paused", "enabled" }.Contains((change.CampaignStatus ?? "").ToLowerInvariant()))
            return "Campaign status must be enabled or paused.";
        return null;
    }

    private async Task<(bool success, string response, string requestJson)> ApplyCampaignState(AmazonAccountConfig account, string? campaignId, string state)
    {
        if (string.IsNullOrWhiteSpace(campaignId))
            return (false, "Campaign ID is missing.", "");
        var (verified, error) = await _campaigns.UpdateCampaignStateAsync(account, campaignId, state);
        var requestJson = JsonSerializer.Serialize(new { campaigns = new[] { new { campaignId, state } } }, JsonOptions);
        return verified is not null
            ? (true, JsonSerializer.Serialize(new { verifiedState = verified }, JsonOptions), requestJson)
            : (false, error ?? "Amazon rejected the campaign status update.", requestJson);
    }

    private ApplyRecommendationResult ApplyFailed(
        AiRecommendation rec,
        ApplyRecommendationRequest request,
        RecommendationReviewDto review,
        string error,
        string apiRequest = "")
    {
        var failedAt = DateTimeOffset.UtcNow;
        rec.Status = "ApplyFailed";
        rec.ApprovedAt ??= failedAt;
        _metrics.UpsertRecommendation(rec);
        _metrics.UpsertRecommendationApplyRecord(new RecommendationApplyRecord
        {
            RecommendationId = rec.RecommendationId,
            AccountKey = rec.AccountKey,
            ProductId = rec.ProductId,
            Status = "ApplyFailed",
            ApprovedAt = rec.ApprovedAt,
            ApplyFailedAt = failedAt,
            ApplyErrorMessage = error,
            BeforeSnapshotJson = JsonSerializer.Serialize(review.CurrentSetup, JsonOptions),
            ProposedChangeJson = JsonSerializer.Serialize(review.ProposedChange, JsonOptions),
            FinalAppliedChangeJson = "",
            AfterSnapshotJson = "",
            AmazonApiRequestJson = apiRequest,
            AmazonApiResponseJson = error,
            UserEditedChangeJson = JsonSerializer.Serialize(request.ProposedChange, JsonOptions),
            UserApprovalNotes = request.UserNotes,
            DataQualityLabel = review.DataQualityLabel
        });

        return new ApplyRecommendationResult
        {
            Success = false,
            Status = "ApplyFailed",
            Message = "Amazon Ads changes were not applied.",
            ApprovedAt = rec.ApprovedAt,
            Error = error,
            AmazonApiRequestJson = apiRequest,
            AmazonApiResponseJson = error
        };
    }

    private static string BuildChatPrompt(RecommendationReviewDto review, string question, IReadOnlyList<RecommendationChatMessageDto> history) =>
        $"""
        User question: {question}

        Prior conversation in this recommendation chat:
        {JsonSerializer.Serialize(history, JsonOptions)}

        Selected recommendation context:
        {JsonSerializer.Serialize(review, JsonOptions)}

        Answer the user's question about this recommendation only. Explain what could happen if the user does nothing, risk, money affected, and what to watch after applying when relevant.
        """;

    private static string BuildChinesePrompt(RecommendationReviewDto review, string question, IReadOnlyList<RecommendationChatMessageDto> history)
    {
        var userQuestion = string.IsNullOrWhiteSpace(question)
            ? "请用傻瓜模式解释这个建议。"
            : question;
        return $"""
        用户问题：{userQuestion}

        之前这条建议里的对话：
        {JsonSerializer.Serialize(history, JsonOptions)}

        Recommendation context:
        {JsonSerializer.Serialize(review, JsonOptions)}

        请用非常简单的中文回答。不要长篇大论，不要逐个解释 ROAS/ACOS/CPC/CTR/CVR 的定义，除非用户专门问。
        默认控制在 6 句话以内，用下面格式：

        1. 一句话总结：
        一句话告诉用户要做什么。

        2. 为什么：
        用生活化语言说哪里不划算或哪里有机会。

        3. 我该怎么做：
        给出 1-3 个具体步骤。

        4. 风险和观察：
        简短说风险高不高，改完看什么。
        """;
    }
}
