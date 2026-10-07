<#
.SYNOPSIS
    Builds, checks and deploys AM-Pay and its self-service portal to Azure App Service.

.DESCRIPTION
    1. Runs the test suite once (skip with -SkipTests).
    2. For each app: publishes in Release, refuses to ship anything that looks like local
       data (App_Data, database files, the folder/ of client documents, user-secrets),
       zips with forward-slash paths (Windows PowerShell's Compress-Archive writes
       backslashes, which App Service on Linux rejects), deploys with `az webapp deploy`
       using the signed-in Azure account, and waits for /healthz to report it healthy.

    Each app migrates its own database when it starts.

.EXAMPLE
    .\deploy\deploy-azure.ps1                    # both apps
.EXAMPLE
    .\deploy\deploy-azure.ps1 -Target portal -SkipTests
.EXAMPLE
    .\deploy\deploy-azure.ps1 -AppName ampay-prod-x -PortalAppName ampay-portal-prod-x -ResourceGroup rg-ampay-prod

.NOTES
    Needs the .NET 8 SDK and the Azure CLI, signed in with `az login`.
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'ampay', 'portal')]
    [string] $Target = 'all',
    [string] $ResourceGroup = $(if ($env:AMPAY_AZ_RG) { $env:AMPAY_AZ_RG } else { 'rg-ampay-staging' }),
    [string] $AppName = $(if ($env:AMPAY_AZ_APP) { $env:AMPAY_AZ_APP } else { 'ampay-staging-o823r' }),
    [string] $PortalAppName = $(if ($env:AMPAY_AZ_PORTAL_APP) { $env:AMPAY_AZ_PORTAL_APP } else { 'ampay-portal-o823r' }),
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "`nDEPLOY STOPPED: $text" -ForegroundColor Red; exit 1 }

# ------------------------------------------------------------------ prerequisites

$az = (Get-Command az -ErrorAction SilentlyContinue).Source
if (-not $az) {
    $default = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
    if (Test-Path $default) { $az = $default } else { Fail 'Azure CLI not found. Install it: winget install --id Microsoft.AzureCLI -e' }
}

Step "Checking Azure sign-in"
$account = & $az account show --query "{sub:name, user:user.name}" -o json 2>$null | ConvertFrom-Json
if (-not $account) { Fail 'Not signed in to Azure. Run: az login' }
Write-Host "Subscription: $($account.sub) as $($account.user)"

$targets = @()
if ($Target -in 'all', 'ampay') {
    $targets += [pscustomobject]@{ Name = 'AM-Pay'; Project = 'src\AMPay.Web'; App = $AppName; MustContain = 'Contracts\Fonts\NotoSans-Regular.ttf' }
}
if ($Target -in 'all', 'portal') {
    $targets += [pscustomobject]@{ Name = 'Self-service portal'; Project = 'src\AMPay.Portal'; App = $PortalAppName; MustContain = 'wwwroot\js\network.js' }
}

foreach ($t in $targets) {
    $state = & $az webapp show --name $t.App --resource-group $ResourceGroup --query state -o tsv 2>$null
    if (-not $state) { Fail "Web app '$($t.App)' not found in resource group '$ResourceGroup'." }
    Write-Host "Target: $($t.Name) -> $($t.App) ($state)"
}

# What is being shipped, so a deploy can always be traced back to source.
$commit = (git -C $repo rev-parse --short HEAD 2>$null)
$dirty = (git -C $repo status --porcelain 2>$null)
if ($dirty) { Write-Host "Note: deploying uncommitted changes on top of $commit." -ForegroundColor Yellow }

if (-not $SkipTests) {
    Step "Running tests"
    dotnet test (Join-Path $repo 'tests\AMPay.Tests') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Tests failed. Nothing was deployed.' }
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

foreach ($t in $targets) {
    $work = Join-Path ([System.IO.Path]::GetTempPath()) "ampay-deploy-$($t.App)"
    $publish = Join-Path $work 'publish'
    $zip = Join-Path $work 'package.zip'

    Step "$($t.Name): publishing"
    if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) }
    dotnet publish (Join-Path $repo $t.Project) -c Release -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "$($t.Name): publish failed." }

    Step "$($t.Name): checking the package for local data"
    $files = Get-ChildItem -LiteralPath $publish -Recurse -File
    $forbidden = $files | Where-Object {
        $rel = $_.FullName.Substring($publish.Length + 1)
        $rel -match '(^|\\)App_Data(\\|$)' -or
        $rel -match '(^|\\)folder(\\|$)' -or
        $_.Extension -in '.mdf', '.ldf', '.bak' -or
        $_.Name -eq 'secrets.json'
    }
    if ($forbidden) {
        $forbidden | ForEach-Object { Write-Host "  $($_.FullName.Substring($publish.Length + 1))" }
        Fail "$($t.Name): the package contains local data or secrets (listed above). Nothing was deployed."
    }
    if (-not (Test-Path (Join-Path $publish $t.MustContain))) { Fail "$($t.Name): $($t.MustContain) is missing from the package." }
    Write-Host "$($files.Count) files, clean."

    Step "$($t.Name): packaging"
    $archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($f in $files) {
            $entry = $f.FullName.Substring($publish.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, 'Optimal')
        }
    }
    finally { $archive.Dispose() }
    Write-Host ("{0:N1} MB" -f ((Get-Item $zip).Length / 1MB))

    Step "$($t.Name): deploying to $($t.App)"
    & $az webapp deploy --name $t.App --resource-group $ResourceGroup --src-path $zip --type zip --async false --output none
    if ($LASTEXITCODE -ne 0) {
        Fail "$($t.Name): Azure rejected the deployment. Details: az webapp log deployment show --name $($t.App) --resource-group $ResourceGroup"
    }

    Step "$($t.Name): waiting for the app and its database"
    $url = "https://$($t.App).azurewebsites.net/healthz"
    $deadline = (Get-Date).AddMinutes(4)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 90
            if ($r.StatusCode -eq 200) { $healthy = $true; break }
        }
        catch { Write-Host "  not ready yet ($($_.Exception.Message.Split([Environment]::NewLine)[0]))" }
        # The free-offer databases pause when idle and can take up to a minute to wake.
        Start-Sleep -Seconds 10
    }
    if (-not $healthy) {
        Fail "$($t.Name): deployed, but $url did not answer within 4 minutes. Logs: az webapp log tail --name $($t.App) --resource-group $ResourceGroup"
    }

    Write-Host "$($t.Name): https://$($t.App).azurewebsites.net - healthy." -ForegroundColor Green
}

Write-Host "`nDeployed $commit$(if ($dirty) { ' + local changes' })." -ForegroundColor Green
