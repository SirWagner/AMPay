# Failures met so far, and what fixed them

## Deployment

| Symptom | Cause | Fix |
|---|---|---|
| rsync `Invalid argument (22)` for hundreds of files, `failed to stat "/home/site/wwwroot/wwwroot\lib\..."` | Zip built with `Compress-Archive` on Windows PowerShell: backslash paths | Build the zip with `scripts/zip-for-linux.ps1` (or the repo deploy script) |
| `Site failed to start within 10 mins`, exit code **134**; logs show `Database '…' is not currently available` (**40613**) | Free/serverless database still waking from its idle pause; the app crashed on its startup migration | App must retry startup migration (pattern in `provision.md` §6) and have `WEBSITES_CONTAINER_START_TIME_LIMIT=600`. Waking it first with a `/healthz` call also helps |
| `HTTP_502`, `Kudu Status: 502` right at upload | The deployment service was restarting - usually because app settings were just changed | Wait a minute and run the deploy again |
| Deployment "failed" but the site is fine a few minutes later | Azure's own start probe timed out while the app was still waiting for its database | Check `/healthz` before redeploying; read the startup log (below) |
| App redirect-loops, or everyone shares one rate limit | HTTPS ends at Azure's front end; app sees HTTP from one address | `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` |
| 404 "Your web app is running and waiting for your content" | App created, code never deployed | Deploy |
| Running app throws "Invalid column name …" after adding a migration | `dotnet ef migrations add` builds *before* writing the migration; the dll you ran was older | Rebuild after adding a migration, then publish |

## Configuration and identity

| Symptom | Cause | Fix |
|---|---|---|
| `"-o was unexpected at this time."` or settings silently missing | `az.cmd` runs through `cmd.exe`; parentheses in arguments break it (Key Vault references, `--query length(...)`) | Pass settings/bodies as `@file.json`; avoid functions with parentheses in `--query` |
| Connection-string set rejects the file | File is an object, not a list | List of `{name, type, slotSetting, value}` |
| Key Vault reference shows the literal `@Microsoft.KeyVault(...)` text | App identity lacks **Key Vault Secrets User**, or the role has not propagated | Grant (with approval), wait ~1 minute, restart; check `configreferences` status = `Resolved` |
| SQL grant "worked" but the app cannot log in | A `SELECT` with a collation clash in the same batch cancelled the whole batch | Run `CREATE USER`/`ALTER ROLE` in their own batch, then verify membership |
| `RandomNumberGenerator does not contain a method named 'GetBytes'` | Windows PowerShell 5.1 (.NET Framework) has no static overload | `[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)` |

## Signing in to Azure

| Symptom | Cause | Fix |
|---|---|---|
| `AADSTS530035: Access has been blocked by security defaults` with `--use-device-code` | Tenant security defaults block device-code sign-in | Use normal `az login` |
| `az login` hangs at "Select the account you want to log in with" | Windows account broker opened a dialog behind other windows | `az config set core.enable_broker_on_windows=false`, then `az login` |
| Sign-in opens the wrong browser | Default browser is not where the user is signed in | `$env:BROWSER = '"C:\Program Files\Google\Chrome\Application\chrome.exe" %s'` before `az login` |
| `AADSTS90123 ... access_denied` | Sign-in cancelled or denied in the browser | Retry; pick the account that owns the subscription |

## Reading logs

- Enable container logs once: `az webapp log config --name <app> --resource-group rg-ampay-staging --docker-container-logging filesystem`.
- `az webapp log download --log-file <zip>` then read the newest `*_default_docker.log` **straight
  from the zip** with `System.IO.Compression.ZipFile` - extracting hits Windows' path-length limit.
- `*_docker.log` (without `default_`) is the platform's own log: start/stop states, exit codes.
- Database state: `az sql db show --name <db> --server sql-ampay-staging-o823r --resource-group rg-ampay-staging --query "{status:status,pausedDate:pausedDate,resumedDate:resumedDate}"`.

## Local testing before Azure

Visual Studio and the Claude shell run *separate* LocalDB instances that fight over the same
`.mdf`. Test on an isolated instance (`sqllocaldb create AMPayTest`), and drop each test database
with `DROP DATABASE` before deleting the instance - deleting the instance leaves the files behind,
and the next `CREATE DATABASE` with that name fails with error 5170.
