#requires -Version 5.1
<#
.SYNOPSIS
    OSDU Delivery container deploy: detect which images actually changed since the
    tag each app is serving, build those in ACR (fast + parallel, once per commit),
    point the container apps at the new tag, verify the new revision is actually
    serving, and roll back automatically if it is not. With -Infrastructure, plan
    and apply an environment's Terraform instead.

.DESCRIPTION
    The image tag is the current commit's short SHA (what the apps actually run).
    Keep this script at the repo root. Do NOT run it while a manual deploy is in
    flight: it wipes .deploy-ctx on start.

    ENVIRONMENTS: deploy-dev.ps1, deploy-test.ps1 and deploy-prod.ps1 run this script
    with -Environment dev, test or prod, which reads that environment from
    osdu/deploy/terraform/environments/<env>.tfvars: its subscription_id,
    resource_group_name and acr_name, and the app names when it overrides them. The
    same file is the environment's Terraform variables, so the estate Terraform
    creates and the estate this script deploys to are one definition. The GitHub
    Deploy workflow runs the same script. When OSDU_ENVIRONMENT_DIR names a directory,
    the environment's <env>.tfvars and <env>.backend.hcl are read from there instead,
    so an environment's values can stay out of the repository, whose copies then keep
    their placeholders (the Deploy workflow writes them there from its GitHub
    environment's OSDU_ENVIRONMENT_TFVARS and OSDU_ENVIRONMENT_BACKEND variables).

    ESTATE without an environment: the resource group, registry, subscription and app
    naming schemes are parameters with no defaults for the estate itself (-ResourceGroup,
    -Registry, -Subscription, or OSDU_DEPLOY_RG / OSDU_DEPLOY_ACR / OSDU_DEPLOY_SUBSCRIPTION).
    They name an EXISTING estate: the script targets apps by name and creates nothing.

    Built once per commit: an image whose tag (the commit) the registry already holds is
    reused, not built again, so promoting a commit to an environment sharing the registry
    costs no build.

    INFRASTRUCTURE (-Infrastructure, with -Environment): terraform init against the
    environment's state (its <env>.backend.hcl), plan with its variables
    (its <env>.tfvars, image_tag = this commit), show the plan and apply it once
    the environment's name is typed back (-AutoApprove skips the question: the GitHub
    workflow, where the environment's approval is the gate). No secret reaches Terraform:
    the deployment generates its own into the estate's vault, and a person puts the rest
    there (each partition's client secret, a repository's token, or the private key of the
    GitHub App the environment's github_app names, which then reads its GitHub repositories
    instead of a token: sqlflow/docs/reference/guides/github-app.md). What an environment
    keeps out of the repository (its OSDU partitions, TF_VAR_osdu_partitions) comes from
    the process environment or from the git-ignored .sqlflow/deploy/<env>.env (NAME=value
    lines), never from a committed file. The images of apps the plan is about to create
    are built first; an app that exists keeps the image it serves (the Terraform ignores
    image changes: this script rolls them forward). Once applied, the script waits for the
    control plane's start-up pass and shows each of its steps, each repository's sync and the
    credential it reads with, and whether the chat assistant is on, signed in as the initial
    admin with the password the
    deployment generated: the estate is ready to use when every step is done.

    Change detection (the single interface: run it bare and it deploys what is required):
      * Each app declares the paths its image is built from (its Dockerfile plus the
        source trees that Dockerfile reads). The tag an app currently serves IS a
        commit SHA, so `git diff <servingTag> HEAD -- <paths>` decides whether the
        image needs a rebuild. Unchanged apps are skipped, with the reason printed.
      * Bare invocation considers control-plane, worker, and gui, and mcp when the estate
        has an MCP server app (it is optional: an estate without one skips it). Naming apps
        explicitly deploys them unconditionally (no change filter): an explicit ask is an
        order, and it is also the escape hatch when detection must be bypassed.
      * An app with no serving image, a foreign image, or a tag that is not a commit
        in this clone counts as changed: when the baseline cannot be trusted, deploy.

    Fast path (why this is not just `az acr build .`):
      * Context is a `git archive` of TRACKED files only, extracted to .deploy-ctx.
        `az acr build .` uploads the whole working tree and does NOT honor
        .dockerignore on the client side, so it ships gigabytes of bin/obj/
        node_modules every build. The archive is tens of MB.
      * Every image builds from the SAME repository-root context: the .NET hosts in
        osdu/hosts reference both osdu/src and sqlflow/src, the GUI compiles osdu/gui
        together with the vendored sqlflow/gui sources it imports, and the MCP server
        compiles the vendored sqlflow/tools/sqlflow-mcp with both trees' documentation.
      * Each build runs FROM INSIDE the context directory with `--file <path> .`,
        because az resolves --file against the current working directory, not the
        context path.
      * Builds run in parallel, and each is verified by its ACR run id polled to a
        terminal state. The az CLI can crash on a glyph AFTER the server build
        succeeds (cp1252 cannot encode its check mark), so the local exit code is not
        trusted; the run id (printed before any crash) is.

    SAFETY (why this script refuses to guess):
      * An estate can hold the same app under more than one naming scheme (a renamed
        estate beside the one it replaced). `az containerapp update -n <name>` targets by
        name alone, so a stale name silently deploys to the DEAD environment and still
        reports success. This script resolves each app across every -AppPrefix given and
        REFUSES to continue when the choice is ambiguous: pass -Target with the prefix
        you mean.
      * Every deploy prints the resolved app, its environment, and the image it is
        replacing, before anything is changed. Use -WhatIf to see that plan and stop.
      * A deploy is not "done" when the API accepts it. Each app is verified to be
        serving the new tag on a healthy revision; if it is not, the app is rolled back
        to the exact image it was running and the script exits non-zero.
      * Building from a dirty tree is an error, not a warning: the image would not
        contain your changes. Pass -Force if that is genuinely what you want.

    NOT handled here: creating the container apps (they must already exist, with their
    identity/secrets/env wired), and the flow YAML in the estate's flow repositories.

.EXAMPLE
    .\deploy-dev.ps1
    Deploy to dev whichever of control-plane, worker, gui (and mcp, when the estate has it)
    changed since the tag each serves. deploy-test.ps1 and deploy-prod.ps1 do the same for
    test and prod; each is this script with -Environment.

.EXAMPLE
    .\deploy-test.ps1 all
    Same change detection, across every app: control-plane, worker, gui, mcp.

.EXAMPLE
    .\deploy-prod.ps1 control-plane worker
    Force exactly those apps, changed or not. Known: control-plane worker gui mcp

.EXAMPLE
    .\deploy-prod.ps1 -WhatIf
    Resolve the targets and print the plan without building or deploying.

.EXAMPLE
    .\deploy-dev.ps1 -Infrastructure
    Plan dev's Terraform and apply it after the environment's name is typed back.
    With -WhatIf it stops after the plan.

.EXAMPLE
    .\deploy.ps1 -ResourceGroup my-rg -Registry myacr -Subscription 00000000-0000-0000-0000-000000000000
    Name an estate no environment file describes, instead of through OSDU_DEPLOY_RG,
    OSDU_DEPLOY_ACR and OSDU_DEPLOY_SUBSCRIPTION.

.EXAMPLE
    .\deploy.ps1 -ResourceGroup my-rg -Registry myacr -Subscription <id> -AppPrefix osdu-delivery-,osdu- -Target osdu-delivery-
    Search both naming schemes, and deploy to the osdu-delivery- one where an app exists under both.
#>
# PositionalBinding=$false so bare app names only ever bind to -Apps. Without it PowerShell
# hands the first positional argument to the next declared parameter, and `.\deploy.ps1
# control-plane worker` fails with "control-plane does not belong to the set" for -Target.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Apps,

    # The environment, as osdu/deploy/terraform/environments/<name>.tfvars defines it: the estate this deploys
    # to and the Terraform -Infrastructure plans. deploy-dev.ps1, deploy-test.ps1 and deploy-prod.ps1 pass it.
    [string] $Environment,

    # Plan and apply the environment's Terraform instead of rolling out images (needs -Environment).
    [switch] $Infrastructure,

    # Apply the Terraform plan without asking for the environment's name: the GitHub workflow, whose
    # environment approval is the gate.
    [switch] $AutoApprove,

    # Which naming scheme to deploy to when an app exists under more than one of -AppPrefix.
    # 'auto' searches them in order; anything else must be one of the prefixes given.
    [string] $Target = 'auto',

    # Build and deploy even though the image build paths have uncommitted changes.
    [switch] $Force,

    # Resolve targets and print the plan (with -Infrastructure: the Terraform plan), then stop.
    [switch] $WhatIf,

    # Leave a failed deploy in place instead of restoring the previous image.
    [switch] $NoRollback,

    # The estate. No defaults: naming it is a deliberate act, and a wrong name would deploy these
    # images over another product's running apps. Pass them, or set OSDU_DEPLOY_RG,
    # OSDU_DEPLOY_ACR and OSDU_DEPLOY_SUBSCRIPTION.
    [string] $ResourceGroup = $env:OSDU_DEPLOY_RG,

    [string] $Registry = $env:OSDU_DEPLOY_ACR,

    [string] $Subscription = $env:OSDU_DEPLOY_SUBSCRIPTION,

    # The container app name prefixes to look for, newest first, and the image repository prefix.
    # An app is resolved across every prefix given, which is how a renamed estate stays reachable.
    [string[]] $AppPrefix = @('osdu-delivery-'),

    [string] $ImagePrefix = 'osdu-delivery-'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$TerraformDir = Join-Path $PSScriptRoot 'osdu/deploy/terraform'
Import-Module (Join-Path $PSScriptRoot 'osdu/deploy/OsduEnvironment.psm1') -Force

$EnvironmentVariables = @{}
if ($Environment) {
    # The environment names the estate; a parameter naming another one as well is a contradiction, not an override.
    foreach ($conflict in 'ResourceGroup', 'Registry', 'Subscription', 'AppPrefix', 'ImagePrefix', 'Target') {
        if ($PSBoundParameters.ContainsKey($conflict)) {
            throw "-$conflict cannot be given with -Environment: the environment's $Environment.tfvars names the estate."
        }
    }

    $OsduEnvironment = Get-OsduEnvironment -Name $Environment -RepoRoot $PSScriptRoot -WithBackend:$Infrastructure
    $EnvironmentVariables = $OsduEnvironment.Variables
    $Subscription = $OsduEnvironment.Subscription
    $ResourceGroup = $OsduEnvironment.ResourceGroup
    $Registry = $OsduEnvironment.Registry
    $AppPrefix = @($OsduEnvironment.AppPrefix)
}
elseif ($Infrastructure) {
    throw '-Infrastructure needs -Environment: the Terraform of an environment is planned with its file (deploy-dev.ps1 -Infrastructure).'
}

if ($Infrastructure -and $Apps -and $Apps.Count -gt 0) {
    throw "-Infrastructure plans the whole environment; name no apps ($($Apps -join ', '))."
}

foreach ($required in @(
    @{ Name = 'ResourceGroup'; Value = $ResourceGroup; Variable = 'OSDU_DEPLOY_RG'; What = 'the resource group holding the container apps' },
    @{ Name = 'Registry'; Value = $Registry; Variable = 'OSDU_DEPLOY_ACR'; What = 'the container registry to build the images in' },
    @{ Name = 'Subscription'; Value = $Subscription; Variable = 'OSDU_DEPLOY_SUBSCRIPTION'; What = 'the subscription that estate lives in' })) {
    if ([string]::IsNullOrWhiteSpace($required.Value)) {
        throw "Name the estate before deploying: run deploy-dev.ps1, deploy-test.ps1 or deploy-prod.ps1 (or pass -Environment), or pass -$($required.Name) or set $($required.Variable) to $($required.What)."
    }
}

if ($AppPrefix.Count -eq 0 -or ($AppPrefix | Where-Object { [string]::IsNullOrWhiteSpace($_) })) {
    throw 'Every -AppPrefix must be a non-blank container app name prefix.'
}

if ([string]::IsNullOrWhiteSpace($ImagePrefix)) {
    throw 'Name the image repository prefix with -ImagePrefix.'
}

$Rg  = $ResourceGroup
$Acr = $Registry
$Sub = $Subscription

# The naming schemes an app can live under, in the order they are searched. The ACR repository name
# is $ImagePrefix<app> regardless of what the container app resource is called.
$NamePrefix = [ordered]@{}
for ($i = 0; $i -lt $AppPrefix.Count; $i++) {
    $NamePrefix["scheme$($i + 1)"] = $AppPrefix[$i]
}

# app -> the Dockerfile that builds it (repo-relative, as used from inside the context root)
# and the repo paths that feed the image. Paths drive change detection: an app is rebuilt only
# when `git diff <servingTag> HEAD -- <Paths>` is non-empty, so a docs-only commit deploys
# nothing (except to the MCP server, which compiles the documentation in). Every image uses the
# repository root as its context. An Optional app is one an estate may not have: the candidate
# sets skip it when no container app answers to it, while naming it still requires it.
$DotnetBuildInputs = @(
    'osdu/src', 'osdu/hosts', 'sqlflow/src',
    'OsduDelivery.sln', 'global.json', '.dockerignore',
    'osdu/Directory.Build.props', 'osdu/Directory.Packages.props',
    'sqlflow/Directory.Build.props', 'sqlflow/Directory.Packages.props'
)
$Config = [ordered]@{
    'control-plane' = @{
        Dockerfile = 'osdu/deploy/docker/control-plane.Dockerfile'
        Paths      = @('osdu/deploy/docker/control-plane.Dockerfile') + $DotnetBuildInputs
    }
    'worker'        = @{
        Dockerfile = 'osdu/deploy/docker/worker.Dockerfile'
        Paths      = @('osdu/deploy/docker/worker.Dockerfile', 'osdu/deploy/docker/worker-entrypoint.sh') + $DotnetBuildInputs
    }
    'gui'           = @{
        Dockerfile = 'osdu/deploy/docker/gui.Dockerfile'
        Paths      = @(
            'osdu/deploy/docker/gui.Dockerfile',
            'osdu/deploy/docker/nginx.conf',
            'osdu/deploy/docker/40-runtime-config.sh',
            '.dockerignore',
            'osdu/gui',
            'sqlflow/gui'
        )
    }
    'mcp'           = @{
        Dockerfile = 'osdu/deploy/docker/mcp.Dockerfile'
        Paths      = @(
            'osdu/deploy/docker/mcp.Dockerfile',
            '.dockerignore',
            'osdu/hosts/osdu-delivery-mcp',
            'osdu/docs',
            'sqlflow/tools',
            'sqlflow/docs/reference',
            'sqlflow/docs/wiki'
        )
        Optional   = $true
    }
}

# Explicitly named apps deploy unconditionally; the bare default and 'all' are candidate sets
# that change detection narrows to what is actually required.
$ExplicitSelection = $true
if (-not $Apps -or $Apps.Count -eq 0) {
    $Apps = @($Config.Keys)
    $ExplicitSelection = $false
}
if ($Apps -contains 'all') {
    $Apps = @($Config.Keys)
    $ExplicitSelection = $false
}
foreach ($a in $Apps) {
    if (-not $Config.Contains($a)) {
        throw "Unknown app '$a'. Known: $($Config.Keys -join ', '), or 'all' for every one."
    }
}

$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'

$Tag = (git rev-parse --short HEAD).Trim()
if (-not $Tag) { throw 'Could not read git HEAD. Run this from the OSDU Delivery repo.' }


# Every az call goes through Invoke-Az (osdu/deploy/OsduEnvironment.psm1), which judges az by its exit code alone.

function Test-ImageBuilt {
    # Whether the registry already holds this app's image of $Tag: built from the same commit, it is the same image.
    param([string] $App)
    $res = Invoke-Az @('acr', 'repository', 'show', '--name', $Acr, '--image', "${ImagePrefix}${App}:$Tag", '-o', 'none')
    return $res.ExitCode -eq 0
}

function Wait-RunTerminal {
    param([string] $RunId)
    for ($i = 0; $i -lt 120; $i++) {
        $res = Invoke-Az @('acr', 'task', 'show-run', '-r', $Acr, '--run-id', $RunId, '--query', 'status', '-o', 'tsv')
        $st = ''
        if ($res.ExitCode -eq 0 -and $res.Output) { $st = ($res.Output | Select-Object -First 1).ToString().Trim() }
        if ($st -eq 'Succeeded') { return $true }
        if ($st -in @('Failed', 'Canceled', 'Error', 'Timeout')) {
            Write-Host "   run $RunId -> $st" -ForegroundColor Red
            return $false
        }
        Start-Sleep -Seconds 10
    }
    Write-Host "   run $RunId did not finish in time" -ForegroundColor Red
    return $false
}

function Build-Images {
    <#
        Makes sure the registry holds $Tag's image of each app in $BuildApps: an image it already holds is
        reused, and the rest are built in parallel in ACR from one clean context (tracked files only), each
        verified by its ACR run id. Throws when any build does not succeed, before anything is deployed.
    #>
    param([string[]] $BuildApps)

    $toBuild = @()
    foreach ($a in $BuildApps) {
        if (Test-ImageBuilt -App $a) {
            Write-Host ("   {0,-14} {1}.azurecr.io/{2}{0}:{3} is built already, reused" -f $a, $Acr, $ImagePrefix, $Tag) -ForegroundColor DarkGray
        }
        else {
            $toBuild += $a
        }
    }
    if ($toBuild.Count -eq 0) { return }

    # All the images share one context: the .NET hosts span osdu/ and sqlflow/, and the GUI compiles both GUI trees.
    $CtxRoot = Join-Path $PSScriptRoot '.deploy-ctx'
    Remove-Item -Recurse -Force $CtxRoot -ErrorAction SilentlyContinue
    $RepoCtx = Join-Path $CtxRoot 'repo'

    Write-Host ''
    Write-Host 'Preparing clean build context (tracked files only)...'
    New-Item -ItemType Directory -Force -Path $RepoCtx | Out-Null
    git archive --format=tar.gz -o (Join-Path $CtxRoot 'repo.tar.gz') HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }
    tar -xzf (Join-Path $CtxRoot 'repo.tar.gz') -C $RepoCtx

    Write-Host "Building $($toBuild.Count) image(s) in parallel at $Tag..." -ForegroundColor Cyan
    $jobs = foreach ($a in $toBuild) {
        $df = $Config[$a].Dockerfile
        Start-Job -Name $a -ScriptBlock {
            param($Acr, $App, $Tag, $Df, $Ctx, $Prefix)
            $env:PYTHONUTF8 = '1'; $env:PYTHONIOENCODING = 'utf-8'
            # Run from inside the context so az finds the Dockerfile (it resolves --file against the cwd).
            Set-Location -LiteralPath $Ctx
            az acr build --registry $Acr --image "${Prefix}${App}:$Tag" --file $Df . 2>&1
        } -ArgumentList $Acr, $a, $Tag, $df, $RepoCtx, $ImagePrefix
    }
    $jobs | Wait-Job | Out-Null

    Write-Host ''
    Write-Host '=== Verifying builds on ACR ===' -ForegroundColor Cyan
    $allOk = $true
    foreach ($j in $jobs) {
        $out = (Receive-Job $j) | Out-String
        Remove-Job $j
        $m = [regex]::Match($out, 'Queued a build with ID:\s*(\S+)')
        if (-not $m.Success) {
            Write-Host "   $($j.Name): build never queued" -ForegroundColor Red
            Write-Host $out
            $allOk = $false
            continue
        }
        $runId = $m.Groups[1].Value
        Write-Host "   $($j.Name): run $runId, waiting for result..."
        if (Wait-RunTerminal $runId) {
            Write-Host "   $($j.Name): Succeeded" -ForegroundColor Green
        }
        else { $allOk = $false }
    }
    if (-not $allOk) { throw 'One or more builds did not succeed - not deploying. See above.' }
}

