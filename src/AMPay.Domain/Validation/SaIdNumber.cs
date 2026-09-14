using System.Globalization;

namespace AMPay.Domain.Validation;

/// <summary>
/// South African identity number rules.
/// <para>
/// A local pre-filter so a typo is caught in the browser instead of by a rejected batch.
/// Netcash ValidateId remains the authority - passing here does not mean the number is issued
/// to a real person.
/// </para>
/// <para>
/// Layout: YYMMDD SSSS C A Z. Six date digits, a four-digit sequence where the first digit
/// encodes gender, a citizenship digit, one historic digit, and a Luhn check digit over all 13.
/// </para>
/// </summary>
public static class SaIdNumber
{
    public static bool IsValid(string? idNumber)
    {
        if (string.IsNullOrWhiteSpace(idNumber)) return false;

        var digits = idNumber.Trim();
        if (digits.Length != 13 || !digits.All(char.IsDigit)) return false;

        return TryParseDateOfBirth(digits, out _) && PassesLuhn(digits);
    }

    /// <summary>
    /// Extract date of birth. The two-digit year is ambiguous, so anything that would land in
    /// the future is treated as belonging to the previous century.
    /// </summary>
    public static bool TryParseDateOfBirth(string idNumber, out DateTime dateOfBirth)
    {
        dateOfBirth = default;
        if (string.IsNullOrWhiteSpace(idNumber) || idNumber.Length < 6) return false;

        var datePart = idNumber[..6];
        if (!int.TryParse(datePart[..2], out var yy)) return false;
        if (!int.TryParse(datePart.Substring(2, 2), out var mm)) return false;
        if (!int.TryParse(datePart.Substring(4, 2), out var dd)) return false;

        var century = 2000 + yy > DateTime.UtcNow.Year ? 1900 : 2000;

        try
        {
            dateOfBirth = new DateTime(century + yy, mm, dd, 0, 0, 0, DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Sequence digits 7-10; the first is under 5 for female, 5 or above for male.</summary>
    public static string? GetGender(string idNumber)
    {
        if (string.IsNullOrWhiteSpace(idNumber) || idNumber.Length < 7) return null;
        if (!int.TryParse(idNumber.Substring(6, 1), out var g)) return null;
        return g < 5 ? "Female" : "Male";
    }

    /// <summary>Digit 11: 0 for a citizen, 1 for a permanent resident.</summary>
    public static bool? IsCitizen(string idNumber)
    {
        if (string.IsNullOrWhiteSpace(idNumber) || idNumber.Length < 11) return null;
        return idNumber[10] switch { '0' => true, '1' => false, _ => null };
    }

    /// <summary>Luhn checksum across all thirteen digits.</summary>
    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';

            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    /// <summary>Format for display as YYMMDD SSSS CAZ. Never log a full ID number.</summary>
    public static string Mask(string? idNumber)
    {
        if (string.IsNullOrWhiteSpace(idNumber) || idNumber.Length < 13) return "*************";
        return string.Concat(idNumber.AsSpan(0, 6), "*******");
    }

    /// <summary>Sanity guard for a Netcash account number: eleven digits beginning with 5.</summary>
    public static bool IsValidNetcashAccountNumber(string? accountNumber) =>
        !string.IsNullOrWhiteSpace(accountNumber)
        && accountNumber.Length == 11
        && accountNumber.All(char.IsDigit)
        && accountNumber[0] == '5';
}
