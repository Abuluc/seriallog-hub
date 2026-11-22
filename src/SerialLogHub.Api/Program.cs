using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Middleware;
using SerialLogHub.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SerialLog Hub API",
        Version = "v1",
        Description = "REST API bridging SerialLog Hub Web Dashboard and Desktop COM Port Bridge."
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API Key needed to access endpoints. Header: X-API-KEY",
        In = ParameterLocation.Header,
        Name = "X-API-KEY",
        Type = SecuritySchemeType.ApiKey
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        }
    });
});

string connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=serialloghub.db";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton<PasswordSecurityService>();
builder.Services.AddHostedService<CloudDemoResponderService>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "local",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 180,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:3000" };
builder.Services.AddCors(options =>
{
    options.AddPolicy("ConfiguredOrigins", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

await DbSeeder.InitializeAsync(app.Services);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("ConfiguredOrigins");
app.UseRateLimiter();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapControllers();

app.Run();

public partial class Program { }
