using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>WP-6: the pure function that derives the new type name for a Dd.1 failure (the model never derives a name).</summary>
    public class Dd1RenameProposalTests
    {
        private static long _id = 7000;
        private static SpecType T(string category, string typeName, string family = "") =>
            new SpecType { TypeId = ++_id, Category = category, TypeName = typeName, FamilyName = family, InstanceCount = 2, InstanceIds = new List<long> { ++_id, ++_id } };

        [Fact]
        public void Proposed_name_has_single_spaces_as_the_writer_will_write_it()
        {
            Assert.Equal("Window 900 x 2100", SpecChecks.ProposeRename(T("Windows", "Door  900   x 2100")).NewName);
        }

        [Fact]
        public void A_first_word_that_names_another_category_is_swapped_for_the_English_word_of_the_category()
        {
            Assert.Equal("Window 900", SpecChecks.ProposeRename(T("Windows", "Door 900")).NewName);
            Assert.Equal("Window 900", SpecChecks.ProposeRename(T("Windows", "Pintu 900")).NewName);      // a Malay word: the new name is English
            Assert.Equal("Window 900 x 2100", SpecChecks.ProposeRename(T("Windows", "Tingkap".Replace("Tingkap", "Door") + " 900 x 2100")).NewName);
            Assert.Equal("WINDOW 900", SpecChecks.ProposeRename(T("Windows", "DOOR 900")).NewName);          // the letter case of the word is kept
            Assert.Equal("window 900", SpecChecks.ProposeRename(T("Windows", "door 900")).NewName);
            Assert.Equal("Column 400", SpecChecks.ProposeRename(T("Structural Columns", "Wall 400")).NewName);
            var p = SpecChecks.ProposeRename(T("Windows", "Door 900"));
            Assert.Contains("type name", p.Source);
            Assert.Contains("not the JKR code", p.Source);
        }

        [Fact]
        public void A_failing_JKR_code_gets_no_proposal_a_person_decides()
        {
            Assert.Null(SpecChecks.ProposeRename(T("Doors", "(TKh281a) 600 x 1800")));            // the code says Window: the family may really be a window
            Assert.Null(SpecChecks.ProposeRename(T("Windows", "(PT2p600a) Door 900")));          // a code AND a word: the code is the evidence
        }

        [Fact]
        public void Code_prefix_swap_keeps_the_letter_case_of_the_two_letters_and_is_off_for_the_button()
        {
            Assert.Equal("(CO100) x", SpecChecks.SwapCodePrefix("(PT100) x", "Columns"));
            Assert.Equal("(Co100) x", SpecChecks.SwapCodePrefix("(Pt100) x", "Columns"));         // the book writes 'Co100' for columns
            Assert.Equal("(Co100) x", SpecChecks.SwapCodePrefix("(Pt100) x", "Structural Columns"));
            Assert.Equal("(PT100a) x", SpecChecks.SwapCodePrefix("(CO100a) x", "Doors"));
            Assert.Equal("(Pt100) x", SpecChecks.SwapCodePrefix("(Co100) x", "Doors"));
            Assert.Null(SpecChecks.SwapCodePrefix("(PT100) x", "Floors"));                          // LB and LA: two prefixes
            Assert.Null(SpecChecks.SwapCodePrefix("Door 900", "Doors"));                            // no code
            // a failing code is proposed only when the caller asks for it (the button never does)
            var t = T("Windows", "(PT2p600a) 900");
            Assert.Null(SpecChecks.ProposeRename(t));
            Assert.Equal("(TK2p600a) 900", SpecChecks.ProposeRename(t, allowCode: true).NewName);
            var col = T("Columns", "(Pt100) 400");
            Assert.Equal("(Co100) 400", SpecChecks.ProposeRename(col, allowCode: true).NewName);
        }

        [Fact]
        public void A_family_that_contradicts_the_category_blocks_the_proposal()
        {
            Assert.Null(SpecChecks.ProposeRename(T("Windows", "Door 900", "Wall-Basic")));        // family says Walls: both disagree, a real model error
            Assert.Null(SpecChecks.ProposeRename(T("Windows", "Door 900", "Window-Double")));                           // family agrees: Dd.1 passes, nothing to rename
            Assert.Equal("Window 900", SpecChecks.ProposeRename(T("Windows", "Door 900", "jkrAR_xyz")).NewName);        // family says nothing
            Assert.Null(SpecChecks.ProposeRename(T("Windows", "900", "Door-Single")));            // the FAMILY is the evidence: not the name
        }

        [Fact]
        public void Nothing_is_derivable_for_a_category_without_one_English_word_or_a_name_that_is_already_right()
        {
            Assert.Null(SpecChecks.ProposeRename(T("Generic Models", "Floor")));                  // no category word
            Assert.Null(SpecChecks.ProposeRename(T("Casework", "Door Closer")));                  // accessory name: not judged
            Assert.Null(SpecChecks.ProposeRename(T("Windows", "Window 900")));                    // already correct
            Assert.Null(SpecChecks.ProposeRename(T("Windows", "Opening 900")));                   // says nothing
            Assert.Null(SpecChecks.ProposeRename(null));
        }

        [Fact]
        public void Names_that_clash_in_a_batch_or_with_an_existing_type_are_dropped()
        {
            var a = T("Windows", "Door 900", "F1");
            var b = T("Windows", "Pintu 900", "F1");                    // also becomes 'Window 900' in the same family
            var c = T("Windows", "Door 1200", "F1");
            var d = T("Windows", "Door 1500", "F2");
            var e = T("Windows", "Door 600", "F1");
            var existing = T("Windows", "window 600", "F1");            // Revit type names are unique per family, case-insensitive
            var otherFamily = T("Windows", "Window 900", "F2");
            var m = new SpecModel(); m.Types.AddRange(new[] { a, b, c, d, e, existing, otherFamily });
            var r = SpecChecks.ProposeRenames(m, new[] { a, b, c, d, e });
            Assert.False(r.ContainsKey(a)); Assert.False(r.ContainsKey(b));       // clash inside the batch: neither gets a name
            Assert.Equal("Window 1200", r[c].NewName);
            Assert.False(r.ContainsKey(e));                                        // 'Window 600' exists in the family
            Assert.Equal("Window 1500", r[d].NewName);                             // F2 has no clash
        }

        [Fact]
        public void The_evaluator_puts_the_proposal_on_the_failure_row_and_none_where_a_person_decides()
        {
            var name = T("Windows", "Door 900");
            var code = T("Doors", "(TKh281a) 600 x 1800");
            var m = new SpecModel(); m.Types.AddRange(new[] { name, code });
            var fails = SpecChecks.Rules["Dd.1"](m.Normalize()).Where(x => x.Status == "fail").ToList();
            Assert.Equal(2, fails.Count);
            var n = fails.Single(f => f.RenameTo.Length > 0);
            Assert.Equal("Door 900", n.RenameFrom);
            Assert.Equal("Window 900", n.RenameTo);
            Assert.Contains("type name", n.RenameSource);
            var c = fails.Single(f => f.RenameTo.Length == 0);
            Assert.Equal("", c.RenameFrom);
            Assert.Equal("", c.RenameSource);
        }
    }
}
