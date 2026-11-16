// BINA Revit Sync - signed staging release on the netcup Windows agent (label "windows-sign").
//
// Trigger: run the job revit/revit-addin-sync/staging with "Build with Parameters" and give TAG
// (an existing tag such as v0.0.31-staging; create and push it first). The job discovers
// branches only, so a tag push does not start it.
// The pipeline checks out that tag, pauses for approval, then runs installer\sign-release.ps1,
// which builds, signs every DLL and the installer, uploads to TM One and flips latest.json LAST.
//
// Signing needs the Certum SimplySign certificate:
//   * Agent: node netcup-sign, label windows-sign, WebSocket (inbound) agent. Start agent.jar from
//     the logged-in desktop session of Windows user adham (Startup folder or logon task), NOT as
//     a Windows service: SimplySign's virtual card exists only in that session. Leave the RDP
//     session disconnected, never signed out.
//   * At the approval step, the person with the SimplySign phone connects SimplySign Desktop on
//     netcup with the one-time code, then clicks Proceed. The code lasts ~30 s and the session
//     drops after a VM suspend, so connect right before Proceed.
//
// Prod is never published from here (prod signing stays on the operator's box).
// A tag without -staging fails the build on purpose.
//
// Jenkins credentials (Secret text): TMONE_SERVER, TMONE_BUCKET, TMONE_ACCESS_KEY_ID,
// TMONE_SECRET_ACCESS_KEY. They exist only in Jenkins, never on the netcup disk.
//
// TEST MODE: while TEST_PREFIX is set, everything uploads to that TM One folder instead of the
// real staging folder, and the engine bundle is skipped (the test key is write-only, so it
// cannot read the engine pointer). Set TEST_PREFIX = '' once a test publish is proven.
// Before that, turn off publish-staging-unsigned in .github/workflows/release.yml so a tag
// push does not also publish an unsigned build to the real staging folder.

def TEST_PREFIX = 'revit-addin/test'   // '' = real staging prefix (revit-copilot/releases-staging)

pipeline {
    agent { label 'windows-sign' }

    options {
        // One build at a time: netcup also runs the staging BIM converter.
        disableConcurrentBuilds()
        buildDiscarder(logRotator(numToKeepStr: '20'))
        timeout(time: 4, unit: 'HOURS')
    }

    parameters {
        string(name: 'TAG', defaultValue: '', description: 'Existing tag to release, e.g. v0.0.31-staging (pushed to GitHub, on a commit that has this Jenkinsfile).')
    }

    environment {
        DOTNET_NOLOGO = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }

    stages {
        stage('Resolve tag') {
            steps {
                script {
                    def tag = (env.TAG_NAME ?: params.TAG ?: '').trim()
                    if (!tag) {
                        echo 'No TAG - nothing to sign or publish. Run Build with Parameters and give an existing vX.Y.Z-staging tag.'
                        currentBuild.result = 'NOT_BUILT'
                        return
                    }
                    if (!(tag ==~ /^v\d+\.\d+\.\d+-staging$/)) {
                        error("Tag '${tag}' - want vMAJOR.MINOR.PATCH-staging. Prod is never published from Jenkins.")
                    }
                    env.RELEASE_TAG = tag
                    env.REVIT_RELEASE_PREFIX = TEST_PREFIX
                    currentBuild.displayName = "#${env.BUILD_NUMBER} ${tag}" + (TEST_PREFIX ? ' (TEST folder)' : '')
                    echo "Release ${tag} -> " + (TEST_PREFIX ? "TEST folder ${TEST_PREFIX}" : 'real staging folder')
                }
            }
        }

        stage('Check out tag') {
            when { expression { env.RELEASE_TAG } }
            steps {
                // sign-release.ps1 refuses a dirty tree or a HEAD that is not the tag.
                pwsh '''
                    $ErrorActionPreference = 'Stop'
                    git fetch --tags --quiet
                    git clean -ffdx
                    git checkout --detach --force $env:RELEASE_TAG
                    if ($LASTEXITCODE -ne 0) { throw "cannot check out $env:RELEASE_TAG" }
                    git status --short
                '''
            }
        }

        stage('Approve signing') {
            when { expression { env.RELEASE_TAG } }
            steps {
                timeout(time: 60, unit: 'MINUTES') {
                    input message: "Sign and publish ${env.RELEASE_TAG}? Connect SimplySign Desktop on netcup first (phone code), then click Proceed.",
                          ok: 'Proceed'
                }
            }
        }

        stage('Find certificate') {
            when { expression { env.RELEASE_TAG } }
            steps {
                script {
                    // Exactly one valid code-signing cert must be live, as in the manual runbook.
                    env.CERT_THUMBPRINT = pwsh(returnStdout: true, script: '''
                        $ErrorActionPreference = 'Stop'
                        $certs = @(Get-ChildItem Cert:\\CurrentUser\\My -CodeSigningCert |
                                   Where-Object { $_.NotAfter -gt (Get-Date) -and $_.NotBefore -lt (Get-Date) })
                        if ($certs.Count -ne 1) { throw "Expected 1 valid code-signing cert in CurrentUser\\My, found $($certs.Count). Is SimplySign connected for this Windows user?" }
                        $certs[0].Thumbprint
                    ''').trim()
                    echo "Signing cert thumbprint ${env.CERT_THUMBPRINT}"
                }
            }
        }

        stage('Build, sign, publish') {
            when { expression { env.RELEASE_TAG } }
            steps {
                withCredentials([
                    string(credentialsId: 'TMONE_SERVER', variable: 'TMONE_SERVER'),
                    string(credentialsId: 'TMONE_BUCKET', variable: 'TMONE_BUCKET'),
                    string(credentialsId: 'TMONE_ACCESS_KEY_ID', variable: 'TMONE_ACCESS_KEY_ID'),
                    string(credentialsId: 'TMONE_SECRET_ACCESS_KEY', variable: 'TMONE_SECRET_ACCESS_KEY'),
                ]) {
                    pwsh '''
                        $ErrorActionPreference = 'Stop'
                        $extra = @()
                        if ($env:REVIT_RELEASE_PREFIX) {
                            Write-Host "TEST publish to $env:REVIT_RELEASE_PREFIX - addin-only, no engine bundle"
                        } else {
                            # Seed exactly the engine the staging feed already serves (fails loudly if it cannot).
                            $r = & installer/fetch-engine-bundle.ps1 -Prefix 'revit-copilot/releases-staging' `
                                   -OutDir (Join-Path (Get-Location) 'artifacts/engine')
                            if ([string]$r.present -eq 'true') { $extra = @('-EngineZip', $r.zip) }
                        }
                        & installer/sign-release.ps1 -Tag $env:RELEASE_TAG -Thumbprint $env:CERT_THUMBPRINT @extra
                        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                    '''
                }
            }
        }
    }

    post {
        success {
            script {
                if (env.RELEASE_TAG) {
                    echo "Published ${env.RELEASE_TAG}. Check: curl -s https://bina-ai-stg-onebistro.binacloud.ai/addin/version.json (feed can lag ~10 min)."
                }
            }
        }
    }
}
