using AMPay.Domain.Entities;
using AMPay.Domain.Enums;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AMPay.Web.Services;

/// <summary>
/// Creates the roles, the AM-Pay platform tenant, the sudo user, and Albatross Money as the
/// first customer - the arrangement described in the AM-Pay business model.
/// </summary>
public static class SeedData
{
    public static async Task InitialiseAsync(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<AppDbContext>();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");

        await db.Database.MigrateAsync();

        foreach (var (name, description) in AppRoles.All)
        {
            if (!await roles.RoleExistsAsync(name))
                await roles.CreateAsync(new ApplicationRole(name) { Description = description });
        }

        // AM-Pay itself. The platform user belongs here.
        var platform = await db.Tenants.FirstOrDefaultAsync(t => t.IsPlatformOwner);
        if (platform is null)
        {
            platform = new Tenant
            {
                Name = "AM-Pay Fintech",
                TradingName = "AM-Pay",
                IsPlatformOwner = true,
                Status = TenantStatus.Active,
                ContactEmail = "info@albatrossmoney.com",
                ContactNumber = "011 894 1087"
            };
            db.Tenants.Add(platform);
            await db.SaveChangesAsync();
            log.LogInformation("Seeded platform tenant {Name}.", platform.Name);
        }

        // Albatross Money - the first customer and live test case.
        var albatross = await db.Tenants.FirstOrDefaultAsync(t => t.NcrNumber == "NCRCP9166");
        if (albatross is null)
        {
            albatross = new Tenant
            {
                Name = "Albatross Money",
                TradingName = "Albatross Money",
                NcrNumber = "NCRCP9166",
                Status = TenantStatus.Active,
                ContactEmail = "info@albatrossmoney.com",
                ContactNumber = "011 894 1087"
            };
            db.Tenants.Add(albatross);
            await db.SaveChangesAsync();

            // Service key placeholders. The secret name is stored; the key itself lives in
            // user-secrets or Key Vault and is never written to the database.
            foreach (var service in new[]
                     {
                         NetcashServiceId.Account,
                         NetcashServiceId.DebitOrders,
                         NetcashServiceId.PayNow
                     })
            {
                db.TenantServiceKeys.Add(new TenantServiceKey
                {
                    TenantId = albatross.Id,
                    ServiceId = service,
                    SecretName = $"Netcash:ServiceKeys:{albatross.Id}:{(int)service}",
                    Status = ServiceKeyStatus.Unverified
                });
            }

            await db.SaveChangesAsync();
            log.LogInformation("Seeded customer tenant {Name}.", albatross.Name);
        }

        // The sudo account.
        var sudoEmail = config["Seed:SuperAdmin:Email"] ?? "admin@ampay.local";
        var sudoPassword = config["Seed:SuperAdmin:Password"];

        var sudo = await users.FindByEmailAsync(sudoEmail);
        if (sudo is null)
        {
            if (string.IsNullOrWhiteSpace(sudoPassword))
            {
                // Refuse to invent a password. A guessable default on an account that can
                // reach every tenant is not a convenience worth having.
                log.LogWarning(
                    "No super admin seeded. Set Seed:SuperAdmin:Password in user-secrets, then restart. " +
                    "Suggested: dotnet user-secrets set \"Seed:SuperAdmin:Password\" \"<a strong password>\"");
                return;
            }

            sudo = new ApplicationUser
            {
                UserName = sudoEmail,
                Email = sudoEmail,
                EmailConfirmed = true,
                FullName = config["Seed:SuperAdmin:FullName"] ?? "AM-Pay Platform Administrator",
                TenantId = null // platform user: deliberately unscoped
            };

            var created = await users.CreateAsync(sudo, sudoPassword);
            if (!created.Succeeded)
            {
                log.LogError("Could not create the super admin: {Errors}",
                    string.Join("; ", created.Errors.Select(e => e.Description)));
                return;
            }

            await users.AddToRoleAsync(sudo, AppRoles.SuperAdmin);
            log.LogInformation("Seeded super admin {Email}.", sudoEmail);
        }
    }
}
