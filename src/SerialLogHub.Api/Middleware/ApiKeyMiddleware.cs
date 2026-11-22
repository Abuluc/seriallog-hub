using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Data;
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

        string providedKey = extractedKey.ToString();
        string? configuredKey = _configuration["ApiSecurity:ApiKey"];

        bool matchesConfigured = OperatorScopeGuard.FixedTimeEquals(providedKey, configuredKey);

        bool matchesDatabase = matchesConfigured;
        if (!matchesDatabase)
        {
            var activeKeys = await dbContext.ApiKeys
                .AsNoTracking()
                .Where(k => k.ExpiresAt > DateTime.UtcNow)
                .Select(k => k.Key)
                .ToListAsync();

            matchesDatabase = activeKeys.Any(k => OperatorScopeGuard.FixedTimeEquals(providedKey, k));
        }

        if (!matchesDatabase)
        {
            _logger.LogWarning("Rejected request to {Path} due to invalid API key.", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid Api Key.");
            return;
        }

        await _next(context);
    }
}
