using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.Json;

namespace AmazonAdsManager.Api.Functions;

public class AuthFunction
{
    private readonly OAuthService _oauth;
    private readonly AmazonAccountResolver _resolver;
    private readonly ApiAccessService _access;

    public AuthFunction(OAuthService oauth, AmazonAccountResolver resolver, ApiAccessService access)
    {
        _oauth = oauth;
        _resolver = resolver;
        _access = access;
    }

    [Function("AuthLoginUrl")]
    public IActionResult GetLoginUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/login-url")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var redirectUri = BuildRedirectUri(req);
        var (loginUrl, state) = _oauth.GetLoginUrl(redirectUri);
        return new OkObjectResult(ApiResult<OAuthLoginUrlResponse>.Ok(new OAuthLoginUrlResponse
        {
            LoginUrl = loginUrl,
            State = state
        }));
    }

    [Function("AuthCallback")]
    public async Task<IActionResult> Callback(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/callback")] HttpRequest req)
    {
        var code = req.Query["code"].ToString();
        var state = req.Query["state"].ToString();
        var error = req.Query["error"].ToString();

        if (!string.IsNullOrEmpty(error))
        {
            return new ContentResult
            {
                ContentType = "text/html",
                StatusCode = 200,
                Content = CallbackHtml("Amazon login failed", $"Error: {error}", isError: true)
            };
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return new ContentResult
            {
                ContentType = "text/html",
                StatusCode = 200,
                Content = CallbackHtml("Invalid callback", "Missing code or state parameter.", isError: true)
            };
        }

        var redirectUri = BuildRedirectUri(req);
        await _oauth.HandleCallbackAsync(code, state, redirectUri);

        return new ContentResult
        {
            ContentType = "text/html",
            StatusCode = 200,
            Content = CallbackHtml("Amazon login successful!", "You can close this window and return to the app.", isError: false)
        };
    }

    [Function("AuthPending")]
    public IActionResult GetPending(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/pending")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var state = req.Query["state"].ToString();
        if (string.IsNullOrWhiteSpace(state))
            return new BadRequestObjectResult(ApiResult.Fail("state is required"));

        var result = _oauth.GetPending(state);
        return new OkObjectResult(ApiResult<OAuthPendingResult>.Ok(result));
    }

    [Function("AuthSaveAccount")]
    public async Task<IActionResult> SaveAccount(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/save-account")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        SaveAccountRequest? saveReq;
        try
        {
            saveReq = await JsonSerializer.DeserializeAsync<SaveAccountRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return new BadRequestObjectResult(ApiResult.Fail("Invalid request body"));
        }

        if (saveReq is null || string.IsNullOrWhiteSpace(saveReq.State) ||
            string.IsNullOrWhiteSpace(saveReq.AccountKey) || string.IsNullOrWhiteSpace(saveReq.ProfileId))
            return new BadRequestObjectResult(ApiResult.Fail("state, accountKey, and profileId are required"));

        var account = _oauth.BuildAccount(saveReq.State, saveReq.AccountKey, saveReq.DisplayName, saveReq.ProfileId);
        if (account is null)
            return new BadRequestObjectResult(ApiResult.Fail("OAuth session not found or expired. Please reconnect."));

        _resolver.AddAccount(account);

        return new OkObjectResult(ApiResult<SafeAmazonAccountDto>.Ok(new SafeAmazonAccountDto
        {
            AccountKey = account.AccountKey,
            DisplayName = account.DisplayName
        }));
    }

    [Function("AuthResolveProfiles")]
    public async Task<IActionResult> ResolveProfiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/resolve-profiles")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var account = _resolver.Resolve(accountKey);
        if (account is null)
            return new NotFoundObjectResult(ApiResult.Fail($"Account '{accountKey}' not found"));

        try
        {
            var profiles = await _oauth.ResolveProfilesFromRefreshTokenAsync(account.RefreshToken);
            return new OkObjectResult(ApiResult<List<AmazonAdsProfile>>.Ok(profiles));
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }

    [Function("AuthUpdateProfile")]
    public async Task<IActionResult> UpdateProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/update-profile")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        UpdateProfileRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<UpdateProfileRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return new BadRequestObjectResult(ApiResult.Fail("Invalid request body"));
        }

        if (body is null || string.IsNullOrWhiteSpace(body.AccountKey) || string.IsNullOrWhiteSpace(body.ProfileId))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey and profileId are required"));

        var account = _resolver.Resolve(body.AccountKey);
        if (account is null)
            return new NotFoundObjectResult(ApiResult.Fail($"Account '{body.AccountKey}' not found"));

        _resolver.UpdateProfileId(body.AccountKey, body.ProfileId);
        return new OkObjectResult(ApiResult.Ok());
    }

    private static string BuildRedirectUri(HttpRequest req)
    {
        var host = req.Host.ToUriComponent();
        var scheme = req.Scheme;
        return $"{scheme}://{host}/api/auth/callback";
    }

    private static string CallbackHtml(string title, string message, bool isError)
    {
        var color = isError ? "#dc3545" : "#198754";
        var icon = isError ? "✗" : "✓";
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + title + "</title>" +
               "<style>body{font-family:system-ui,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#f8f9fa}" +
               ".box{text-align:center;padding:2rem;background:white;border-radius:8px;box-shadow:0 2px 8px rgba(0,0,0,.12);max-width:400px}" +
               "h2{color:" + color + "}p{color:#6c757d}</style></head><body>" +
               "<div class=\"box\"><h2>" + icon + " " + title + "</h2><p>" + message + "</p>" +
               "<script>setTimeout(()=>window.close(),2000);</script></div></body></html>";
    }
}
