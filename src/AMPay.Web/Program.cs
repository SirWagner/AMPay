using AMPay.Infrastructure;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Controllers;
using AMPay.Web.Services;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;

// One number format for the whole application, whatever the host's regional settings.
// Two reasons, and the second is the one that matters:
//  - every amount renders the same way (R 14,500.00) on every screen and in every message;
//  - a decimal posted from an <input type="number"> always arrives with a "." separator,
//    and model binding parses form values in the current culture. On a server set to
//    en-ZA (decimal comma) "5000.50" is rejected as invalid; under a culture that uses "."
//    to group thousands it would quietly bind as 500050.
var appCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = appCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = appCulture;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------
// GAP - production configuration.
// Add Azure Key Vault as a configuration source here so Netcash service keys and the
// software vendor key never live in appsettings.json:
//
//   if (!builder.Environment.IsDevelopment())
//       builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());
// ---------------------------------------------------------------------------------------

builder.Services.AddAMPayInfrastructure(builder.Configuration);

builder.Services
    .AddIdentity<ApplicationUser, ApplicationRole>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireDigit = true;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/Denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AppPolicies.PlatformOnly, p => p.RequireRole(AppRoles.SuperAdmin));

    options.AddPolicy(AppPolicies.TenantAdministration, p =>
        p.RequireRole(AppRoles.SuperAdmin, AppRoles.TenantAdmin));

    options.AddPolicy(AppPolicies.CanCapture, p =>
        p.RequireRole(AppRoles.SuperAdmin, AppRoles.TenantAdmin, AppRoles.Capturer));

    // A capturer is absent by design: the person who uploaded the ID must not be the
    // person who attests that it is genuine.
    options.AddPolicy(AppPolicies.CanReviewDocuments, p =>
        p.RequireRole(AppRoles.SuperAdmin, AppRoles.TenantAdmin, AppRoles.Reviewer));

    options.AddPolicy(AppPolicies.CanViewClients, p =>
        p.RequireRole(AppRoles.SuperAdmin, AppRoles.TenantAdmin, AppRoles.Capturer,
                      AppRoles.Reviewer, AppRoles.Viewer));

    options.AddPolicy(AppPolicies.CanApproveCredit, p =>
        p.RequireRole(AppRoles.SuperAdmin, AppRoles.TenantAdmin));
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentTenant, CurrentTenant>();
builder.Services.AddScoped<INetcashCapabilityService, NetcashCapabilityService>();

// The self-service portal: a separate app with its own database. AM-Pay calls it; it never calls AM-Pay.
builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.SectionName));
builder.Services.AddHttpClient<IPortalApi, PortalClient>();
builder.Services.AddScoped<PortalImporter>();

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Holds users on an administrator-set password at the change-password page.
builder.Services.AddControllersWithViews(options => options.Filters.Add<MustChangePasswordFilter>());

// How quickly a password reset, a password change or a deactivation reaches sessions that
// are already signed in elsewhere. The default is 30 minutes.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));

// Public pages - contract signing today, the self-service portal's callbacks later - are
// reachable without signing in, so each client address gets a budget. The PIN policy is the
// one that matters: with a six-digit PIN, five wrong tries burn it and this caps how fast a
// guesser can ask for new ones.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static string ClientKey(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    options.AddPolicy(RateLimits.PublicPages, http =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1)
        }));

    options.AddPolicy(RateLimits.SigningPin, http =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(10)
        }));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseRateLimiter();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

// Liveness for the deploy script and Azure: answers once the app has started, and says
// whether the database is reachable. Anonymous, and reveals nothing beyond up or down.
app.MapGet("/healthz", async (AppDbContext db, CancellationToken ct) =>
{
    var dbOk = await db.Database.CanConnectAsync(ct);
    return dbOk ? Results.Text("ok") : Results.Text("database unreachable", statusCode: 503);
}).AllowAnonymous();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Applies migrations and seeds roles, the platform tenant and the sudo user.
// Azure SQL's free and serverless tiers pause when idle and take minutes to wake, refusing
// connections meanwhile (error 40613). Wait for the database rather than crash the app -
// Azure gives a starting container up to WEBSITES_CONTAINER_START_TIME_LIMIT (600s) here.
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
for (var attempt = 1; ; attempt++)
{
    try
    {
        await SeedData.InitialiseAsync(app.Services, app.Configuration);
        break;
    }
    catch (Exception ex) when (attempt < 16 && ex is not OperationCanceledException)
    {
        startupLog.LogWarning("Database not ready (attempt {Attempt}): {Message} Retrying in 30 seconds.",
            attempt, ex.GetBaseException().Message);
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

app.Run();
