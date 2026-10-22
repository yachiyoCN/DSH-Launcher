# One-shot commit + force push. Keeps the repo at a SINGLE commit.
#
# Usage:
#   .\scripts\push-latest.ps1                     # amend changes into the only commit
#   .\scripts\push-latest.ps1 -Message "new text" # replace the commit message
#   .\scripts\push-latest.ps1 -Tag v1.0.1         # also move a tag (triggers CI release)
#   .\scripts\push-latest.ps1 -WhatIf             # dry run: show what would happen
#
# How it works: git commit --amend folds the change into the single commit,
# then --force-with-lease overwrites the remote. Commit count stays at 1.

param(
    [string]$Message,
    [string]$Tag,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $branch = git branch --show-current
    if ($branch -ne 'main') { throw "Not on main branch (current: $branch). Aborted." }

    $count = [int](git rev-list --count HEAD)
    if ($count -gt 1) {
        Write-Warning "History has $count commits; this script maintains single-commit repos only."
    }

    git add -A
    $staged = @(git diff --cached --name-only)
    if ($staged.Count -eq 0) {
        Write-Host "[skip] no changes to commit" -ForegroundColor Yellow
    } else {
        Write-Host "[stage] would fold these into the single commit:" -ForegroundColor Cyan
        $staged | ForEach-Object { Write-Host "   $_" }
    }

    if ($Tag) {
        Write-Host "[tag] target tag: $Tag" -ForegroundColor Cyan
    }

    if ($WhatIf) {
        Write-Host ""
        Write-Host "[WhatIf] would run (nothing changed):" -ForegroundColor Yellow
        Write-Host "   git commit --amend $(if ($Message) { '-m ' + $Message } else { '--no-edit' })"
        if ($Tag) { Write-Host "   git tag -f -a $Tag -m $Tag" }
        Write-Host "   git push --force-with-lease origin main"
        if ($Tag) { Write-Host "   git push --force origin refs/tags/$Tag" }
        return
    }

    if ($staged.Count -gt 0) {
        if ($Message) { git commit --amend -m $Message | Out-Null }
        else { git commit --amend --no-edit | Out-Null }
        if ($LASTEXITCODE -ne 0) { throw "git commit --amend failed" }
        Write-Host "[ok] amended" -ForegroundColor Green
    }

    if ($Tag) {
        Write-Host "[tag] moving $Tag to the new commit" -ForegroundColor Cyan
        git tag -f -a $Tag -m $Tag | Out-Null
    }

    git push --force-with-lease origin main
    if ($LASTEXITCODE -ne 0) { throw "push main failed" }
    if ($Tag) {
        git push --force origin "refs/tags/$Tag"
        if ($LASTEXITCODE -ne 0) { throw "push tag failed" }
    }

    $sha = git rev-parse --short HEAD
    $n = git rev-list --count HEAD
    Write-Host ""
    Write-Host "[done] origin/main = $sha  (commits: $n)" -ForegroundColor Green
}
finally { Pop-Location }
