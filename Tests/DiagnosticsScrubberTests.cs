using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>F6 scrubbing: what leaves the machine in a diagnostics zip.</summary>
    public class DiagnosticsScrubberTests
    {
        private static readonly DiagnosticsScrubber Scrubber = new DiagnosticsScrubber(
            userName: "ali.hassan",
            userProfile: @"C:\Users\ali.hassan",
            machineName: "DESKTOP-7KQ2",
            binaRoot: @"C:\Users\ali.hassan\AppData\Local\Bina");

        [Fact]
        public void Bina_folder_paths_become_bina_placeholder_and_keep_the_rest()
        {
            Assert.Equal(@"loaded <bina>\RevitSync\versions\0.0.80\net8.0\RevitWebAppSync.dll",
                Scrubber.Scrub(@"loaded C:\Users\ali.hassan\AppData\Local\Bina\RevitSync\versions\0.0.80\net8.0\RevitWebAppSync.dll"));
        }

        [Fact]
        public void Bina_root_match_is_case_insensitive_and_accepts_forward_slashes()
        {
            Assert.Equal("<bina>/RevitSync/updater.log",
                Scrubber.Scrub("c:/users/ALI.HASSAN/appdata/local/bina/RevitSync/updater.log"));
        }

        [Fact]
        public void Model_paths_outside_bina_keep_only_the_extension()
        {
            Assert.Equal(@"sync <path>\.rvt done",
                Scrubber.Scrub(@"sync D:\Projects\Hospital Kajang\ARC\HK-ARC-L01 Central.rvt done"));
            Assert.Equal(@"open ""<path>\.dwg""",
                Scrubber.Scrub(@"open ""C:\Users\ali.hassan\Desktop\site plan v2.dwg"""));
        }

        [Fact]
        public void Unc_paths_are_scrubbed_too()
        {
            Assert.Equal(@"link <path>\.nwc",
                Scrubber.Scrub(@"link \\fileserver\projects\JKR Block A\model.nwc"));
        }

        [Fact]
        public void Two_paths_on_one_line_are_scrubbed_separately()
        {
            Assert.Equal(@"<path>\.rvt, <path>\.rfa",
                Scrubber.Scrub(@"C:\a\one.rvt, C:\b\two.rfa"));
        }

        [Fact]
        public void Stack_traces_keep_class_and_method_names()
        {
            var line = @"   at RevitWebAppSync.Services.UpdateService.CheckAsync() in C:\agent\_work\src\UpdateService.cs:line 259";
            var scrubbed = Scrubber.Scrub(line);
            Assert.Contains("at RevitWebAppSync.Services.UpdateService.CheckAsync()", scrubbed);
            Assert.Contains(@"<path>\.cs:line 259", scrubbed);
            Assert.DoesNotContain("agent", scrubbed);
        }

        [Fact]
        public void Home_folder_directories_become_home()
        {
            Assert.Equal(@"cwd <home>\Documents\Work",
                Scrubber.Scrub(@"cwd C:\Users\ali.hassan\Documents\Work"));
        }

        [Fact]
        public void User_name_machine_name_and_emails_are_replaced()
        {
            Assert.Equal("operator <user>@<machine> signed in as <email>",
                Scrubber.Scrub("operator ali.hassan@DESKTOP-7KQ2 signed in as Ali.Hassan@jkr.gov.my"));
            Assert.Equal("host <machine> user <user>",
                Scrubber.Scrub("host desktop-7kq2 user ALI.HASSAN"));
        }

        [Fact]
        public void User_name_inside_a_longer_word_is_left_alone()
        {
            var s = new DiagnosticsScrubber("ali", @"C:\Users\ali", "PC1", @"C:\Users\ali\AppData\Local\Bina");
            Assert.Equal("validation passed for <user>", s.Scrub("validation passed for ali"));
        }

        [Theory]
        [InlineData("refresh token expired")]
        [InlineData("Password rejected")]
        [InlineData("Authorization: Bearer eyJhbGciOi")]
        [InlineData("sent BEARER header")]
        [InlineData("Set-Cookie: session=abc")]
        [InlineData("access_token=xyz")]
        public void Lines_carrying_secrets_are_dropped(string secretLine)
        {
            var scrubbed = Scrubber.Scrub("before\n" + secretLine + "\nafter");
            Assert.Equal("before\nafter", scrubbed);
        }

        [Fact]
        public void Line_endings_and_versions_and_timestamps_survive()
        {
            var text = "2026-09-30 10:00:01 [updater] up to date (current 0.0.80, feed 0.0.80)\r\n" +
                       "2026-09-30 10:00:02 [loader] picked 0.0.80\r\n";
            Assert.Equal(text, Scrubber.Scrub(text));
        }

        [Fact]
        public void Empty_identity_values_do_not_scrub_everything()
        {
            var s = new DiagnosticsScrubber("", null, " ", null);
            Assert.Equal("plain line 0.0.80", s.Scrub("plain line 0.0.80"));
        }

        [Fact]
        public void Null_and_empty_input_are_returned_as_is()
        {
            Assert.Null(Scrubber.Scrub(null));
            Assert.Equal("", Scrubber.Scrub(""));
        }
    }
}
