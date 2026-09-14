using AMPay.Domain.Enums;

namespace AMPay.Domain.Entities;

/// <summary>
/// Banking tab - bank account. Field names track the Netcash NIF transaction record
/// so the batch builder can map straight across without a translation layer.
/// </summary>
public class ClientBankAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>Netcash field 132 (AN30). The name the bank holds for the account.</summary>
    public string AccountHolderName { get; set; } = string.Empty;

    public string BankName { get; set; } = string.Empty;

    /// <summary>Netcash field 134 (N6).</summary>
    public string BranchCode { get; set; } = string.Empty;

    /// <summary>Netcash field 136 (N16).</summary>
    public string AccountNumber { get; set; } = string.Empty;

    /// <summary>Netcash field 133 (N1).</summary>
    public BankAccountType AccountType { get; set; } = BankAccountType.Cheque;

    public bool IsPrimary { get; set; }

    // ---- Account verification (AVS) ----
    public bool AvsVerified { get; set; }
    public DateTime? AvsVerifiedUtc { get; set; }
    /// <summary>Raw AVS response retained for audit: account exists, ID match, initials match, etc.</summary>
    public string? AvsResultJson { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Never render a full account number in UI or logs.</summary>
    public string MaskedAccountNumber =>
        string.IsNullOrEmpty(AccountNumber) || AccountNumber.Length <= 4
            ? "****"
            : new string('*', AccountNumber.Length - 4) + AccountNumber[^4..];
}

/// <summary>
/// Banking tab - wallet. Maxmoney allows a wallet alongside a bank account; retained here
/// because township and micro-merchant clients frequently have no bank account at all.
/// Wallets cannot carry a DebiCheck mandate - collections require a bank account.
/// </summary>
public class ClientWallet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string WalletNumber { get; set; } = string.Empty;
    public string? AccountHolderName { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
