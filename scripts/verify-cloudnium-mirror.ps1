<#
================================================================================================
 READ-ONLY. This script NEVER mutates anything, anywhere:

   - The rsync it runs against the remote host is ALWAYS invoked with --dry-run. There is no
     switch, parameter, or code path in this file that can remove --dry-run.
   - It never writes to $LocalPath, never writes to the remote host, never touches the
     Scheduled Task definition, and never truncates or rotates the sync log.
   - The only "write" this script performs is its own console/host output (and, if -OutFile is
     given, a plain text report file the caller explicitly asked for).

 It exists to answer, without changing anything: "if the real sync task ran right now, what
 would it change?" and "is the scheduled sync healthy?" -- as a pre-flight / spot-check for the
 real mutating pipeline that lives OUTSIDE this repo, at <LOCAL_MIRROR_PATH>\scripts\ (real path
 in the gitignored scripts\cloudnium.local.ps1 -- see scripts\cloudnium.example.ps1).
================================================================================================
#>

[CmdletBinding()]
param(
    [string]$RemoteHost,
    [string]$RemoteUser,
    [string]$RemotePath = "/opt/palworld/data/Pal/Saved/",
    [string]$LocalPath,
    [string]$SshKey,
    [string]$TaskName = "\Palworld Sync From Cloudnium",
    [string]$LogPath,
    [int]$LogTailLines = 40,
    [string]$OutFile
)

$ErrorActionPreference = "Stop"

# ------------------------------------------------------------------------------------------------
# Local coordinates. Real production values (host, user, key filename, local mirror paths) never
# live in this committed script -- they come from the gitignored scripts\cloudnium.local.ps1
# (copied from scripts\cloudnium.example.ps1), loaded here. Any value passed explicitly on the
# command line always wins over the local-config file.
# ------------------------------------------------------------------------------------------------
$cloudniumLocalConfig = Join-Path $PSScriptRoot "cloudnium.local.ps1"

$requiredParams = "RemoteHost", "RemoteUser", "LocalPath", "SshKey", "LogPath"
$missingFromCommandLine = $requiredParams | Where-Object { -not $PSBoundParameters.ContainsKey($_) }

if (Test-Path $cloudniumLocalConfig) {
    . $cloudniumLocalConfig

    if (-not $PSBoundParameters.ContainsKey('RemoteHost')) { $RemoteHost = $CloudniumRemoteHost }
    if (-not $PSBoundParameters.ContainsKey('RemoteUser')) { $RemoteUser = $CloudniumRemoteUser }
    if (-not $PSBoundParameters.ContainsKey('LocalPath')) { $LocalPath = $CloudniumLocalPath }
    if (-not $PSBoundParameters.ContainsKey('SshKey')) { $SshKey = $CloudniumSshKey }
    if (-not $PSBoundParameters.ContainsKey('LogPath') -and $CloudniumLogPath) { $LogPath = $CloudniumLogPath }
    if (-not $PSBoundParameters.ContainsKey('TaskName') -and $CloudniumTaskName) { $TaskName = $CloudniumTaskName }
    if (-not $PSBoundParameters.ContainsKey('RemotePath') -and $CloudniumRemotePath) { $RemotePath = $CloudniumRemotePath }
} elseif ($missingFromCommandLine.Count -gt 0) {
    Write-Host ""
    Write-Host "ERROR: No local Cloudnium config found, and required parameter(s) not supplied on the command line: $($missingFromCommandLine -join ', ')" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Copy scripts\cloudnium.example.ps1 to scripts\cloudnium.local.ps1 and fill in your" -ForegroundColor Yellow
    Write-Host "  real host, user, SSH key filename, and local mirror path:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "    Copy-Item scripts\cloudnium.example.ps1 scripts\cloudnium.local.ps1" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  scripts\cloudnium.local.ps1 is gitignored -- it is never committed. Alternatively," -ForegroundColor Yellow
    Write-Host "  pass every required value explicitly: -RemoteHost -RemoteUser -LocalPath -SshKey -LogPath" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

if (($requiredParams | Where-Object { -not (Get-Variable -Name $_ -ValueOnly -ErrorAction SilentlyContinue) }).Count -gt 0) {
    Write-Host "ERROR: scripts\cloudnium.local.ps1 exists but is missing one or more required values. Compare it against scripts\cloudnium.example.ps1." -ForegroundColor Red
    exit 1
}

# Accumulates non-fatal problems found along the way; surfaced in the summary and used to decide
# the exit code. This script never throws on a "the sync looks unhealthy" finding -- only on a
# genuine inability to check (e.g. WSL missing) -- so a caller always gets a full report.
$script:findings = [System.Collections.Generic.List[string]]::new()
$script:exitCode = 0

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "==== $Title ====" -ForegroundColor Cyan
}

