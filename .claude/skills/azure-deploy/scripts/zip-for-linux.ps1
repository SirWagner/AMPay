<#
.SYNOPSIS
    Zips a publish folder so Azure App Service on Linux accepts it.

.DESCRIPTION
    Windows PowerShell 5.1's Compress-Archive stores paths with backslashes
    ("wwwroot\css\site.css"). Linux reads those as file names containing backslashes and the
    deployment fails with rsync "Invalid argument (22)" for every file in a subfolder. This
    writes forward-slash entries instead, and refuses folders that hold local data.

.EXAMPLE
    powershell -File .claude/skills/azure-deploy/scripts/zip-for-linux.ps1 -Source C:\temp\publish -Destination C:\temp\app.zip
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Destination
)

$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path $Source).Path.TrimEnd('\')

$files = Get-ChildItem -LiteralPath $Source -Recurse -File
$forbidden = $files | Where-Object {
    $rel = $_.FullName.Substring($Source.Length + 1)
    $rel -match '(^|\\)App_Data(\\|$)' -or $rel -match '(^|\\)folder(\\|$)' -or
    $_.Extension -in '.mdf', '.ldf', '.bak' -or $_.Name -eq 'secrets.json'
}
if ($forbidden) {
    $forbidden | ForEach-Object { Write-Host "  $($_.FullName.Substring($Source.Length + 1))" }
    throw 'The folder contains local data or secrets (listed above). Nothing was zipped.'
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $Destination) { [System.IO.File]::Delete($Destination) }

$archive = [System.IO.Compression.ZipFile]::Open($Destination, 'Create')
try {
    foreach ($f in $files) {
        $entry = $f.FullName.Substring($Source.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, 'Optimal')
    }
}
finally { $archive.Dispose() }

"{0} files -> {1} ({2:N1} MB)" -f $files.Count, $Destination, ((Get-Item -LiteralPath $Destination).Length / 1MB)
