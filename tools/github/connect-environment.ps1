#requires -Version 5.1
<#
.SYNOPSIS
    Lets the GitHub Deploy workflow deploy to one environment, with no secret anywhere: a user-assigned managed identity in
    the environment's resource group, trusted for the repository's GitHub environment of the same name (a federated
    credential), the roles deploy.ps1 and the Terraform need, and the GitHub environment's AZURE_CLIENT_ID and
    AZURE_TENANT_ID variables. When the environment's files are kept outside the repository (OSDU_ENVIRONMENT_DIR), it
    also sets the GitHub environment's OSDU_ENVIRONMENT_TFVARS and OSDU_ENVIRONMENT_BACKEND to them, which the Deploy
    workflow writes back into files for deploy.ps1.

.DESCRIPTION
    Reads the environment from osdu/deploy/terraform/environments/<env>.tfvars and <env>.backend.hcl, or from the
    directory OSDU_ENVIRONMENT_DIR names, which must be filled in. Idempotent: an identity, a credential or a role
    assignment that exists is kept, so it can run again after anything changed.

      identity              <IdentityName> (default osdu-delivery-github-<env>) in the environment's resource group
      federated credential  github-<env>: issuer https://token.actions.githubusercontent.com, audience
                            api://AzureADTokenExchange, and the subject GitHub presents for the repository's
                            environment <env>: repo:<owner>/<repo>:environment:<env>, or, where the repository uses
                            immutable subjects, repo:<owner>@<owner id>/<repo>@<repo id>:environment:<env> (read from
                            the repository's OIDC settings with gh)
      on the resource group Contributor (the apps, the registry's builds, everything the Terraform creates),
                            Role Based Access Control Administrator (the role assignments the Terraform gives the apps'
                            identities), Key Vault Secrets Officer (the connection strings the Terraform writes in
                            the vault, and the initial admin's password deploy.ps1 signs in with to show the estate's
                            start-up)
      on the state account  Storage Blob Data Contributor (the environment's Terraform state)

    A managed identity, not an app registration: it lives in the environment's resource group and needs no right in the
    directory. The Terraform creates the sign-in registration itself only when its identity may create applications in
    Microsoft Graph (provision_entra_app, the default); where it may not, set provision_entra_app = false and
    azure_ad_client_id to an existing registration in the environment's file, or have a directory administrator grant the
    identity Graph's Application.ReadWrite.OwnedBy.

    Run it signed in to az as someone who may create role assignments on the resource group and the state storage
    account (Owner, or User Access Administrator beside Contributor). The GitHub environment must exist
    (tools/github/apply-repo-settings.ps1 creates dev, test and prod); its variables are set with gh when gh is
    installed and signed in, and otherwise printed as the gh commands to run.

.PARAMETER Environment
    The environment: dev, test, prod, or any other file in osdu/deploy/terraform/environments.

.PARAMETER Repository
    The GitHub repository as owner/name. Default: the one the git remote origin points at.

.PARAMETER Subject
    The subject the federated credential trusts, when it cannot be read from the repository's OIDC settings: a
    repository whose subject template is customized, or no gh. The Deploy workflow's "Login to Azure" step prints the
    subject GitHub presented when none matched.

.PARAMETER IdentityName
    The managed identity's name. Default: osdu-delivery-github-<env>.

.PARAMETER WhatIf
    Read Azure and print what would be created or assigned; change nothing.

.EXAMPLE
    .\tools\github\connect-environment.ps1 -Environment dev -WhatIf

.EXAMPLE
    .\tools\github\connect-environment.ps1 -Environment prod -Repository equinor/osdu-delivery
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string] $Environment,
    [string] $Repository,
    [string] $IdentityName,
    [string] $Subject,
    [switch] $WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Import-Module (Join-Path $RepoRoot 'osdu/deploy/OsduEnvironment.psm1') -Force

$target = Get-OsduEnvironment -Name $Environment -RepoRoot $RepoRoot -WithBackend
if (-not $IdentityName) { $IdentityName = "osdu-delivery-github-$Environment" }

if (-not $Repository) {
    $origin = (git -C $RepoRoot remote get-url origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $origin) { throw 'No git remote origin to take the repository from; pass -Repository <owner>/<name>.' }
    if ("$origin".Trim() -notmatch 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?/?$') {
        throw "The git remote origin '$origin' is not a GitHub repository; pass -Repository <owner>/<name>."
    }
    $Repository = "$($Matches[1])/$($Matches[2])"
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "-Repository '$Repository' is not <owner>/<name>."
}

foreach ($key in 'resource_group_name', 'storage_account_name') {
    if (-not $target.Backend.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($target.Backend[$key])) {
        throw "$Environment.backend.hcl in $($target.Source) sets no $key."
    }
}
$stateSubscription = if ($target.Backend.ContainsKey('subscription_id') -and $target.Backend['subscription_id']) { $target.Backend['subscription_id'] } else { $target.Subscription }

function Invoke-AzOrThrow {
    param([Parameter(Mandatory)][string[]] $AzArgs, [Parameter(Mandatory)][string] $What)
    $res = Invoke-Az $AzArgs
    if ($res.ExitCode -ne 0) {
        Write-Host (@($res.Output) + @($res.Errors) | Out-String)
        throw "$What failed (az exit $($res.ExitCode)); see above."
    }
    return $res.Output -join "`n"
}

function Invoke-Change {
    # A change to Azure or GitHub: printed, and made unless -WhatIf.
    param([Parameter(Mandatory)][string] $Description, [Parameter(Mandatory)][scriptblock] $Action)
    if ($WhatIf) {
        Write-Host "   would $Description" -ForegroundColor Yellow
        return $null
    }
    Write-Host "   $Description"
    return & $Action
}

function Get-GitHubSubject {
    <#
        The subject GitHub's OIDC token carries for a job in the repository's environment: the repository's subject
        prefix (repo:<owner>/<repo>, or with immutable subjects repo:<owner>@<owner id>/<repo>@<repo id>), read from
        its OIDC settings, then the environment. A customized template has other claims, which are not guessed.
    #>
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Warning "gh is not installed, so the repository's OIDC settings cannot be read: trusting repo:${Repository}:environment:$Environment. Where the repository uses immutable subjects the Deploy workflow's login says no federated identity matched, and prints the subject to pass as -Subject."
        return "repo:${Repository}:environment:$Environment"
    }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $answer = & gh api "repos/$Repository/actions/oidc/customization/sub" 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -ne 0) {
        throw "The OIDC settings of $Repository could not be read with gh ($($answer -join ' ')); pass -Subject with the subject the Deploy workflow's login prints."
    }
    $settings = ($answer | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n" | ConvertFrom-Json
    if ($settings.PSObject.Properties['use_default'] -and -not $settings.use_default) {
        throw "$Repository customizes its OIDC subject ($($settings.include_claim_keys -join ', ')), which this script does not compose; pass -Subject with the subject the Deploy workflow's login prints."
    }
    $prefix = if ($settings.PSObject.Properties['sub_claim_prefix'] -and $settings.sub_claim_prefix) { $settings.sub_claim_prefix } else { "repo:$Repository" }
    return "${prefix}:environment:$Environment"
}

Write-Host ''
Write-Host "=== GitHub deploys to $Environment ===" -ForegroundColor Cyan
Write-Host "   repository:  $Repository (GitHub environment '$Environment')"
Write-Host "   estate:      $($target.ResourceGroup) in subscription $($target.Subscription)"
Write-Host "   state:       $($target.Backend['storage_account_name']) in $($target.Backend['resource_group_name']) (subscription $stateSubscription)"
Write-Host "   identity:    $IdentityName"
Write-Host ''

[void](Invoke-AzOrThrow @('account', 'set', '--subscription', $target.Subscription) "Selecting subscription $($target.Subscription)")
$exists = Invoke-AzOrThrow @('group', 'exists', '--name', $target.ResourceGroup) "Looking up resource group $($target.ResourceGroup)"
if ($exists.Trim() -ne 'true') {
    throw "Resource group $($target.ResourceGroup) does not exist; create it once (az group create --name $($target.ResourceGroup) --location <region>), then run this again."
}
$resourceGroupId = (Invoke-AzOrThrow @('group', 'show', '--name', $target.ResourceGroup, '--query', 'id', '-o', 'tsv') 'Reading the resource group').Trim()
$stateAccountId = (Invoke-AzOrThrow @('storage', 'account', 'show', '--subscription', $stateSubscription, '--resource-group', $target.Backend['resource_group_name'], '--name', $target.Backend['storage_account_name'], '--query', 'id', '-o', 'tsv') "Reading state storage account $($target.Backend['storage_account_name'])").Trim()

# The identity.
$identity = $null
$found = Invoke-Az @('identity', 'show', '--resource-group', $target.ResourceGroup, '--name', $IdentityName, '-o', 'json')
if ($found.ExitCode -eq 0) {
    $identity = ($found.Output -join "`n") | ConvertFrom-Json
    Write-Host "   identity $IdentityName exists (client id $($identity.clientId))" -ForegroundColor DarkGray
}
else {
    $created = Invoke-Change "create the managed identity $IdentityName in $($target.ResourceGroup)" {
        Invoke-AzOrThrow @('identity', 'create', '--resource-group', $target.ResourceGroup, '--name', $IdentityName, '--tags', "environment=$Environment", 'purpose=github-deploy', '-o', 'json') "Creating identity $IdentityName"
    }
    if ($created) { $identity = $created | ConvertFrom-Json }
}

# The federated credential: GitHub's token for this repository's environment, and nothing else, signs in as it.
$credentialName = "github-$Environment"
$subject = if ($Subject) { $Subject } else { Get-GitHubSubject }
$issuer = 'https://token.actions.githubusercontent.com'
if ($identity) {
    $credential = Invoke-Az @('identity', 'federated-credential', 'show', '--resource-group', $target.ResourceGroup, '--identity-name', $IdentityName, '--name', $credentialName, '-o', 'json')
    $current = $null
    if ($credential.ExitCode -eq 0) {
        $current = ($credential.Output -join "`n") | ConvertFrom-Json
    }
    if ($current -and $current.subject -eq $subject -and $current.issuer -eq $issuer) {
        Write-Host "   federated credential $credentialName trusts $subject" -ForegroundColor DarkGray
    }
    else {
        $verb = if ($current) { 'update' } else { 'create' }
        [void](Invoke-Change "$verb the federated credential $credentialName for $subject" {
            Invoke-AzOrThrow @('identity', 'federated-credential', $verb, '--resource-group', $target.ResourceGroup, '--identity-name', $IdentityName, '--name', $credentialName, '--issuer', $issuer, '--subject', $subject, '--audiences', 'api://AzureADTokenExchange', '-o', 'none') "Writing federated credential $credentialName"
        })
    }
}
else {
    Write-Host "   would create the federated credential $credentialName for $subject" -ForegroundColor Yellow
}

# The roles.
$assignments = @(
    @{ Role = 'Contributor'; Scope = $resourceGroupId; Where = "resource group $($target.ResourceGroup)" },
    @{ Role = 'Role Based Access Control Administrator'; Scope = $resourceGroupId; Where = "resource group $($target.ResourceGroup)" },
    @{ Role = 'Key Vault Secrets Officer'; Scope = $resourceGroupId; Where = "resource group $($target.ResourceGroup)" },
    @{ Role = 'Storage Blob Data Contributor'; Scope = $stateAccountId; Where = "state account $($target.Backend['storage_account_name'])" }
)
foreach ($assignment in $assignments) {
    if (-not $identity) {
        Write-Host "   would assign $($assignment.Role) on $($assignment.Where)" -ForegroundColor Yellow
        continue
    }
    # The ids, counted here: on Windows az is a .cmd, and cmd misreads a JMESPath function's parentheses (length(@)).
    $ids = @((Invoke-AzOrThrow @('role', 'assignment', 'list', '--assignee', $identity.principalId, '--role', $assignment.Role, '--scope', $assignment.Scope, '--query', '[].id', '-o', 'tsv') "Reading the $($assignment.Role) assignments") -split "`n" | Where-Object { $_.Trim() })
    if ($ids.Count -gt 0) {
        Write-Host "   $($assignment.Role) on $($assignment.Where)" -ForegroundColor DarkGray
        continue
    }
    [void](Invoke-Change "assign $($assignment.Role) on $($assignment.Where)" {
        Invoke-AzOrThrow @('role', 'assignment', 'create', '--assignee-object-id', $identity.principalId, '--assignee-principal-type', 'ServicePrincipal', '--role', $assignment.Role, '--scope', $assignment.Scope, '-o', 'none') "Assigning $($assignment.Role)"
    })
}

# The GitHub environment's variables.
$clientId = if ($identity) { $identity.clientId } else { '<client id of the identity, once created>' }
$tenantId = (Invoke-AzOrThrow @('account', 'show', '--query', 'tenantId', '-o', 'tsv') 'Reading the tenant').Trim()
$variables = [ordered]@{ AZURE_CLIENT_ID = $clientId; AZURE_TENANT_ID = $tenantId }
# An environment whose files are kept outside the repository gives them to the Deploy workflow whole, as two variables
# of its GitHub environment: they hold no secret (the files never do), and a variable shows in the run's log as it is.
$files = [ordered]@{}
if ($target.FromOutside) {
    $files['OSDU_ENVIRONMENT_TFVARS'] = $target.VarFile
    $files['OSDU_ENVIRONMENT_BACKEND'] = $target.BackendFile
}
$gh = Get-Command gh -ErrorAction SilentlyContinue
Write-Host ''
if ($gh -and $identity) {
    $values = [ordered]@{}
    foreach ($name in $variables.Keys) { $values[$name] = $variables[$name] }
    foreach ($name in $files.Keys) { $values[$name] = [System.IO.File]::ReadAllText($files[$name]) }
    foreach ($name in $values.Keys) {
        [void](Invoke-Change "set the $Environment environment's $name in $Repository" {
            $prev = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            # gh reads the value from standard input when no --body is given, which keeps a file's lines as they are;
            # PowerShell 5.1 would pipe it as ASCII.
            $OutputEncoding = New-Object System.Text.UTF8Encoding $false
            $out = $values[$name] | & gh variable set $name --repo $Repository --env $Environment 2>&1
            $code = $LASTEXITCODE
            $ErrorActionPreference = $prev
            if ($code -ne 0) {
                Write-Host ($out | Out-String)
                throw "gh variable set $name failed; does the GitHub environment '$Environment' exist (tools/github/apply-repo-settings.ps1)?"
            }
        })
    }
}
else {
    Write-Host "Set these variables on the GitHub environment '$Environment' (Settings > Environments, or gh):" -ForegroundColor Yellow
    foreach ($name in $variables.Keys) {
        Write-Host "   gh variable set $name --repo $Repository --env $Environment --body $($variables[$name])"
    }
    foreach ($name in $files.Keys) {
        Write-Host "   Get-Content -Raw '$($files[$name])' | gh variable set $name --repo $Repository --env $Environment"
    }
}

Write-Host ''
Write-Host "The environment's secret, for 'Plan and apply the environment's Terraform' (set it on the GitHub environment '$Environment'):" -ForegroundColor Cyan
Write-Host '   TF_VAR_OSDU_PARTITIONS   {"<data-partition-id>": {"default": true, "url": ..., "client_id": ...}}, with no secret in it'
Write-Host '   gh secret set TF_VAR_OSDU_PARTITIONS --repo <owner/name> --env <environment>   (gh reads the value from standard input)'
Write-Host 'No other secret reaches Terraform: the deployment generates its own into the estate''s vault, and a person puts each' -ForegroundColor Cyan
Write-Host 'partition''s client secret and each private repository''s token (or the GitHub App''s private key) there, under the names' -ForegroundColor Cyan
Write-Host 'terraform output osdu_partitions and the environment''s repositories and github_app give.' -ForegroundColor Cyan
if ($WhatIf) {
    Write-Host ''
    Write-Host '-WhatIf: nothing was created, assigned or set.' -ForegroundColor Yellow
}
