using System.Globalization;
using System.Text;

namespace AMPay.Infrastructure.Netcash;

/// <summary>
/// Netcash NIF transaction field identifiers, as used in the K and T records.
/// Names and widths are taken from the Netcash debit order and DebiCheck documentation.
/// </summary>
public static class NifField
{
    /// <summary>AN32. Account reference - unique per masterfile entry.</summary>
    public const int AccountReference = 101;
    /// <summary>AN50. Masterfile account name.</summary>
    public const int AccountName = 102;
    /// <summary>AN13. SA ID number.</summary>
    public const int IdNumber = 111;
    /// <summary>N1. 0 = not an SA ID, 1 = SA ID and CDV validated.</summary>
    public const int IsIdNumber = 127;
    /// <summary>N1. 1 = bank account, 2 = credit card.</summary>
    public const int BankingDetailType = 131;
    /// <summary>AN30. Bank account holder name.</summary>
    public const int BankAccountName = 132;
    /// <summary>N1. 1 = cheque, 2 = savings.</summary>
    public const int BankAccountType = 133;
    /// <summary>N6. Branch code.</summary>
    public const int BranchCode = 134;
    /// <summary>Filler, or credit card expiry year.</summary>
    public const int Filler = 135;
    /// <summary>N16. Bank account number, or credit card token.</summary>
    public const int AccountNumber = 136;
    /// <summary>N. Default debit amount, in cents.</summary>
    public const int DefaultDebitAmount = 161;
    /// <summary>N. Amount for this collection, in cents.</summary>
    public const int Amount = 162;
    /// <summary>N11. Mobile number.</summary>
    public const int MobileNumber = 202;
    /// <summary>N2. DebiCheck tracking days, 1 to 10.</summary>
    public const int TrackingDays = 232;
    /// <summary>AN14. DebiCheck mandate template id, e.g. NCDCT000000003.</summary>
    public const int MandateTemplateId = 242;
    /// <summary>N. DebiCheck collection amount, in cents.</summary>
    public const int DebiCheckCollectionAmount = 243;
    /// <summary>N1. 0 = first collection same as 243, 1 = differs.</summary>
    public const int FirstCollectionDiffers = 246;
    /// <summary>N. First collection amount in cents, when 246 = 1.</summary>
    public const int FirstCollectionAmount = 247;
    /// <summary>N8. First collection date, CCYYMMDD.</summary>
    public const int FirstCollectionDate = 248;
    /// <summary>AN50. Approved DebiCheck mandate reference.</summary>
    public const int MandateReference = 249;
    /// <summary>AN7. Collection frequency day code.</summary>
    public const int CollectionDayCode = 250;
    /// <summary>AN999. Echoed back on the return report.</summary>
    public const int Extra1 = 301;
    /// <summary>AN49. Echoed back on the return report.</summary>
    public const int Extra2 = 302;
    /// <summary>AN49. Echoed back on the return report.</summary>
    public const int Extra3 = 303;
}

/// <summary>Header field 4. The instruction that tells Netcash what the file is for.</summary>
public static class NifInstruction
{
    /// <summary>Create or update masterfile entries. Prerequisite for TT2 DebiCheck.</summary>
    public const string Update = "Update";
    /// <summary>Same-day collection.</summary>
    public const string SameDay = "Sameday";
    /// <summary>Two-day collection.</summary>
    public const string TwoDay = "TwoDay";
    /// <summary>Batch DebiCheck authentication (TT2 and delayed TT1).</summary>
    public const string DebiCheckAuthentication = "DebiCheckAuthentication";
}

/// <summary>
/// Builds a Netcash NIF batch file.
/// <para>
/// The format is unforgiving and the failure mode is silent: a mis-ordered K record or a
/// footer total that does not match produces a load report full of line errors rather than a
/// transport failure. This builder owns those invariants - fields are always emitted in
/// ascending key order, and the footer is computed from what was actually added.
/// </para>
/// <para>
/// Records are tab-delimited and terminated with a linefeed. Amounts are in cents and dates
/// are CCYYMMDD; both conversions happen here and nowhere else.
/// </para>
/// </summary>
public class NifFileBuilder
{
    private const char Delimiter = '\t';
    private const string LineTerminator = "\n";

    private readonly string _serviceKey;
    private readonly string _softwareVendorKey;
    private readonly string _instruction;
    private readonly string _batchName;
    private readonly DateTime _actionDate;

    private readonly SortedSet<int> _fields = new();
    private readonly List<Dictionary<int, string>> _rows = new();