function Assert-Committed {
    <#
        A dirty tree means an image or a plan would NOT contain the working changes: images are built from the
        committed tree, and an environment applied from uncommitted Terraform is one nobody can reproduce. That
        has burned enough deploys to be an error rather than a warning.
    #>
    param([string[]] $Paths, [string] $What)

    $dirty = git status --porcelain -- @Paths
    if (-not $dirty) { return }
    if (-not $Force) {
        Write-Host ''
        Write-Host "Uncommitted changes in the paths $What comes from:" -ForegroundColor Yellow
        Write-Host ($dirty | Out-String)
        throw "Refusing to continue: $What comes from the COMMITTED tree ($Tag) and would not include the changes above. Commit them, or pass -Force to go ahead with $Tag anyway."
    }
    Write-Warning "Uncommitted changes in the paths $What comes from - it is built from the committed tree ($Tag) and will NOT include them (-Force given)."
}

$r = Invoke-Az @('account', 'set', '--subscription', $Sub)
if ($r.ExitCode -ne 0) { throw 'az account set failed.' }

# --- Resolve each logical app to a real container app, refusing to guess ---------------

# Every container app in the resource group, fetched once. Resolution then happens in
# memory: `az containerapp show` on a missing app writes to stderr, which PowerShell 5.1
# turns into a terminating NativeCommandError under $ErrorActionPreference='Stop', so
# probing name-by-name would blow up on the very case it needs to handle (not found).
$listResult = Invoke-Az @('containerapp', 'list', '-g', $Rg, '-o', 'json')
if ($listResult.ExitCode -ne 0) { throw "Could not list container apps in $Rg (az exit $($listResult.ExitCode)): $($listResult.Errors -join ' ')" }
# ConvertFrom-Json emits a JSON array as ONE object, so assign first; piping it straight
# into a filter would treat the whole array as a single element.
$AllApps = if ($listResult.Output.Count -gt 0) { @((($listResult.Output -join "`n") | ConvertFrom-Json)) } else { @() }

