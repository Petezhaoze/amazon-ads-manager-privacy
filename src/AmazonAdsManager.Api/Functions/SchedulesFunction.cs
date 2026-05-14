using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.Json;

namespace AmazonAdsManager.Api.Functions;

public class SchedulesFunction
{
    private readonly ScheduleRepository _repo;
    private readonly ApiAccessService _access;

    public SchedulesFunction(ScheduleRepository repo, ApiAccessService access)
    {
        _repo = repo;
        _access = access;
    }

    [Function("GetSchedules")]
    public IActionResult Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "schedules")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        var schedules = string.IsNullOrWhiteSpace(accountKey)
            ? _repo.GetAll()
            : _repo.GetByAccount(accountKey);
        return new OkObjectResult(ApiResult<IReadOnlyList<CampaignSchedule>>.Ok(schedules));
    }

    [Function("UpsertSchedule")]
    public async Task<IActionResult> Upsert(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "schedules")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        CampaignSchedule? schedule;
        try
        {
            schedule = await JsonSerializer.DeserializeAsync<CampaignSchedule>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return new BadRequestObjectResult(ApiResult.Fail("Invalid JSON body"));
        }

        if (schedule is null || string.IsNullOrWhiteSpace(schedule.AccountKey) || string.IsNullOrWhiteSpace(schedule.CampaignId))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey and campaignId are required"));

        var saved = _repo.Upsert(schedule);
        return new OkObjectResult(ApiResult<CampaignSchedule>.Ok(saved));
    }

    [Function("DeleteSchedule")]
    public IActionResult Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "schedules/{id}")] HttpRequest req,
        string id)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        return _repo.Delete(id)
            ? new OkObjectResult(ApiResult.Ok())
            : new NotFoundObjectResult(ApiResult.Fail("Schedule not found"));
    }
}
