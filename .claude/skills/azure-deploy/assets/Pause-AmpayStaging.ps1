<#
  Pause-AmpayStaging - run by the ampay-monthly-cap budget (via action group webhook) when
  spend reaches the monthly cap. Stops the AM-Pay staging web app and drops its plan to the
  Free tier, which ends the plan's hourly charge. Data is untouched: the SQL database is on
  the free offer and pauses itself.

  To resume: scale asp-ampay-staging back to B1, turn Always On back on, start the web app.
#>
param([object] $WebhookData)

$ErrorActionPreference = 'Stop'

$rg   = 'rg-ampay-staging'
$apps = @('ampay-staging-o823r', 'ampay-portal-o823r')   # everything on the plan
$plan = 'asp-ampay-staging'

Connect-AzAccount -Identity | Out-Null
$sub = (Get-AzContext).Subscription.Id
$base = "/subscriptions/$sub/resourceGroups/$rg/providers/Microsoft.Web"

function Call($method, $path, $payload) {
    $r = Invoke-AzRestMethod -Method $method -Path "$path`?api-version=2023-12-01" -Payload $payload
    if ($r.StatusCode -ge 300) { throw "$method $path failed: $($r.StatusCode) $($r.Content)" }
}

# The Free tier does not allow Always On, so it goes first, on every app on the plan.
foreach ($app in $apps) {
    Call PATCH "$base/sites/$app/config/web" '{"properties":{"alwaysOn":false}}'
    Call POST  "$base/sites/$app/stop" $null
}
Call PATCH "$base/serverfarms/$plan" '{"sku":{"name":"F1","tier":"Free"}}'

Write-Output "AM-Pay staging paused: $($apps -join ', ') stopped and $plan scaled to F1 (Free) at $((Get-Date).ToUniversalTime().ToString('u'))."
