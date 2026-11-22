namespace SerialLogHub.Api.Services;

public static class ProductionSecurityValidator
{
    private static readonly HashSet<string> ForbiddenProductionSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "dev-serialloghub-api-key",
        "dev-serialloghub-bridge-key",
        "CHANGE_ME_IN_PRODUCTION",
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

    /// <summary>
    /// Validates security configuration at startup. In Production (unless AllowPublicDemoSandbox is explicitly enabled
    /// with a strong non-dev API key), rejects missing, short, or development fallback credentials.
    /// </summary>
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

        bool demoEnabled = configuration.GetValue("DemoMode:Enabled", false);
        bool allowPublicDemoSandbox = configuration.GetValue("DemoMode:CloudVirtualDeviceResponder", false);
        string? demoPassword = configuration["DemoMode:DemoPassword"];

        // If DemoMode is enabled in Production without being an explicit CloudVirtualDeviceResponder sandbox,
        // reject the default Demo1234! password
        if (demoEnabled && !allowPublicDemoSandbox &&
            (string.IsNullOrWhiteSpace(demoPassword) || string.Equals(demoPassword.Trim(), "Demo1234!", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Production startup failed: Default DemoMode password 'Demo1234!' is not permitted in Production without CloudVirtualDeviceResponder sandbox mode.");
        }
    }
}
