using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Netcash;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAMPayInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sql => sql.EnableRetryOnFailure()));

        services.Configure<NetcashOptions>(configuration.GetSection(NetcashOptions.SectionName));

        services.AddSingleton<INetcashSecretStore, ConfigurationSecretStore>();

        var netcash = configuration.GetSection(NetcashOptions.SectionName).Get<NetcashOptions>()
                      ?? new NetcashOptions();

        if (netcash.UseStubs)
        {
            // No Netcash credentials required. Every call is answered locally and logged
            // at Warning so a stubbed response is never mistaken for a real one.
            services.AddScoped<INetcashPartnerService, StubNetcashPartnerService>();
            services.AddScoped<INetcashDebiCheckService, StubNetcashDebiCheckService>();
            services.AddScoped<INetcashDebitOrderService, StubNetcashDebitOrderService>();
            services.AddScoped<INetcashValidationService, StubNetcashValidationService>();
            services.AddScoped<INetcashPayNowService, StubNetcashPayNowService>();
        }
        else
        {
            // GAP - live Netcash clients.
            // Generate the SOAP clients with dotnet-svcutil, add an implementation of each
            // interface, and register it here in place of the stub. Until that exists, failing
            // loudly at startup is safer than silently falling back to fake payment responses.
            throw new InvalidOperationException(
                "Netcash:UseStubs is false but no live Netcash client is registered. " +
                "Implement the INetcash* interfaces against the NIWS SOAP endpoints and " +
                "register them in AddAMPayInfrastructure before disabling stubs.");
        }

        return services;
    }
}
