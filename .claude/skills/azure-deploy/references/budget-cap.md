# The monthly budget cap that pauses the apps

The owner's rule: about **R500 a month**; if spend reaches it, **pause and notify**. Azure budgets
only notify, so the pause is built from four parts:

```
Budget ampay-monthly-cap (subscription, US$30/month, billed in USD)
  80% actual  -> email
  100% forecast -> email
  100% actual -> email + action group ag-ampay-budget-cap
                    -> webhook "budget-cap" -> runbook Pause-AmpayStaging (aa-ampay-staging)
                         -> turns Always On off, stops every app on the plan, scales the plan to F1 (free)
```

Alerts go to the addresses set on the budget and action group (kept out of this public repo). Azure's cost data lags
8-24 hours, so spend can run slightly past the cap before the pause fires. The free-offer
databases never bill past their allowance (they pause themselves), so the plan is the only thing
the runbook has to stop.

## Converting the cap

Billing is in **USD**. Convert R500 with Azure's own ratio from `scripts/price-check.ps1`
(about R16.45 per dollar in October 2026, so US$30). Recheck if the rand moves a lot.

## Adding a new app to the pause

The runbook source is `assets/Pause-AmpayStaging.ps1`. Add the app to `$apps`, then upload and
publish (runbook content is a `text/powershell` body, so this needs `az rest`):

```powershell
$az  = "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
$sub = & $az account show --query id -o tsv
$aa  = "https://management.azure.com/subscriptions/$sub/resourceGroups/rg-ampay-staging/providers/Microsoft.Automation/automationAccounts/aa-ampay-staging"
& $az rest --method put --url "$aa/runbooks/Pause-AmpayStaging/draft/content?api-version=2023-11-01" --headers "Content-Type=text/powershell" --body "@.claude/skills/azure-deploy/assets/Pause-AmpayStaging.ps1" --output none
& $az rest --method post --url "$aa/runbooks/Pause-AmpayStaging/publish?api-version=2023-11-01" --output none
```

The automation account's identity has **Website Contributor** and **Web Plan Contributor** on the
resource group, so a new app in the same group needs no new grant.

## Resuming after a pause

Scale the plan back to B1, turn Always On on for each app, start each app:

```powershell
& $az appservice plan update --name asp-ampay-staging --resource-group rg-ampay-staging --sku B1 --output none
foreach ($app in 'ampay-staging-o823r','ampay-portal-o823r') {
  & $az webapp config set --name $app --resource-group rg-ampay-staging --always-on true --output none
  & $az webapp start --name $app --resource-group rg-ampay-staging
}
```

Only with the user's go-ahead: resuming restarts the cost.

## Rebuilding the alert path from scratch (if ever lost)

1. Budget via `az rest PUT .../providers/Microsoft.Consumption/budgets/<name>?api-version=2023-05-01`
   with notifications `actual80`, `actual100` (with `contactGroups: [<action group id>]`),
   `forecast100`. Updates need the budget's current `eTag` in the body.
2. Automation account (Basic, system identity) and the two role grants above (needs approval).
3. Runbook, type `PowerShell72`, content from `assets/`, then publish.
4. Webhook: `POST .../webhooks/generateUri?api-version=2015-10-31`, then `PUT .../webhooks/budget-cap`.
   **The webhook URL is a secret** and can only be read at creation: keep it in a variable, write
   it only to a temp file you delete, never print it. If lost, delete and recreate the webhook.
5. Action group (`location: Global`) with the email receivers and an `automationRunbookReceivers`
   entry pointing at the webhook (`serviceUri` = the secret URL, `webhookResourceId`).
