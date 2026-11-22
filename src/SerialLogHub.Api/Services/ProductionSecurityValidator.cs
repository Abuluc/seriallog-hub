namespace SerialLogHub.Api.Services;

public static class ProductionSecurityValidator
{
    private static readonly HashSet<string> ForbiddenProductionSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "dev-serialloghub-api-key",
        "dev-serialloghub-bridge-key",
        "dev-serialloghub-webproxy-hmac-secret",
        "CHANGE_ME_IN_PRODUCTION",
        "CHANGE_ME_WEBPROXY_SECRET_IN_PRODUCTION",
        "replace-with-strong-api-key",
        "replace-with-strong-bridge-key",
        "Demo1234!"
    };

    public static bool IsForbiddenDevelopmentSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return true;
        }

        return ForbiddenProductionSecrets.Contains(secret.Trim());
    }

    public static void ValidateApiConfiguration(IConfiguration configuration, bool isProduction)
    {
        if (!isProduction)
        {
            return;
        }

        bool requireApiKey = configuration.GetValue("ApiSecurity:RequireApiKey", true);
        if (!requireApiKey)
        {
            throw new InvalidOperationException(
                "Production startup failed: ApiSecurity:RequireApiKey cannot be disabled in Production.");
        }

        string? apiKey = configuration["ApiSecurity:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Trim().Length < 16 || IsForbiddenDevelopmentSecret(apiKey))
        {
            throw new InvalidOperationException(
                "Production startup failed: ApiSecurity:ApiKey is missing, shorter than 16 characters, or uses a forbidden development fallback key.");
        }

        string? webProxySecret = configuration["ApiSecurity:WebProxySigningSecret"];
        if (!string.IsNullOrWhiteSpace(webProxySecret))
        {
            if (webProxySecret.Trim().Length < 16 ||
                IsForbiddenDevelopmentSecret(webProxySecret) ||
                OperatorScopeGuard.FixedTimeEquals(webProxySecret.Trim(), apiKey.Trim()))
            {
                throw new InvalidOperationException(
                    "Production startup failed: ApiSecurity:WebProxySigningSecret must be at least 16 characters, distinct from ApiSecurity:ApiKey, and not a development fallback secret.");
            }
        }

        bool demoEnabled = configuration.GetValue("DemoMode:Enabled", false);
        bool allowPublicDemoSandbox = configuration.GetValue("DemoMode:CloudVirtualDeviceResponder", false);
        string? demoPassword = configuration["DemoMode:DemoPassword"];

        if (demoEnabled && !allowPublicDemoSandbox &&
            (string.IsNullOrWhiteSpace(demoPassword) || string.Equals(demoPassword.Trim(), "Demo1234!", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Production startup failed: Default DemoMode password 'Demo1234!' is not permitted in Production without CloudVirtualDeviceResponder sandbox mode.");
        }
    }
}
