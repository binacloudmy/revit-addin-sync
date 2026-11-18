// BINA Revit Sync - staging release on the netcup Windows agent (label "windows-sign").
//
// Trigger: run the job revit/revit-addin-sync/<branch> with "Build with Parameters" and give TAG
// (an existing tag such as v0.0.31-staging; create and push it first). The job discovers
// branches only, so a tag push does not start it.
//
// Two paths, chosen by the branch the job runs on:
//   * staging (and any test branch)  UNSIGNED staging release (operator decision: staging is
//     unsigned for good). installer\build-unsigned-staging.ps1 builds, zips and publishes with
//     publish-unsigned-staging.ps1, flipping latest.json LAST. No SimplySign, no approval step.
//   * main                           SIGNED path: approval pause, SimplySign certificate,
//     installer\sign-release.ps1 (build, sign every DLL and the installer, upload, flip
//     latest.json LAST). Kept for the later signed release. Needs the agent in the adham
//     desktop session (SimplySign's virtual card exists only there) and the phone code.
//
// Agent: node netcup-sign, label windows-sign, WebSocket (inbound). The unsigned path can run
// from a Windows service; the signed path needs the logged-in desktop session.
// Prod is never published from here. A tag without -staging fails the build on purpose.
//
// Jenkins credentials (Secret text): TMONE_SERVER, TMONE_BUCKET, TMONE_ACCESS_KEY_ID,
// TMONE_SECRET_ACCESS_KEY. They exist only in Jenkins, never on the netcup disk.
//
// TEST MODE: while TEST_PREFIX is set, everything uploads to that TM One folder instead of the
// real staging folder, and the engine bundle is skipped (the test key is write-only, so it
// cannot read the engine pointer). Set TEST_PREFIX = '' once a test publish is proven. With
// TEST_PREFIX = '' only the staging branch may publish.

def TEST_PREFIX = ''   // '' = real staging prefix (revit-copilot/releases-staging). Test publish to revit-addin/test proven with v0.0.88-staging (build #2).

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
                        echo 'No TAG - nothing to publish. Run Build with Parameters and give an existing vX.Y.Z-staging tag.'
                        currentBuild.result = 'NOT_BUILT'
                        return
                    }
                    if (!(tag ==~ /^v\d+\.\d+\.\d+-staging$/)) {
                        error("Tag '${tag}' - want vMAJOR.MINOR.PATCH-staging. Prod is never published from Jenkins.")
                    }
                    if (!TEST_PREFIX && env.BRANCH_NAME != 'staging' && env.BRANCH_NAME != 'main') {
                        error("Branch '${env.BRANCH_NAME}' may only publish to the test folder. Real staging publishes from the staging branch.")
                    }
                    env.RELEASE_TAG = tag
                    env.REVIT_RELEASE_PREFIX = TEST_PREFIX
                    env.SIGNED = (env.BRANCH_NAME == 'main') ? 'true' : 'false'
                    currentBuild.displayName = "#${env.BUILD_NUMBER} ${tag}" + (TEST_PREFIX ? ' (TEST folder)' : '')
                    echo "Release ${tag} from ${env.BRANCH_NAME} (" + (env.SIGNED == 'true' ? 'SIGNED' : 'unsigned') + ') -> ' +
                         (TEST_PREFIX ? "TEST folder ${TEST_PREFIX}" : 'real staging folder')
                }
            }
        }

        stage('Check out tag') {
            when { expression { env.RELEASE_TAG } }
            steps {
                // Both publish paths want a clean tree at the tag.
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

        // ---------------- unsigned staging path (staging branch and test branches) ----------------

        stage('Build and publish (unsigned staging)') {
            when { expression { env.RELEASE_TAG && env.SIGNED != 'true' } }
            steps {
                withCredentials([
                    string(credentialsId: 'TMONE_SERVER', variable: 'TMONE_SERVER'),
                    string(credentialsId: 'TMONE_BUCKET', variable: 'TMONE_BUCKET'),
                    string(credentialsId: 'TMONE_ACCESS_KEY_ID', variable: 'TMONE_ACCESS_KEY_ID'),
                    string(credentialsId: 'TMONE_SECRET_ACCESS_KEY', variable: 'TMONE_SECRET_ACCESS_KEY'),
                ]) {
                    pwsh '''
                        $ErrorActionPreference = 'Stop'
                        $extra = @{}   # hashtable: an array splat would pass -EngineZip as a positional value
                        if ($env:REVIT_RELEASE_PREFIX) {
                            Write-Host "TEST publish to $env:REVIT_RELEASE_PREFIX - addin-only, no engine bundle"
                        } else {
                            # Seed exactly the engine the staging feed already serves (fails loudly if it cannot).
                            $r = & installer/fetch-engine-bundle.ps1 -Prefix 'revit-copilot/releases-staging' `
                                   -OutDir (Join-Path (Get-Location) 'artifacts/engine')
                            if ([string]$r.present -eq 'true') { $extra.EngineZip = [string]$r.zip }
                        }
                        & installer/build-unsigned-staging.ps1 -Tag $env:RELEASE_TAG @extra
                        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                    '''
                }
            }
        }

        // ---------------- signed path (main branch) ----------------

        stage('Approve signing') {
            when { expression { env.RELEASE_TAG && env.SIGNED == 'true' } }
            steps {
                timeout(time: 60, unit: 'MINUTES') {
                    input message: "Sign and publish ${env.RELEASE_TAG}? Connect SimplySign Desktop on netcup first (phone code), then click Proceed.",
                          ok: 'Proceed'
                }
            }
        }

        stage('Find certificate') {
            when { expression { env.RELEASE_TAG && env.SIGNED == 'true' } }
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
            when { expression { env.RELEASE_TAG && env.SIGNED == 'true' } }
            steps {
                withCredentials([
                    string(credentialsId: 'TMONE_SERVER', variable: 'TMONE_SERVER'),
                    string(credentialsId: 'TMONE_BUCKET', variable: 'TMONE_BUCKET'),
                    string(credentialsId: 'TMONE_ACCESS_KEY_ID', variable: 'TMONE_ACCESS_KEY_ID'),
                    string(credentialsId: 'TMONE_SECRET_ACCESS_KEY', variable: 'TMONE_SECRET_ACCESS_KEY'),
                ]) {
                    pwsh '''
                        $ErrorActionPreference = 'Stop'
                        $extra = @{}   # hashtable: an array splat would pass -EngineZip as a positional value
                        if ($env:REVIT_RELEASE_PREFIX) {
                            Write-Host "TEST publish to $env:REVIT_RELEASE_PREFIX - addin-only, no engine bundle"
                        } else {
                            $r = & installer/fetch-engine-bundle.ps1 -Prefix 'revit-copilot/releases-staging' `
                                   -OutDir (Join-Path (Get-Location) 'artifacts/engine')
                            if ([string]$r.present -eq 'true') { $extra.EngineZip = [string]$r.zip }
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
                    echo "Published ${env.RELEASE_TAG}. Check: curl -s https://bina-ai-stg-onebistro.binacloud.ai/addin/version.json (feed can lag ~10 min; test-folder publishes do not change it)."
                }
            }
        }
    }
}
