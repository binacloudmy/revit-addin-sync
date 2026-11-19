using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    public class DisciplineTypesTests
    {
        // JKR naming puts the discipline right after "jkr" (14zefhy56zu).
        [Theory]
        [InlineData("jkrST27_2a_(BEpj00_p19-211441)_A-PTBR_w01_(K).rvt", "Structure")]
        [InlineData("jkrAR20_2a_(BEpj00_p19-211441)_A-PTBR_w01_(K)_220602.rvt", "Architecture")]
        [InlineData("jkrME22_2b_ACMV_(BEsm_19-001)_A-PTBR_w01_(S)_230706.rvt", "Mechanical")]
        [InlineData("jkrEL27_2b_(BEsm_19-001)_A-PTBR_w01_(S)_230706.rvt", "Electrical")]
        [InlineData("jkrCV27_2a_(BE_p19-211441)_F0_wr00_W-01_(S).rvt", "Civil")]
        public void FromFileName_ReadsJkrDisciplineCode(string name, string expected) =>
            Assert.Equal(expected, DisciplineTypes.FromFileName(name));
    }
}
