<#
  Publishes each sample Host deployment and checks what ships:
    - nothing from the heavy OpenTail stack (Core, Workflows, Semantic Kernel, Roslyn, ...) may be in the output
    - a deployment contains only the modules it names
  Also packs every Host package to prove they are packable.
  Usage: pwsh scripts/check-host-deployments.ps1 [-Keep] [-NoAudit] [-RestoreConfigFile <nuget.config>] [-ConnectivityFeed <url or folder>]
  The vulnerability audit is on unless -NoAudit.
  The Connectivity packages come from opentail-connectivity's feed (GitHub Packages: set GITHUB_PACKAGES_TOKEN). Without a token,
  point -ConnectivityFeed at a folder of those packages and -RestoreConfigFile at a nuget.config that maps them to it.
#>
param([switch]$Keep, [switch]$NoAudit, [string]$RestoreConfigFile, [string]$ConnectivityFeed = 'https://nuget.pkg.github.com/opentail-net/index.json')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ("otail-deploy-check-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$audit = if ($NoAudit) { '-p:NuGetAudit=false' } else { '-p:NuGetAudit=true' }
$restoreArgs = @(); if ($RestoreConfigFile) { $restoreArgs += "-p:RestoreConfigFile=$((Resolve-Path $RestoreConfigFile).Path)" }

$forbidden = @(
  'OpenTail.Core', 'OpenTail.Core.Agents', 'OpenTail.Core.Workflows', 'OpenTail.WorkflowRuntime', 'OpenTail.TaskRuntime',
  'Microsoft.SemanticKernel*', 'Microsoft.CodeAnalysis*', 'System.Reactive', 'LLamaSharp*', 'Hangfire*', 'Spectre.Console*')

$deployments = @(
  @{ Name = 'OpenTail.Host.Sample.EmailOnly';    Must = @('OpenTail.Host','OpenTail.Host.Abstractions','OpenTail.Modules.Email','MailKit');
                                                 MustNot = @('OpenTail.Modules.OpenApi','OpenTail.Modules.Files','OpenTail.Modules.Sql','OpenTail.Modules.Web','OpenTail.Modules.Calendar','OpenTail.Modules.Csv','Microsoft.OpenApi*') },
  @{ Name = 'OpenTail.Host.Sample.FilesOpenApi'; Must = @('OpenTail.Host','OpenTail.Host.Abstractions','OpenTail.Modules.Files','OpenTail.Modules.OpenApi','Microsoft.OpenApi');
                                                 MustNot = @('OpenTail.Modules.Email','MailKit','OpenTail.Connectivity.Inbox*','OpenTail.Host.Http','OpenTail.Host.Notifications') },
  @{ Name = 'OpenTail.Host.Sample.SqlServer';    Must = @('OpenTail.Host','OpenTail.Host.Abstractions','OpenTail.Modules.Sql','Microsoft.Data.SqlClient');
                                                 MustNot = @('OpenTail.Modules.Email','MailKit','OpenTail.Modules.Files','OpenTail.Modules.OpenApi','Npgsql','MySqlConnector','OpenTail.Host.Http') },
  @{ Name = 'OpenTail.Host.Sample.TeamHttp';     Must = @('OpenTail.Host','OpenTail.Host.Abstractions','OpenTail.Host.Http','OpenTail.Host.Notifications','OpenTail.Modules.Files','OpenTail.Modules.OpenApi','ModelContextProtocol.AspNetCore');
                                                 MustNot = @('OpenTail.Modules.Email','MailKit','OpenTail.Modules.Sql','OpenTail.Modules.Calendar') },
  # The container image's program: every module and profile compiled in, still nothing from the heavy stack.
  @{ Name = 'OpenTail.Host.Server'; SamplePolicy = $false;
                                                 Must = @('OpenTail.Host','OpenTail.Host.Http','OpenTail.Host.Notifications','ModelContextProtocol.AspNetCore',
                                                          'OpenTail.Modules.Files','OpenTail.Modules.Email.Imap','OpenTail.Modules.Microsoft365','OpenTail.Modules.Google',
                                                          'OpenTail.Modules.GitHubIssues','OpenTail.Modules.ServiceNow','OpenTail.Modules.OpenApi','OpenTail.Profiles.YouTube','OpenTail.Profiles.X','OpenTail.Profiles.Jira','OpenTail.Profiles.Confluence');
                                                 MustNot = @() })

$failed = $false
foreach ($d in $deployments) {
  $out = Join-Path $work $d.Name
  Write-Host "== publishing $($d.Name)"
  dotnet publish (Join-Path $root "$($d.Name)/$($d.Name).csproj") -c Release -o $out $audit @restoreArgs --nologo -v q | Out-Null
  if ($LASTEXITCODE -ne 0) { Write-Host "   publish FAILED"; $failed = $true; continue }

  $dlls = Get-ChildItem $out -Filter *.dll | ForEach-Object { $_.BaseName }
  $sizeMb = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
  Write-Host "   $($dlls.Count) assemblies, $sizeMb MB"

  foreach ($pattern in $forbidden) {
    $hit = $dlls | Where-Object { $_ -like $pattern }
    if ($hit) { Write-Host "   FORBIDDEN present: $($hit -join ', ')"; $failed = $true }
  }
  foreach ($pattern in $d.MustNot) {
    $hit = $dlls | Where-Object { $_ -like $pattern }
    if ($hit) { Write-Host "   SHOULD NOT ship: $($hit -join ', ')"; $failed = $true }
  }
  foreach ($name in $d.Must) {
    if ($dlls -notcontains $name) { Write-Host "   MISSING: $name"; $failed = $true }
  }
  if ($d.SamplePolicy -ne $false -and -not (Get-ChildItem $out -Filter 'policy.*.json')) { Write-Host "   MISSING sample policy file"; $failed = $true }
}

Write-Host "== packing Host packages"
$packs = 'OpenTail.Host.Abstractions','OpenTail.Host','OpenTail.Host.Admin',
         'OpenTail.Host.Notifications','OpenTail.Host.Http','OpenTail.Modules.Files','OpenTail.Modules.Email','OpenTail.Modules.OpenApi',
         'OpenTail.Modules.Csv','OpenTail.Modules.Sql','OpenTail.Modules.Calendar','OpenTail.Modules.Web','OpenTail.Modules.GitHubIssues',
         'OpenTail.Modules.Email.Imap','OpenTail.Modules.Calendar.Ics','OpenTail.Modules.Microsoft365','OpenTail.Modules.ServiceNow',
         'OpenTail.Modules.Social','OpenTail.Modules.Google','OpenTail.Modules.Storage',
         'OpenTail.Profiles.YouTube','OpenTail.Profiles.X','OpenTail.Profiles.Jira','OpenTail.Profiles.Confluence'
$pkgDir = Join-Path $work 'packages'
foreach ($p in $packs) {
  dotnet pack (Join-Path $root "$p/$p.csproj") -c Release -o $pkgDir $audit @restoreArgs --nologo -v q | Out-Null
  if ($LASTEXITCODE -ne 0) { Write-Host "   pack FAILED: $p"; $failed = $true }
}
Get-ChildItem $pkgDir -Filter *.nupkg -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("   {0} ({1} KB)" -f $_.Name, [math]::Round($_.Length / 1KB)) }

