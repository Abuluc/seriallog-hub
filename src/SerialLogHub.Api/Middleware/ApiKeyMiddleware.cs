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

        // 1. Check database credentials first (supports scoped operator keys, expiration, and revocation)
        var dbKeys = await dbContext.ApiKeys
            .AsNoTracking()
            .ToListAsync();

        ApiKeyEntity? matchedDbKey = dbKeys.FirstOrDefault(k =>
            OperatorScopeGuard.VerifyHashedOrPlainCredential(providedKey, k.Key));

        if (matchedDbKey != null)
        {
            if (matchedDbKey.IsRevoked || matchedDbKey.ExpiresAt <= DateTime.UtcNow)
            {
                _logger.LogWarning("Rejected expired or revoked API credential (Id={KeyId}) for {Path}.", matchedDbKey.Id, path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Expired or revoked Api Key.");
                return;
            }

            // If this API credential is scoped to a specific operator (e.g. DesktopBridge / OperatorSession), bind it on HttpContext
            if (!string.IsNullOrWhiteSpace(matchedDbKey.OwnerEmail) && matchedDbKey.OwnerEmail != "*")
            {
                OperatorScopeGuard.SetAuthenticatedOperator(context, matchedDbKey.OwnerEmail, matchedDbKey.ClientType);
            }
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

        // 2. Also validate X-OPERATOR-TOKEN if supplied alongside the base API key
        if (context.Request.Headers.TryGetValue(OperatorScopeGuard.OperatorTokenHeaderName, out var opTokenHeader) &&
            !string.IsNullOrWhiteSpace(opTokenHeader))
        {
            string rawToken = opTokenHeader.ToString().Trim();
            ApiKeyEntity? matchedToken = dbKeys.FirstOrDefault(k =>
                OperatorScopeGuard.VerifyHashedOrPlainCredential(rawToken, k.Key));

            if (matchedToken == null || matchedToken.IsRevoked || matchedToken.ExpiresAt <= DateTime.UtcNow)
            {
                _logger.LogWarning("Rejected invalid, expired, or revoked X-OPERATOR-TOKEN on {Path}.", path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or expired operator token.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(matchedToken.OwnerEmail) && matchedToken.OwnerEmail != "*")
            {
                OperatorScopeGuard.SetAuthenticatedOperator(context, matchedToken.OwnerEmail, matchedToken.ClientType);
            }
        }
        // 3. Or validate cryptographic HMAC proof (X-OPERATOR-EMAIL + X-OPERATOR-TIMESTAMP + X-OPERATOR-PROOF) from WebServer/Bridge
        else if (context.Request.Headers.TryGetValue(OperatorScopeGuard.OperatorEmailHeaderName, out var emailHeader) &&
                 context.Request.Headers.TryGetValue(OperatorScopeGuard.OperatorTimestampHeaderName, out var tsHeader) &&
                 context.Request.Headers.TryGetValue(OperatorScopeGuard.OperatorProofHeaderName, out var proofHeader))
        {
            string assertedEmail = emailHeader.ToString().Trim().ToLowerInvariant();
            string signingSecret = configuredKey ?? "dev-serialloghub-api-key";
            if (!OperatorScopeGuard.VerifyOperatorHmacProof(assertedEmail, tsHeader.ToString(), proofHeader.ToString(), signingSecret))
            {
                _logger.LogWarning("Rejected invalid HMAC operator proof for {Email} on {Path}.", assertedEmail, path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid cryptographic operator proof.");
                return;
            }

            // If the API key itself was already bound to a different operator, block impersonation!
            if (context.Items.TryGetValue(OperatorScopeGuard.ContextOperatorEmailKey, out var existingOwner) &&
                existingOwner is string boundOwner &&
                !string.Equals(boundOwner, assertedEmail, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Blocked operator impersonation attempt: credential owner {Owner} tried to assert {Asserted}.", boundOwner, assertedEmail);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Operator credential owner mismatch.");
                return;
            }

            OperatorScopeGuard.SetAuthenticatedOperator(context, assertedEmail, ApiCredentialClientTypes.WebServer);
        }

        await _next(context);
    }
}
