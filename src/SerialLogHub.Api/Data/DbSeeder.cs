using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Entities;
using SerialLogHub.Api.Services;

namespace SerialLogHub.Api.Data;

public static class DbSeeder
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var passwordService = scope.ServiceProvider.GetRequiredService<PasswordSecurityService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        await db.Database.EnsureCreatedAsync();

        string configuredApiKey = config["ApiSecurity:ApiKey"] ?? "dev-serialloghub-api-key";
        if (!await db.ApiKeys.AnyAsync(k => k.Key == configuredApiKey))
        {
            db.ApiKeys.Add(new ApiKeyEntity
            {
                Key = configuredApiKey,
                ExpiresAt = DateTime.UtcNow.AddYears(5)
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded API key into ApiKeys table.");
        }

        bool demoEnabled = config.GetValue("DemoMode:Enabled", false);
        if (!demoEnabled)
        {
            return;
        }

        string demoEmail = config["DemoMode:DemoEmail"] ?? "demo@serialloghub.local";
        string demoUsername = config["DemoMode:DemoUsername"] ?? "demo_operator";
        string demoPassword = config["DemoMode:DemoPassword"] ?? "Demo1234!";
        string defaultVirtualPort = config["DemoMode:DefaultVirtualPort"] ?? "COM99 (Virtual DataLogger - Demo)";

        if (!await db.Users.AnyAsync(u => u.Email == demoEmail))
        {
            db.Users.Add(new UserEntity
            {
                Username = demoUsername,
                Email = demoEmail,
                HashedPassword = passwordService.HashForStorage(demoEmail, demoPassword),
                IsEmailVerified = true,
                VerificationToken = "demo-verified",
                ResetPasswordToken = "None",
                TokenExpiryTime = DateTime.UtcNow.AddYears(1)
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded demo operator account: {Email}", demoEmail);
        }

        if (!await db.UsersPort.AnyAsync(p => p.UserEmail == demoEmail))
        {
            db.UsersPort.Add(new UserPortEntity
            {
                UserEmail = demoEmail,
                Ports = JsonSerializer.Serialize(new List<string> { defaultVirtualPort })
            });
            await db.SaveChangesAsync();
        }
    }
}
