using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using SerialLogHub.Web.Hubs;
using SerialLogHub.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<UserConnectionManager>();
builder.Services.AddControllersWithViews();

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

builder.Services.AddHttpClient<ApiClientService>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    string baseUrl = config["ApiSettings:BaseUrl"] ?? "http://localhost:8080/api/";
    if (!baseUrl.EndsWith('/'))
    {
        baseUrl += "/";
    }

    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    string? apiKey = config["ApiSettings:ApiKey"];
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        client.DefaultRequestHeaders.Add("X-API-KEY", apiKey);
    }
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/api/Auth/logout";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(1);
        options.SlidingExpiration = true;
    });

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromDays(1);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<NotificationHub>("/notificationHub");

app.MapControllerRoute(
    name: "UserSettings",
    pattern: "UserSettings/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "Login",
    pattern: "Login/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
