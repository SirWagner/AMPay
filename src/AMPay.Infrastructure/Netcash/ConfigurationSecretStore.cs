using AMPay.Domain.Netcash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Netcash;

/// <summary>
/// Resolves Netcash service keys from IConfiguration.
/// <para>
/// In development that means user-secrets (dotnet user-secrets set). In production, point
/// the same configuration at Azure Key Vault - the provider changes, this class does not.
/// </para>
/// <para>
/// GAP - production hardening. Before go-live, add Azure Key Vault as a configuration source
/// in Program.cs and confirm no key is ever read from appsettings.json. A service key in a
/// checked-in config file is a live payment credential in source control.
/// </para>
/// </summary>
public class ConfigurationSecretStore : INetcashSecretStore
{
    private readonly IConfiguration _configuration;
    private readonly NetcashOptions _options;

    public ConfigurationSecretStore(IConfiguration configuration, IOptions<NetcashOptions> options)
    {
        _configuration = configuration;
        _options = options.Value;
    }

    public Task<string?> GetServiceKeyAsync(string secretName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secretName)) return Task.FromResult<string?>(null);

        // Key Vault flattens ":" to "--"; accept both spellings so the same secret name works
        // against user-secrets locally and Key Vault in Azure.
        var value = _configuration[secretName]
                    ?? _configuration[secretName.Replace(':', '-')]
                    ?? _configuration[secretName.Replace(':', '_')];

        return Task.FromResult(string.IsNullOrWhiteSpace(value) ? null : value);
    }

    public Task<string> GetSoftwareVendorKeyAsync(CancellationToken ct = default)
    {
        var value = _configuration[_options.SoftwareVendorKeySecretName];

        if (string.IsNullOrWhiteSpace(value))
        {
            // The documented sandbox vendor key. AM-Pay receives its own on ISV sign-off,
            // and that one must come from the secret store, never from here.
            value = "24ade73c-98cf-47b3-99be-cc7b867b3080";
        }

        return Task.FromResult(value);
    }
}
