<#
.SYNOPSIS
    Makes Netcash live on Azure staging for one Netcash account: the same test setup as local.

.DESCRIPTION
    1. Copies the account's service keys (1 DebiCheck/debit orders, 5 Account, 14 Pay Now) and
       the software vendor key from the git-ignored src/AMPay.Web/appsettings.Local.json into
       Key Vault. A key missing locally is asked for with the typing hidden.
    2. Creates the Pay Now callback secret in Key Vault if there is none yet.
    3. Points the AM-Pay app at them with Key Vault references, sets the account's DebiCheck
       mandate template, and switches on Partner, Validation, AVS, DebiCheck and Pay Now.
       Credit bureau (Risk) stays a stub until its key exists. The app restarts by itself.

    Nothing is printed except names. Run it signed in with `az login` as someone who may write
    secrets to the vault. Costs: a few Key Vault operations - cents.

    Afterwards, in the Netcash portal, set the Pay Now service's URLs (printed at the end).

.EXAMPLE
    ./deploy/azure-netcash-live.ps1 -Account 53117132692 -MandateTemplate NCDCT000003645
    ./deploy/azure-netcash-live.ps1 -Account 53117132692 -Off      # everything back to the stub
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^5\d{10}$')] [string]$Account,
    [ValidatePattern('^NCDCT\d{9}$')] [string]$MandateTemplate,
    [string]$ResourceGroup = 'rg-ampay-staging',
    [string]$App = 'ampay-staging-o823r',
    [string]$Vault = 'kv-ampay-o823r',
    [switch]$Off
)

$ErrorActionPreference = 'Stop'
$cmd = Get-Command az.cmd -ErrorAction SilentlyContinue
$az = if ($cmd) { $cmd.Source } else { 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' }

$switches = 'Partner', 'Validation', 'Avs', 'DebiCheck', 'PayNow'

# Through a file: az.cmd goes via cmd.exe, which mangles the parentheses in Key Vault references.
function Set-AppSettings([string[]]$Settings) {
    $file = [IO.Path]::GetTempFileName()
    try {
        ($Settings | ForEach-Object { $i = $_.IndexOf('='); @{ name = $_.Substring(0, $i); value = $_.Substring($i + 1); slotSetting = $false } }) |
            ConvertTo-Json | Set-Content -Path $file -Encoding utf8
        & $az webapp config appsettings set -g $ResourceGroup -n $App --settings "@$file" --query '[].name' -o none
    } finally {
        [IO.File]::Delete($file)
    }
}

if ($Off) {
    Set-AppSettings ($switches | ForEach-Object { "Netcash__Live__$_=false" })
    Write-Host 'Netcash is back on the stubs on Azure. The keys stay in Key Vault.'
    return
}

$local = $null
$localFile = Join-Path $PSScriptRoot '..\src\AMPay.Web\appsettings.Local.json'
if (Test-Path $localFile) { $local = Get-Content $localFile -Raw | ConvertFrom-Json }

function Read-Hidden([string]$Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$guid = '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'
$services = [ordered]@{ '1' = 'Debit orders / DebiCheck'; '5' = 'Account (bank and ID checks)'; '14' = 'Pay Now' }
$settings = @()
$ref = { param($n) "@Microsoft.KeyVault(VaultName=$Vault;SecretName=$n)" }

$tmp = [IO.Path]::GetTempFileName()
try {
    function Store([string]$Name, [string]$Value) {
        [IO.File]::WriteAllText($tmp, $Value)
        & $az keyvault secret set --vault-name $Vault --name $Name --file $tmp --query 'name' -o none
        Write-Host "Stored $Name"
    }

    # ---- the service keys
    foreach ($id in $services.Keys) {
        $key = if ($local) { $local.Netcash.ServiceKeys.$Account.$id } else { $null }
        if (-not $key) { $key = Read-Hidden "$($services[$id]) service key (service $id)" }
        if ($key -notmatch $guid) { throw "Service $id`: that does not look like a Netcash service key (a GUID)." }

        $name = "Netcash--ServiceKeys--$Account--$id"
        Store $name $key
        $settings += "Netcash__ServiceKeys__$($Account)__$id=$(& $ref $name)"
    }

    # ---- the software vendor key (Netcash's published test vendor key on a test account)
    $vendor = if ($local) { $local.Netcash.SoftwareVendorKey } else { $null }
    if (-not $vendor) { $vendor = Read-Hidden 'Software vendor key' }
    if ($vendor -notmatch $guid) { throw 'That does not look like a software vendor key (a GUID).' }
    Store 'Netcash--SoftwareVendorKey' $vendor
    $settings += "Netcash__SoftwareVendorKey=$(& $ref 'Netcash--SoftwareVendorKey')"

    # ---- the Pay Now callback secret: created once, kept thereafter
    $secretName = 'Netcash--PayNowCallbackSecret'
    $exists = & $az keyvault secret list --vault-name $Vault --query "[?name=='$secretName'].name" -o tsv
    if (-not $exists) {
        $bytes = New-Object byte[] 36
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        Store $secretName ([Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B'))
    } else {
        Write-Host "$secretName already exists - kept"
    }
    $settings += "Netcash__PayNowCallbackSecret=$(& $ref $secretName)"
} finally {
    [IO.File]::Delete($tmp)
}

# ---- the DebiCheck mandate template: not a secret
if (-not $MandateTemplate -and $local) { $MandateTemplate = $local.Netcash.MandateTemplates.$Account }
if ($MandateTemplate) { $settings += "Netcash__MandateTemplates__$Account=$MandateTemplate" }

# ---- the live switches. UseStubs stays as it is: each service is switched on by itself.
$settings += $switches | ForEach-Object { "Netcash__Live__$_=true" }

Set-AppSettings $settings

$base = "https://$App.azurewebsites.net"
Write-Host ''
Write-Host "Netcash is LIVE on Azure staging for $Account (the app restarts by itself):" -ForegroundColor Green
Write-Host "  $($switches -join ', ')$(if ($MandateTemplate) { "; DebiCheck template $MandateTemplate" })"
Write-Host '  Credit bureau (Risk) stays a stub until its key is loaded.'
Write-Host ''
Write-Host 'In the Netcash portal -> Pay Now service settings, set:'
Write-Host "  Accept URL:   $base/Checkout/Accepted"
Write-Host "  Decline URL:  $base/Checkout/Declined"
Write-Host "  Notify URL:   $base/Checkout/Notify"
Write-Host "  Redirect URL: $base/Checkout/Accepted"
Write-Host ''
Write-Host "And in AM-Pay: Customers -> the business -> Netcash account number = $Account."