    /// <summary>Which field the footer total is summed from: 162 for collections, 243 for DebiCheck.</summary>
    private int _amountField = NifField.Amount;

    public NifFileBuilder(
        string serviceKey,
        string softwareVendorKey,
        string instruction,
        string batchName,
        DateTime actionDate)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
            throw new ArgumentException("Service key is required.", nameof(serviceKey));
        if (string.IsNullOrWhiteSpace(softwareVendorKey))
            throw new ArgumentException("Software vendor key is required.", nameof(softwareVendorKey));
        if (string.IsNullOrWhiteSpace(batchName))
            throw new ArgumentException("Batch name is required.", nameof(batchName));

        _serviceKey = serviceKey;
        _softwareVendorKey = softwareVendorKey;
        _instruction = instruction;
        _batchName = batchName;
        _actionDate = actionDate;
    }

    /// <summary>Tell the builder which field the footer sum is taken from.</summary>
    public NifFileBuilder WithAmountField(int fieldId)
    {
        _amountField = fieldId;
        return this;
    }

    /// <summary>Add one transaction row. Fields may be supplied in any order.</summary>
    public NifFileBuilder AddTransaction(Action<NifTransactionBuilder> configure)
    {
        var row = new NifTransactionBuilder();
        configure(row);

        if (row.Values.Count == 0)
            throw new InvalidOperationException("A transaction record must contain at least one field.");

        foreach (var key in row.Values.Keys) _fields.Add(key);
        _rows.Add(row.Values);
        return this;
    }

    public int TransactionCount => _rows.Count;

    /// <summary>Footer total, in cents, computed from the rows actually added.</summary>
    public long TotalAmountCents =>
        _rows.Sum(r => r.TryGetValue(_amountField, out var v)
                       && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cents)
            ? cents
            : 0L);

    /// <summary>Render the complete file.</summary>
    public string Build()
    {
        if (_rows.Count == 0)
            throw new InvalidOperationException("A NIF file must contain at least one transaction record.");

        var sb = new StringBuilder();

        // Header: H, service key, version, instruction, batch name, action date, software vendor key.
        sb.Append(string.Join(Delimiter,
            "H", _serviceKey, "1", _instruction, _batchName,
            FormatDate(_actionDate), _softwareVendorKey));
        sb.Append(LineTerminator);

        // Key record: field ids in ascending order. The T records must follow this order exactly.
        sb.Append("K");
        foreach (var f in _fields) sb.Append(Delimiter).Append(f.ToString(CultureInfo.InvariantCulture));
        sb.Append(LineTerminator);

        // Transaction records, padded so every row matches the key record.
        foreach (var row in _rows)
        {
            sb.Append("T");
            foreach (var f in _fields)
                sb.Append(Delimiter).Append(row.TryGetValue(f, out var v) ? v : string.Empty);
            sb.Append(LineTerminator);
        }

        // Footer: F, transaction count, sum in cents, end-of-file indicator.
        sb.Append(string.Join(Delimiter,
            "F",
            _rows.Count.ToString(CultureInfo.InvariantCulture),
            TotalAmountCents.ToString(CultureInfo.InvariantCulture),
            "9999"));
        sb.Append(LineTerminator);

        return sb.ToString();
    }

    /// <summary>Netcash dates are CCYYMMDD everywhere, with no separators.</summary>
    public static string FormatDate(DateTime d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Rands to cents. Uses away-from-zero rounding so a half-cent never silently
    /// disappears from a collection total.
    /// </summary>
    public static long ToCents(decimal rands) =>
        (long)decimal.Round(rands * 100m, 0, MidpointRounding.AwayFromZero);
}

/// <summary>Collects the fields of a single T record.</summary>
public class NifTransactionBuilder
{
    internal Dictionary<int, string> Values { get; } = new();

    public NifTransactionBuilder Set(int fieldId, string? value)
    {
        Values[fieldId] = value ?? string.Empty;
        return this;
    }

    public NifTransactionBuilder Set(int fieldId, int value) =>
        Set(fieldId, value.ToString(CultureInfo.InvariantCulture));

    public NifTransactionBuilder Set(int fieldId, bool value) => Set(fieldId, value ? "1" : "0");

    public NifTransactionBuilder SetDate(int fieldId, DateTime value) =>
        Set(fieldId, NifFileBuilder.FormatDate(value));

    /// <summary>Set a money field, converting rands to the cents Netcash expects.</summary>
    public NifTransactionBuilder SetAmount(int fieldId, decimal rands) =>
        Set(fieldId, NifFileBuilder.ToCents(rands).ToString(CultureInfo.InvariantCulture));
}
