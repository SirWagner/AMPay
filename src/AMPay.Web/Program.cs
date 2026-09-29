using AMPay.Infrastructure;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using AMPay.Web.Services;
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

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.AddControllersWithViews();

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
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Applies migrations and seeds roles, the platform tenant and the sudo user.
await SeedData.InitialiseAsync(app.Services, app.Configuration);

app.Run();
