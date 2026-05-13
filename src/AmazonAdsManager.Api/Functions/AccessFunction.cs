using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

namespace AmazonAdsManager.Api.Functions;

public class AccessFunction(IConfiguration config)
{
    [Function("CheckAccess")]
    public IActionResult Check(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "access/check")] HttpRequest req)
    {
        var expected = config["AccessPassword"] ?? "";
        if (string.IsNullOrEmpty(expected))
            return new OkObjectResult(new { ok = true });

        req.Form.TryGetValue("password", out var provided);
        if (string.IsNullOrEmpty(provided))
            provided = req.Headers["x-access-password"];

        return provided == expected
            ? new OkObjectResult(new { ok = true })
            : new UnauthorizedObjectResult(new { ok = false });
    }
}
