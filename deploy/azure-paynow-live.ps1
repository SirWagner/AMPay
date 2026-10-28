<#
.SYNOPSIS
    Switches Pay Now to live on Azure staging for one Netcash account.

.DESCRIPTION
    1. Puts the account's Pay Now service key into Key Vault (from the git-ignored
       src/AMPay.Web/appsettings.Local.json, or asks for it with the typing hidden).
    2. Creates the Pay Now callback secret in Key Vault if there is none yet (random; nobody
       needs to know it - it lets AM-Pay prove a "payment complete" call is genuine).
    3. Points the AM-Pay app at both with Key Vault references and sets Netcash:Live:PayNow.
       Everything else Netcash stays a stub. The app restarts by itself.

    Nothing is printed except names. Run it signed in with `az login` as someone who may write
    secrets to the vault.

    Afterwards, in the Netcash portal, set the Pay Now service's URLs (printed at the end).

.EXAMPLE
    ./deploy/azure-paynow-live.ps1 -Account 53117132692
    ./deploy/azure-paynow-live.ps1 -Account 53117132692 -Off     # back to the stub
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^5\d{10}$')] [string]$Account,
    [string]$ResourceGroup = 'rg-ampay-staging',
    [string]$App = 'ampay-staging-o823r',
    [string]$Vault = 'kv-ampay-o823r',
    [switch]$Off
)

$ErrorActionPreference = 'Stop'
$cmd = Get-Command az.cmd -ErrorAction SilentlyContinue
$az = if ($cmd) { $cmd.Source } else { 'az' }

if ($Off) {
    & $az webapp config appsettings set -g $ResourceGroup -n $App --settings 'Netcash__Live__PayNow=false' --query '[].name' -o none
    Write-Host 'Pay Now is back on the stub on Azure.'
    return
}

$keyName = "Netcash--ServiceKeys--$Account--14"
$secretName = 'Netcash--PayNowCallbackSecret'

# ---- the Pay Now key
$key = $null
$localFile = Join-Path $PSScriptRoot '..\src\AMPay.Web\appsettings.Local.json'
if (Test-Path $localFile) {
    $local = Get-Content $localFile -Raw | ConvertFrom-Json
    $key = $local.Netcash.ServiceKeys.$Account.'14'
}
if (-not $key) {
    $secure = Read-Host -Prompt 'Pay Now service key' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}
if ($key -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') { throw 'That does not look like a Netcash service key (a GUID).' }

$tmp = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllText($tmp, $key)
    & $az keyvault secret set --vault-name $Vault --name $keyName --file $tmp --query 'name' -o none
    Write-Host "Stored $keyName"

    # ---- the callback secret: created once, kept thereafter
    $exists = & $az keyvault secret list --vault-name $Vault --query "[?name=='$secretName'].name" -o tsv
    if (-not $exists) {
        $bytes = New-Object byte[] 36
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        [IO.File]::WriteAllText($tmp, [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B'))
        & $az keyvault secret set --vault-name $Vault --name $secretName --file $tmp --query 'name' -o none
        Write-Host "Created $secretName"
    } else {
        Write-Host "$secretName already exists - kept"
    }
} finally {
    [IO.File]::Delete($tmp)
}

# ---- the app: Key Vault references (no values) and the live switch
$ref = { param($n) "@Microsoft.KeyVault(VaultName=$Vault;SecretName=$n)" }
$settings = @(
    "Netcash__ServiceKeys__$($Account)__14=$(& $ref $keyName)",
    "Netcash__PayNowCallbackSecret=$(& $ref $secretName)",
    'Netcash__Live__PayNow=true'
)
$file = [IO.Path]::GetTempFileName()
try {
    # Through a file: az.cmd goes via cmd.exe, which mangles the parentheses in the references.
    ($settings | ForEach-Object { $i = $_.IndexOf('='); @{ name = $_.Substring(0, $i); value = $_.Substring($i + 1); slotSetting = $false } }) |
        ConvertTo-Json | Set-Content -Path $file -Encoding utf8
    & $az webapp config appsettings set -g $ResourceGroup -n $App --settings "@$file" --query '[].name' -o none
} finally {
    [IO.File]::Delete($file)
}

$base = "https://$App.azurewebsites.net"
Write-Host ''
Write-Host 'Pay Now is LIVE on Azure staging (the app restarts by itself).' -ForegroundColor Green
Write-Host ''
Write-Host 'Now, in the Netcash portal -> Pay Now service settings, set:'
Write-Host "  Accept URL:   $base/Checkout/Accepted"
Write-Host "  Decline URL:  $base/Checkout/Declined"
Write-Host "  Notify URL:   $base/Checkout/Notify"
Write-Host "  Redirect URL: $base/Checkout/Accepted"
Write-Host ''
Write-Host "And in AM-Pay: Customers -> the business -> Netcash account number = $Account."
