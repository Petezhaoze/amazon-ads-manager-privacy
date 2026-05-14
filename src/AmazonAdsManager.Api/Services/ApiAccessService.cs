using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace AmazonAdsManager.Api.Services;

public class ApiAccessService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);
    private readonly IConfiguration _config;

    public ApiAccessService(IConfiguration config)
    {
        _config = config;
    }

    public bool HasAccessPassword => !string.IsNullOrWhiteSpace(AccessPassword);

    public bool IsPasswordValid(string? provided)
    {
        var expected = AccessPassword;
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrEmpty(provided))
            return false;

        return FixedTimeEquals(provided, expected);
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateToken()
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(TokenLifetime);
        var payload = $"{expiresAt.ToUnixTimeSeconds()}.{Guid.NewGuid():N}";
        return ($"{payload}.{Sign(payload)}", expiresAt);
    }

    public IActionResult? RequireAuthorized(HttpRequest req)
    {
        return IsTokenValid(GetToken(req))
            ? null
            : new UnauthorizedObjectResult(new { ok = false, error = "API access token is missing or expired." });
    }

    private bool IsTokenValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[0], out var expiresUnix))
            return false;

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresUnix);
        if (expiresAt <= DateTimeOffset.UtcNow)
            return false;

        var payload = $"{parts[0]}.{parts[1]}";
        return FixedTimeEquals(parts[2], Sign(payload));
    }

    private string Sign(string payload)
    {
        var secret = _config["ApiAuthSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            secret = AccessPassword;

        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("AccessPassword or ApiAuthSecret must be configured.");

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private string? GetToken(HttpRequest req)
    {
        var authorization = req.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();

        var token = req.Headers["x-api-access-token"].ToString();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private string? AccessPassword => _config["AccessPassword"];

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
