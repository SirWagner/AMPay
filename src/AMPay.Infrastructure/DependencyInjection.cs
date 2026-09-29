using AMPay.Domain.Credit;
using AMPay.Domain.Documents;
using AMPay.Domain.Netcash;
using AMPay.Infrastructure.Credit;
using AMPay.Infrastructure.Data;
using AMPay.Infrastructure.Documents;
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

        // Credit origination. The statutory ceilings and the Regulation 23A expense norms
        // bind from configuration so that a gazetted rate change is a settings edit rather
        // than a code change and a release.
        services.Configure<NcaCreditLimits>(configuration.GetSection(NcaCreditLimits.SectionName));
        services.Configure<AffordabilityNorms>(configuration.GetSection(AffordabilityNorms.SectionName));

        services.AddScoped<ILoanPricingService, LoanPricingService>();
        services.AddScoped<IAffordabilityService, AffordabilityService>();

        // Supporting documents. Local disk today; swap in a blob implementation for
        // anything running on more than one server.
        services.Configure<DocumentStorageOptions>(
            configuration.GetSection(DocumentStorageOptions.SectionName));
        services.AddSingleton<IDocumentStore, LocalDocumentStore>();

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
