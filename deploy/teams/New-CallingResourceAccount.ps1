<#
.SYNOPSIS
    Creates the Teams resource account for the Mail Router calling bot (README "Teams calls", step 3).

.DESCRIPTION
    1. Creates a Teams resource account linked to the bot's app registration (same as GRAPH__CLIENTID).
    2. Syncs it so Teams routes calls to the bot.
    3. Sets the usage location and assigns the free "Teams Phone Resource Account" licence.
    4. Optionally assigns a calling licence and/or a phone number, needed to transfer calls to outside numbers.

    Run it as a Teams administrator + user/licence administrator. It is safe to re-run: existing objects are reused.
    After it finishes, allow up to a few hours (sometimes 24 h) before calls forwarded to the account reach the bot.

.EXAMPLE
    .\New-CallingResourceAccount.ps1 -UserPrincipalName mailrouter-bot@auxitech.ch

.EXAMPLE
    # With Operator Connect number (needed for transfers to external numbers):
    .\New-CallingResourceAccount.ps1 -UserPrincipalName mailrouter-bot@auxitech.ch -PhoneNumber +41211234567 -PhoneNumberType OperatorConnect
#>
[CmdletBinding()]
param(
    # UPN of the new resource account. Must use a domain verified in your tenant.
    [Parameter(Mandatory)] [string] $UserPrincipalName,

    [string] $DisplayName = "Mail Router Assistant",

    # App registration (client) id: same as GRAPH__CLIENTID.
    [string] $ApplicationId = "8d0b14e2-4ec9-44fd-b517-316edc0b53d7",

    [string] $TenantId = "422e5f8e-d6e2-4bcd-82cd-f4c7eeaf79fd",

    # Two-letter country code, required before licences can be assigned.
    [string] $UsageLocation = "CH",

    # Optional extra licence SKU part number for PSTN calling, e.g. a Calling Plan.
    # Leave empty for Operator Connect / Direct Routing (no calling plan licence needed).
    [string] $CallingLicenceSku = "",

    # Optional phone number for the resource account (E.164), and how it's provided.
    [string] $PhoneNumber = "",
    [ValidateSet("CallingPlan", "OperatorConnect", "DirectRouting")] [string] $PhoneNumberType = "OperatorConnect",

    # Direct Routing only: voice routing policy that allows the outbound calls.
    [string] $VoiceRoutingPolicy = ""
)

$ErrorActionPreference = "Stop"
# Free licence every resource account needs.
$ResourceAccountSku = "PHONESYSTEM_VIRTUALUSER"

# ---------- Modules ----------
foreach ($m in "MicrosoftTeams", "Microsoft.Graph.Users", "Microsoft.Graph.Users.Actions", "Microsoft.Graph.Identity.DirectoryManagement") {
    if (-not (Get-Module -ListAvailable -Name $m)) {
        Write-Host "Installing module $m..." -ForegroundColor Yellow
        Install-Module $m -Scope CurrentUser -Force -AllowClobber
    }
}
Import-Module MicrosoftTeams
Import-Module Microsoft.Graph.Users
Import-Module Microsoft.Graph.Users.Actions   # Set-MgUserLicense
Import-Module Microsoft.Graph.Identity.DirectoryManagement

Write-Host "Signing in to Teams and Microsoft Graph (tenant $TenantId)..." -ForegroundColor Cyan
Connect-MicrosoftTeams -TenantId $TenantId | Out-Null
Connect-MgGraph -TenantId $TenantId -Scopes "User.ReadWrite.All", "Organization.Read.All" -NoWelcome