function Get-AppRecord {
    <#
        Returns the container app resource for a logical app name, or $null when it does
        not exist. Also carries the environment name and the image currently deployed, so
        the plan can be shown and a rollback target captured before anything changes.
    #>
    param([string] $ResourceName)

    $obj = $AllApps | Where-Object { $_.name -eq $ResourceName } | Select-Object -First 1
    if (-not $obj) { return $null }

    $envName = ($obj.properties.environmentId -split '/')[-1]
    $image = $null
    if ($obj.properties.template.containers -and $obj.properties.template.containers.Count -gt 0) {
        $image = $obj.properties.template.containers[0].image
    }

    return [pscustomobject]@{
        Name        = $ResourceName
        Environment = $envName
        Image       = $image
    }
}

function Resolve-Target {
    <#
        Finds the one container app a logical app refers to. When both naming schemes
        exist the choice is genuinely ambiguous, so this throws and asks for -Target
        instead of picking one: silently deploying to the retired estate looks like a
        success and is the exact failure this guard exists to prevent.
    #>
    param([string] $App)

    $found = @()
    foreach ($scheme in $NamePrefix.Keys) {
        if ($Target -ne 'auto' -and $NamePrefix[$scheme] -ne $Target) { continue }
        $rec = Get-AppRecord -ResourceName "$($NamePrefix[$scheme])$App"
        if ($rec) {
            $rec | Add-Member -NotePropertyName Scheme -NotePropertyValue $scheme -Force
            $found += $rec
        }
    }

    if ($found.Count -eq 0) {
        $tried = @()
        foreach ($scheme in $NamePrefix.Keys) {
            if ($Target -ne 'auto' -and $NamePrefix[$scheme] -ne $Target) { continue }
            $tried += "$($NamePrefix[$scheme])$App"
        }
        throw "Container app for '$App' not found in $Rg (looked for: $($tried -join ', ')). Create it first, or check -Target."
    }

    if ($found.Count -gt 1) {
        $detail = ($found | ForEach-Object { "$($_.Name) (env $($_.Environment), prefix $($NamePrefix[$_.Scheme]))" }) -join ' AND '
        throw "Ambiguous target for '$App': $detail. Re-run with -Target and one of $($AppPrefix -join ', ') so this does not deploy to the wrong estate."
    }

    return $found[0]
}

