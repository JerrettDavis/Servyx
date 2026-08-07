<#
================================================================================================
 Template for scripts\cloudnium.local.ps1 -- the gitignored file that holds YOUR real Cloudnium /
 Palworld host coordinates for verify-cloudnium-mirror.ps1.

 Setup:
   1. Copy this file to scripts\cloudnium.local.ps1 (already ignored by git via `scripts/*.local.ps1`
      in .gitignore -- verify with `git check-ignore -v scripts/cloudnium.local.ps1`).
   2. Replace every placeholder below with your real values.
   3. Run scripts\verify-cloudnium-mirror.ps1 -- it dot-sources cloudnium.local.ps1 automatically
      if present.

 Do NOT fill in real values in THIS file -- cloudnium.example.ps1 is committed to git and must stay
 a safe, generic template.
================================================================================================
#>

# The remote host's public IP address or DNS name.
$CloudniumRemoteHost = "<REMOTE_HOST>"

# The SSH user on the remote host.
$CloudniumRemoteUser = "<REMOTE_USER>"

# Remote path (on the Cloudnium host) that holds the Palworld save data.
$CloudniumRemotePath = "/opt/palworld/data/Pal/Saved/"

# SSH private key used to authenticate to the remote host, as a path resolvable inside WSL
# (verify-cloudnium-mirror.ps1 runs rsync/ssh through `wsl -e bash -lc`).
$CloudniumSshKey = "~/.ssh/<REMOTE_KEY_NAME>"

# Local (Windows) path that the out-of-repo rsync pipeline mirrors the remote save data into.
$CloudniumLocalPath = "<LOCAL_MIRROR_PATH>\data\Pal\Saved"

# Local (Windows) path to that pipeline's sync log.
$CloudniumLogPath = "<LOCAL_MIRROR_PATH>\logs\sync-from-cloudnium.log"

# Name of the Windows Scheduled Task that runs the real (mutating) sync pipeline.
$CloudniumTaskName = "\Palworld Sync From Cloudnium"

# SHA256 host-key fingerprint pinned for this host (see docs/connectors.md "Host-key pinning").
# Not consumed directly by verify-cloudnium-mirror.ps1 today; kept here so all of this host's
# real coordinates live in one gitignored place for other Cloudnium tooling to reuse.
$CloudniumFingerprint = "<REMOTE_FINGERPRINT>"