# ---------- 1. Resource account ----------
$ra = Get-CsOnlineApplicationInstance -Identity $UserPrincipalName -ErrorAction SilentlyContinue
if ($ra) {
    Write-Host "Resource account $UserPrincipalName already exists (ObjectId $($ra.ObjectId))." -ForegroundColor Green
    if ($ra.ApplicationId -ne $ApplicationId) {
        Write-Host "Linking it to app $ApplicationId..." -ForegroundColor Yellow
        Set-CsOnlineApplicationInstance -Identity $UserPrincipalName -ApplicationId $ApplicationId | Out-Null
    }
} else {
    Write-Host "Creating resource account $UserPrincipalName..." -ForegroundColor Cyan
    $ra = New-CsOnlineApplicationInstance -UserPrincipalName $UserPrincipalName -ApplicationId $ApplicationId -DisplayName $DisplayName
    Write-Host "Created (ObjectId $($ra.ObjectId))." -ForegroundColor Green
}

# ---------- 2. Sync to the bot ----------
Write-Host "Syncing the resource account with the bot..." -ForegroundColor Cyan
Sync-CsOnlineApplicationInstance -ObjectId $ra.ObjectId -ApplicationId $ApplicationId

# ---------- 3. Usage location + resource account licence ----------
# The Entra user can take a minute to appear after creation.
$user = $null
for ($i = 0; $i -lt 12 -and -not $user; $i++) {
    $user = Get-MgUser -UserId $ra.ObjectId -Property Id,UsageLocation,AssignedLicenses -ErrorAction SilentlyContinue
    if (-not $user) { Start-Sleep -Seconds 10 }
}
if (-not $user) { throw "Entra user $($ra.ObjectId) not found yet. Wait a few minutes and re-run the script." }

if ($user.UsageLocation -ne $UsageLocation) {
    Write-Host "Setting usage location to $UsageLocation..." -ForegroundColor Cyan
    Update-MgUser -UserId $user.Id -UsageLocation $UsageLocation
}

function Add-Licence([string] $skuPartNumber) {
    $sku = Get-MgSubscribedSku -All | Where-Object SkuPartNumber -eq $skuPartNumber
    if (-not $sku) {
        throw "Licence '$skuPartNumber' isn't in your tenant. Get it in the Microsoft 365 admin centre (Billing > Purchase services) and re-run."
    }
    $free = $sku.PrepaidUnits.Enabled - $sku.ConsumedUnits
    if ($user.AssignedLicenses.SkuId -contains $sku.SkuId) {
        Write-Host "Licence $skuPartNumber already assigned." -ForegroundColor Green
        return
    }
    if ($free -le 0) { throw "No free '$skuPartNumber' licence left ($($sku.ConsumedUnits)/$($sku.PrepaidUnits.Enabled) used)." }
    Write-Host "Assigning licence $skuPartNumber..." -ForegroundColor Cyan
    Set-MgUserLicense -UserId $user.Id -AddLicenses @(@{ SkuId = $sku.SkuId }) -RemoveLicenses @() | Out-Null
}

Add-Licence $ResourceAccountSku
if ($CallingLicenceSku) { Add-Licence $CallingLicenceSku }

# ---------- 4. Phone number (optional, for transfers to external numbers) ----------
if ($PhoneNumber) {
    Write-Host "Assigning $PhoneNumber ($PhoneNumberType)..." -ForegroundColor Cyan
    Set-CsPhoneNumberAssignment -Identity $UserPrincipalName -PhoneNumber $PhoneNumber -PhoneNumberType $PhoneNumberType
    if ($PhoneNumberType -eq "DirectRouting" -and $VoiceRoutingPolicy) {
        Grant-CsOnlineVoiceRoutingPolicy -Identity $UserPrincipalName -PolicyName $VoiceRoutingPolicy
    }
}

# ---------- Summary ----------
$ra = Get-CsOnlineApplicationInstance -Identity $UserPrincipalName
Write-Host ""
Write-Host "Done." -ForegroundColor Green
$ra | Format-List DisplayName, UserPrincipalName, ObjectId, ApplicationId, PhoneNumber
Write-Host "Next:" -ForegroundColor Cyan
Write-Host "  - Teams > Settings > Calls > If unanswered > Forward to > Contact or number > '$DisplayName'."
Write-Host "  - Wait for the sync (often 1-4 h, up to 24 h), then call yourself and don't pick up."
Write-Host "  - Pod logs should then show 'Call notification: ... incoming'."
