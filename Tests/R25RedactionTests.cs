#nullable enable
// r25 audit (SHOULD): Revit's own wording slipped through DialogRedaction. Navisworks, point-cloud, CAD, image and PDF files kept their
// names; a cloud model path ("BIM 360://Project/Model.rvt", "Autodesk Docs://...") kept the project name; a full name ("owned by Aminah
// Binti Ali") kept all but the first word; "edited by user aminah.b" and an unquoted "until aminah.b" kept the user. Revit-free.

using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class R25RedactionTests
    {
        [Theory]
        [InlineData("Cannot find the linked file 'ACME Hospital Structure.nwc'", "Cannot find the linked file '<file>'")]
        [InlineData("Point cloud ACME_Scan_Level2.rcp is missing", "Point cloud <file> is missing")]
        [InlineData("Scan Tower.rcs, Site.dxf, Plan.dgn, Mass.sat and Model.skp were not found", "Scan <file>, <file>, <file>, <file> and <file> were not found")]
        [InlineData("Coordination.nwd, Sheets.pdf, Logo.png, Photo.jpg and Photo.JPEG", "<file>, <file>, <file>, <file> and <file>")]
        [InlineData("Image ACME logo.png not found", "Image ACME <file> not found")]
        public void RevitsOtherFileTypesAreRedacted(string raw, string expected) =>
            Assert.Equal(expected, DialogRedaction.RedactPaths(raw, "nobody-here", null));

        [Theory]
        [InlineData("BIM 360://ACME Hospital Tower/ARC Model.rvt could not be found", "BIM 360://<path>")]
        [InlineData("Autodesk Docs://ACME Hospital Tower/ARC Model.rvt", "Autodesk Docs://<path>")]
        [InlineData("The central model 'Autodesk Docs://ACME Tower/ARC.rvt' is not available", "The central model 'Autodesk Docs://<path>' is not available")]
        public void ACloudModelPathIsRedactedToTheEnd(string raw, string expected) =>
            Assert.Equal(expected, DialogRedaction.RedactPaths(raw, "nobody-here", null));

        [Theory]
        [InlineData("Element is owned by Aminah Binti Ali", "Element is owned by <user>")]
        [InlineData("Element is owned by Aminah Binti Ali, request it first", "Element is owned by <user>, request it first")]
        [InlineData("The element is being edited by user aminah.b", "The element is being edited by <user>")]
        [InlineData("Can't edit the element until aminah.b resaves the element to central", "Can't edit the element until <user> resaves the element to central")]
        [InlineData("Can't edit until you Reload Latest", "Can't edit until you Reload Latest")]
        [InlineData("Wait until the sync ends", "Wait until the sync ends")]
        public void WorksharingNamesInRevitsOwnWordsAreRedacted(string raw, string expected) =>
            Assert.Equal(expected, DialogRedaction.RedactPaths(raw, "nobody-here", null));

        // r25 verifier: the user rules took any capitalised word after "until" and after "used by" as a user. Only Revit's
        // worksharing wording names one: "owned/edited/borrowed/reserved/checked out by", "locked by user", "until <name> resaves".
        [Theory]
        [InlineData("Save until Friday.")]
        [InlineData("Wait until Revit finishes loading.")]
        [InlineData("This view is locked until Level 2 is visible.")]
        [InlineData("Wait until Level 3 is cut, then try again.")]
        [InlineData("Element is used by Group Type 'Typical Toilet'.")]
        [InlineData("Wall type 'Generic - 200mm' is used by Revit Family Door")]
        [InlineData("Element is owned by you. Relinquish it when done.")]
        [InlineData("Constraints are not satisfied. The dimension is locked by Constraint.")]
        public void OrdinaryRevitTextIsNotTakenForAUser(string raw) =>
            Assert.Equal(raw, DialogRedaction.RedactPaths(raw, "nobody-here", null));

        [Theory]
        [InlineData("The workset is locked by user aminah.b", "The workset is locked by <user>")]
        [InlineData("Can't edit until Aminah releases the element", "Can't edit until <user> releases the element")]
        [InlineData("Wait until aminah.b syncs with central", "Wait until <user> syncs with central")]
        [InlineData("Can't edit until Aminah relinquishes it", "Can't edit until <user> relinquishes it")]
        [InlineData("Element is owned by a.rahman", "Element is owned by <user>")]
        [InlineData("Element is borrowed by an.nur, request it first", "Element is borrowed by <user>, request it first")]
        [InlineData("Can't edit until a.rahman resaves the element", "Can't edit until <user> resaves the element")]
        public void TheWorksharingFormsStillTakeTheUser(string raw, string expected) =>
            Assert.Equal(expected, DialogRedaction.RedactPaths(raw, "nobody-here", null));

        // r25 final pass: a Malaysian name has a connector (bin, binti, bt, b., a/l, a/p, @) or a particle (van, de, al) before its
        // last part, in any case, and Revit may print the name in lower case. The whole name goes, after "until" too.
        [Theory]
        [InlineData("Can't edit the element until Aminah Binti Ali resaves it", "Can't edit the element until <user> resaves it")]
        [InlineData("Element is owned by Ahmad Faiz bin Hassan", "Element is owned by <user>")]
        [InlineData("Element is owned by aminah binti ali.", "Element is owned by <user>.")]
        [InlineData("Element is owned by Siti bt Kassim, request it first", "Element is owned by <user>, request it first")]
        [InlineData("Element is borrowed by Ahmad b. Ali", "Element is borrowed by <user>")]
        [InlineData("Element is owned by Muthu a/l Rajan", "Element is owned by <user>")]
        [InlineData("Element is owned by Kavitha a/p ramu", "Element is owned by <user>")]
        [InlineData("Element is owned by Ali @ Abu Bakar", "Element is owned by <user>")]
        [InlineData("Element is edited by Jan van Dijk", "Element is edited by <user>")]
        [InlineData("Element is edited by Maria de Souza", "Element is edited by <user>")]
        [InlineData("Element is owned by Hassan al Hadi", "Element is owned by <user>")]
        [InlineData("Element is owned by ahmad faiz hassan, request it first", "Element is owned by <user>, request it first")]
        [InlineData("Element is borrowed by siti aminah in the central model", "Element is borrowed by <user> in the central model")]
        [InlineData("Element is owned by aminah ali until she syncs", "Element is owned by <user> until she syncs")]
        public void AMalaysianNameIsRedactedWhole(string raw, string expected) =>
            Assert.Equal(expected, DialogRedaction.RedactPaths(raw, "nobody-here", null));

        [Theory]
        [InlineData("Save until Friday.")]
        [InlineData("Wait until Revit finishes loading.")]
        [InlineData("Hidden until Level 2 is visible.")]
        [InlineData("Element is used by Group Type 'Typical Toilet'.")]
        [InlineData("Wall type is used by Revit Family Door.")]
        [InlineData("Element is owned by you.")]
        [InlineData("The dimension is locked by Constraint.")]
        [InlineData("Element is owned by another user.")]
        [InlineData("The element is owned by the central model.")]
        public void TheNameRulesLeaveOrdinaryTextAlone(string raw) =>
            Assert.Equal(raw, DialogRedaction.RedactPaths(raw, "nobody-here", null));
    }
}