function Test-AppPresent {
    # Whether any naming scheme in play has a container app for $App; never throws.
    param([string] $App)
    foreach ($scheme in $NamePrefix.Keys) {
        if ($Target -ne 'auto' -and $NamePrefix[$scheme] -ne $Target) { continue }
        if (Get-AppRecord -ResourceName "$($NamePrefix[$scheme])$App") { return $true }
    }
    return $false
}

function Get-ChangeStatus {
    <#
        Decides whether an app's image needs a rebuild by diffing its watch paths between
        the commit it is SERVING (the tag on its current image is a short SHA) and HEAD.
        Any baseline that cannot be trusted (no image, a foreign image, a tag that is not
        a commit in this clone) counts as changed: guessing "unchanged" there would skip
        a deploy that is in fact required, which is the one wrong answer.
    #>
    param([string] $App, [pscustomobject] $Record)

    if (-not $Record.Image) {
        return [pscustomobject]@{ Changed = $true; Reason = 'nothing deployed yet' }
    }
    if ($Record.Image -notmatch "/$([regex]::Escape($ImagePrefix))$([regex]::Escape($App)):([^:/]+)$") {
        return [pscustomobject]@{ Changed = $true; Reason = "serving a foreign image ($($Record.Image))" }
    }
    $baseTag = $Matches[1]

    git rev-parse --quiet --verify "$baseTag^{commit}" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Changed = $true; Reason = "serving tag $baseTag is not a commit in this clone" }
    }

    $paths = @($Config[$App].Paths)
    git diff --quiet $baseTag HEAD -- @paths
    if ($LASTEXITCODE -eq 0) {
        return [pscustomobject]@{ Changed = $false; Reason = "no changes vs serving tag $baseTag" }
    }
    if ($LASTEXITCODE -eq 1) {
        $files = @(git diff --name-only $baseTag HEAD -- @paths)
        return [pscustomobject]@{ Changed = $true; Reason = "$($files.Count) file(s) changed since $baseTag" }
    }
    return [pscustomobject]@{ Changed = $true; Reason = "git diff vs $baseTag failed (exit $LASTEXITCODE), assuming changed" }
}

# --- Infrastructure: the environment's Terraform -------------------------------------------

