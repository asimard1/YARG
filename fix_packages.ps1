<#
.SYNOPSIS
    Clones git-based UPM dependencies from manifest.json into Packages/LocalPackages,
    then rewrites manifest.json to point at the local copies using each package's
    real name (from its package.json), not the repo name.

.USAGE
    Place this script in D:\YARG (or wherever your Unity project root is) and run it
    from there, or just run it from anywhere -- it locates the project root via
    $PSScriptRoot.
#>

$ErrorActionPreference = "Stop"

$root              = $PSScriptRoot
$manifestPath      = Join-Path $root "Packages\manifest.json"
$localPackagesDir  = Join-Path $root "Packages\LocalPackages"
$localPackagesRel  = "LocalPackages"   # relative to Packages/, used inside manifest.json

if (!(Test-Path $manifestPath)) {
    Write-Error "manifest.json not found at $manifestPath"
    exit 1
}

if (!(Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Error "git is not on PATH."
    exit 1
}

New-Item -ItemType Directory -Path $localPackagesDir -Force | Out-Null

# Avoid git's "dubious ownership" refusal for cloned folders on this drive.
cmd /c "git config --global --add safe.directory `"*`" 2>&1" | Out-Null

# git writes routine notices (e.g. "switching to <sha>") to stderr. With
# $ErrorActionPreference = 'Stop', PowerShell would treat that as a terminating
# error even though the command succeeded. This helper runs git with a relaxed
# preference so only a non-zero exit code counts as failure.
function Invoke-GitCommand {
    param([Parameter(Mandatory)][string[]]$GitArgs)
    $quoted  = $GitArgs | ForEach-Object { '"' + $_ + '"' }
    $cmdLine = "git " + ($quoted -join ' ') + " 2>&1"
    $output  = cmd /c $cmdLine
    $output | ForEach-Object { Write-Host "    $_" }
    return $LASTEXITCODE
}

# Read manifest as raw JSON text so we preserve everything (scopedRegistries etc.)
# and just swap out the dependencies block.
$manifestRaw  = Get-Content $manifestPath -Raw
$manifest     = $manifestRaw | ConvertFrom-Json
$dependencies = $manifest.dependencies

# Matches: https://github.com/<owner>/<repo>[.git][?path=<subpath>][#<ref>]
$githubPattern = '^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/#?]+?)(?:\.git)?(?:\?path=(?<subpath>[^#]+))?(?:#(?<ref>.+))?$'

$newDependencies = [ordered]@{}

foreach ($pkg in $dependencies.PSObject.Properties) {
    $name = $pkg.Name
    $url  = $pkg.Value

    if ($url -notmatch $githubPattern) {
        # Not a github url (registry version, or already a file: reference) -- keep as-is.
        $newDependencies[$name] = $url
        continue
    }

    $owner   = $matches.owner
    $repo    = $matches.repo
    $subPath = if ($matches.subpath) { $matches.subpath.Trim('/') } else { "" }
    $ref     = $matches.ref

    $targetDir = Join-Path $localPackagesDir $repo

    Write-Host "== $repo ==" -ForegroundColor Cyan

    if (Test-Path $targetDir) {
        Write-Host "  Already cloned, skipping clone. Delete '$targetDir' to re-sync." -ForegroundColor DarkGray
    }
    else {
        $cloneUrl = "https://github.com/$owner/$repo.git"

        $exitCode = Invoke-GitCommand @('clone', '--quiet', $cloneUrl, $targetDir)
        if ($exitCode -ne 0) {
            Write-Warning "  Clone failed for $cloneUrl. Skipping."
            $newDependencies[$name] = $url
            continue
        }

        if ($ref) {
            Push-Location $targetDir
            $exitCode = Invoke-GitCommand @('checkout', '--quiet', $ref)
            if ($exitCode -ne 0) {
                # Local branch may not exist yet as a plain name -- try tracking the remote branch explicitly.
                $exitCode = Invoke-GitCommand @('checkout', '--quiet', '-b', $ref, "origin/$ref")
            }

            # Verify we actually landed on the requested ref -- never silently proceed on the wrong one.
            $currentRef = (cmd /c "git rev-parse --abbrev-ref HEAD 2>&1").Trim()
            $currentTag = (cmd /c "git describe --tags --exact-match 2>&1").Trim()
            $onRef = ($exitCode -eq 0) -and (($currentRef -eq $ref) -or ($currentTag -eq $ref))

            Pop-Location

            if (-not $onRef) {
                Write-Warning "  Could not verify checkout of ref '$ref' for $repo (on '$currentRef' instead). Skipping -- fix manually and re-run."
                Remove-Item -Recurse -Force $targetDir
                $newDependencies[$name] = $url
                continue
            }
        }
    }

    # Locate package.json: first at the expected (sub)path, then fall back to a recursive search.
    $expectedDir = if ($subPath) { Join-Path $targetDir $subPath } else { $targetDir }
    $pkgJsonPath = Join-Path $expectedDir "package.json"

    if (!(Test-Path $pkgJsonPath)) {
        $found = Get-ChildItem -Path $targetDir -Filter "package.json" -Recurse -ErrorAction SilentlyContinue |
                 Select-Object -First 1
        if ($found) {
            $pkgJsonPath = $found.FullName
            Write-Warning "  package.json not at expected path; using $($found.FullName) instead."
        }
        else {
            Write-Warning "  No package.json found anywhere under $targetDir. Keeping original manifest entry."
            $newDependencies[$name] = $url
            continue
        }
    }

    $actualName = (Get-Content $pkgJsonPath -Raw | ConvertFrom-Json).name
    if (-not $actualName) {
        Write-Warning "  package.json at $pkgJsonPath has no 'name' field. Keeping original manifest entry."
        $newDependencies[$name] = $url
        continue
    }

    # Path of the package.json's folder, relative to $targetDir (handles subPath correctly
    # even if the fallback search found it somewhere unexpected).
    $pkgFolder   = Split-Path $pkgJsonPath -Parent
    $targetFull  = (Resolve-Path $targetDir).Path.TrimEnd('\')
    $pkgFull     = (Resolve-Path $pkgFolder).Path.TrimEnd('\')
    $relFromRepo = $pkgFull.Substring($targetFull.Length).Trim('\') -replace '\\', '/'

    $manifestValue = if ($relFromRepo -and $relFromRepo -ne ".") {
        "file:$localPackagesRel/$repo/$relFromRepo"
    } else {
        "file:$localPackagesRel/$repo"
    }

    $newDependencies[$actualName] = $manifestValue
    Write-Host "  -> '$actualName' => $manifestValue" -ForegroundColor Green
}

$manifest.dependencies = $newDependencies

# The YARG.Online.* contract packages live under src/ inside the YARG.Online repo,
# not at its root -- fix up any manifest entries pointing at the old flat path.
foreach ($key in @($newDependencies.Keys)) {
    if ($newDependencies[$key] -match '^file:\.\./YARG\.Online\.') {
        $newDependencies[$key] = $newDependencies[$key] -replace '^file:\.\./YARG\.Online\.', 'file:../YARG.Online/src/YARG.Online.'
    }
}

$jsonOut  = $manifest | ConvertTo-Json -Depth 10
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $jsonOut, $utf8NoBom)

$lockPath = Join-Path $root "Packages\packages-lock.json"
if (Test-Path $lockPath) {
    Remove-Item -Force $lockPath
    Write-Host "Removed stale packages-lock.json." -ForegroundColor DarkGray
}

Write-Host "`nDone. manifest.json updated." -ForegroundColor Cyan
