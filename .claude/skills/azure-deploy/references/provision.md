# Provisioning a new app (and its database) on the shared plan

Run from **PowerShell**. Only after the user has approved the resources and the cost.
Replace `<app>` (globally unique, e.g. `ampay-payroll-o823r`) and `<db>` (e.g. `ampay-payroll`).

```powershell
$az  = "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
$rg  = "rg-ampay-staging"
$srv = "sql-ampay-staging-o823r"
$kv  = "kv-ampay-o823r"
$sp  = "<scratchpad folder>"     # for request bodies; never the repo
```

## 1. Web app on the existing plan (R0 extra)

```powershell
& $az webapp create --name <app> --resource-group $rg --plan asp-ampay-staging --runtime "DOTNETCORE:8.0" --assign-identity "[system]" --https-only true --query "{host:defaultHostName,state:state}" -o tsv
& $az webapp config set --name <app> --resource-group $rg --ftps-state Disabled --min-tls-version 1.2 --always-on true --http20-enabled true --query "{ftps:ftpsState,tls:minTlsVersion}" -o tsv
foreach ($n in 'scm','ftp') { & $az resource update --resource-group $rg --namespace Microsoft.Web --resource-type basicPublishingCredentialsPolicies --parent "sites/<app>" --name $n --set properties.allow=false --query properties.allow -o tsv }
```

Turning basic publishing credentials off is fine: `az webapp deploy` signs in with your Azure account.

## 2. Database on the free offer (R0)

```powershell
& $az sql db create --name <db> --server $srv --resource-group $rg --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 2 --use-free-limit --free-limit-exhaustion-behavior AutoPause --backup-storage-redundancy Local --query "{name:name,free:useFreeLimit,status:status}" -o json
```

`AutoPause` means "stop until next month" if the free allowance runs out - it can never bill.
It also pauses after 60 minutes idle; the first request afterwards waits while it wakes.

## 3. Let the app's identity into its own database only

The server is Entra-only (no SQL passwords). Open the firewall for this PC briefly, grant, close it:

```powershell
$ip = Invoke-RestMethod "https://api.ipify.org"
& $az sql server firewall-rule create --resource-group $rg --server $srv --name TempSetupClient --start-ip-address $ip --end-ip-address $ip --query name -o tsv
try {
  $token = & $az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
  $cn = New-Object System.Data.SqlClient.SqlConnection "Server=tcp:$srv.database.windows.net,1433;Database=<db>;Encrypt=True;Connection Timeout=120"
  $cn.AccessToken = $token; $cn.Open()
  $cmd = $cn.CreateCommand()
  $cmd.CommandText = "IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '<app>') CREATE USER [<app>] FROM EXTERNAL PROVIDER; ALTER ROLE db_owner ADD MEMBER [<app>];"
  [void]$cmd.ExecuteNonQuery(); $cn.Close()
} finally {
  & $az sql server firewall-rule delete --resource-group $rg --server $srv --name TempSetupClient
}
```

Keep the grant statements in their own batch: a collation error in any `SELECT` in the same batch
(e.g. concatenating `DB_NAME()` with a name) cancels the whole batch, grant included.
`db_owner` is needed because the app migrates its schema at startup; tighten it once migrations
run from the pipeline instead.

## 4. Secrets into Key Vault, never into config or chat

Generate straight into a scratch file, store, delete. Windows PowerShell 5.1 has no static
`RandomNumberGenerator.GetBytes(int)`; use the instance form:

```powershell
$f = "$sp\kv-secret.tmp"
$b = New-Object byte[] 36; [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
Set-Content -LiteralPath $f -Value ([Convert]::ToBase64String($b).Replace('+','A').Replace('/','B')) -NoNewline -Encoding ascii
& $az keyvault secret set --vault-name $kv --name "<Section>--<Key>" --file $f --query name -o tsv
[System.IO.File]::Delete($f)
```

Secret names use `--` for the `:` in .NET configuration keys (`Portal--ApiKey` = `Portal:ApiKey`).

Grant the app read access (a role grant - needs the user's explicit OK):

```powershell
$kvId = & $az keyvault show --name $kv --query id -o tsv
$appPid = & $az webapp identity show --name <app> --resource-group $rg --query principalId -o tsv
& $az role assignment create --assignee-object-id $appPid --assignee-principal-type ServicePrincipal --role "Key Vault Secrets User" --scope $kvId --query roleDefinitionName -o tsv
```

## 5. App settings and connection string - always via JSON files

`az.cmd` goes through `cmd.exe`, which breaks on the parentheses in Key Vault references. Write
the settings to a file and pass `@file`:

```powershell
ConvertTo-Json -InputObject @(@{ name = "DefaultConnection"; type = "SQLAzure"; slotSetting = $false; value = "Server=tcp:$srv.database.windows.net,1433;Database=<db>;Authentication=Active Directory Managed Identity;Encrypt=True;Connection Timeout=90" }) | Set-Content "$sp\conn.json" -Encoding ascii
& $az webapp config connection-string set --name <app> --resource-group $rg --settings "@$sp\conn.json" --output none

[ordered]@{
  "ASPNETCORE_ENVIRONMENT" = "Production"
  "ASPNETCORE_FORWARDEDHEADERS_ENABLED" = "true"
  "WEBSITES_CONTAINER_START_TIME_LIMIT" = "600"
  "TZ" = "Africa/Johannesburg"
  "<Section>__<Key>" = "@Microsoft.KeyVault(VaultName=$kv;SecretName=<Section>--<Key>)"
} | ConvertTo-Json | Set-Content "$sp\settings.json" -Encoding ascii
& $az webapp config appsettings set --name <app> --resource-group $rg --settings "@$sp\settings.json" --query "[].name" -o tsv
```

The connection-string file must be a **list** of `{name, type, slotSetting, value}`; a plain
object is rejected. The connection-string name must match what the app reads
(`GetConnectionString("DefaultConnection")`, or `"Portal"` for the portal).

Check each Key Vault reference resolved:

```powershell
$sub = & $az account show --query id -o tsv
& $az rest --method get --url "https://management.azure.com/subscriptions/$sub/resourceGroups/$rg/providers/Microsoft.Web/sites/<app>/config/configreferences/appsettings/<Section>__<Key>?api-version=2023-12-01" --query "properties.status" -o tsv
```

## 6. The app's own code must survive a sleeping database

Copy this pattern into the new app's `Program.cs` around the startup migration (AM-Pay and the
portal both have it):

```csharp
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
for (var attempt = 1; ; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MyDbContext>().Database.MigrateAsync();
        break;
    }
    catch (Exception ex) when (attempt < 16 && ex is not OperationCanceledException)
    {
        startupLog.LogWarning("Database not ready (attempt {Attempt}): {Message} Retrying in 30 seconds.",
            attempt, ex.GetBaseException().Message);
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}
```

And a health endpoint the deploy script waits on:

```csharp
app.MapGet("/healthz", async (MyDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Text("ok") : Results.Text("database unreachable", statusCode: 503));
```

Also register `UseSqlServer(..., sql => sql.EnableRetryOnFailure())`.

## 7. Add it to the deploy script and the pause runbook

- `deploy/deploy-azure.ps1`: add a target (name, project path, app name, a file that must be in
  the package) so it deploys like the others.
- The pause runbook stops every app on the plan - add the new app (see `budget-cap.md`), or the
  budget cap will scale the plan down under a still-running app.