function Add-Finding {
    param([string]$Message, [switch]$Fail)
    $script:findings.Add($Message)
    if ($Fail) {
        $script:exitCode = 1
    }
    Write-Host "  ! $Message" -ForegroundColor Yellow
}

function Invoke-WslReadOnly {
    <#
        Runs a single command line inside WSL via `wsl -e bash -lc`. This helper itself imposes
        no read-only restriction -- the caller is responsible for only ever passing read-only
        commands (rsync --dry-run, du, find, ssh <read-only remote command>). Every call site in
        this file is read-only by construction; see the banner above.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [int[]]$AllowedExitCodes = @(0)
    )

    $output = wsl -e bash -lc $Command 2>&1
    $code = $LASTEXITCODE
    if ($code -notin $AllowedExitCodes) {
        throw "WSL command failed (exit $code): $Command`n$output"
    }
    return $output
}

# ------------------------------------------------------------------------------------------------
# 0. Preconditions
# ------------------------------------------------------------------------------------------------
Write-Section "Preconditions"

if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) {
    throw "WSL is required (rsync and the SSH key live there), and it was not found on PATH."
}

Write-Host "  Remote:    ${RemoteUser}@${RemoteHost}:${RemotePath}"
Write-Host "  Local:     $LocalPath"
Write-Host "  SSH key:   $SshKey (resolved inside WSL)"
Write-Host "  Task:      $TaskName"
Write-Host "  Log:       $LogPath"

$wslLocalPath = (wsl -e wslpath -a $LocalPath).Trim()
if (-not $wslLocalPath) {
    throw "Could not convert local path to a WSL path: $LocalPath"
}

# ------------------------------------------------------------------------------------------------
# 1. Dry-run rsync -- same source, same excludes, same key invocation as the real sync, but
#    --dry-run is hard-coded and cannot be overridden by any parameter on this script.
# ------------------------------------------------------------------------------------------------
Write-Section "Dry-run rsync (what WOULD change)"

$sshOptions = "ssh -i $SshKey -o BatchMode=yes -o StrictHostKeyChecking=accept-new"
$remoteSpec = "${RemoteUser}@${RemoteHost}:$RemotePath"
$excludeArgs = @(
    "--exclude='*/world_save_temp/***'",
    "--exclude='*/world_save_bak/***'",
    "--exclude='*/backup/world/***'",
    "--exclude='.atomic_save_update_manifest_world.json'",
    "--exclude='*.new_tmp'"
) -join " "

# --dry-run is not a parameter of this script and is always present. --itemize-changes shows a
# per-file changed-flags line without transferring anything; --stats summarizes counts/bytes.
$rsyncCommand = "rsync -az --dry-run --itemize-changes --delete --delete-excluded --delay-updates " +
    "--partial --timeout=120 --stats $excludeArgs -e '$sshOptions' '$remoteSpec' '$wslLocalPath/'"

$rsyncOutput = Invoke-WslReadOnly -Command $rsyncCommand -AllowedExitCodes @(0, 24)
$rsyncOutput | ForEach-Object { Write-Host "  $_" }

# rsync's itemize-changes prefixes a deletion with '*deleting'. A deletion in the mirror
# direction (remote -> local) can legitimately happen when the remote pruned old save rotations,
# but it can also mean remote data loss -- either way it is worth a human's attention, hence the
# non-zero exit rather than a silent pass.
$deletionLines = $rsyncOutput | Where-Object { $_ -match '^\*deleting\s' }
$changeLines = $rsyncOutput | Where-Object { $_ -match '^[<>ch.*][fdLDS]' }

if ($deletionLines.Count -gt 0) {
    Add-Finding -Fail "Dry-run reports $($deletionLines.Count) deletion(s) in the mirror direction -- verify these are expected save-rotation pruning, not remote data loss."
    $deletionLines | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
} else {
    Write-Host "  No deletions reported." -ForegroundColor Green
}

