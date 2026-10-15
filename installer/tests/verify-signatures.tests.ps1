<#
.SYNOPSIS
  Unit tests for installer/lib/SignatureChecks.ps1 (AI-75).

.DESCRIPTION
  Exercises the pure decision logic with synthetic System.Management.Automation.
  Signature-shaped objects backed by REAL in-memory certificates (built with
  CertificateRequest), so subject-DN parsing and escaping are exercised for
  real. No production certificates, no secrets, no network, no store writes.

  Requires PowerShell 7.3+ (.NET 7+), which CI's windows-latest satisfies.
  Run from anywhere:  installer\tests\verify-signatures.tests.ps1
  Exit code is the failure count.
#>
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "verify-signatures.tests.ps1 requires PowerShell 7+ (uses CertificateRequest). Current: $($PSVersionTable.PSVersion)"
}
if (-not [System.Security.Cryptography.X509Certificates.X500DistinguishedName].GetMethod('EnumerateRelativeDistinguishedNames')) {
    throw "verify-signatures.tests.ps1 requires .NET 7+ (pwsh 7.3+): X500DistinguishedName.EnumerateRelativeDistinguishedNames is unavailable on this host."
}

. (Join-Path $PSScriptRoot '..\lib\SignatureChecks.ps1')

$script:total = 0
$script:failures = 0
function Pass([string]$name) { $script:total++; Write-Host "PASS  $name" }
function Fail([string]$name, [string]$why) { $script:total++; $script:failures++; Write-Host "FAIL  $name`n      $why" }

function New-TestCertificate([string]$Subject) {
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $Subject, $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    return $req.CreateSelfSigned(
        [System.DateTimeOffset]::UtcNow.AddDays(-1),
        [System.DateTimeOffset]::UtcNow.AddDays(1))
}

function New-SignatureObject($Certificate, [string]$Status = 'Valid', [bool]$WithTimestamp = $true) {
    return [pscustomobject]@{
        Status                 = $Status
        StatusMessage          = ''
        SignerCertificate      = $Certificate
        TimeStamperCertificate = if ($WithTimestamp) { [pscustomobject]@{ Subject = 'CN=Test Timestamp' } } else { $null }
    }
}

function Assert-Result([string]$Name, $Signature, [string]$ExpectedOrg, [bool]$WantOk, [string]$ReasonLike) {
    $r = Test-SignedArtifact -Sig $Signature -ExpectedOrganization $ExpectedOrg
    $okMatch = ($r.Ok -eq $WantOk)
    $reasonMatch = if ($ReasonLike) { $r.Reason -match $ReasonLike } else { $true }
    if ($okMatch -and $reasonMatch) { Pass $Name }
    else { Fail $Name "Ok=$($r.Ok) Reason='$($r.Reason)' wanted Ok=$WantOk reason~/$ReasonLike/" }
}

$org       = 'BINA CLOUDTECH SDN BHD'
$matching  = New-TestCertificate "CN=BINA, O=$org"
$wrong     = New-TestCertificate 'CN=Other, O=Some Other Sdn Bhd'
$superstr  = New-TestCertificate "CN=Other, O=X $org Y"
$escaped   = New-TestCertificate 'CN=BINA, O="ACME, INC"'
$noOrg     = New-TestCertificate 'CN=OnlyCommonName'
$multiRdn  = New-TestCertificate "CN=BINA+OU=Eng, O=$org"

# --- DN extraction (validates SubjectName, not RawData) ----------------------
try {
    $o = Get-SignerOrganization -Certificate $matching
    if ($o -eq $org) { Pass 'Get-SignerOrganization reads SubjectName O exactly' }
    else { Fail 'Get-SignerOrganization reads SubjectName O exactly' "got '$o'" }
} catch { Fail 'Get-SignerOrganization reads SubjectName O exactly' $_.Exception.Message }

try {
    $o = Get-SignerOrganization -Certificate $escaped
    if ($o -eq 'ACME, INC') { Pass 'Get-SignerOrganization decodes quoted comma exactly' }
    else { Fail 'Get-SignerOrganization decodes quoted comma exactly' "got '$o'" }
} catch { Fail 'Get-SignerOrganization decodes quoted comma exactly' $_.Exception.Message }

try {
    $o = Get-SignerOrganization -Certificate $multiRdn
    if ($o -eq $org) { Pass 'multi-valued RDN elsewhere is skipped, O still found' }
    else { Fail 'multi-valued RDN elsewhere is skipped' "got '$o'" }
} catch { Fail 'multi-valued RDN elsewhere is skipped' $_.Exception.Message }

# --- decision logic ----------------------------------------------------------
Assert-Result 'valid + matching O + timestamp'            (New-SignatureObject $matching) $org $true  $null
Assert-Result 'timestamp absent'                          (New-SignatureObject $matching 'Valid' $false) $org $false 'timestamp absent'
Assert-Result 'wrong organization'                        (New-SignatureObject $wrong) $org $false 'organization mismatch'
Assert-Result 'organization superstring is not a match'   (New-SignatureObject $superstr) $org $false 'organization mismatch'
Assert-Result 'escaped comma organization'                (New-SignatureObject $escaped) 'ACME, INC' $true $null
Assert-Result 'organization attribute absent'             (New-SignatureObject $noOrg) $org $false 'organization attribute absent'
Assert-Result 'null signature object'                     $null $org $false 'no signature object'
Assert-Result 'status NotSigned'                          (New-SignatureObject $matching 'NotSigned') $org $false "status 'NotSigned'"
Assert-Result 'status UnknownError (untrusted)'           (New-SignatureObject $matching 'UnknownError') $org $false "status 'UnknownError'"
Assert-Result 'signer certificate missing'                ([pscustomobject]@{ Status='Valid'; StatusMessage=''; SignerCertificate=$null; TimeStamperCertificate=[pscustomobject]@{ Subject='CN=Test Timestamp' } }) $org $false 'no signer certificate'

# --- production wiring -------------------------------------------------------
$src = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\sign-release.ps1') -Raw
if ($src -match 'Get-AuthenticodeSignature' -and $src -match 'Test-SignedArtifact\s+-Sig') {
    Pass 'sign-release.ps1 wires Get-AuthenticodeSignature -> Test-SignedArtifact'
} else {
    Fail 'sign-release.ps1 wiring' 'missing Get-AuthenticodeSignature or Test-SignedArtifact -Sig'
}
if ($src -match 'signtool verify /pa /all') {
    Pass 'sign-release.ps1 keeps signtool verify /pa /all'
} else {
    Fail 'sign-release.ps1 keeps signtool verify /pa /all' 'signtool /pa /all not found'
}

Write-Host ""
Write-Host "$($script:total - $script:failures) passed, $script:failures failed"
exit $script:failures
