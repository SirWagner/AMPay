<#
.SYNOPSIS
    Builds, checks and deploys AM-Pay to Azure App Service in one step.

.DESCRIPTION
    1. Runs the test suite (skip with -SkipTests).
    2. Publishes src/AMPay.Web in Release.
    3. Refuses to ship anything that looks like local data: App_Data, database files, the
       folder/ of client documents, user-secrets.
    4. Zips with forward-slash paths. Windows PowerShell's Compress-Archive writes
       backslashes, which App Service on Linux rejects file by file.
    5. Deploys with `az webapp deploy` (signed-in Azure account; no publishing passwords).
    6. Waits for /healthz to report the app and its database up.

    Database migrations run by themselves when the app starts.

.EXAMPLE
    .\deploy\deploy-azure.ps1
.EXAMPLE
    .\deploy\deploy-azure.ps1 -SkipTests
.EXAMPLE
    .\deploy\deploy-azure.ps1 -AppName ampay-prod-xxxxx -ResourceGroup rg-ampay-prod

.NOTES
    Needs the .NET 8 SDK and the Azure CLI, signed in with `az login` to the subscription
    that holds the app.
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = $(if ($env:AMPAY_AZ_RG) { $env:AMPAY_AZ_RG } else { 'rg-ampay-staging' }),
    [string] $AppName = $(if ($env:AMPAY_AZ_APP) { $env:AMPAY_AZ_APP } else { 'ampay-staging-o823r' }),
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([System.IO.Path]::GetTempPath()) 'ampay-deploy'
$publish = Join-Path $work 'publish'
$zip = Join-Path $work 'ampay.zip'

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

$state = & $az webapp show --name $AppName --resource-group $ResourceGroup --query state -o tsv 2>$null
if (-not $state) { Fail "Web app '$AppName' not found in resource group '$ResourceGroup'." }
Write-Host "Target: $AppName ($state)"

# What is being shipped, so a deploy can always be traced back to source.
$commit = (git -C $repo rev-parse --short HEAD 2>$null)
$dirty = (git -C $repo status --porcelain 2>$null)
if ($dirty) {
    Write-Host "Note: deploying uncommitted changes on top of $commit." -ForegroundColor Yellow
}

# ------------------------------------------------------------------ test and publish

if (-not $SkipTests) {
    Step "Running tests"
    dotnet test (Join-Path $repo 'tests\AMPay.Tests') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Tests failed. Nothing was deployed.' }
}

Step "Publishing"
if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) }
dotnet publish (Join-Path $repo 'src\AMPay.Web') -c Release -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'Publish failed.' }

# ------------------------------------------------------------------ what must never ship

Step "Checking the package for local data"
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
    Fail 'The package contains local data or secrets (listed above). Nothing was deployed.'
}
if (-not (Test-Path (Join-Path $publish 'Contracts\Fonts\NotoSans-Regular.ttf'))) {
    Fail 'The contract PDF fonts are missing from the package.'
}
Write-Host "$($files.Count) files, clean."

# ------------------------------------------------------------------ zip for Linux

Step "Packaging"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($f in $files) {
        $entry = $f.FullName.Substring($publish.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, 'Optimal')
    }
}
finally { $archive.Dispose() }
Write-Host ("{0:N1} MB" -f ((Get-Item $zip).Length / 1MB))

# ------------------------------------------------------------------ deploy

Step "Deploying to $AppName"
& $az webapp deploy --name $AppName --resource-group $ResourceGroup --src-path $zip --type zip --async false --output none
if ($LASTEXITCODE -ne 0) {
    Fail "Azure rejected the deployment. Details: az webapp log deployment show --name $AppName --resource-group $ResourceGroup"
}

# ------------------------------------------------------------------ verify

Step "Waiting for the app and database"
$url = "https://$AppName.azurewebsites.net/healthz"
$deadline = (Get-Date).AddMinutes(4)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 90
        if ($r.StatusCode -eq 200) { $healthy = $true; break }
    }
    catch { Write-Host "  not ready yet ($($_.Exception.Message.Split([Environment]::NewLine)[0]))" }
    # The free-offer database pauses when idle and can take up to a minute to wake.
    Start-Sleep -Seconds 10
}

if (-not $healthy) {
    Fail "Deployed, but $url did not answer within 4 minutes. Logs: az webapp log tail --name $AppName --resource-group $ResourceGroup"
}

Write-Host "`nDeployed $commit$(if ($dirty) { ' + local changes' }) to https://$AppName.azurewebsites.net - healthy." -ForegroundColor Green
