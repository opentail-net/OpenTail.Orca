#Requires -Version 7.0
<#
.SYNOPSIS
  Sends one A2A request to an OpenTail Host, follows it until it finishes, and says what a person must do next.
.DESCRIPTION
  Uses Invoke-RestMethod only. It reads the agent card, sends the goal with returnImmediately set to true, and asks GetTask for the task's
  state every few seconds until the task is COMPLETED, FAILED, CANCELED or REJECTED, or until the timeout passes. With -Cancel it cancels the
  task instead. The partner key goes in the Authorization header and is never printed. Read docs/guides/a2a.md for the whole flow.
.PARAMETER BaseUrl
  Where the Host listens, for example http://127.0.0.1:5600 (the address of the sample).
.PARAMETER PartnerKey
  The partner's API key. The sample's key is documented as sample only: replace it for a real partner.
.PARAMETER Goal
  The request's text. It is the partner's text, and a reviewer reads it exactly as sent.
.PARAMETER Cancel
  Send the request, then cancel it, instead of waiting for an answer.
.PARAMETER TimeoutSeconds
  How long to follow the task before stopping. The task keeps running on the Host; GetTask reads it again.
.PARAMETER PollSeconds
  How often to ask for the task's state.
.EXAMPLE
  ./scripts/a2a-smoke.ps1 -PartnerKey $env:OPENTAIL_PARTNER_KEY -Goal "Check the invoice."
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://127.0.0.1:5600',
    [Parameter(Mandatory)] [string] $PartnerKey,
    [string] $Goal = 'Smoke test: please confirm you received this.',
    [switch] $Cancel,
    [int] $TimeoutSeconds = 300,
    [int] $PollSeconds = 3
)

$ErrorActionPreference = 'Stop'
$base = $BaseUrl.TrimEnd('/')
$headers = @{ 'Authorization' = "Bearer $PartnerKey"; 'A2A-Version' = '1.0' }
$terminal = @('TASK_STATE_COMPLETED', 'TASK_STATE_FAILED', 'TASK_STATE_CANCELED', 'TASK_STATE_REJECTED')

function Send-A2ARequest([hashtable] $payload) {
    $body = $payload | ConvertTo-Json -Depth 12
    $reply = Invoke-RestMethod -Method Post -Uri "$base/a2a" -Headers $headers -ContentType 'application/json' -Body $body
    if ($reply.PSObject.Properties['error'] -and $reply.error) {
        throw "$($payload.method) was refused: $($reply.error.message) (JSON-RPC code $($reply.error.code))"
    }
    return $reply.result
}

try {
    # 1. The agent card: it proves the address and the key work together.
    $card = Invoke-RestMethod -Uri "$base/.well-known/agent-card.json" -Headers $headers
    Write-Host "Connected to '$($card.name)' at $($card.supportedInterfaces[0].url)."

    # 2. Send the request and return at once; a person decides on it later.
    $sent = Send-A2ARequest @{
        jsonrpc = '2.0'
        id      = '1'
        method  = 'SendMessage'
        params  = @{
            message       = @{ messageId = [guid]::NewGuid().ToString(); role = 'ROLE_USER'; parts = @(@{ text = $Goal }) }
            configuration = @{ returnImmediately = $true }
        }
    }
    $taskId = $sent.task.id
    Write-Host "Sent. Task $taskId is $($sent.task.status.state)."

    if ($Cancel) {
        $cancelled = Send-A2ARequest @{ jsonrpc = '2.0'; id = '2'; method = 'CancelTask'; params = @{ id = $taskId } }
        Write-Host "Cancelled. Task $taskId is $($cancelled.status.state)."
        exit 0
    }

    Write-Host "Next: a person approves the request on the review page: $base/  (look for task $taskId)."
    $last = $sent.task.status.state
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    # 3. Follow the task until it is finished, or until the timeout passes.
    while ($true) {
        $task = Send-A2ARequest @{ jsonrpc = '2.0'; id = '3'; method = 'GetTask'; params = @{ id = $taskId } }
        $state = $task.status.state
        if ($state -ne $last) {
            Write-Host "State: $state"
            if ($state -eq 'TASK_STATE_WORKING') {
                Write-Host "Next: an agent of the inbox profile takes the work (next_work), completes it (complete_work), and a person approves the reply on the review page: $base/"
            }
            $last = $state
        }
        if ($terminal -contains $state) { break }
        if ((Get-Date) -gt $deadline) {
            Write-Host "Stopped waiting after $TimeoutSeconds seconds. The task keeps running; read it again with GetTask (task $taskId)."
            exit 2
        }
        Start-Sleep -Seconds $PollSeconds
    }

    # 4. The end: the answer only when the task is COMPLETED.
    if ($state -eq 'TASK_STATE_COMPLETED') {
        Write-Host "Answer: $($task.artifacts[0].parts[0].text)"
    }
    else {
        Write-Host "Ended as $state. The partner gets no answer text for this state."
    }
    exit 0
}
catch {
    Write-Host "Failed: $($_.Exception.Message)"
    exit 1
}