if ($changeLines.Count -gt 0) {
    Write-Host "  $($changeLines.Count) file(s) would be created/updated."
} else {
    Write-Host "  No file changes reported -- local mirror already matches the remote (modulo excludes)."
}

# ------------------------------------------------------------------------------------------------
# 2. Scheduled task health
# ------------------------------------------------------------------------------------------------
Write-Section "Scheduled task: $TaskName"

$taskLeafName = Split-Path $TaskName -Leaf
$task = Get-ScheduledTask -TaskName $taskLeafName -ErrorAction SilentlyContinue |
    Where-Object { ("{0}{1}" -f $_.TaskPath, $_.TaskName) -eq $TaskName -or $_.TaskName -eq $taskLeafName } |
    Select-Object -First 1

if (-not $task) {
    Add-Finding -Fail "Scheduled task '$TaskName' was not found on this machine."
} else {
    $info = $task | Get-ScheduledTaskInfo
    Write-Host "  State:          $($task.State)"
    Write-Host "  Last run time:  $($info.LastRunTime)"
    Write-Host "  Last result:    $($info.LastTaskResult) (0 = success)"
    Write-Host "  Next run time:  $($info.NextRunTime)"

    if ($info.LastTaskResult -ne 0) {
        Add-Finding -Fail "Scheduled task last result was $($info.LastTaskResult), not 0."
    }

    $staleAfter = (Get-Date).AddHours(-3)
    if ($info.LastRunTime -and $info.LastRunTime -lt $staleAfter) {
        Add-Finding "Last run time ($($info.LastRunTime)) is more than 3 hours ago for an hourly task -- check whether the task is actually firing."
    }
}

# ------------------------------------------------------------------------------------------------
# 3. Sync log tail
# ------------------------------------------------------------------------------------------------
Write-Section "Sync log tail ($LogTailLines lines): $LogPath"

if (Test-Path $LogPath) {
    Get-Content -Path $LogPath -Tail $LogTailLines | ForEach-Object { Write-Host "  $_" }
} else {
    Add-Finding "Log file not found at '$LogPath' -- the task may never have run yet, or logs elsewhere."
}

# ------------------------------------------------------------------------------------------------
# 4. Local vs remote size/file-count comparison (read-only over SSH: du -sh / find | wc -l)
# ------------------------------------------------------------------------------------------------
Write-Section "Local mirror vs remote Pal/Saved (size and file count)"

$localSummary = $null
if (Test-Path $LocalPath) {
    $localFiles = Get-ChildItem -Path $LocalPath -Recurse -File -ErrorAction SilentlyContinue
    $localCount = $localFiles.Count
    $localBytes = ($localFiles | Measure-Object -Property Length -Sum).Sum
    $localSummary = "$localCount files, {0:N2} MB" -f ($localBytes / 1MB)
    Write-Host "  Local:  $localSummary"
} else {
    Add-Finding "Local path '$LocalPath' does not exist."
}

try {
    $remoteDuCommand = "$sshOptions ${RemoteUser}@${RemoteHost} 'du -sh $RemotePath 2>/dev/null'"
    $remoteFindCommand = "$sshOptions ${RemoteUser}@${RemoteHost} 'find $RemotePath -type f 2>/dev/null | wc -l'"

    $remoteDu = (Invoke-WslReadOnly -Command $remoteDuCommand).Trim()
    $remoteCount = (Invoke-WslReadOnly -Command $remoteFindCommand).Trim()

    Write-Host "  Remote: $remoteCount files, $remoteDu"
} catch {
    Add-Finding "Could not read remote size/file count over SSH: $($_.Exception.Message)"
}

# ------------------------------------------------------------------------------------------------
# Summary
# ------------------------------------------------------------------------------------------------
Write-Section "Summary"

if ($script:findings.Count -eq 0) {
    Write-Host "  No issues found." -ForegroundColor Green
} else {
    Write-Host "  $($script:findings.Count) finding(s):" -ForegroundColor Yellow
    $script:findings | ForEach-Object { Write-Host "   - $_" }
}

if ($OutFile) {
    $report = @()
    $report += "Cloudnium mirror verification report - $(Get-Date -Format o)"
    $report += ""
    $report += "Findings:"
    if ($script:findings.Count -eq 0) {
        $report += "  (none)"
    } else {
        $script:findings | ForEach-Object { $report += "  - $_" }
    }
    $report | Set-Content -Path $OutFile
    Write-Host ""
    Write-Host "Report written to $OutFile"
}

exit $script:exitCode
