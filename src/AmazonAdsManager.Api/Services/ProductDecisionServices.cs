using AmazonAdsManager.Shared.Models;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class ProductRecommendationDecisionService
{
    private readonly ProductAiRecommendationRepository _recommendations;
    private readonly ProductTrainingExampleRepository _training;

    public ProductRecommendationDecisionService(
        ProductAiRecommendationRepository recommendations,
        ProductTrainingExampleRepository training)
    {
        _recommendations = recommendations;
        _training = training;
    }

    public void Approve(string recommendationId)
    {
        var rec = _recommendations.GetById(recommendationId);
        if (rec is null) return;

        rec.Status = "Approved";
        _recommendations.Upsert(rec);

        SaveTrainingExample(rec, "Approved", null);
    }

    public void Ignore(string recommendationId)
    {
        var rec = _recommendations.GetById(recommendationId);
        if (rec is null) return;

        rec.Status = "Ignored";
        _recommendations.Upsert(rec);

        SaveTrainingExample(rec, "Ignored", null);
    }

    public void Edit(string recommendationId, string editedAction)
    {
        var rec = _recommendations.GetById(recommendationId);
        if (rec is null) return;

        rec.Status = "Edited";
        _recommendations.Upsert(rec);

        SaveTrainingExample(rec, "Edited", editedAction);
    }

    private void SaveTrainingExample(ProductAiRecommendation rec, string decision, string? editedAction)
    {
        var example = new ProductTrainingExample
        {
            AccountKey = rec.AccountKey,
            ProductId = rec.ProductId,
            InputJson = rec.OriginalInputJson,
            RecommendationJson = JsonSerializer.Serialize(rec),
            Decision = decision,
            EditedAction = editedAction
        };
        _training.Upsert(example);
    }
}

public class ProductTrainingDataExportService
{
    private readonly ProductTrainingExampleRepository _training;

    public ProductTrainingDataExportService(ProductTrainingExampleRepository training)
    {
        _training = training;
    }

    public string ExportProductTrainingDataAsJsonL(string accountKey, string productId)
    {
        var examples = _training.GetByProduct(accountKey, productId);
        var lines = examples.Select(e => JsonSerializer.Serialize(new
        {
            e.InputJson,
            e.RecommendationJson,
            e.Decision,
            e.EditedAction,
            e.Outcome,
            e.CreatedAt
        }));

        return string.Join("\n", lines);
    }

    public List<ProductTrainingExample> ExportProductTrainingData(string accountKey, string productId) =>
        _training.GetByProduct(accountKey, productId).ToList();
}
