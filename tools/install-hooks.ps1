# Installs the pre-push verify gate into .git/hooks (docs/spec-ui-test-harness.md section 5).
# Git hooks are LOCAL (not version-controlled), so run this ONCE per clone:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/install-hooks.ps1
#
# After that every `git push` runs tools/checks/verify-gate.ps1 (build Release + Debug,
# OrderCheck fixtures, repo guards) and a failing gate blocks the push.

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src  = Join-Path $repo 'tools\checks\pre-push'
$dst  = Join-Path $repo '.git\hooks\pre-push'
if (-not (Test-Path (Split-Path $dst -Parent))) {
    Write-Error "No .git\hooks directory — is $repo a git repository?"
    exit 1
}
Copy-Item -Path $src -Destination $dst -Force
Write-Host "Installed pre-push hook -> $dst"
Write-Host "Test it without pushing:  powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/verify-gate.ps1"
exit 0
