// look tool (G-03): the add-in's per-turn view-image stash.
//
// Privacy contract on the add-in side: the exported PNG lives in memory for
// one turn only. The router clears the stash when a turn starts and TAKES it
// (read + clear) when the turn ends; the handler deletes the temp file at once.
// The stash is static, so these tests share it: [Collection] keeps them serial.

using System;
using System.IO;
using System.Linq;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    [Collection("LookImageStash")]
    public class LookImageStashTests
    {
        // A minimal PNG header: signature + IHDR for 640 x 400.
        private static byte[] Png(int w = 640, int h = 400)
        {
            var b = new byte[64];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
            b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
            b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
            return b;
        }

        [Fact]
        public void TakeAll_ReturnsTheTurnsImagesAndClears()
        {
            LookImageStash.Clear();
            var stored = LookImageStash.Add("PTBR_Aras Tanah", Png());
            Assert.NotNull(stored);
            Assert.Equal(640, stored.Width);
            Assert.Equal(400, stored.Height);
            Assert.Equal(Convert.ToBase64String(Png()), stored.Base64);

            var taken = LookImageStash.TakeAll();
            Assert.Single(taken);
            Assert.Equal("PTBR_Aras Tanah", taken[0].ViewName);
            // End of turn: nothing is left behind for the next turn.
            Assert.Equal(0, LookImageStash.Count);
            Assert.Null(LookImageStash.TakeAll());
        }

        [Fact]
        public void Clear_DropsLeftoversFromAnAbortedTurn()
        {
            LookImageStash.Clear();
            LookImageStash.Add("A", Png());
            LookImageStash.Clear();
            Assert.Equal(0, LookImageStash.Count);
            Assert.Null(LookImageStash.TakeAll());
        }

        [Fact]
        public void Add_CapsImagesPerTurnAndIgnoresEmpty()
        {
            LookImageStash.Clear();
            Assert.Null(LookImageStash.Add("empty", Array.Empty<byte>()));
            Assert.Null(LookImageStash.Add("null", null));
            for (int i = 0; i < LookImageStash.MaxPerTurn; i++)
                Assert.NotNull(LookImageStash.Add("v" + i, Png()));
            Assert.Null(LookImageStash.Add("one too many", Png()));
            Assert.Equal(LookImageStash.MaxPerTurn, LookImageStash.TakeAll().Count);
        }

        [Fact]
        public void TryPngSize_RejectsNonPng()
        {
            Assert.True(LookImageStash.TryPngSize(Png(1280, 720), out var w, out var h));
            Assert.Equal((1280, 720), (w, h));
            Assert.False(LookImageStash.TryPngSize(new byte[] { 0x47, 0x49, 0x46, 0x38 }, out _, out _));
            Assert.False(LookImageStash.TryPngSize(null, out _, out _));
        }

        // ─── source pins: the wiring the stash depends on ────────────────────

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "RevitWebAppSync.csproj")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray()));
        }

        [Fact]
        public void Router_ClearsAtTurnStart_AndTakesAtTurnEnd()
        {
            var router = Repo("UI", "Copilot", "RevitChatRouter.cs");
            Assert.Contains("LookImageStash.Clear()", router);
            Assert.Contains("LookImages = RevitWebAppSync.Services.LookImageStash.TakeAll()", router);
        }

        [Fact]
        public void LookTool_IsDispatched_DeletesItsFile_AndNeverPersistsToHistory()
        {
            Assert.Contains("\"look\"", Repo("BinaVibe", "Mcp", "Tools", "ToolRegistry.cs"));
            Assert.Contains("look", BinaVibe.Mcp.Tools.InstalledToolManifest.Names);
            var tool = Repo("BinaVibe", "Mcp", "Tools", "LookTool.cs");
            Assert.Contains("finally { TryDelete(path); }", tool);
            // The persisted history type must never carry the image.
            var models = Repo("UI", "Copilot", "Model", "CopilotModels.cs");
            var history = models.Substring(models.IndexOf("public class HistoryEntry", StringComparison.Ordinal));
            history = history.Substring(0, history.IndexOf("public class Mention", StringComparison.Ordinal));
            Assert.DoesNotContain("LookImage", history);
        }
    }
}
