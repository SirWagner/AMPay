<#
.SYNOPSIS
    Live Azure list prices, in rand, for the resources this repo uses - so a deployment plan can
    be priced before anyone is asked to approve it.

.DESCRIPTION
    Reads Microsoft's public Retail Prices API (no sign-in needed). Prints hourly/daily prices
    and a monthly estimate for each option, and the rand-per-dollar ratio Azure itself uses, since
    the subscription is billed in USD.

.EXAMPLE
    powershell -File .claude/skills/azure-deploy/scripts/price-check.ps1
.EXAMPLE
    powershell -File .claude/skills/azure-deploy/scripts/price-check.ps1 -Region westeurope
#>
param([string] $Region = 'southafricanorth')

$ErrorActionPreference = 'Stop'

function Price([string] $currency, [string] $filter) {
    $url = "https://prices.azure.com/api/retail/prices?currencyCode='$currency'&`$filter=" + [uri]::EscapeDataString($filter)
    (Invoke-RestMethod $url).Items | Where-Object { $_.type -eq 'Consumption' }
}

$rows = @()

# App Service plans (per hour)
foreach ($p in @(
    @{ Label = 'App Service Linux B1 (recommended)'; Product = 'Azure App Service Basic Plan - Linux'; Sku = 'B1' },
    @{ Label = 'App Service Linux F1 (free, sleeps)'; Product = 'Azure App Service Free Plan - Linux'; Sku = 'F1' },
    @{ Label = 'App Service Windows B1'; Product = 'Azure App Service Basic Plan'; Sku = 'B1' },
    @{ Label = 'App Service Linux P0v3'; Product = 'Azure App Service Premium v3 Plan - Linux'; Sku = 'P0v3' })) {
    $f = "serviceName eq 'Azure App Service' and armRegionName eq '$Region' and productName eq '$($p.Product)' and skuName eq '$($p.Sku)'"
    $zar = (Price 'ZAR' $f | Select-Object -First 1).retailPrice
    $usd = (Price 'USD' $f | Select-Object -First 1).retailPrice
    if ($null -ne $zar) { $rows += [pscustomobject]@{ Resource = $p.Label; Unit = '1 hour'; ZAR = $zar; USD = $usd; MonthZAR = [math]::Round($zar * 730, 2) } }
}

# Azure SQL Basic (per day); the free offer is R0 within its limits
$f = "serviceName eq 'SQL Database' and armRegionName eq '$Region' and productName eq 'SQL Database Single Basic' and meterName eq 'B DTU'"
$zar = (Price 'ZAR' $f | Select-Object -First 1).retailPrice
$usd = (Price 'USD' $f | Select-Object -First 1).retailPrice
if ($null -ne $zar) { $rows += [pscustomobject]@{ Resource = 'Azure SQL Basic (5 DTU)'; Unit = '1 day'; ZAR = $zar; USD = $usd; MonthZAR = [math]::Round($zar * 30.4, 2) } }
$rows += [pscustomobject]@{ Resource = 'Azure SQL free offer (serverless, auto-pause)'; Unit = 'month'; ZAR = 0; USD = 0; MonthZAR = 0 }

# Key Vault operations
$f = "serviceName eq 'Key Vault' and armRegionName eq '$Region' and skuName eq 'Standard' and meterName eq 'Operations'"
$zar = (Price 'ZAR' $f | Select-Object -First 1).retailPrice
$usd = (Price 'USD' $f | Select-Object -First 1).retailPrice
if ($null -ne $zar) { $rows += [pscustomobject]@{ Resource = 'Key Vault (per 10k operations)'; Unit = '10K ops'; ZAR = $zar; USD = $usd; MonthZAR = $null } }

$rows | Format-Table -AutoSize | Out-String -Width 200

$b1 = $rows | Where-Object { $_.Resource -like 'App Service Linux B1*' } | Select-Object -First 1
if ($b1 -and $b1.USD) {
    $rate = $b1.ZAR / $b1.USD
    "Azure's own rate: R{0:N2} per US dollar (billing is in USD)." -f $rate
    "Budget cap check: R500 = US`${0:N2}" -f (500 / $rate)
}
"Notes: a new app on an existing plan adds R0. Prices are list prices; confirm in the portal for discounts."
