// Guarded Save As planner — Revit-free (bina-ai R2 Task 24).
//
// Everything that can be decided without Revit is decided here: the target
// path, name validation, every refusal (workshared in the first release,
// existing target without explicit overwrite, missing directory, same path
// as the current document, invalid name) and the confirm token bound to the
// exact destination that the apply must echo.

using System.Collections.Generic;
using BinaVibe.Saving;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SaveAsPlanTests
    {
        private static SaveAsFacts Facts(bool workshared = false, string current = @"C:\Models\A.rvt", bool targetExists = false, bool dirExists = true) =>
            new() { CurrentPath = current, Title = "A", IsModified = true, IsWorkshared = workshared,
                    TargetExists = targetExists, DirectoryExists = dirExists, Writable = true };

        [Fact]
        public void Plan_BuildsTargetPath_AndTokenBoundToIt()
        {
            var plan = SaveAsPlan.Build(Facts(), directory: @"C:\Users\x\Desktop", fileName: "B", overwrite: false);
            Assert.Equal(@"C:\Users\x\Desktop\B.rvt", plan.TargetPath);     // .rvt appended
            Assert.True(plan.WouldSave);
            Assert.Empty(plan.Refusals);
            Assert.Equal(12, plan.ConfirmToken.Length);
            Assert.Equal(plan.ConfirmToken, SaveAsPlan.TokenFor(@"C:\Users\x\Desktop\B.rvt"));
            Assert.NotEqual(plan.ConfirmToken, SaveAsPlan.TokenFor(@"C:\Users\x\Desktop\C.rvt"));
        }

        [Fact]
        public void Refuses_WorksharedInFirstRelease()
        {
            var plan = SaveAsPlan.Build(Facts(workshared: true), @"C:\out", "B.rvt", false);
            Assert.False(plan.WouldSave);
            Assert.Contains(plan.Refusals, r => r.Code == "workshared_not_supported");
        }

        [Fact]
        public void Refuses_ExistingTargetWithoutOverwrite_AllowsWithExplicitOverwrite()
        {
            Assert.Contains(SaveAsPlan.Build(Facts(targetExists: true), @"C:\out", "B.rvt", false).Refusals, r => r.Code == "target_exists");
            var ok = SaveAsPlan.Build(Facts(targetExists: true), @"C:\out", "B.rvt", overwrite: true);
            Assert.True(ok.WouldSave);
            Assert.True(ok.Overwrites);
        }

        [Fact]
        public void Refuses_SamePathAsCurrent_MissingDirectory_InvalidName()
        {
            Assert.Contains(SaveAsPlan.Build(Facts(current: @"C:\out\B.rvt"), @"C:\out", "B.rvt", false).Refusals, r => r.Code == "same_as_current");
            Assert.Contains(SaveAsPlan.Build(Facts(dirExists: false), @"C:\nope", "B.rvt", false).Refusals, r => r.Code == "directory_missing");
            Assert.Contains(SaveAsPlan.Build(Facts(), @"C:\out", "bad:name?.rvt", false).Refusals, r => r.Code == "invalid_name");
            Assert.Contains(SaveAsPlan.Build(Facts(), @"C:\out", @"..\B.rvt", false).Refusals, r => r.Code == "invalid_name");
        }

        [Fact]
        public void Preview_Shape_CarriesCurrentTargetRefusalsAndToken()
        {
            var p = SaveAsPlan.Build(Facts(targetExists: true), @"C:\out", "B.rvt", false).ToPreview();
            Assert.Equal(false, p["would_save"]);
            Assert.Equal(@"C:\out\B.rvt", ((Dictionary<string, object?>)p["target"]!)["path"]);
            Assert.Equal(true, ((Dictionary<string, object?>)p["target"]!)["exists"]);
            Assert.Equal(false, ((Dictionary<string, object?>)p["current"]!)["is_workshared"]);
            Assert.NotEmpty((List<object>)p["refusals"]!);
            Assert.IsType<string>(p["confirm_token"]);
        }
    }
}