# All packages must carry one version (they ship as a set).
$versions = Get-ChildItem $pkgDir -Filter *.nupkg | ForEach-Object { $_.BaseName -replace '^OpenTail\.[A-Za-z0-9.]+?\.(\d+\.\d+\.\d+.*)$', '$1' } | Sort-Object -Unique
if (@($versions).Count -ne 1) { Write-Host "   packages have different versions: $($versions -join ', ')"; $failed = $true }
else { Write-Host "   all packages are version $versions" }

# A consumer outside the repo restores ONLY from the packed feed, the Connectivity feed and nuget.org, and builds: proves the
# dependency lists are complete.
Write-Host "== consuming the packages from a clean feed"
$pkgNames = Get-ChildItem $pkgDir -Filter 'OpenTail.Host.Http.*.nupkg' | Select-Object -First 1
$version = $pkgNames.BaseName.Substring('OpenTail.Host.Http.'.Length)
$consumer = Join-Path $work 'consumer'
New-Item -ItemType Directory -Path $consumer | Out-Null
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="local" value="$pkgDir" /><add key="connectivity" value="$ConnectivityFeed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping>
    <packageSource key="connectivity"><package pattern="OpenTail.Connectivity.*" /></packageSource>
    <packageSource key="local"><package pattern="OpenTail.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
  $(if ($ConnectivityFeed -like 'http*') { '<packageSourceCredentials><connectivity><add key="Username" value="opentail-net" /><add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" /></connectivity></packageSourceCredentials>' })
