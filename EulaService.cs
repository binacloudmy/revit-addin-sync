using System;
using System.IO;
using Newtonsoft.Json;

namespace BinaConnector
{
    /// <summary>Tracks whether the user has accepted the EULA, and which version.</summary>
    public static class EulaService
    {
        public const string CurrentVersion = "1.0";

        // Source of truth: kept here in code; bundle-templates/EULA.html must be kept in
        // sync (the build copies it into the bundle as Resources/EULA.html).
        public const string EulaText =
@"BINA Platform Connector — End User License Agreement
Version 1.0 — Last updated 2026-05-07

Publisher: BINA CLOUDTECH SDN BHD (Malaysia)

PLEASE READ THIS AGREEMENT CAREFULLY. BY CLICKING ""I AGREE"" YOU ACCEPT THE TERMS BELOW.
IF YOU DO NOT AGREE, DO NOT INSTALL OR USE THIS SOFTWARE.

1. License Grant
   BINA CLOUDTECH SDN BHD grants you a non-exclusive, non-transferable, royalty-free
   license to install and use the BINA Platform Connector (""Software"") solely with
   Autodesk Revit, for the purpose of uploading project files to BINA Cloud services.

2. Account
   Use of the Software requires a valid BINA Cloud account. You are responsible for
   maintaining the confidentiality of your credentials and for all activity under
   your account.

3. Data Transmission and Privacy
   The Software transmits Revit model files and associated metadata that you choose
   to upload to BINA Cloud. You retain all ownership of your content. BINA processes
   uploaded content only as required to provide the BINA Cloud service.

   Personal data processing is governed by the BINA CLOUDTECH SDN BHD Privacy Policy,
   available at https://bina.cloud/privacy_policy.html, which is incorporated into
   this Agreement by reference. Your use of the BINA Cloud service is also subject
   to the BINA Cloud Terms & Conditions, available at
   https://bina.cloud/terms-and-conditions.html.

4. No Warranty
   THE SOFTWARE IS PROVIDED ""AS IS"" WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
   INCLUDING BUT NOT LIMITED TO WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
   PARTICULAR PURPOSE, AND NON-INFRINGEMENT.

5. Limitation of Liability
   TO THE MAXIMUM EXTENT PERMITTED BY APPLICABLE LAW, BINA CLOUDTECH SDN BHD SHALL
   NOT BE LIABLE FOR ANY INDIRECT, INCIDENTAL, SPECIAL, CONSEQUENTIAL, OR PUNITIVE
   DAMAGES ARISING OUT OF OR RELATED TO YOUR USE OF THE SOFTWARE.

6. Termination
   This license remains in effect until terminated. You may terminate it at any time
   by uninstalling the Software. BINA may terminate this license for material breach
   of these terms.

7. Governing Law
   This Agreement is governed by the laws of Malaysia. Any dispute shall be submitted
   to the exclusive jurisdiction of the Malaysian courts.

8. Autodesk App Store Acknowledgments
   Where you obtained the Software through the Autodesk App Store, you acknowledge
   and agree that:
   (a) This Agreement is concluded between you and BINA CLOUDTECH SDN BHD only, and
       not with Autodesk, Inc. or any of its affiliates.
   (b) BINA CLOUDTECH SDN BHD, and not Autodesk, is solely responsible for the
       Software and for any warranties (whether express or implied by law),
       maintenance, or support related to the Software, to the extent not
       effectively disclaimed in Section 4.
   (c) Autodesk, Inc., its affiliates, and their respective officers, directors,
       employees, agents, contractors, partners, licensors, and licensees
       (collectively, the ""Autodesk Parties"") have no warranty obligation
       whatsoever with respect to the Software, and to the maximum extent
       permitted by applicable law, will not be liable for any claim, loss, or
       expense arising out of or relating to your use of the Software.
   (d) The Autodesk Parties are intended third-party beneficiaries of this
       Agreement and are entitled to enforce its terms against you.

9. Contact
   Questions about this agreement: info@bina.cloud
   Web: https://bina.cloud

By clicking ""I Agree"" below, you acknowledge that you have read this Agreement,
understand it, and agree to be bound by its terms.
";

        private class Acceptance
        {
            public string Version { get; set; }
            public string AcceptedAt { get; set; }
        }

        public static bool HasAccepted()
        {
            try
            {
                if (!File.Exists(Paths.EulaAcceptedFile)) return false;
                string json = File.ReadAllText(Paths.EulaAcceptedFile);
                var record = JsonConvert.DeserializeObject<Acceptance>(json);
                return record != null
                    && !string.IsNullOrEmpty(record.Version)
                    && record.Version == CurrentVersion;
            }
            catch { return false; }
        }

        public static void RecordAcceptance()
        {
            try
            {
                Paths.EnsureDirectories();
                var record = new Acceptance
                {
                    Version = CurrentVersion,
                    AcceptedAt = DateTime.UtcNow.ToString("o")
                };
                File.WriteAllText(Paths.EulaAcceptedFile, JsonConvert.SerializeObject(record, Formatting.Indented));
            }
            catch { /* persistence failures are non-fatal — user will be re-prompted next launch */ }
        }
    }
}
