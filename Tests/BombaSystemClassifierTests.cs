using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    // Phase-2 §A.5 truth table: category-scoped name classification.
    // Pure strings — no Revit API — so this runs on macOS too.
    public class BombaSystemClassifierTests
    {
        [Fact]
        public void SprinklerCategory_AloneSuffices()
        {
            Assert.Equal("sprinkler_heads", BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatSprinklers, "M_Sprinkler Pendent", "15mm"));
        }

        [Theory]
        [InlineData(BombaSystemClassifier.CatMechanical, "Hose Reel Cabinet", "Recessed")]
        [InlineData(BombaSystemClassifier.CatPlumbing, "FP_HoseReel_Drum", "30m")]
        [InlineData(BombaSystemClassifier.CatGeneric, "HR Cabinet", "Standard")]
        public void HoseReels_AreCategorySloppy(string cat, string family, string type)
        {
            Assert.Equal("hose_reels", BombaSystemClassifier.Classify(cat, family, type));
        }

        [Theory]
        [InlineData("Fire Hydrant", "External")]
        [InlineData("Pili Bomba", "Luaran")]
        public void Hydrants_IncludingMalay(string family, string type)
        {
            Assert.Equal("hydrants", BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatPipeAccessory, family, type));
        }

        [Fact]
        public void FireAlarm_CallPoint_IsManual()
        {
            Assert.Equal("manual_call_points", BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatFireAlarm, "Break Glass Call Point", "Wall"));
        }

        [Fact]
        public void FireAlarm_Bell_IsManual()
        {
            Assert.Equal("manual_call_points", BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatFireAlarm, "Fire Alarm Bell", "6 inch"));
        }

        [Fact]
        public void FireAlarm_Unmatched_CountsAsDetector_Conservative()
        {
            // Over-counting presence can only soften "missing" into
            // "present" — never fabricate a "missing".
            Assert.Equal("detectors", BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatFireAlarm, "Mystery Device", "X"));
        }

        [Fact]
        public void FireAlarm_Annunciator_IsMonitoringPanel()
        {
            Assert.Equal("fire_monitoring_panels", BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatFireAlarm, "Fire Alarm Annunciator Panel", "Main"));
        }

        [Fact]
        public void Suppression_FM200()
        {
            Assert.Equal("other_suppression", BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatMechanical, "FM200 Cylinder Bank", "Standard"));
        }

        [Fact]
        public void PaSpeaker_InCommunicationCategory()
        {
            Assert.Equal("pa_speakers", BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatCommunication, "PA Ceiling Speaker", "6W"));
        }

        [Theory]
        [InlineData("Breeching Inlet", "dry_riser_inlets")]
        [InlineData("Wet Riser Landing Valve", "wet_riser_outlets")]
        public void Risers(string family, string expected)
        {
            Assert.Equal(expected, BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatPipeAccessory, family, "Standard"));
        }

        [Theory]
        [InlineData(BombaSystemClassifier.CatMechanical, "AHU-1", "Standard")]
        [InlineData(BombaSystemClassifier.CatPlumbing, "WC Suite", "Standard")]
        [InlineData(BombaSystemClassifier.CatGeneric, "Planter Box", "1200")]
        public void NonFireElements_ReturnNull(string cat, string family, string type)
        {
            Assert.Null(BombaSystemClassifier.Classify(cat, family, type));
        }

        [Fact]
        public void MalayDetector_Classifies()
        {
            Assert.Equal("detectors", BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatGeneric, "Pengesan Asap", "Siling"));
        }

        [Fact]
        public void HrWordBoundary_DoesNotMatchInsideWords()
        {
            // "THRESHOLD" contains "hr" but is not a hose reel.
            Assert.Null(BombaSystemClassifier.Classify(
                BombaSystemClassifier.CatGeneric, "Threshold Ramp", "Standard"));
        }
    }
}
