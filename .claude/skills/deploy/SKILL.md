---
name: deploy
description: Merge every open branch into main, run the full build and test suite, ship AM-Pay and the self-service portal to Azure staging, verify them, and hand over the links and the admin sign-in. Run only when the user types /deploy.
disable-model-invocation: true
---

# /deploy - merge everything, test, ship, hand over

The user typing `/deploy` is their explicit "yes" to **this run only**: merging the open branches
into `main` and deploying `main` to Azure staging. It is not a standing approval for the next run,
and it does not cover anything else that costs money or grants permissions - for those, stop and ask.

Follow the `azure-deploy` skill's rules throughout: lowest cost, nothing new provisioned, and no
secret printed in the chat. The repo is **public**.

Stop at the first failure, say exactly what failed and where, and leave things as they are - never
force-push, never skip hooks, never deploy a `main` whose tests failed.

## 1. Pre-flight

```bash
git status --short
gh auth status
git fetch --prune
```

```powershell
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' account show --query "user.name" -o tsv
```

- Not signed in to GitHub or Azure: stop and tell the user which sign-in to do (`gh auth login`, `az login`).
- Uncommitted changes to tracked files: list them and ask whether to commit, stash or leave them.
  This command never commits work it did not make. (`.claude/launch.json` may hold a local test
  entry - leave it alone.)
- A local copy of the app running from `src/AMPay.Web/bin/Release` locks the build: stop it first.

## 2. Collect every branch that is not in `main`

```bash
gh pr list --state open --base main --json number,headRefName,title
gh pr list --state open --json number,headRefName,baseRefName,title     # stacked PRs too
git branch -r --no-merged origin/main
git branch --no-merged main
```

- A local branch with commits not on `origin`: `git push -u origin <branch>`.
- A branch with no pull request: open one against `main` with `gh pr create --base main --fill`
  (end the body with the attribution line from the system reminder, if one is given).
- Skip `main`, and any branch the user has said to leave out.

**Order:** merge a branch before any branch built on top of it (`git merge-base --is-ancestor A B`
means A goes first). A stacked PR whose base branch is merged and deleted is retargeted to `main`
by GitHub; if not, `gh pr edit <n> --base main`.

## 3. Check each pull request before merging it

For each PR, in order:

```bash
gh pr view <n> --json mergeable,mergeStateStatus,statusCheckRollup,files
gh pr diff <n>
```

- **Not mergeable / conflicts**: stop. Name the PR and the conflicting files. Do not resolve
  conflicts on your own unless they are trivial and you show the user the resolution.
- **Failing checks**: stop and show which check failed.
- **Public-repo scan** of the diff - refuse to merge if it adds any of:
  - `appsettings.Local.json`, `appsettings.Development.json` with real values, `secrets.json`,
    `*.pfx`, `*.publishsettings`, anything under `folder/`, `App_Data/`, `*.mdf`, `*.ldf`, `*.bak`;
  - a Netcash service key, the software vendor key, an Azure key or connection string with a
    password or `AccountKey=`, a `Bearer` token;
  - a real South African ID or bank account number (test data uses obviously fictional ones,
    e.g. `8501015800085`, `1234567890`).

Then merge, keeping history:

```bash
gh pr merge <n> --merge --delete-branch
```

## 4. Test the merged `main`

```bash
git checkout main && git pull --ff-only
dotnet build src/AMPay.Web/AMPay.Web.csproj -c Release -nologo -v q
dotnet build src/AMPay.Portal/AMPay.Portal.csproj -c Release -nologo -v q
dotnet test tests/AMPay.Tests -c Release -nologo
```

Any build error or failing test: **stop - do not deploy.** Report the failures (test names and
messages). `main` now holds the merges; say so, and propose the fix as a new branch.

Note the commit being shipped: `git rev-parse --short HEAD`.

## 5. Ship to Azure staging

Tests already ran, so skip them in the script:

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\deploy-azure.ps1 -SkipTests
```

The script refuses to package local data or local settings, deploys both apps, and waits for each
`/healthz`. Each app migrates its own database on start; a paused free database can take a few
minutes to wake - that is normal, not a failure. On any failure read
`.claude/skills/azure-deploy/references/troubleshooting.md` before retrying.

## 6. Verify

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://ampay-staging-o823r.azurewebsites.net/healthz          # 200
curl -s -o /dev/null -w "%{http_code}\n" https://ampay-staging-o823r.azurewebsites.net/Account/Login  # 200
curl -s -o /dev/null -w "%{http_code}\n" https://ampay-portal-o823r.azurewebsites.net/healthz          # 200
```

Report anything that is not 200 rather than calling the deploy done.

## 7. Hand over the links and sign-in

- **App:** https://ampay-staging-o823r.azurewebsites.net
- **Self-service portal:** https://ampay-portal-o823r.azurewebsites.net
- **Admin email** - not a secret, read it from the app settings:

  ```powershell
  & 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' webapp config appsettings list -g rg-ampay-staging -n ampay-staging-o823r --query "[?name=='Seed__SuperAdmin__Email'].value" -o tsv
  ```

- **Admin password** - never in the chat. Copy it from Key Vault straight to the user's clipboard:

  ```powershell
  & 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' keyvault secret show --vault-name kv-ampay-o823r -n Seed--SuperAdmin--Password --query value -o tsv | Set-Clipboard
  ```

  Tell the user it is on their clipboard. This is the password the admin was **created** with; if
  someone changed it in the app since, it will not work - say so, and point to the app's
  forgotten-password page. Per-business logins are made inside the app; their passwords are
  hashed and cannot be read back.

## 8. Report

One short message:

| | |
|---|---|
| Merged | each PR as a link, `SirWagner/AMPay#n`, with its title |
| Tests | passed / total |
| Shipped | commit hash on `main`, both apps healthy |
| Links | app and portal |
| Sign-in | admin email; password copied to the clipboard |

Then what is **not** live: on staging every Netcash service runs on its test stand-in unless keys
were loaded into Key Vault (`deploy/azure-paynow-live.ps1` for Pay Now), and SMS and email go to
the outbox, not to clients. Mention the monthly budget cap (US$30) if the deploy added anything
that bills.
