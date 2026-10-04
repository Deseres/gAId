# Publishes the API with the React app inside wwwroot and zips it for Azure App Service.
# On the App Service, set the stack to .NET 10 and these application settings:
#   OpenAI__ApiKey
#   Google__ApiKey
#   Google__MapsApiKey   optional browser key; if empty, Google__ApiKey is used

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root 'deploy\publish'
$zip = Join-Path $root 'deploy\guaid.zip'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Path (Split-Path $publish) -Force | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }

$frontend = Join-Path $root 'frontend'
if (-not (Test-Path (Join-Path $frontend 'node_modules'))) {
    npm ci --prefix $frontend
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
}

dotnet publish (Join-Path $root 'Back\GuAId.Api\GuAId.Api.csproj') -c Release -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$index = Join-Path $publish 'wwwroot\index.html'
if (-not (Test-Path $index)) { throw 'Frontend was not copied into the publish output.' }

tar -a -c -f $zip -C $publish .
if ($LASTEXITCODE -ne 0) { throw 'zip failed' }

Write-Host "Zip ready: $zip"