function Invoke-Terraform {
    <#
        Runs terraform in osdu/deploy/terraform, its output shown as it comes, and returns its exit code. stderr
        is shown rather than raised: PowerShell 5.1 turns any native stderr into an error under 'Stop'.
    #>
    param([Parameter(Mandatory)][string[]] $TfArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & terraform "-chdir=$TerraformDir" @TfArgs 2>&1 | ForEach-Object { Write-Host "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    return $code
}

# The Terraform variables an earlier version of the estate read, each with where its value lives now. None is read any
# more: one still set is dropped from this process, so no secret reaches Terraform by an old name.
$RetiredTerraformVariables = [ordered]@{
    TF_VAR_jwt_signing_key               = 'the deployment generates the signing key into the vault (osdu-delivery-jwt-signing-key)'
    TF_VAR_admin_password                = 'the deployment generates the initial admin''s password into the vault (osdu-delivery-admin-password)'
    TF_VAR_node_token                    = 'the deployment generates the node credential into the vault (osdu-delivery-node-token)'
    TF_VAR_git_token                     = 'a repository''s token is a vault secret its repositories entry names (credential_secret_name)'
    TF_VAR_git_username                  = 'a repository''s username is its repositories entry''s credential_username'
    TF_VAR_osdu_partition_client_secrets = 'each partition''s client secret is a vault secret (terraform output osdu_partitions names it)'
    TF_VAR_sql_entra_admin_login         = 'the people who own the databases are sql_admins'
    TF_VAR_sql_entra_admin_object_id     = 'the people who own the databases are sql_admins'
    TF_VAR_worker_flow_env               = 'every run is given sql_connections and properties by the control plane'
    TF_VAR_data_database_name            = 'the data database is a sql_connections entry'
}

function Import-EnvironmentValues {
    <#
        What the environment keeps out of the repository for Terraform (TF_VAR_osdu_partitions, and anything else a
        NAME=value line sets), from the git-ignored .sqlflow/deploy/<env>.env when it exists, into this process only. A
        variable already set in the process wins: that is how the GitHub workflow passes its environment's values. A
        retired variable is dropped, naming where its value lives now. Names are printed, never values.
    #>
    $file = Join-Path $PSScriptRoot ".sqlflow/deploy/$Environment.env"
    $loaded = @()
    if (Test-Path -LiteralPath $file) {
        foreach ($line in Get-Content -LiteralPath $file) {
            if ($line -match '^\s*(#|$)') { continue }
            if ($line -notmatch '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
                throw ".sqlflow/deploy/$Environment.env has a line that is not NAME=value; fix it (its content is not shown, it may be a secret)."
            }
            $name = $Matches[1]
            $value = $Matches[2].Trim()
            if ($RetiredTerraformVariables.Contains($name)) {
                Write-Host "   .sqlflow/deploy/$Environment.env sets $name, which is no longer read: $($RetiredTerraformVariables[$name]). Remove the line." -ForegroundColor Yellow
                continue
            }
            if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name))) {
                [Environment]::SetEnvironmentVariable($name, $value)
                $loaded += $name
            }
        }
        Write-Host "   values: $($loaded -join ', ') from .sqlflow/deploy/$Environment.env"
    }
    else {
        Write-Host "   values: from the process environment (no .sqlflow/deploy/$Environment.env)"
    }

    foreach ($name in $RetiredTerraformVariables.Keys) {
        if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name))) {
            Write-Host "   $name is set in this process and no longer read: $($RetiredTerraformVariables[$name]). Dropped." -ForegroundColor Yellow
            [Environment]::SetEnvironmentVariable($name, $null)
        }
    }
}

function Show-EstateReadiness {
    <#
        The control plane sets the estate up at every start, in one ordered pass of named steps (GET /api/v1/bootstrap):
        the catalog and the module migrated, the initial admin, the node credential, the repositories, the partitions
        and the values every run is given. This waits for that pass at /health/startup (it answers anyone, with no
        detail), then signs in as the initial admin, with the password the deployment generated into the vault, and
        shows each step, each repository's sync and whether the chat assistant is on. Throws when the control plane
        never answers: the app did not start. A step that fails is shown with its reason (already redacted by the
        control plane) and is retried by the control plane; most often it waits for a secret a person puts in the vault.
    #>
    param(
        [Parameter(Mandatory)][string] $BaseUrl,
        [Parameter(Mandatory)][string] $Vault,
        [Parameter(Mandatory)][string] $AdminPasswordSecret,
        [Parameter(Mandatory)][string] $AdminUsername
    )

    # How long the control plane is given to finish its pass, and its first repository syncs, before what it reports is
    # shown as it stands.
    $startupWaitSeconds = 900
    $syncWaitSeconds = 300

    $base = $BaseUrl.TrimEnd('/')
    Write-Host ''
    Write-Host "=== Start-up pass ($base) ===" -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($startupWaitSeconds)
    $status = 'unreachable'
    $shown = ''
    $answered = $false
    while ($true) {
        try {
            $response = Invoke-WebRequest -Uri "$base/health/startup" -UseBasicParsing -TimeoutSec 30
            $status = "$([int]$response.StatusCode)"
        }
        catch {
            $failed = $_.Exception
            $status = if ($failed.PSObject.Properties['Response'] -and $failed.Response) { "$([int]$failed.Response.StatusCode)" } else { 'unreachable' }
        }
        if ($status -ne 'unreachable') { $answered = $true }
        if ($status -eq '200' -or (Get-Date) -ge $deadline) { break }
        if ($status -ne $shown) {
            Write-Host "   /health/startup: $(if ($status -eq '503') { 'the pass is not done yet' } else { $status }), waiting"
            $shown = $status
        }
        Start-Sleep -Seconds 15
    }
    if (-not $answered) {
        throw "The control plane at $base did not answer within $startupWaitSeconds s: its app did not start. See its revision and log: az containerapp logs show --resource-group $Rg --name $($AppPrefix[0])control-plane"
    }

    $read = Invoke-Az @('keyvault', 'secret', 'show', '--vault-name', $Vault, '--name', $AdminPasswordSecret, '--query', 'value', '-o', 'tsv')
    if ($read.ExitCode -ne 0) {
        Write-Host "   The initial admin's password could not be read from $Vault/$AdminPasswordSecret, so the steps are not shown: $(($read.Errors | Select-Object -First 3) -join ' ')" -ForegroundColor Yellow
        Write-Host "   /health/startup answers $status$(if ($status -eq '200') { ': every step is done' } else { ': a step is not done; GET /api/v1/bootstrap names it' })."
        return
    }

    $headers = $null
    try {
        $body = @{ username = $AdminUsername; password = ($read.Output -join '').Trim() } | ConvertTo-Json -Compress
        $token = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/login" -ContentType 'application/json' -Body $body -TimeoutSec 30
        $headers = @{ Authorization = "Bearer $($token.accessToken)" }
    }
    catch {
        Write-Host "   Signing in as $AdminUsername failed ($($_.Exception.Message)); the initial admin is made by a start-up step, so the pass is not done yet." -ForegroundColor Yellow
        return
    }
    finally {
        $body = $null
        $read = $null
    }

    $bootstrap = Invoke-RestMethod -Uri "$base/api/v1/bootstrap" -Headers $headers -TimeoutSec 30
    foreach ($step in @($bootstrap.steps)) {
        $color = switch ($step.state) { 'done' { 'Green' } 'failed' { 'Red' } 'refused' { 'Red' } default { 'Yellow' } }
        $detail = if ($step.error) { $step.error } elseif ($step.summary) { $step.summary } else { '' }
        Write-Host ("   {0,-24} {1,-8} {2}" -f $step.name, $step.state, $detail) -ForegroundColor $color
    }

    # A repository the pass registered syncs at once; its first sync is shown once it has run.
    $syncDeadline = (Get-Date).AddSeconds($syncWaitSeconds)
    while ($true) {
        $sources = @((Invoke-RestMethod -Uri "$base/api/v1/repos/sources?pageSize=200" -Headers $headers -TimeoutSec 30).items)
        $waiting = @($sources | Where-Object { $_.enabled -and -not $_.lastSyncUtc -and -not $_.lastError })
        if ($waiting.Count -eq 0 -or (Get-Date) -ge $syncDeadline) { break }
        Start-Sleep -Seconds 15
    }
    Write-Host ''
    Write-Host '   Repositories' -ForegroundColor Cyan
    if ($sources.Count -eq 0) { Write-Host '   none registered' }
    foreach ($source in $sources) {
        if ($source.lastError) {
            Write-Host ("   {0,-24} failed   {1}" -f $source.name, $source.lastError) -ForegroundColor Red
        }
        elseif ($source.lastSyncUtc) {
            Write-Host ("   {0,-24} synced   {1} at {2}" -f $source.name, $source.lastSyncedSha, $source.lastSyncUtc) -ForegroundColor Green
        }
        else {
            Write-Host ("   {0,-24} pending  not synced yet" -f $source.name) -ForegroundColor Yellow
        }

        # The credential it reads with (a GitHub App, a token, none), never a secret; one that cannot be used says why.
        if ($source.PSObject.Properties['credential'] -and $source.credential) {
            Write-Host ("   {0,-24}          read with {1}" -f '', $source.credential.summary)
            if ($source.credential.PSObject.Properties['problem'] -and $source.credential.problem) {
                Write-Host ("   {0,-24}          {1}" -f '', $source.credential.problem) -ForegroundColor Yellow
            }
        }
    }

    $chat = Invoke-RestMethod -Uri "$base/api/v1/chat/capabilities" -Headers $headers -TimeoutSec 30
    Write-Host ''
    Write-Host ("   Chat assistant: {0}" -f $(if ($chat.enabled) { "on ($($chat.provider))" } else { 'off' })) -ForegroundColor $(if ($chat.enabled) { 'Green' } else { 'Yellow' })

    Write-Host ''
    if ($bootstrap.completed) {
        Write-Host "   $Environment is ready to use: every start-up step is done." -ForegroundColor Green
    }
    else {
        Write-Host "   $Environment is not ready yet: the control plane retries the steps above that are not done. A step waiting for a vault secret is done once a person puts it in $Vault." -ForegroundColor Yellow
    }
}

