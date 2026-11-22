using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Entities;
using SerialLogHub.Api.Services;

namespace SerialLogHub.Api.Middleware;

public class ApiKeyMiddleware
{
    private const string ApiKeyHeaderName = "X-API-KEY";
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext dbContext)
    {
        bool requireApiKey = _configuration.GetValue("ApiSecurity:RequireApiKey", true);
        if (!requireApiKey)
        {
            await _next(context);
            return;
        }

        string path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedKey) ||
            string.IsNullOrWhiteSpace(extractedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing Api Key.");
            return;
        }

        string providedKey = extractedKey.ToString().Trim();
        string? configuredKey = _configuration["ApiSecurity:ApiKey"];
        string providedHash = OperatorScopeGuard.HashCredential(providedKey);

        // 1. Direct indexed O(1) SQL lookup by SHA-256 hash (never loads all ApiKeys into memory)
        ApiKeyEntity? matchedDbKey = await dbContext.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == providedHash);

        if (matchedDbKey != null)
        {
            if (matchedDbKey.IsRevoked || matchedDbKey.ExpiresAt <= DateTime.UtcNow)
            {
                _logger.LogWarning("Rejected expired or revoked API credential (Id={KeyId}) for {Path}.", matchedDbKey.Id, path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Expired or revoked Api Key.");
                return;
            }

            if (string.IsNullOrWhiteSpace(matchedDbKey.OwnerEmail) || matchedDbKey.OwnerEmail == "*")
            {
                _logger.LogWarning("Rejected ownerless or wildcard API key (Id={KeyId}) on {Path}.", matchedDbKey.Id, path);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("API credential lacks an explicit operator owner.");
                return;
            }

            OperatorScopeGuard.SetAuthenticatedOperator(context, matchedDbKey.OwnerEmail, matchedDbKey.ClientType);
        }
        else
        {
            bool matchesConfigured = OperatorScopeGuard.FixedTimeEquals(providedKey, configuredKey);
            if (!matchesConfigured)
            {
                _logger.LogWarning("Rejected request to {Path} due to invalid API key.", path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid Api Key.");
                return;
            }
        }

        // 2. Direct indexed O(1) SQL lookup for X-OPERATOR-TOKEN if supplied
        if (context.Request.Headers.TryGetValue(OperatorScopeGuard.OperatorTokenHeaderName, out var opTokenHeader) &&
            !string.IsNullOrWhiteSpace(opTokenHeader))
        {
            string rawToken = opTokenHeader.ToString().Trim();
            string tokenHash = OperatorScopeGuard.HashCredential(rawToken);
            ApiKeyEntity? matchedToken = await dbContext.ApiKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.Key == tokenHash);

            if (matchedToken == null || matchedToken.IsRevoked || matchedToken.ExpiresAt <= DateTime.UtcNow)
            {
                _logger.LogWarning("Rejected invalid, expired, or revoked X-OPERATOR-TOKEN on {Path}.", path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or expired operator token.");
                return;
            }

            if (string.IsNullOrWhiteSpace(matchedToken.OwnerEmail) || matchedToken.OwnerEmail == "*")
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Operator token lacks a valid owner.");
                return;
            }

            OperatorScopeGuard.SetAuthenticatedOperator(context, matchedToken.OwnerEmail, matchedToken.ClientType);
        }
        // 3. Or validate HMAC proof signed with the dedicated WebProxySigningSecret (NEVER the shared ApiKey)
        else if (context.Request.Headers.TryGetValue(OperatorScopeGuard.OperatorEmailHeaderName, out var emailHeader) &&
                 context.Request.Headers.TryGetValue(OperatorScopeGuard.OperatorTimestampHeaderName, out var tsHeader) &&
                 context.Request.Headers.TryGetValue(OperatorScopeGuard.OperatorProofHeaderName, out var proofHeader))
        {
            string assertedEmail = emailHeader.ToString().Trim().ToLowerInvariant();
            string? webProxySecret = OperatorScopeGuard.ResolveDistinctWebProxySigningSecret(_configuration);
            if (string.IsNullOrWhiteSpace(webProxySecret) ||
                !OperatorScopeGuard.VerifyOperatorHmacProof(assertedEmail, tsHeader.ToString(), proofHeader.ToString(), webProxySecret))
            {
                _logger.LogWarning("Rejected invalid HMAC operator proof for {Email} on {Path}.", assertedEmail, path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid cryptographic operator proof.");
                return;
            }

            if (context.Items.TryGetValue(OperatorScopeGuard.ContextOperatorEmailKey, out var existingOwner) &&
                existingOwner is string boundOwner &&
                !string.Equals(boundOwner, assertedEmail, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Blocked operator impersonation attempt: credential owner {Owner} tried to assert {Asserted}.", boundOwner, assertedEmail);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Operator credential owner mismatch.");
                return;
            }

            OperatorScopeGuard.SetAuthenticatedOperator(context, assertedEmail, "WebServerProxy");
        }

        await _next(context);
    }
}
