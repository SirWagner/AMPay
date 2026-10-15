<#
.SYNOPSIS
    Loads a Netcash test account's keys into this machine's user-secrets and switches the
    service-key check to live. Nothing is written into the repository.

.DESCRIPTION
    Asks for the Netcash account number and each service key with the typing hidden, then
    stores them with `dotnet user-secrets` under names keyed by the account number:

        Netcash:ServiceKeys:<account>:1    debit orders (DebiCheck)
        Netcash:ServiceKeys:<account>:5    account service
        Netcash:ServiceKeys:<account>:14   Pay Now
        Netcash:SoftwareVendorKey          optional - empty uses Netcash's documented test vendor key

    and sets Netcash:Live:Partner = true, so the "Validate keys" button calls Netcash for real.
    Every other Netcash call stays a stub.

    The same names work in Azure Key Vault ("--" in place of ":"), so moving to staging later
    is the same values under the same names.

.EXAMPLE
    ./deploy/netcash-test-keys.ps1
    ./deploy/netcash-test-keys.ps1 -Off      # back to stubs for the key check
#>
param([switch]$Off)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\AMPay.Web'

function Set-Secret([string]$name, [string]$value) {
    dotnet user-secrets set $name $value --project $project | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not store $name." }
}

function Read-Hidden([string]$prompt) {
    $secure = Read-Host -Prompt $prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

if ($Off) {
    Set-Secret 'Netcash:Live:Partner' 'false'
    Write-Host 'Service-key check is back on stubs. The keys are still stored.'
    return
}

$account = (Read-Host 'Netcash account number (11 digits, starts with 5)').Trim()
if ($account -notmatch '^5\d{10}$') { throw 'A Netcash account number is 11 digits and starts with 5.' }

$services = [ordered]@{ '1' = 'Debit orders (DebiCheck)'; '5' = 'Account service'; '14' = 'Pay Now' }
$stored = @()
foreach ($id in $services.Keys) {
    $key = Read-Hidden "$($services[$id]) service key (Enter to skip)"
    if ([string]::IsNullOrWhiteSpace($key)) { continue }
    if ($key -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
        Write-Warning "$($services[$id]): that does not look like a Netcash key (a GUID). Skipped."
        continue
    }
    Set-Secret "Netcash:ServiceKeys:${account}:$id" $key
    $stored += $services[$id]
}

$vendor = Read-Hidden 'Software vendor key (Enter to use the Netcash test vendor key)'
if (-not [string]::IsNullOrWhiteSpace($vendor)) { Set-Secret 'Netcash:SoftwareVendorKey' $vendor; $stored += 'Software vendor key' }

Set-Secret 'Netcash:Live:Partner' 'true'

Write-Host ''
Write-Host "Stored: $($stored -join ', ')" -ForegroundColor Green
Write-Host 'Service-key validation is now LIVE; everything else stays a stub.'
Write-Host ''
Write-Host 'Next:'
Write-Host "  1. Restart the app (Visual Studio: stop and run again)."
Write-Host "  2. Customers -> Albatross Money -> set the Netcash account number to $account."
Write-Host '  3. Netcash service keys -> Albatross Money -> Validate.'
Write-Host '  Two failures in a row pause further tries for 10 minutes (Netcash locks at three).'
