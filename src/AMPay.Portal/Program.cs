using System.Threading.RateLimiting;
using AMPay.Domain.Credit;
using AMPay.Portal.Controllers;
using AMPay.Portal.Data;
using AMPay.Portal.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

// Same reason as AM-Pay: one number format, whatever the host's regional settings.
var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.SectionName));
var portal = builder.Configuration.GetSection(PortalOptions.SectionName).Get<PortalOptions>() ?? new PortalOptions();

// A development key on a public server would hand anyone AM-Pay's view of every application.
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(portal.ApiKey) || portal.ApiKey.StartsWith("dev-", StringComparison.OrdinalIgnoreCase) || portal.ApiKey.Length < 32))
{
    throw new InvalidOperationException(
        "Portal:ApiKey must be set to a strong secret (32+ characters, from Key Vault) outside Development.");
}

builder.Services.AddDbContext<PortalDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Portal"), sql => sql.EnableRetryOnFailure()));

// The same pricing engine AM-Pay uses, and the same statutory limits.
builder.Services.Configure<NcaCreditLimits>(builder.Configuration.GetSection(NcaCreditLimits.SectionName));
builder.Services.AddScoped<ILoanPricingService, LoanPricingService>();

builder.Services.AddScoped<OtpService>();
builder.Services.AddScoped<EstimateService>();
builder.Services.AddSingleton<PortalDocumentStore>();
builder.Services.AddScoped<ApiKeyFilter>();

builder.Services.AddAuthentication(ApplicantClaims.Scheme)
    .AddCookie(ApplicantClaims.Scheme, o =>
    {
        o.Cookie.Name = ".ampay.portal";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromHours(2);
        o.SlidingExpiration = true;
        // Sign-in is per lender, so there is no single login page; the controllers redirect.
        o.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx => { ctx.Response.Redirect("/"); return Task.CompletedTask; }
        };
    });

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Key(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    options.AddPolicy(PortalRateLimits.Pages, http => RateLimitPartition.GetFixedWindowLimiter(Key(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy(PortalRateLimits.Codes, http => RateLimitPartition.GetFixedWindowLimiter(Key(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
    options.AddPolicy(PortalRateLimits.Api, http => RateLimitPartition.GetFixedWindowLimiter(Key(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddControllersWithViews();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/healthz", async (PortalDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Text("ok") : Results.Text("database unreachable", statusCode: 503));

// The portal's own database, migrated on start. It never touches AM-Pay's.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
