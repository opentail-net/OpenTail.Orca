<#
.SYNOPSIS
  Builds the Teams app package for the OpenTail approvals bot: manifest.json plus the two icons, zipped for upload in the Teams admin center.

.EXAMPLE
  pwsh scripts/make-teams-app.ps1 -BotAppId 00000000-0000-0000-0000-000000000000 -PublicBaseUrl https://approvals.corp.example

.NOTES
  -AppId is the Teams app's own id; keep the same value for every later version of the package (it is generated when omitted and
  printed, so you can reuse it). The package holds no secret.
#>
param(
  [Parameter(Mandatory)] [string] $BotAppId,
  [Parameter(Mandatory)] [string] $PublicBaseUrl,
  [string] $AppId = [guid]::NewGuid().ToString(),
  [string] $Name = 'OpenTail approvals',
  [string] $Company = 'OpenTail',
  [string] $Version = '1.0.0',
  [string] $AccentColor = '#0969DA',
  [string] $Output = 'opentail-teams-app.zip'
)
$ErrorActionPreference = 'Stop'

if (-not [guid]::TryParse($BotAppId, [ref]([guid]::Empty))) { throw "BotAppId must be the bot's Microsoft App ID (a GUID)." }
$uri = [uri]$PublicBaseUrl
if ($uri.Scheme -ne 'https') { throw 'PublicBaseUrl must be https (Teams opens it and posts to the bot there).' }
$base = $PublicBaseUrl.TrimEnd('/')

# ---- a minimal PNG writer (solid shapes only), so the repository holds no binary icons ----
function Write-Png([string] $path, [int] $size, [scriptblock] $pixel) {
  $raw = New-Object System.IO.MemoryStream
  for ($y = 0; $y -lt $size; $y++) {
    $raw.WriteByte(0)                                   # filter: none
    for ($x = 0; $x -lt $size; $x++) { $p = & $pixel $x $y; $raw.Write([byte[]]$p, 0, 4) }
  }
  $z = New-Object System.IO.MemoryStream
  $zs = New-Object System.IO.Compression.ZLibStream($z, [System.IO.Compression.CompressionLevel]::Optimal)
  $raw.Position = 0; $raw.CopyTo($zs); $zs.Dispose()
  function Chunk([string] $type, [byte[]] $data) {
    $len = [BitConverter]::GetBytes([uint32]$data.Length); [Array]::Reverse($len)
    $td = [Text.Encoding]::ASCII.GetBytes($type) + $data
    $crc = [BitConverter]::GetBytes([System.IO.Hashing.Crc32]::HashToUInt32($td)); [Array]::Reverse($crc)
    return $len + $td + $crc
  }
  $ihdr = [byte[]]@(0,0,0,0, 0,0,0,0, 8, 6, 0, 0, 0)
  $s = [BitConverter]::GetBytes([uint32]$size); [Array]::Reverse($s)
  [Array]::Copy($s, 0, $ihdr, 0, 4); [Array]::Copy($s, 0, $ihdr, 4, 4)
  $png = [byte[]]@(137,80,78,71,13,10,26,10) + (Chunk 'IHDR' $ihdr) + (Chunk 'IDAT' $z.ToArray()) + (Chunk 'IEND' ([byte[]]@()))
  [IO.File]::WriteAllBytes($path, $png)
}
try { Add-Type -AssemblyName System.IO.Hashing -ErrorAction Stop } catch {
  # Older PowerShell without System.IO.Hashing: a small CRC-32 in place of it.
  Add-Type -TypeDefinition @'
namespace System.IO.Hashing { public static class Crc32 { public static uint HashToUInt32(byte[] d) { uint c = 0xFFFFFFFF;
  foreach (var b in d) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; } return ~c; } } }
'@
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("opentail-teams-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$hex = $AccentColor.TrimStart('#')
$r = [Convert]::ToByte($hex.Substring(0,2),16); $g = [Convert]::ToByte($hex.Substring(2,2),16); $b = [Convert]::ToByte($hex.Substring(4,2),16)
# Distance from (px,py) to the segment (ax,ay)-(bx,by): the check mark is two thick segments.
function SegDist([double]$px, [double]$py, [double]$ax, [double]$ay, [double]$bx, [double]$by) {
  $dx = $bx - $ax; $dy = $by - $ay
  $t = [math]::Max(0.0, [math]::Min(1.0, (($px - $ax) * $dx + ($py - $ay) * $dy) / ($dx * $dx + $dy * $dy)))
  return [math]::Sqrt([math]::Pow($px - ($ax + $t * $dx), 2) + [math]::Pow($py - ($ay + $t * $dy), 2))
}
# Colour icon: 192x192, the accent colour with a white check mark. Outline icon: 32x32, white check and frame on transparent.
Write-Png (Join-Path $work 'color.png') 192 { param($x,$y)
  $onCheck = (SegDist $x $y 52 100 82 132) -lt 11 -or (SegDist $x $y 82 132 142 64) -lt 11
  if ($onCheck) { @(255,255,255,255) } else { @($r,$g,$b,255) } }
Write-Png (Join-Path $work 'outline.png') 32 { param($x,$y)
  $edge = $x -lt 2 -or $y -lt 2 -or $x -gt 29 -or $y -gt 29
  $onCheck = (SegDist $x $y 8 16 13 22) -lt 1.8 -or (SegDist $x $y 13 22 24 10) -lt 1.8
  if ($edge -or $onCheck) { @(255,255,255,255) } else { @(0,0,0,0) } }

$manifest = [ordered]@{
  '$schema'       = 'https://developer.microsoft.com/en-us/json-schemas/teams/v1.17/MicrosoftTeams.schema.json'
  manifestVersion = '1.17'
  version         = $Version
  id              = $AppId
  developer       = [ordered]@{ name = $Company; websiteUrl = $base; privacyUrl = "$base/#privacy"; termsOfUseUrl = "$base/#terms" }
  name            = [ordered]@{ short = $Name; full = "${Name}: review what AI prepared before it is sent" }
  description     = [ordered]@{
    short = 'Approve, change or reject what your AI prepared.'
    full  = 'Actions an AI prepared for you (an email reply, a ticket comment) arrive here for a decision. Approve, change the text and approve, or reject with a reason. Nothing is sent until a person decides; the full details are on the review page.'
  }
  icons           = [ordered]@{ color = 'color.png'; outline = 'outline.png' }
  accentColor     = $AccentColor
  bots            = @([ordered]@{ botId = $BotAppId; scopes = @('personal', 'team'); supportsFiles = $false; isNotificationOnly = $false })
  permissions     = @('identity', 'messageTeamMembers')
  validDomains    = @($uri.Host)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $work 'manifest.json')

$outPath = [IO.Path]::GetFullPath($Output)
if (Test-Path $outPath) { Remove-Item $outPath }
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $outPath
Remove-Item $work -Recurse -Force
Write-Host "Teams app package: $outPath"
Write-Host "Teams app id: $AppId   (pass -AppId $AppId when you build the next version)"
