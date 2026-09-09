using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace Tests
{
    /// <summary>
    /// CadSourceVerdict.IsRefused decides whether an AutoCAD-vertical (Civil 3D / Architecture /
    /// MEP) drawing must be refused, purely from plain data — no ACadSharp types, no DWG on disk.
    /// Root cause this guards: class NAMES alone (AECC_*/AEC_*/AECB_*) used to refuse before a
    /// single entity was read, so a plain-linework drawing that once passed through AutoCAD
    /// Architecture (CLASSES records outlive the AEC objects that created them) was refused with
    /// no layers, no geometry, even though it had readable plain linework.
    /// </summary>
    public class CadSourceVerdictTests
    {
        private static readonly (string, bool, int)[] NoAec = new (string, bool, int)[0];

        [Fact]
        public void No_aec_classes_never_refuses_and_never_warns()
        {
            Assert.False(CadSourceVerdict.IsRefused(NoAec, readableCount: 42, out string warning));
            Assert.Null(warning);
            Assert.False(CadSourceVerdict.IsRefused(null, readableCount: 0, out string warning2));
            Assert.Null(warning2);
        }

        [Fact]
        public void Aec_class_names_with_zero_instances_never_refuse_even_with_no_readable_geometry()
        {
            var classes = new (string, bool, int)[]
            {
                ("AECC_PIPE", true, 0),
                ("AEC_WALL", true, 0),
                ("AECB_DUCT", true, 0),
            };
            Assert.False(CadSourceVerdict.IsRefused(classes, readableCount: 0, out string warning));
            Assert.Null(warning);
        }

        [Fact]
        public void Aec_names_with_zero_instances_and_readable_geometry_never_refuse_or_warn()
        {
            var classes = new (string, bool, int)[] { ("AEC_WALL", true, 0) };
            Assert.False(CadSourceVerdict.IsRefused(classes, readableCount: 10, out string warning));
            Assert.Null(warning);
        }

        [Fact]
        public void Aec_entity_instances_with_readable_geometry_warns_but_does_not_refuse()
        {
            var classes = new (string, bool, int)[]
            {
                ("AECC_PIPE", true, 3),
                ("aec_wall", true, 2),   // case-insensitive, like the original CadFileReader rule
                ("SCALE", false, 100),   // not an entity class - never counted
            };
            bool refused = CadSourceVerdict.IsRefused(classes, readableCount: 25, out string warning);
            Assert.False(refused);
            Assert.NotNull(warning);
            Assert.Contains("skipped", warning);
            Assert.Contains("5", warning);   // 3 + 2
        }

        [Fact]
        public void Aec_entity_instances_with_no_readable_geometry_refuses()
        {
            var classes = new (string, bool, int)[] { ("AECB_DUCT", true, 7) };
            bool refused = CadSourceVerdict.IsRefused(classes, readableCount: 0, out string warning);
            Assert.True(refused);
            Assert.Null(warning);
        }

        [Fact]
        public void Non_entity_aec_class_records_never_count()
        {
            // IsAnEntity=false: a registered class with no entity of its own must not count
            // toward the instance total, even with a non-zero InstanceCount.
            var classes = new (string, bool, int)[] { ("AEC_WALL", false, 9) };
            Assert.False(CadSourceVerdict.IsRefused(classes, readableCount: 0, out string warning));
            Assert.Null(warning);
        }
    }
}