function Invoke-Infrastructure {
    if (-not (Get-Command terraform -ErrorAction SilentlyContinue)) {
        throw 'terraform is not on the PATH; install the version osdu/deploy/terraform/versions.tf requires.'
    }
    Write-Host ''
    Write-Host "=== OSDU Delivery infrastructure: $Environment ===" -ForegroundColor Cyan
    Write-Host "   tag:  $Tag (the image of an app the plan creates)"
    Write-Host "   rg:   $Rg"
    Write-Host "   acr:  $Acr"

    # Terraform reads the resource group and the registry; it creates neither. The answer is asked for as text: the
    # Deploy workflow sets AZURE_CORE_OUTPUT to none, under which az group exists prints nothing at all.
    $exists = Invoke-Az @('group', 'exists', '--name', $Rg, '-o', 'tsv')
    if ($exists.ExitCode -ne 0) {
        throw "Could not check that resource group $Rg exists in subscription ${Sub}: $("$($exists.Errors)".Trim())"
    }
    if ("$($exists.Output)".Trim() -ne 'true') {
        throw "Resource group $Rg does not exist in subscription $Sub. Create it once: az group create --subscription $Sub --name $Rg --location <region>"
    }
    $registry = Invoke-Az @('acr', 'show', '--name', $Acr, '--resource-group', $Rg, '-o', 'none')
    if ($registry.ExitCode -ne 0) {
        throw ("Container registry $Acr could not be read in $Rg ($("$($registry.Errors)".Trim())). If it does not exist, " +
            "create it once: az acr create --subscription $Sub --resource-group $Rg --name $Acr --sku Standard")
    }

    Assert-Committed -Paths @('osdu/deploy/terraform') -What "the $Environment plan"

    # The apps this environment has, and the images of those the plan will create: an app that exists keeps the
    # image it serves, so only a missing one needs $Tag's image in the registry before Terraform creates it.
    $wanted = @('control-plane', 'worker', 'gui')
    if ($OsduEnvironment.McpEnabled) { $wanted += 'mcp' }
    $missing = @($wanted | Where-Object { -not (Test-AppPresent -App $_) })
    if ($missing.Count -gt 0) {
        Write-Host "   new apps: $($missing -join ', ') (their images at $Tag are built first)"
        Assert-Committed -Paths @($missing | ForEach-Object { $Config[$_].Paths } | Sort-Object -Unique) -What "image $Tag"
        if (-not $WhatIf) { Build-Images -BuildApps $missing }
    }

    Import-EnvironmentValues

    $planDir = Join-Path $PSScriptRoot '.sqlflow/deploy'
    New-Item -ItemType Directory -Force -Path $planDir | Out-Null
    # A plan holds what it writes to the vault (the connection strings, and under password authentication the SQL
    # password): it lives in the git-ignored .sqlflow/deploy and is removed once applied.
    $planFile = Join-Path $planDir "$Environment.tfplan"
    Remove-Item -LiteralPath $planFile -Force -ErrorAction SilentlyContinue

    Write-Host ''
    Write-Host "=== terraform init ($Environment state) ===" -ForegroundColor Cyan
    if ((Invoke-Terraform @('init', '-input=false', '-reconfigure', "-backend-config=$($OsduEnvironment.BackendFile)")) -ne 0) {
        throw "terraform init failed for $Environment; see above."
    }

    Write-Host ''
    Write-Host "=== terraform plan ($Environment) ===" -ForegroundColor Cyan
    $planned = Invoke-Terraform @('plan', '-input=false', '-detailed-exitcode', "-var-file=$($OsduEnvironment.VarFile)", "-var=image_tag=$Tag", "-out=$planFile")
    if ($planned -eq 0) {
        Remove-Item -LiteralPath $planFile -Force -ErrorAction SilentlyContinue
        Write-Host ''
        Write-Host "$Environment is as its Terraform describes it: nothing to apply." -ForegroundColor Green
        return
    }
    if ($planned -ne 2) {
        Remove-Item -LiteralPath $planFile -Force -ErrorAction SilentlyContinue
        throw "terraform plan failed for $Environment; see above."
    }

    if ($WhatIf) {
        Remove-Item -LiteralPath $planFile -Force -ErrorAction SilentlyContinue
        Write-Host ''
        Write-Host "-WhatIf: the plan above was not applied to $Environment." -ForegroundColor Yellow
        return
    }

    if (-not $AutoApprove) {
        Write-Host ''
        $answer = Read-Host "Apply this plan to $Environment? Type '$Environment' to apply, anything else to stop"
        if ($answer -cne $Environment) {
            Remove-Item -LiteralPath $planFile -Force -ErrorAction SilentlyContinue
            Write-Host 'Not applied.' -ForegroundColor Yellow
            return
        }
    }

    Write-Host ''
    Write-Host "=== terraform apply ($Environment) ===" -ForegroundColor Cyan
    $applied = Invoke-Terraform @('apply', '-input=false', $planFile)
    Remove-Item -LiteralPath $planFile -Force -ErrorAction SilentlyContinue
    if ($applied -ne 0) { throw "terraform apply failed for $Environment; see above." }

    # What the estate is, from its own outputs, and whether it is ready to use.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $outputJson = & terraform "-chdir=$TerraformDir" output -json 2>$null
    $ErrorActionPreference = $prev
    if ($LASTEXITCODE -ne 0 -or -not $outputJson) {
        throw "terraform output failed for $Environment after the apply; run terraform -chdir=osdu/deploy/terraform output to see why."
    }
    $outputs = ($outputJson -join "`n") | ConvertFrom-Json
    Write-Host ''
    Write-Host "=== $Environment ===" -ForegroundColor Green
    foreach ($name in 'gui_url', 'control_plane_base_url', 'mcp_url', 'landing_url', 'key_vault_name', 'admin_password_secret') {
        if ($outputs.PSObject.Properties[$name] -and $outputs.$name.value) { Write-Host ("   {0,-24} {1}" -f $name, $outputs.$name.value) }
    }
    if ($outputs.PSObject.Properties['entra_assignment_reminder'] -and $outputs.entra_assignment_reminder.value) {
        Write-Host "   $($outputs.entra_assignment_reminder.value)" -ForegroundColor Yellow
    }
    if ($outputs.PSObject.Properties['github_app_private_key_secret'] -and $outputs.github_app_private_key_secret.value) {
        # The GitHub App's private key is put in the vault by hand; until it is there, each repository the App reads
        # reports that it cannot read the key. Only the secret's existence is asked, never its value.
        $vault = $outputs.key_vault_name.value
        $keySecret = $outputs.github_app_private_key_secret.value
        $found = Invoke-Az @('keyvault', 'secret', 'show', '--vault-name', $vault, '--name', $keySecret, '--query', 'id', '-o', 'tsv')
        if ($found.ExitCode -eq 0) {
            Write-Host ("   {0,-24} {1}/{2}" -f 'github_app_private_key', $vault, $keySecret)
        }
        else {
            Write-Host "   The GitHub App's private key is not in $vault/$keySecret yet. Put the .pem GitHub generated there:" -ForegroundColor Yellow
            Write-Host "        az keyvault secret set --vault-name $vault --name $keySecret --file <the App's .pem>"
            Write-Host '        The repositories it reads sync once it is there (sqlflow/docs/reference/guides/github-app.md).'
        }
    }
    if ($outputs.PSObject.Properties['external_trigger_key_secrets'] -and $outputs.external_trigger_key_secrets.value) {
        # A key trigger's key is put in the vault by hand; until it is there, the control plane's external-triggers
        # start-up step fails naming it. Only each secret's existence is asked, never its value.
        $vault = $outputs.key_vault_name.value
        foreach ($trigger in $outputs.external_trigger_key_secrets.value.PSObject.Properties) {
            $found = Invoke-Az @('keyvault', 'secret', 'show', '--vault-name', $vault, '--name', $trigger.Value, '--query', 'id', '-o', 'tsv')
            if ($found.ExitCode -eq 0) {
                Write-Host ("   {0,-24} {1}/{2}" -f "trigger $($trigger.Name)", $vault, $trigger.Value)
            }
            else {
                Write-Host "   Trigger '$($trigger.Name)' has no key in $vault/$($trigger.Value) yet. Put one there and give it to the system that calls:" -ForegroundColor Yellow
                Write-Host "        az keyvault secret set --vault-name $vault --name $($trigger.Value) --value sqlft_<at least 32 random letters, digits, - or _>"
                Write-Host '        (sqlflow/docs/reference/concepts/external-triggers.md#configured-triggers)'
            }
        }
    }
    if ($outputs.PSObject.Properties['sql_setup_script'] -and $outputs.sql_setup_script.value) {
        Write-Host '   SQL: on a server this deployment does not administer, an Entra administrator of it runs the setup script, database by' -ForegroundColor Yellow
        Write-Host '        database (every statement can run again): terraform -chdir=osdu/deploy/terraform output -raw sql_setup_script'
    }

    $adminUsername = if ($EnvironmentVariables.ContainsKey('admin_username')) { $EnvironmentVariables['admin_username'] } else { 'admin' }
    Show-EstateReadiness -BaseUrl $outputs.control_plane_base_url.value -Vault $outputs.key_vault_name.value `
        -AdminPasswordSecret $outputs.admin_password_secret.value -AdminUsername $adminUsername
}

# A native command's exit code (terraform plan answers 2 for "changes", git diff 1 for "differs") is not this script's:
# every way out that succeeded says so, so a caller (the GitHub workflow's PowerShell step) reads 0.
if ($Infrastructure) {
    Invoke-Infrastructure
    exit 0
}

Write-Host ''
Write-Host '=== OSDU Delivery deploy ===' -ForegroundColor Cyan
Write-Host "   tag:  $Tag"
$mode = if ($ExplicitSelection) { 'forced (named explicitly)' } else { 'candidates (change-filtered below)' }
Write-Host "   apps: $($Apps -join ', ') [$mode]"
if ($Environment) { Write-Host "   env:  $Environment (from $($OsduEnvironment.Source))" }
Write-Host "   rg:   $Rg"
Write-Host "   acr:  $Acr"
Write-Host "   name: $($AppPrefix -join ', ')<app>, image $ImagePrefix<app>"
Write-Host "   target: $Target"
Write-Host ''

Write-Host '=== Resolved targets ===' -ForegroundColor Cyan

$targets = [ordered]@{}
$present = @()
foreach ($a in $Apps) {
    # An optional app the estate does not have is no candidate; named explicitly, it must exist.
    if (-not $ExplicitSelection -and $Config[$a].ContainsKey('Optional') -and $Config[$a].Optional -and -not (Test-AppPresent -App $a)) {
        Write-Host ("   {0,-14} -> not in this estate (optional), skipped" -f $a) -ForegroundColor DarkGray
        continue
    }
    $rec = Resolve-Target -App $a
    $targets[$a] = $rec
    $present += $a
    Write-Host ("   {0,-14} -> {1,-28} env {2,-22} now: {3}" -f $a, $rec.Name, $rec.Environment, $rec.Image)
}
$Apps = $present
if ($Apps.Count -eq 0) {
    throw "None of the apps to deploy exists in $Rg."
}

# Explicit names are an order; candidate sets (bare default, 'all') get narrowed to the
# apps whose build inputs actually changed since the tag each one is serving.
if (-not $ExplicitSelection) {
    Write-Host ''
    Write-Host '=== Change detection (build inputs vs each serving tag) ===' -ForegroundColor Cyan
    $selected = @()
    foreach ($a in $Apps) {
        $st = Get-ChangeStatus -App $a -Record $targets[$a]
        if ($st.Changed) {
            Write-Host ("   {0,-14} deploy  {1}" -f $a, $st.Reason)
            $selected += $a
        }
        else {
            Write-Host ("   {0,-14} skip    {1}" -f $a, $st.Reason) -ForegroundColor DarkGray
        }
    }
    if ($selected.Count -eq 0) {
        Write-Host ''
        Write-Host "Nothing to deploy: every candidate app already serves its build inputs at HEAD ($Tag)." -ForegroundColor Green
        exit 0
    }
    $Apps = $selected
}

# Checked over the watch paths of the apps about to be built.
Assert-Committed -Paths @($Apps | ForEach-Object { $Config[$_].Paths } | Sort-Object -Unique) -What "image $Tag"

# Deploying half the estate to one environment and half to another is never intended.
$envs = @($Apps | ForEach-Object { $targets[$_].Environment } | Sort-Object -Unique)
if ($envs.Count -gt 1) {
    throw "Selected apps span more than one environment ($($envs -join ', ')). Re-run with an explicit -Target."
}

if ($WhatIf) {
    Write-Host ''
    Write-Host "-WhatIf: nothing built or deployed. Would deploy tag $Tag to: $($Apps -join ', ')." -ForegroundColor Yellow
    exit 0
}

function Set-AppImage {
    param([string] $ResourceName, [string] $Image)
    $res = Invoke-Az @('containerapp', 'update', '-n', $ResourceName, '-g', $Rg, '--image', $Image, '-o', 'none')
    if ($res.ExitCode -ne 0) {
        Write-Host (@($res.Output) + @($res.Errors) | Out-String)
        throw "az containerapp update failed for $ResourceName (exit $($res.ExitCode))."
    }
}

function Test-Serving {
    <#
        True once the app's active revision runs $Image AND that revision is healthy.
        Matching on the image alone is not enough: during a swap the OLD revision is
        still active and Running, so a naive check passes against the previous build.
    #>
    param([string] $ResourceName, [string] $Image, [int] $TimeoutSeconds = 900)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $res = Invoke-Az @('containerapp', 'revision', 'list', '-n', $ResourceName, '-g', $Rg, '-o', 'json')
        if ($res.ExitCode -eq 0 -and $res.Output) {
            $revs = @((($res.Output -join "`n") | ConvertFrom-Json))
            foreach ($r in $revs) {
                if (-not $r.properties.active) { continue }
                $img = $null
                if ($r.properties.template.containers -and $r.properties.template.containers.Count -gt 0) {
                    $img = $r.properties.template.containers[0].image
                }
                if ($img -ne $Image) { continue }

                $running = "$($r.properties.runningState)"
                $health  = "$($r.properties.healthState)"
                # A node scaled to zero reports Scaled/Inactive rather than Running, which is
                # correct and healthy for it, so both are accepted.
                if ($running -in @('Running', 'RunningAtMaxScale', 'Scaled', 'Succeeded') -and $health -ne 'Unhealthy') {
                    return $true
                }
                if ($running -eq 'Failed' -or $health -eq 'Unhealthy') {
                    Write-Host "   $ResourceName revision $($r.name): runningState=$running healthState=$health" -ForegroundColor Red
                    return $false
                }
            }
        }
        Start-Sleep -Seconds 10
    }
    Write-Host "   $ResourceName did not serve $Image within $TimeoutSeconds s" -ForegroundColor Red
    return $false
}

