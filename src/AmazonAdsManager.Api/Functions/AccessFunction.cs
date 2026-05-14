using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;

namespace AmazonAdsManager.Api.Functions;

public class AccessFunction(ApiAccessService access)
{
    [Function("CheckAccess")]
    public IActionResult Check(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "access/check")] HttpRequest req)
    {
        if (!access.HasAccessPassword)
            return new ObjectResult(new AccessCheckResponse { Ok = false })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };

        req.Form.TryGetValue("password", out var provided);
        if (string.IsNullOrEmpty(provided))
            provided = req.Headers["x-access-password"];

        if (!access.IsPasswordValid(provided))
            return new UnauthorizedObjectResult(new AccessCheckResponse { Ok = false });

        var (token, expiresAt) = access.CreateToken();
        return new OkObjectResult(new AccessCheckResponse
        {
            Ok = true,
            Token = token,
            ExpiresAt = expiresAt
        });
    }
}
