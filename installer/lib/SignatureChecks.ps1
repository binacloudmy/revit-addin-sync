# AI-75: first-party Authenticode verification helpers.
#
# Why: sign-release.ps1 signs BinaLoader.dll, every RevitWebAppSync.dll payload
# and the setup EXE, but its final gate only verified the plugin payloads and
# the EXE with `signtool verify`, and never checked the signer identity or the
# timestamp. This module adds those checks.
#
# Testability: the decision logic in Test-SignedArtifact is a pure function over
# a System.Management.Automation.Signature-shaped object, so it is unit tested
# with synthetic objects and in-memory certificates. The production caller
# (installer/sign-release.ps1) is responsible for obtaining a real signature via
# Get-AuthenticodeSignature and for the signtool chain/trust validation.
#
# Test-SignedArtifact does NOT verify cryptographic trust on its own. It reads
# fields the platform already validated (Status) plus signer identity and
# timestamp PRESENCE. Chain trust and timestamp TRUST come from
# Get-AuthenticodeSignature (Status -eq 'Valid') and `signtool verify /pa /all`.
#
# Identity: subject O, issuer O (the issuing CA), and, when the release pins a
# cert with -Thumbprint, the exact signer thumbprint.
#
# Runtime: Get-SignerOrganization needs the .NET 7+ API
# X500DistinguishedName.EnumerateRelativeDistinguishedNames() (PowerShell 7.3+).
# The requirement is checked at call time with a clear error.

function Test-DnApiAvailable {
    [CmdletBinding()]
    param()
    # Process-level check (no certificate needed): used as an early preflight in
    # sign-release.ps1, before any build/signing work.
    return [bool][System.Security.Cryptography.X509Certificates.X500DistinguishedName].GetMethod('EnumerateRelativeDistinguishedNames')
}

function Test-DnEnumerationApiAvailable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )
    return [bool]$Certificate.SubjectName.PSObject.Methods['EnumerateRelativeDistinguishedNames']
}

function Get-SignerOrganization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )
    # SubjectName IS the encoded Subject Distinguished Name. Do NOT use RawData
    # (that is the entire certificate DER, not the subject DN).
    return Get-DnOrganization -Certificate $Certificate -Dn $Certificate.SubjectName
}

function Get-IssuerOrganization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )
    # IssuerName is the issuing CA's DN as encoded in the leaf, parsed the same
    # platform-decoded way as the subject (no string parsing).
    return Get-DnOrganization -Certificate $Certificate -Dn $Certificate.IssuerName
}

function Get-DnOrganization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X500DistinguishedName]$Dn
    )
    if (-not (Test-DnEnumerationApiAvailable -Certificate $Certificate)) {
        throw ("Get-SignerOrganization requires a .NET 7+ runtime exposing " +
               "X500DistinguishedName.EnumerateRelativeDistinguishedNames() " +
               "(PowerShell 7.3+). Current: PowerShell " +
               $PSVersionTable.PSVersion.ToString() + " on .NET " +
               [System.Environment]::Version.ToString() + ".")
    }

    foreach ($rdn in $Dn.EnumerateRelativeDistinguishedNames()) {
        # A multi-valued RDN has no public single-type accessor: the platform
        # throws if we ask (HasMultipleElements). Skip it rather than fail, so an
        # unrelated multi-valued RDN elsewhere in the DN cannot break the scan.
        if ($rdn.HasMultipleElements) { continue }
        if ($rdn.GetSingleElementType().Value -eq '2.5.4.10') {   # OID 2.5.4.10 = O
            return $rdn.GetSingleElementValue()                   # already unescaped by the platform
        }
    }
    return $null   # no single-element Organization attribute present
}

function Test-SignedArtifact {
    [CmdletBinding()]
    param(
        # AllowNull so this function owns the null-signature failure path
        # instead of parameter binding rejecting it.
        [Parameter(Mandatory)][AllowNull()] $Sig,
        [string]$ExpectedOrganization = 'BINA CLOUDTECH SDN BHD',
        # O of the issuing CA (Certum Code Signing 2021 CA). Closes the "any
        # trusted CA issuing a cert with our O" gap on the /a auto-select path.
        [string]$ExpectedIssuerOrganization = 'Asseco Data Systems S.A.',
        # When set (sign-release -Thumbprint), the signer must be exactly the
        # cert build-installer exported as bina-cloudtech.cer for pre-trust.
        [string]$ExpectedThumbprint = '',
        [System.StringComparison]$Comparison = [System.StringComparison]::OrdinalIgnoreCase
    )
    if ($null -eq $Sig) { return @{ Ok = $false; Reason = 'no signature object' } }
    if ($Sig.Status -ne 'Valid') {
        return @{ Ok = $false; Reason = "Authenticode status '$($Sig.Status)': $($Sig.StatusMessage)" }
    }
    if ($null -eq $Sig.SignerCertificate) {
        return @{ Ok = $false; Reason = 'no signer certificate' }
    }
    # Timestamp PRESENCE only. Trust is proven by Status (above) and by the
    # caller's `signtool verify /pa /all`; presence alone is not trust.
    if ($null -eq $Sig.TimeStamperCertificate) {
        return @{ Ok = $false; Reason = 'timestamp absent (TimeStamperCertificate is null)' }
    }
    $org = Get-SignerOrganization -Certificate $Sig.SignerCertificate
    if ($null -eq $org) {
        return @{ Ok = $false; Reason = "organization attribute absent (expected '$ExpectedOrganization')" }
    }
    if (-not [string]::Equals($org, $ExpectedOrganization, $Comparison)) {
        return @{ Ok = $false; Reason = "organization mismatch: cert O='$org', expected '$ExpectedOrganization'" }
    }
    $issuerOrg = Get-IssuerOrganization -Certificate $Sig.SignerCertificate
    if (-not [string]::Equals($issuerOrg, $ExpectedIssuerOrganization, $Comparison)) {
        return @{ Ok = $false; Reason = "issuer mismatch: cert issuer O='$issuerOrg', expected '$ExpectedIssuerOrganization'" }
    }
    $pin = ''
    if ($ExpectedThumbprint) {
        $want = $ExpectedThumbprint -replace '\s', ''
        $got  = $Sig.SignerCertificate.Thumbprint
        if (-not [string]::Equals($got, $want, [System.StringComparison]::OrdinalIgnoreCase)) {
            return @{ Ok = $false; Reason = "thumbprint mismatch: signer $got, expected $want (the pre-trusted cert)" }
        }
        $pin = "; thumbprint pinned"
    }
    return @{ Ok = $true; Reason = "valid; signer O='$org'; issuer O='$issuerOrg'$pin; timestamp present (trust via Authenticode/signtool)" }
}
