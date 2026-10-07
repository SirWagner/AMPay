---
name: azure-deploy
description: Inspect a .NET web app (with or without a SQL database) and put it on Azure at the lowest possible cost - pricing it in rand first, provisioning App Service + Azure SQL + Key Vault with managed identity, deploying, verifying, and keeping it under the monthly budget cap that pauses the apps. Use this whenever the user wants to deploy, ship, host, publish or "put on Azure" an app or a new app in this repo (AM-Pay, the self-service portal, Payroll, Point of Sale or anything new), add a database to Azure, redeploy after changes, check Azure cost, or fix a failed Azure deployment - even if they only say "ship it" or "push it live".
---

# Azure deploy, at the lowest cost

This skill takes an app from "it runs locally" to "it runs on Azure, healthy, inside the budget".
It was written from real deployments of AM-Pay and its self-service portal, and the notes on what
went wrong are as important as the steps.

**The two rules that override everything else**

1. **Keep the cost as low as it can be.** The owner pays in US dollars with no spending limit,
   under a monthly cap (currently US$30, about R500) that pauses the apps when hit. Every resource
   you add must justify its cost; the cheapest option that genuinely works wins.
2. **Ask before anything that costs money, grants permissions or deploys.** Creating a paid
   resource, granting an identity a role, and deploying code each need the user's explicit "yes"
   in this conversation. Show the exact resources and the monthly cost in rand first. A "yes" to one
   deploy is not a standing "yes" to the next.

Never print a secret (passwords, API keys, webhook URLs, connection strings with keys). Generate
them straight into Key Vault through a temporary file you delete, and hand values to the user via
Key Vault or their clipboard - not the chat.

## Step 1 - Look at the app

Before touching Azure, find out what you are deploying:

- The project (`*.csproj` with `Microsoft.NET.Sdk.Web`), its target framework and how it starts.
- Its database: `DbContext`s, connection-string names, whether it migrates itself at startup.
- Configuration it needs (`appsettings.json` sections, options classes) and which values are secret.
- Files that must ship (fonts, static assets) and files that must **never** ship: `App_Data`,
  `folder/` (real client documents in this repo), `*.mdf/*.ldf/*.bak`, `secrets.json`.
- Whether it calls other apps (e.g. AM-Pay calls the portal's API with a shared key).

Then check what already exists in Azure - reuse beats create:

```bash
az account show --query "{sub:name, user:user.name}" -o json
az resource list --resource-group rg-ampay-staging --query "[].{name:name,type:type}" -o table
```

Current staging (South Africa North, resource group `rg-ampay-staging`): Linux B1 plan
`asp-ampay-staging`, apps `ampay-staging-o823r` and `ampay-portal-o823r`, SQL server
`sql-ampay-staging-o823r` (Entra-only auth), Key Vault `kv-ampay-o823r`, automation account
`aa-ampay-staging` with the pause runbook, budget `ampay-monthly-cap`.

## Step 2 - Choose the cheapest setup that works

| Need | Cheapest sensible choice | Why |
|---|---|---|
| Host a web app | **Join the existing B1 Linux plan** | A plan is billed per hour whether it runs 1 app or 5; a new app on it costs R0 |
| Host on its own (rare) | Linux B1 (~R292/month); F1 free only for throwaway demos | Windows plans cost ~4x Linux; F1 sleeps and caps CPU |
| Database | **Azure SQL free offer**: serverless, `--use-free-limit --free-limit-exhaustion-behavior AutoPause` | R0; up to 10 per subscription; pauses itself instead of billing past the free limit |
| Secrets | Existing Key Vault, RBAC, Key Vault references in app settings | Cents per month; no secret in config |
| SQL login | App's managed identity, Entra-only server | No SQL passwords to leak or rotate |
| Files | The app's own `/home` disk (persistent on App Service) | No storage account until blob code exists |
| Monitoring | App Service log stream / `az webapp log download` | Application Insights only when really needed |

Region: **South Africa North** (keeps client data in South Africa for POPIA).

Price it before asking. Use the bundled script (rand, live list prices):

```powershell
powershell -File .claude/skills/azure-deploy/scripts/price-check.ps1
```

Billing is in USD: convert the rand cap with the ratio the script prints, and say both.

Then ask for approval with a short table: each resource, its setting, its monthly cost in rand,
and the total, plus what it changes in the budget.

## Step 3 - Provision (after the "yes")

Follow `references/provision.md` - it has the exact commands for a new web app, its free
database, managed-identity database access, Key Vault secret and references, hardening, and the
settings every app here needs. The settings that are not optional:

- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` - Azure ends HTTPS in front of the app; without this
  the app sees plain HTTP, redirect-loops, and rate-limits everyone as one address.
- `WEBSITES_CONTAINER_START_TIME_LIMIT=600` - a paused database takes minutes to wake.
- The app itself must **retry its startup migration** while the database wakes (error 40613)
  instead of crashing. Both AM-Pay and the portal do this in `Program.cs`; copy the pattern.
- FTP and basic publishing credentials off, TLS 1.2+, HTTPS only, Always On.
- Connection string type `SQLAzure` with `Authentication=Active Directory Managed Identity`.

Every new app on the shared plan must also be added to the **pause runbook** (see
`references/budget-cap.md`), or the budget cap will not stop it.

## Step 4 - Deploy

For AM-Pay and the portal, use the repo script - it runs tests, refuses to ship local data,
zips correctly for Linux, deploys and waits for `/healthz`:

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\deploy-azure.ps1            # both apps
powershell -ExecutionPolicy Bypass -File .\deploy\deploy-azure.ps1 -Target portal -SkipTests
```

For a new app, add it to that script's target list (project path, app name, a file that must be
in the package) rather than deploying by hand. If you must deploy by hand, build the zip with
`scripts/zip-for-linux.ps1` - Windows PowerShell's `Compress-Archive` writes backslashes, and
Linux App Service then rejects every file in a subfolder.

Each app needs a `/healthz` endpoint that also checks its database; the script waits on it.

Deploy from an up-to-date `main` with a clean working tree, so what runs can be traced to a commit.

## Step 5 - Verify, then report

Check, don't assume:

```bash
curl -s -o /dev/null -w "%{http_code}" https://<app>.azurewebsites.net/healthz      # 200
```

- Key Vault references resolved: `.../config/configreferences/appsettings/<Name>?api-version=2023-12-01` → `Resolved`.
- Anything public returns what it should (sign-in page 200, unknown links 404, APIs 401 without a key).
- The database firewall holds only `AllowAzureServices` (remove any temporary rule you added).

Report in plain words: what is live and where, what it costs per month in rand, what was verified,
what was not, and anything the user must do themselves (you cannot create user accounts or set
passwords on the live site - the user does that in the app).

## When something fails

Read `references/troubleshooting.md` before guessing - it lists every failure met so far with its
cause and fix (paused database 40613, exit code 134, Kudu 502, backslash zips, `cmd.exe` and
parentheses, blocked sign-in methods, the hidden Windows sign-in dialog, Windows path-length limits
when reading logs).

## Working with `az` on this machine

- `az` lives at `C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd` and runs through
  `cmd.exe`: **parentheses break arguments**. Pass settings and request bodies as `@file.json`,
  and keep `--query` free of `(`/`)` (no `length()`, `keys()`, `starts_with()`).
- Run `az` from PowerShell; Git Bash mangles some paths.
- Sign-in: `az login` with `BROWSER` set to Chrome and the Windows broker off
  (`az config set core.enable_broker_on_windows=false`). Device-code sign-in is blocked by the
  tenant's security defaults.