function Show-Log {
    param([string] $ResourceName)
    Write-Host ''
    Write-Host "=== $ResourceName startup log (bootstrap / sync / warnings / errors) ==="
    # Only issues timestamped AFTER 'Bootstrap provisioning completed.' are real; the scheduler
    # races the migrations at startup and prints a harmless early burst.
    $res = Invoke-Az @('containerapp', 'logs', 'show', '-n', $ResourceName, '-g', $Rg, '--tail', '300', '--type', 'console')
    $res.Output | Select-String -SimpleMatch -Pattern 'Bootstrap', 'Synced', 'warn', 'error', 'exception'
}

# --- Build the images the deploy needs, reusing those the registry already holds ----------
Build-Images -BuildApps $Apps

# --- Deploy, verify, and roll back anything that does not come up --------------------
Write-Host ''
Write-Host '=== Deploying container apps ===' -ForegroundColor Cyan
$failed = @()
foreach ($a in $Apps) {
    $rec = $targets[$a]
    $newImage = "$Acr.azurecr.io/${ImagePrefix}${a}:$Tag"
    Write-Host "--- $($rec.Name) -> :$Tag ---"

    Set-AppImage -ResourceName $rec.Name -Image $newImage

    if (Test-Serving -ResourceName $rec.Name -Image $newImage) {
        Write-Host "   $($rec.Name): serving $Tag" -ForegroundColor Green
        continue
    }

    $failed += $a
    if ($NoRollback) {
        Write-Host "   $($rec.Name): FAILED to serve $Tag (-NoRollback, leaving it as is)" -ForegroundColor Red
        continue
    }
    if (-not $rec.Image) {
        Write-Host "   $($rec.Name): FAILED to serve $Tag and no previous image was recorded, cannot roll back" -ForegroundColor Red
        continue
    }

    Write-Host "   $($rec.Name): FAILED to serve $Tag, rolling back to $($rec.Image)" -ForegroundColor Red
    try {
        Set-AppImage -ResourceName $rec.Name -Image $rec.Image
        if (Test-Serving -ResourceName $rec.Name -Image $rec.Image -TimeoutSeconds 600) {
            Write-Host "   $($rec.Name): rolled back and serving $($rec.Image)" -ForegroundColor Yellow
        }
        else {
            Write-Host "   $($rec.Name): ROLLBACK DID NOT COME UP - needs manual attention" -ForegroundColor Red
        }
    }
    catch {
        Write-Host "   $($rec.Name): rollback threw: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# --- Control-plane startup log is the useful one to eyeball --------------------------
if ($Apps -contains 'control-plane' -and $failed -notcontains 'control-plane') {
    Show-Log $targets['control-plane'].Name
}

Write-Host ''
if ($failed.Count -gt 0) {
    throw "Deploy FAILED for: $($failed -join ', ') (tag $Tag). Any app listed above was rolled back unless -NoRollback was given."
}
Write-Host "=== Done. Deployed tag $Tag to: $(($Apps | ForEach-Object { $targets[$_].Name }) -join ', ') ===" -ForegroundColor Green
exit 0