</configuration>
"@ | Set-Content (Join-Path $consumer 'NuGet.config')
$refs = ('Host.Http','Host.Notifications','Modules.Files','Modules.OpenApi','Modules.Csv','Modules.Sql','Modules.Email','Modules.Calendar','Modules.Web' |
  ForEach-Object { "<PackageReference Include=`"OpenTail.$_`" Version=`"$version`" />" }) -join ''
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>$refs</ItemGroup></Project>
"@ | Set-Content (Join-Path $consumer 'Consumer.csproj')
@'
using OpenTail.Host.Http;
using OpenTail.Modules.Csv;
using OpenTail.Modules.Files;
using OpenTail.Modules.Web;
return await HttpDeploymentRunner.RunAsync(args, "consumer", b =>
{
    b.AddModule(new FilesModule(new FilesModuleOptions { Root = Path.GetTempPath() }));
    b.AddModule(new CsvModule(new CsvModuleOptions { Root = Path.GetTempPath() }));
    b.AddModule(new WebModule(new WebModuleOptions { AllowedHosts = ["example.com"] }));
});
'@ | Set-Content (Join-Path $consumer 'Program.cs')
Push-Location $consumer
dotnet build -c Release --nologo -v q | Out-Null
$consumerOk = ($LASTEXITCODE -eq 0)
Pop-Location
if ($consumerOk) { Write-Host "   consumer restored from the feed and built" } else { Write-Host "   consumer build FAILED (a package dependency is missing or unpublished)"; $failed = $true }

# ...and runs: the packed assemblies load and the shared Main works (this prints a key and exits, it serves nothing).
if ($consumerOk) {
  $out = & dotnet (Join-Path $consumer 'bin/Release/net10.0/Consumer.dll') --new-api-key smoke 2>&1
  if ($LASTEXITCODE -eq 0 -and ($out -join "`n") -match 'otk_' -and ($out -join "`n") -match 'sha256:') { Write-Host "   consumer ran from the packed assemblies" }
  else { Write-Host "   consumer FAILED to run: $($out -join ' ')"; $failed = $true }
}

# A deployment that names no module in code: packages referenced, modules enabled by modules.json only (factories found via deps.json).
Write-Host "== enabling modules by package plus config only"
$bricks = Join-Path $work 'bricks'
New-Item -ItemType Directory -Path (Join-Path $bricks 'share') | Out-Null
Copy-Item (Join-Path $consumer 'NuGet.config') $bricks

$brickRefs = ('Host.Http','Modules.Files','Modules.GitHubIssues','Modules.Csv','Modules.Web','Modules.Sql','Modules.Email.Imap','Modules.Calendar.Ics','Modules.Microsoft365','Modules.ServiceNow','Modules.Social','Modules.Google','Profiles.YouTube' | ForEach-Object { "<PackageReference Include=`"OpenTail.$_`" Version=`"$version`" />" }) -join ''
"<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>$brickRefs</ItemGroup></Project>" |
  Set-Content (Join-Path $bricks 'Bricks.csproj')
'return await OpenTail.Host.Http.HttpDeploymentRunner.RunAsync(args, "bricks");' | Set-Content (Join-Path $bricks 'Program.cs')
'{ "Include": [ "policy.d" ], "Tools": {} }' | Set-Content (Join-Path $bricks 'policy.json')
('{ "Modules": [',
 '  { "Type": "files", "Id": "docs", "Config": { "Root": "share" } },',
 '  { "Type": "csv", "Id": "exports", "Config": { "Root": "share" } },',
 '  { "Type": "calendar-google", "Id": "gcal", "Config": { "ClientId": "x.apps.googleusercontent.com", "Account": "a@corp.example", "ReadOnly": true },',
 '    "Credentials": { "client-secret": { "Env": "OTAIL_BRICKS_GCS", "Kind": "Secret" }, "refresh-token": { "Env": "OTAIL_BRICKS_GRT", "Kind": "Secret" } } },',
 '  { "Type": "social", "Id": "social", "Network": { "Hosts": [ "oauth.reddit.com" ] },',
 '    "Credentials": { "reddit": { "Env": "OTAIL_BRICKS_REDDIT", "Host": "oauth.reddit.com" } },',
 '    "Config": { "Reddit": { } } },',
 '  { "Type": "openapi", "Id": "yt", "Network": { "Hosts": [ "www.googleapis.com" ] },',
 '    "Credentials": { "youtube": { "Env": "OTAIL_BRICKS_YT", "Kind": "Header", "HeaderName": "X-Goog-Api-Key", "Host": "www.googleapis.com" } },',
 '    "Config": { "Profile": "package:OpenTail.Profiles.YouTube/youtube" } },',
 '  { "Type": "servicenow", "Id": "sn", "Network": { "Hosts": [ "acme.service-now.com" ] },',
 '    "Credentials": { "servicenow": { "Env": "OTAIL_BRICKS_SN", "Host": "acme.service-now.com" } },',
 '    "Config": { "InstanceUrl": "https://acme.service-now.com" } },',
 '  { "Type": "web", "Id": "site", "Network": { "Hosts": [ "example.com" ] } },',
 '  { "Type": "sql", "Id": "crm", "Config": { "Provider": "sqlite", "ConnectionString": "Data Source=share/crm.db" } },',
 '  { "Type": "email-imap", "Id": "support", "Credentials": { "password": { "Env": "OTAIL_BRICKS_MAIL", "Kind": "Secret" } },',
 '    "Config": { "UserName": "a@corp.example", "Imap": { "Host": "imap.corp.example" }, "AllowSend": false } },',
 '  { "Type": "calendar-ics", "Id": "team", "Network": { "Hosts": [ "calendar.corp.example" ] }, "Config": { "Url": "https://calendar.corp.example/t.ics" } },',
 '  { "Type": "calendar-graph", "Id": "cal", "Credentials": { "client-secret": { "Env": "OTAIL_BRICKS_GRAPH", "Kind": "Secret" } },',
 '    "Config": { "TenantId": "t", "ClientId": "c", "Mailbox": "a@corp.example" } },',
 '  { "Type": "github-issues", "Id": "github", "Network": { "Hosts": [ "api.github.com" ] },',
 '    "Credentials": { "github": { "Env": "OTAIL_BRICKS_TOKEN", "Host": "api.github.com" } },',
 '    "Config": { "Repositories": [ "acme/support" ] } } ] }') -join "`n" | Set-Content (Join-Path $bricks 'modules.json')
Push-Location $bricks
dotnet build -c Release --nologo -v q | Out-Null
$bricksOk = ($LASTEXITCODE -eq 0)
if ($bricksOk) {
  $dll = Join-Path $bricks 'bin/Release/net10.0/Bricks.dll'
  $adopt = (& dotnet $dll --policy policy.json --modules modules.json --adopt-policy github 2>&1) -join "`n"
  $list = (& dotnet $dll --policy policy.json --modules modules.json --list-modules 2>&1) -join "`n"
  if ($adopt -match 'Wrote' -and $list -match 'module github \(github-issues' -and $list -match 'module docs \(files' -and $list -match 'github.add_comment\s+ExternalEffect\s+Queue' -and
      $list -match 'module exports \(csv' -and $list -match 'module site \(web' -and $list -match 'module crm \(sql' -and
      $list -match 'module support \(email-imap' -and $list -match 'module team \(calendar-ics' -and $list -match 'module cal \(calendar-graph' -and
      $list -match 'sn.add_comment\s+ExternalEffect' -and $list -match 'module social \(social' -and $list -match 'module yt \(openapi' -and $list -match 'yt\.get_video\s+Read' -and $list -match 'module gcal \(calendar-google') {
    Write-Host "   modules found from packages, suggested policy adopted, listed with their policy"
  } else { Write-Host "   package-plus-config deployment FAILED:`n$adopt`n$list"; $failed = $true }

  # The same program is its own module worker (Isolation: Process): hand it an init over stdin and expect the module's tools back.
  $init = '{"op":"init","rid":1,"id":"docs","type":"files","config":{"Root":"share"},"baseDirectory":' + (ConvertTo-Json $bricks) + ',"credentials":[],"factoryAssemblies":[]}'
  $reply = ($init | & dotnet $dll --module-worker 2>$null) -join "`n"
  if ($reply -match '"ok":true' -and $reply -match 'docs\.read' -and $reply -match '"assembly":"OpenTail\.Modules\.Files"') { Write-Host "   the deployment runs a module in a worker process (--module-worker)" }
  else { Write-Host "   module worker FAILED: $reply"; $failed = $true }
} else { Write-Host "   package-plus-config deployment did not build"; $failed = $true }
Pop-Location

if (-not $Keep) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Host "kept: $work" }
if ($failed) { Write-Host "DEPLOYMENT CHECK FAILED"; exit 1 }
Write-Host "Deployment check passed."
