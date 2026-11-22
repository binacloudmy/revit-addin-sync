using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using K = RevitWebAppSync.Services.JkrSpec.SpecAKit;

namespace RevitWebAppSync.Services.JkrSpec
{
    // Spec checks for Borang section A (Model integrity and quality): A1 Need Statement
    // minimums, A2 file name, A3 model split, A4 base point, A5 grids/levels, A6 rooms,
    // A7 materials. See SpecEval.cs for the contract and model-audit-spec.md §A for the
    // decisions behind each rule. Pure: no Revit, no WPF.
    public static partial class SpecChecks
    {
        static partial void RegisterA(Dictionary<string, SpecRule> rules)
        {
            rules["A1.1"] = A11CeilingHeight;
            rules["A1.2"] = A12DoorSize;
            rules["A1.3"] = A13MainDoor;
            rules["A1.4"] = A14FlatRoof;
            rules["A1.5"] = A15Fence;
            rules["A1.6"] = A16OtherNsClauses;
            rules["A1.7"] = A17Scope;
            rules["A2.1"] = A21FileName;
            rules["A2.2"] = A22ModelCode;
            rules["A3.1"] = A31Single;
            rules["A3.2"] = A32PartsLinked;
            rules["A3.3"] = A33Bpep;
            rules["A3.4"] = A34NoLinks;
            rules["A3.5"] = A35HowSplit;
            rules["A4.1"] = A41BasePoint;
            rules["A5.1"] = A51Counts;
            rules["A5.2"] = A52GridSequence;
            rules["A5.3"] = A53SubGrids;
            rules["A6.1"] = A61RoomsPerLevel;
            rules["A6.2"] = A62SlabHasRoom;
            rules["A6.3"] = A63Department;
            rules["A7.1"] = A71Materials;
            rules["A7.2"] = A72MaterialNaming;
            rules["A7.3"] = A73MaterialChoice;
        }

        static partial void BackendA(Dictionary<string, string> reasons)
        {
            reasons["A3.3"] = "Needs the BPEP file list, which only the backend has (the uploaded BPEP); the model alone cannot say which part files the BPEP names.";
        }

        // ════════════════════════════════════════════════════════════════════
        // A1 — Need Statement minimums (NS 2025). Only what the NS writes; design
        // concept is never judged (principle 0.3).
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A1.1 (NS §4.1.9): every level's floor-to-ceiling ≥ 3000 mm; a basement level
        /// ≥ 2500 mm; not complying = "any level &lt; 3000 mm — list the level + the actual
        /// value". Each ceiling (Ceilings category only — roof soffits are not ceilings) is
        /// measured from what is physically under it:
        /// • Floor under it: sample points inside the ceiling's underside loops; at each, the
        ///   highest floor top below the underside whose top-face loops hold the point. The
        ///   clear height is underside − that floor top, over the highest floor that holds at
        ///   least <see cref="A11MinFloorShare"/> of the points that found a floor (a sliver
        ///   under a wall does not decide it). Small pads (&lt; 1 m²) are not floors. The
        ///   ceiling then counts for the floor's storey (a non-story floor level → the story
        ///   level nearest the floor top within <see cref="A11StorySlackMm"/>), and names the room
        ///   holding most of those points (real Dewan model, 17 Nov 2026: 16 ceilings 3450 mm
        ///   above Aras 01 hang over changing rooms and toilets whose floors are 800–1200 mm
        ///   higher, so the clear height is 2250–2650 mm).
        /// • No footprint, or no floor under it: its height above its level (HeightOffset),
        ///   a ceiling hosted on a non-story level measured from the story level below it.
        /// Per level, the lowest value below the minimum fails the level, listing each room /
        /// ceiling below it with its value. A basement is a level below 0 or named basement /
        /// bawah tanah / B1. No ceilings at all → cannot be judged; a level with placed rooms
        /// but no ceiling → a person checks it (never a silent pass). Beams (NS: "inclusive
        /// the underside of beam") are not in the snapshot.
        /// </summary>
        private static IEnumerable<SpecResult> A11CeilingHeight(SpecModel m)
        {
            const string id = "A1.1";
            var list = new List<SpecResult>();
            if (m.Ceilings.Count == 0)
            {
                list.Add(Unjudged(id, "Tiada siling dimodelkan — ketinggian siling tidak dapat disemak", key: "no-ceilings")
                    .En("No ceilings modelled — ceiling height cannot be checked"));
                return list;
            }
            var levels = K.LevelMap(m);
            var stories = m.Levels.Where(l => l.IsStory).OrderBy(l => l.Elevation).ToList();
            // A ceiling hosted on a non-story level (e.g. "PTBR_Beam Aras 01") is measured
            // from the story level below it — the room's floor — not from its own level
            // (real model, 16 Nov 2026: 2550 shown where the floor-to-ceiling is 2500).
            (string Level, double Height) ByLevel(SpecCeiling c)
            {
                var own = K.LevelOf(levels, c.LevelName);
                if (own == null || own.IsStory || c.HeightOffset <= 0) return (c.LevelName.Trim(), c.HeightOffset);
                var top = own.Elevation + c.HeightOffset;
                var below = stories.LastOrDefault(l => l.Elevation <= top - 1);
                return below == null ? (c.LevelName.Trim(), c.HeightOffset) : (below.Name.Trim(), top - below.Elevation);
            }
            var floors = m.Floors.Where(f => f.TopElevation != null && f.Loops.Count > 0 && !A11IsPad(f))
                              .Select(f => new SpecPlan.Shape<SpecFloor>(f, f.Loops)).ToList();
            var rooms = m.Rooms.Where(r => r.Area > 0 && r.Loops.Count > 0)
                             .Select(r => new SpecPlan.Shape<SpecRoom>(r, r.Loops)).ToList();
            string StoryOf(SpecFloor f)
            {
                var own = K.LevelOf(levels, f.LevelName);
                if (own != null && own.IsStory) return own.Name.Trim();
                var top = f.TopElevation ?? double.NaN;
                var near = stories.Where(l => Math.Abs(l.Elevation - top) <= A11StorySlackMm)
                                  .OrderBy(l => Math.Abs(l.Elevation - top)).FirstOrDefault();
                return near != null ? near.Name.Trim() : f.LevelName.Trim();
            }
            A11Measure Measure(SpecCeiling c)
            {
                var lv = ByLevel(c);
                var fallback = new A11Measure { Ceiling = c, Level = lv.Level, Height = lv.Height };
                var bottom = c.BottomElevation;
                if (bottom == null)
                {
                    var own = K.LevelOf(levels, c.LevelName);
                    if (own != null && c.HeightOffset > 0) bottom = own.Elevation + c.HeightOffset;
                }
                if (bottom == null || c.Loops.Count == 0 || floors.Count == 0) return fallback;
                var b = bottom.Value;
                var hits = new List<(SpecPoint P, SpecFloor F)>();
                var cb = new SpecPlan.Shape<SpecCeiling>(c, c.Loops);
                var under = floors.Where(s => s.Item.TopElevation.Value <= b - 1 && s.Overlaps(cb)).ToList();
                if (under.Count == 0) return fallback;
                foreach (var p in SpecPlan.Samples(c.Loops, A11SamplesPerCeiling))
                {
                    SpecFloor best = null;
                    foreach (var s in under)
                    {
                        var top = s.Item.TopElevation.Value;
                        if (top > b - 1 || (best != null && top <= best.TopElevation.Value)) continue;
                        if (s.Contains(p.X, p.Y)) best = s.Item;
                    }
                    if (best != null) hits.Add((p, best));
                }
                if (hits.Count == 0) return fallback;
                var need = Math.Max(1, (int)Math.Ceiling(hits.Count * A11MinFloorShare));
                var floor = hits.GroupBy(h => h.F).Where(g => g.Count() >= need)
                                .Select(g => g.Key).OrderByDescending(f => f.TopElevation.Value).FirstOrDefault()
                            ?? hits.GroupBy(h => h.F).OrderByDescending(g => g.Count()).First().Key;
                var over = hits.Where(h => h.F == floor).Select(h => h.P).ToList();
                var ft = floor.TopElevation.Value;
                // The room over that floor: its volume starts below the ceiling and near the floor.
                var room = rooms.Where(r => { var rb = RoomBase(r.Item); return rb == null || (rb.Value < b - 1 && rb.Value >= ft - A11StorySlackMm); })
                                .Select(r => (R: r.Item, N: over.Count(p => r.Contains(p.X, p.Y))))
                                .Where(x => x.N > 0).OrderByDescending(x => x.N).Select(x => x.R).FirstOrDefault();
                var level = StoryOf(floor);
                return new A11Measure
                {
                    Ceiling = c, Level = level.Length > 0 ? level : lv.Level, Height = SpecUnits.CleanMm(b - ft),
                    FromFloor = true, FloorId = floor.Id, Room = room,
                };
            }
            double? RoomBase(SpecRoom r) => r.BaseElevation ?? (K.LevelOf(levels, r.LevelName)?.Elevation);

            var measured = m.Ceilings.Select(Measure).ToList();
            var passed = 0;
            var withCeiling = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in m.Ceilings) withCeiling.Add(c.LevelName.Trim());
            foreach (var g in measured.GroupBy(x => x.Level, StringComparer.OrdinalIgnoreCase)
                                      .OrderBy(g => K.LevelElevation(levels, g.Key)))
            {
                withCeiling.Add(g.Key);
                var name = K.LevelLabel(g.Key);
                var basement = SpecA.IsBasement(g.Key, K.LevelOf(levels, g.Key));
                double req = basement ? 2500 : 3000;
                var tag = basement ? " (basement)" : "";
                var valid = g.Where(x => x.Height > 0).ToList();
                if (valid.Count == 0)
                {
                    list.Add(Unjudged(id, $"{Aras(name)}: ketinggian siling tidak dibaca", g.First().Ceiling.Id, Aras(name), key: "level-unread:" + g.Key)
                        .En($"Level {name}: ceiling height not read"));
                    continue;
                }
                // Lowest first, so the element shown and located is the one with the reported height.
                // The documented minimum, compared exactly. A value that only reads as the minimum
                // when rounded is listed for a person, never failed (no JKR rounding rule, Q1).
                var low = valid.Where(x => K.FitMin(x.Height, req) == MinFit.Below).OrderBy(x => x.Height).ToList();
                var edge = valid.Where(x => K.FitMin(x.Height, req) == MinFit.PrintsAsMin).OrderBy(x => x.Height).ToList();
                if (low.Count == 0 && edge.Count == 0) { passed++; continue; }
                if (low.Count > 0)
                {
                    var min = low[0].Height;
                    var (spaces, spacesEn) = A11Spaces(low);
                    list.Add(Fail(id, $"{Aras(name)}{tag}: siling {K.Mm(min)} mm < {K.Mm(req)} mm{spaces}", elementName: Aras(name),
                                  actual: K.Mm(min) + " mm", required: "≥ " + K.Mm(req) + " mm", key: "level:" + g.Key,
                                  elementIds: low.Select(x => x.Ceiling.Id))
                        .En($"Level {name}{tag}: ceiling {K.Mm(min)} mm < {K.Mm(req)} mm{spacesEn}"));
                }
                if (edge.Count > 0)
                {
                    var low1 = edge[0].Height.ToString("0.###", K.Inv);
                    list.Add(Context(id, $"{Aras(name)}{tag}: siling {low1} mm — di bawah {K.Mm(req)} mm tetapi dipaparkan sebagai {K.Mm(req)} mm selepas pembundaran; JKR tiada peraturan pembundaran — manusia putuskan",
                                     edge[0].Ceiling.Id, Aras(name), key: "edge:" + g.Key, elementIds: edge.Select(x => x.Ceiling.Id))
                        .En($"Level {name}{tag}: ceiling {low1} mm — below {K.Mm(req)} mm but reads as {K.Mm(req)} mm when rounded; JKR gives no rounding rule — a person decides"));
                }
            }
            foreach (var lv in m.Rooms.Where(r => r.Area > 0).Select(r => r.LevelName.Trim()).Where(n => n.Length > 0)
                                       .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => K.LevelElevation(levels, n)))
            {
                if (withCeiling.Contains(lv)) continue;
                list.Add(Unjudged(id, $"{Aras(lv)}: ada room tetapi tiada siling dimodelkan — ketinggian tidak dapat disemak", key: "level-noceiling:" + lv)
                    .En($"Level {lv}: has rooms but no ceiling modelled — height cannot be checked"));
            }
            if (passed > 0)
                list.Add(Pass(id, $"{passed} aras: siling ≥ 3000 mm (basement ≥ 2500 mm)", passed)
                    .En($"{passed} levels: ceiling ≥ 3000 mm (basement ≥ 2500 mm)"));
            return list;
        }

        /// <summary>A1.1: sample points per ceiling underside.</summary>
        internal const int A11SamplesPerCeiling = 36;
        /// <summary>A1.1: share of a ceiling's points over floors that a floor needs to decide
        /// its clear height (a strip under a wall or a kerb at the edge does not).</summary>
        internal const double A11MinFloorShare = 0.10;
        /// <summary>A1.1 geometry reading, NOT a JKR number: a floor under this area (m²) is a pad
        /// (a plinth, a column base), not the floor a ceiling hangs over.</summary>
        internal const double A11PadM2 = 1.0;
        /// <summary>A1.1 geometry reading, NOT a JKR number: how far (mm) a floor top may stand from a
        /// story level and still count for that storey (a beam level beside the story level).</summary>
        internal const double A11StorySlackMm = 1500;
        /// <summary>A1.1: a floor under <see cref="A11PadM2"/> (its computed area, else the area of its
        /// loops, else of its plan box); a floor with no size read is not a pad.</summary>
        internal static bool A11IsPad(SpecFloor f)
        {
            double a = f.Area;
            if (!(a > 0) && f.Loops.Count > 0) a = SpecPlan.Area(f.Loops) / 1e6;
            if (!(a > 0) && f.MaxX > f.MinX && f.MaxY > f.MinY) a = (f.MaxX - f.MinX) * (f.MaxY - f.MinY) / 1e6;
            return a > 0 && a < A11PadM2;
        }
        /// <summary>A1.1: most rooms / ceilings listed in one level's fail.</summary>
        private const int A11MaxSpacesListed = 8;

        private sealed class A11Measure
        {
            public SpecCeiling Ceiling;
            /// <summary>The level the height counts for (group key).</summary>
            public string Level = "";
            public double Height;
            /// <summary>Measured from the floor under it (else from its level).</summary>
            public bool FromFloor;
            public long FloorId;
            public SpecRoom Room;
        }

        /// <summary>" — UM07 Tandas Perempuan 2250 mm, siling 927649 2550 mm": every room (or
        /// ceiling with no room) below the minimum, lowest first, once each. Empty when no
        /// ceiling was measured from a floor and none has a room (the old level-only reading).</summary>
        private static (string Bm, string En) A11Spaces(List<A11Measure> low)
        {
            if (!low.Any(x => x.FromFloor || x.Room != null)) return ("", "");
            string Key(A11Measure x) => x.Room != null ? "room:" + x.Room.Id : "clg:" + x.Ceiling.Id;
            var items = low.GroupBy(Key).Select(g => g.OrderBy(x => x.Height).First()).OrderBy(x => x.Height).ToList();
            string RoomName(SpecRoom r) => (r.Number.Trim() + " " + r.Name.Trim()).Trim();
            var bm = items.Take(A11MaxSpacesListed).Select(x => (x.Room != null ? RoomName(x.Room) : "siling " + x.Ceiling.Id) + " " + K.Mm(x.Height) + " mm");
            var en = items.Take(A11MaxSpacesListed).Select(x => (x.Room != null ? RoomName(x.Room) : "ceiling " + x.Ceiling.Id) + " " + K.Mm(x.Height) + " mm");
            var more = items.Count > A11MaxSpacesListed ? $", … +{items.Count - A11MaxSpacesListed}" : "";
            return (" — " + string.Join(", ", bm) + more, " — " + string.Join(", ", en) + more);
        }

        /// <summary>"Aras 02" for level "02", "Aras 01" for level "Aras 01" (not "Aras Aras 01").</summary>
        private static string Aras(string levelName)
        {
            var n = (levelName ?? "").Trim();
            return n.StartsWith("Aras ", StringComparison.OrdinalIgnoreCase) ? n : "Aras " + n;
        }

        /// <summary>
        /// A1.2 (NS §5.8.5): clear opening ≥ 900 mm wide (single leaf), ≥ 1500 mm (double
        /// leaf), ≥ 2100 mm high. Per placed door type, from its Width / Height. FailOnly: the
        /// NS minimum is the clear opening, the snapshot has the nominal size — a nominal
        /// size below the minimum proves the clear opening is, a nominal size above it does
        /// not prove the clear opening is not. AI-504: no JKR document says how to turn a
        /// nominal size into a clear opening, or how to tell a double leaf (the name is not a
        /// source). So the automatic part is the minimum every door shares: width &lt; 900 or
        /// height &lt; 2100 fails, compared exactly. A door whose name reads as double leaf and
        /// is 900–1499 mm wide is listed for a person (1500 mm applies if it is two leaves);
        /// a value that only reads as the minimum when rounded is listed too, never failed.
        /// </summary>
        private static IEnumerable<SpecResult> A12DoorSize(SpecModel m)
        {
            const string id = "A1.2";
            var list = new List<SpecResult>();
            var doors = m.Types.Where(t => K.IsCat(t, "Doors")).ToList();
            if (doors.Count == 0)
            {
                list.Add(Unjudged(id, "Tiada pintu dalam model — saiz pintu tidak dapat disemak", key: "no-doors")
                    .En("No doors in the model — door sizes cannot be checked"));
                return list;
            }
            var passed = 0;
            foreach (var t in doors)
            {
                var label = K.TypeLabel(t);
                var hasW = K.TryNum(t, "Width", out var w) && w > 0;
                var hasH = K.TryNum(t, "Height", out var h) && h > 0;
                if (!hasW || !hasH)
                {
                    list.Add(Unjudged(id, $"{label} | lebar {(hasW ? K.Mm(w) : "?")} | tinggi {(hasH ? K.Mm(h) : "?")}: nilai tidak dibaca",
                                      K.FirstId(t), K.ElementLabel(t), key: K.TypeKey(t))
                        .En($"{label} | width {(hasW ? K.Mm(w) : "?")} | height {(hasH ? K.Mm(h) : "?")}: value not read"));
                    continue;
                }
                var ms = new List<string>();
                var en = new List<string>();
                var wFit = K.FitMin(w, 900);
                var hFit = K.FitMin(h, 2100);
                if (wFit == MinFit.Below) { ms.Add("lebar < 900 mm"); en.Add("width < 900 mm"); }
                if (hFit == MinFit.Below) { ms.Add("tinggi < 2100 mm"); en.Add("height < 2100 mm"); }
                if (ms.Count > 0)
                {
                    list.Add(Fail(id, $"{label} | {K.Mm(w)} | {K.Mm(h)} mm: {string.Join(", ", ms)}", elementName: K.ElementLabel(t),
                                  actual: $"{K.Mm(w)} × {K.Mm(h)} mm", required: "≥ 900 × 2100 mm", key: K.TypeKey(t),
                                  coverage: SpecCoverage.FailOnly, elementIds: t.InstanceIds)
                        .En($"{label} | {K.Mm(w)} | {K.Mm(h)} mm: {string.Join(", ", en)}"));
                    continue;
                }
                var person = new List<string>();
                var personEn = new List<string>();
                if (wFit == MinFit.PrintsAsMin || hFit == MinFit.PrintsAsMin)
                {
                    person.Add("saiz hanya mencapai minimum selepas pembundaran (JKR tiada peraturan pembundaran)");
                    personEn.Add("the size only reaches the minimum when rounded (JKR gives no rounding rule)");
                }
                if (SpecA.IsDoubleLeaf(t.FamilyName, t.TypeName) && w < 1500)
                {
                    person.Add("nama menunjukkan pintu dua daun — jika dua daun, minimum 1500 mm terpakai; sahkan bilangan daun dan bukaan bersih");
                    personEn.Add("the name reads as a double-leaf door — if it is two leaves the 1500 mm minimum applies; confirm the leaves and the clear opening");
                }
                if (person.Count == 0) { passed++; continue; }
                list.Add(Context(id, $"{label} | {K.Mm(w)} | {K.Mm(h)} mm: {string.Join("; ", person)} — manusia putuskan",
                                 K.FirstId(t), K.ElementLabel(t), key: "person:" + K.TypeKey(t), elementIds: t.InstanceIds)
                    .En($"{label} | {K.Mm(w)} | {K.Mm(h)} mm: {string.Join("; ", personEn)} — a person decides"));
            }
            if (passed > 0)
                list.Add(Pass(id, $"{passed} jenis pintu ≥ minimum (saiz nominal; bukaan bersih tidak dibaca)", passed, SpecCoverage.FailOnly)
                    .En($"{passed} door types ≥ minimum (nominal size; clear opening not read)"));
            return list;
        }

        /// <summary>
        /// A1.3 (NS §5.8.3, §5.8.1): main-entrance doors solid timber / solid core, ≥ 40 mm
        /// thick, with the thickness parameter filled. A main door is a door type whose
        /// name or text parameters say utama / main / entrance / masuk
        /// (<see cref="SpecA.IsMainDoor"/>). None found → the spec's MANUAL path: the
        /// exterior doors (Function = Exterior; else every door type) are listed as
        /// candidates for a person. Thickness missing or &lt; 40 → fail; "hollow" /
        /// "honeycomb" in the name → fail (not solid). ≥ 40 and "solid" in the name or
        /// material → pass; ≥ 40 with no word on the core → partly checked (FailOnly).
        /// </summary>
        private static IEnumerable<SpecResult> A13MainDoor(SpecModel m)
        {
            const string id = "A1.3";
            var list = new List<SpecResult>();
            var doors = m.Types.Where(t => K.IsCat(t, "Doors")).ToList();
            if (doors.Count == 0)
            {
                list.Add(Unjudged(id, "Tiada pintu dalam model — pintu utama tidak dapat disemak", key: "no-doors")
                    .En("No doors in the model — the main door cannot be checked"));
                return list;
            }
            var mains = doors.Where(SpecA.IsMainDoor).ToList();
            if (mains.Count == 0)
            {
                var cands = doors.Where(t => string.Equals(K.Text(t, "Function").Trim(), "Exterior", StringComparison.OrdinalIgnoreCase)).ToList();
                if (cands.Count == 0) cands = doors;
                foreach (var t in cands)
                {
                    var thk = K.TryNum(t, "Thickness", out var v) && v > 0 ? K.Mm(v) + " mm" : "?";
                    list.Add(Unjudged(id, $"Pintu utama tidak dapat dikenal pasti — calon: {K.TypeLabel(t)} (tebal {thk}); manusia sahkan",
                                      K.FirstId(t), K.ElementLabel(t), key: "cand:" + K.TypeKey(t))
                        .En($"Main door not identifiable — candidate: {K.TypeLabel(t)} (thickness {thk}); a person confirms"));
                }
                return list;
            }
            int solidPass = 0, thickPass = 0;
            foreach (var t in mains)
            {
                var label = K.TypeLabel(t);
                var words = K.TypeWords(t);
                if (!K.TryNum(t, "Thickness", out var thk) || thk <= 0)
                {
                    list.Add(Fail(id, $"Pintu utama {label}: parameter Thickness tiada / kosong", elementName: K.ElementLabel(t),
                                  required: "≥ 40 mm", key: K.TypeKey(t), elementIds: t.InstanceIds)
                        .En($"Main door {label}: Thickness parameter missing / empty"));
                    continue;
                }
                var thkFit = K.FitMin(thk, 40, 1);
                if (thkFit == MinFit.PrintsAsMin)
                {
                    list.Add(Context(id, $"Pintu utama {label}: tebal {thk.ToString("0.###", K.Inv)} mm — di bawah 40 mm tetapi dipaparkan sebagai 40 mm selepas pembundaran; JKR tiada peraturan pembundaran — manusia putuskan",
                                     K.FirstId(t), K.ElementLabel(t), key: "edge:" + K.TypeKey(t), elementIds: t.InstanceIds)
                        .En($"Main door {label}: thickness {thk.ToString("0.###", K.Inv)} mm — below 40 mm but reads as 40 mm when rounded; JKR gives no rounding rule — a person decides"));
                    continue;
                }
                if (thkFit == MinFit.Below)
                {
                    list.Add(Fail(id, $"Pintu utama {label}: tebal {K.Mm1(thk)} mm < 40 mm", elementName: K.ElementLabel(t),
                                  actual: K.Mm1(thk) + " mm", required: "≥ 40 mm", key: K.TypeKey(t), elementIds: t.InstanceIds)
                        .En($"Main door {label}: thickness {K.Mm1(thk)} mm < 40 mm"));
                    continue;
                }
                if (Regex.IsMatch(words, @"hollow|honeycomb|berongga", RegexOptions.IgnoreCase))
                {
                    list.Add(Fail(id, $"Pintu utama {label}: hollow core, bukan solid core", elementName: K.ElementLabel(t),
                                  actual: "hollow core", required: "solid core", key: K.TypeKey(t), elementIds: t.InstanceIds)
                        .En($"Main door {label}: hollow core, not solid core"));
                    continue;
                }
                if (Regex.IsMatch(words, @"solid|padu", RegexOptions.IgnoreCase)) solidPass++; else thickPass++;
            }
            if (solidPass > 0)
                list.Add(Pass(id, $"{solidPass} jenis pintu utama: solid core, tebal ≥ 40 mm", solidPass)
                    .En($"{solidPass} main door types: solid core, ≥ 40 mm thick"));
            if (thickPass > 0)
                list.Add(Pass(id, $"{thickPass} jenis pintu utama: tebal ≥ 40 mm; solid core tidak dapat disahkan dari model", thickPass, SpecCoverage.FailOnly)
                    .En($"{thickPass} main door types: ≥ 40 mm thick; solid core cannot be confirmed from the model"));
            return list;
        }

        /// <summary>
        /// A1.4 (NS §5.2.3, §5.14.1): an RC flat roof must carry waterproofing (or double
        /// roofing). AI-504: no JKR document says what a flat roof is (no angle, no name), so the
        /// code no longer decides it. The automatic part is what the NS states: an RC roof
        /// (a concrete material or name: concrete / konkrit / tetulang / RC / JKR code "(kt…)")
        /// that carries waterproofing (a Membrane layer, or a layer material named waterproof /
        /// membrane / kalis air / bitumen) complies whatever its slope. An RC roof without a
        /// waterproofing layer is listed for a person, who decides whether it is a flat roof and
        /// whether double roofing or a separate waterproofing element covers it: never a fail.
        /// A roof whose materials were not read is listed too. No RC roof at all complies.
        /// </summary>
        private static IEnumerable<SpecResult> A14FlatRoof(SpecModel m)
        {
            const string id = "A1.4";
            var list = new List<SpecResult>();
            var elsewhere = K.ModelHasWaterproofing(m);
            int rc = 0, rcOk = 0, unknown = 0;
            foreach (var r in m.Roofs)
            {
                var label = r.TypeName.Length > 0 ? r.TypeName : "Roof " + r.Id;
                var isRc = SpecA.IsConcrete(r.TypeName) || r.Materials.Any(SpecA.IsConcrete);
                if (!isRc && r.Materials.Count == 0)
                {
                    unknown++;
                    list.Add(Context(id, $"Bumbung {label}: bahan tidak dibaca — RC atau tidak, dan sama ada ia flat roof, sahkan (JKR tiada takrif flat roof)",
                                     r.Id, "Roofs · " + label, key: "nomat:" + r.Id)
                        .En($"Roof {label}: materials not read — RC or not, and whether it is a flat roof, a person confirms (JKR gives no flat-roof definition)"));
                    continue;
                }
                if (!isRc) continue;
                rc++;
                var wp = r.LayerFunctions.Any(f => (f ?? "").IndexOf("membrane", StringComparison.OrdinalIgnoreCase) >= 0)
                         || r.Materials.Any(SpecA.IsWaterproofing) || SpecA.IsWaterproofing(r.TypeName);
                if (wp) { rcOk++; continue; }
                var why = elsewhere ? "; ada elemen waterproofing lain dalam model" : "";
                var whyEn = elsewhere ? "; the model has a separate waterproofing element" : "";
                list.Add(Context(id, $"Bumbung RC {label} tiada lapisan waterproofing{why} — NS §5.2.3: flat roof RC perlu waterproofing atau double roofing; manusia putuskan sama ada ia flat roof dan sama ada ia dilindungi (JKR tiada takrif flat roof)",
                                 r.Id, "Roofs · " + label, key: "rc-nowp:" + r.Id)
                    .En($"RC roof {label} has no waterproofing layer{whyEn} — NS §5.2.3: an RC flat roof needs waterproofing or double roofing; a person decides whether it is a flat roof and whether it is covered (JKR gives no flat-roof definition)"));
            }
            if (rcOk > 0)
                list.Add(Pass(id, $"{rcOk} bumbung RC ada waterproofing", rcOk).En($"{rcOk} RC roof{(rcOk == 1 ? " has" : "s have")} waterproofing"));
            else if (rc == 0 && unknown == 0)
                list.Add(Pass(id, $"Tiada bumbung RC dalam model ({m.Roofs.Count} bumbung)", Math.Max(1, m.Roofs.Count))
                    .En($"No RC roof in the model ({m.Roofs.Count} roofs)"));
            return list;
        }

        /// <summary>
        /// A1.5 (NS §4.8.3): decorative fence ≥ 2400 mm high. Fence = a Railings or Walls
        /// type named pagar / fence (stair / balcony handrails excluded); when some are
        /// named hiasan / decorative / ornamental, only those are judged. Height = top rail
        /// height (railings) or the tallest instance (walls). No fence at all → the spec's
        /// MANUAL path, "sahkan skop projek": a person, never a pass.
        /// </summary>
        private static IEnumerable<SpecResult> A15Fence(SpecModel m)
        {
            const string id = "A1.5";
            var list = new List<SpecResult>();
            var fences = m.Types.Where(t => (K.IsCat(t, "Railings") || K.IsCat(t, "Walls")) && SpecA.IsFence(t.FamilyName + " " + t.TypeName)).ToList();
            var deco = fences.Where(t => SpecA.IsDecorative(t.FamilyName + " " + t.TypeName)).ToList();
            if (deco.Count > 0) fences = deco;
            if (fences.Count == 0)
            {
                list.Add(Unjudged(id, "Tiada elemen pagar dalam model — sahkan skop projek", key: "no-fence")
                    .En("No fence elements in the model — confirm the project scope"));
                return list;
            }
            var passed = 0;
            foreach (var t in fences)
            {
                var label = K.TypeLabel(t);
                if (!K.TryNum(t, "Height", out var h) || h <= 0)
                {
                    list.Add(Unjudged(id, $"Pagar {label}: tinggi tidak dibaca", K.FirstId(t), K.ElementLabel(t), key: K.TypeKey(t))
                        .En($"Fence {label}: height not read"));
                    continue;
                }
                var hFit = K.FitMin(h, 2400);
                if (hFit == MinFit.Meets) { passed++; continue; }
                if (hFit == MinFit.PrintsAsMin)
                {
                    list.Add(Context(id, $"Pagar {label}: tinggi {h.ToString("0.###", K.Inv)} mm — di bawah 2400 mm tetapi dipaparkan sebagai 2400 mm selepas pembundaran; JKR tiada peraturan pembundaran — manusia putuskan",
                                     K.FirstId(t), K.ElementLabel(t), key: "edge:" + K.TypeKey(t), elementIds: t.InstanceIds)
                        .En($"Fence {label}: height {h.ToString("0.###", K.Inv)} mm — below 2400 mm but reads as 2400 mm when rounded; JKR gives no rounding rule — a person decides"));
                    continue;
                }
                list.Add(Fail(id, $"Pagar {label}: tinggi {K.Mm(h)} mm < 2400 mm", elementName: K.ElementLabel(t),
                              actual: K.Mm(h) + " mm", required: "≥ 2400 mm", key: K.TypeKey(t), elementIds: t.InstanceIds)
                    .En($"Fence {label}: height {K.Mm(h)} mm < 2400 mm"));
            }
            if (passed > 0)
                list.Add(Pass(id, $"{passed} jenis pagar: tinggi ≥ 2400 mm", passed).En($"{passed} fence types: height ≥ 2400 mm"));
            return list;
        }

        /// <summary>A1.6 (list only): the NS clauses the spec marks "Tidak — semak manual"
        /// (sheet "Klausa NS"), shown so a person checks them. No verdict.</summary>
        private static IEnumerable<SpecResult> A16OtherNsClauses(SpecModel m)
        {
            return SpecA.ManualNsClauses.Select(c =>
                Listing("A1.6", $"Klausa NS {c.Clause} ({c.Rule}): tidak dapat disemak dari model — semak manual")
                    .En($"NS clause {c.Clause} ({c.RuleEn}): cannot be checked from the model — check by hand")).ToList();
        }

        /// <summary>A1.7 (list only): the remark the spec asks for (model-audit-spec.md §A1, principle 0.3).</summary>
        private static IEnumerable<SpecResult> A17Scope(SpecModel m) => new[]
        {
            Listing("A1.7", "Semakan berdasarkan Need Statement minimum kerajaan; konsep reka bentuk tidak disemak.")
                .En("Checked against the government's minimum Need Statement only; the design concept is not assessed."),
        };

        // ════════════════════════════════════════════════════════════════════
        // A2 — file name (PWD BIM Guidelines; F A.2) and the model code (T 21:22).
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A2.1 (F A.2): the file name has every mandatory segment — the spec's test is
        /// "≥ 3 segments per the guideline". Segments are split on '_' outside brackets
        /// ("(BEde1A_p14-001)" stays one segment); a name with fewer than three is split
        /// on '-' too (the F A.2 rule, "dipisah '-' atau '_'"). The segments found are shown.
        /// </summary>
        private static IEnumerable<SpecResult> A21FileName(SpecModel m)
        {
            const string id = "A2.1";
            var name = SpecA.StripExtension(m.FileName);
            if (name.Length == 0)
                return new[] { Unjudged(id, "Fail belum disimpan — tiada nama fail untuk disemak", key: "unsaved").En("File not saved — no file name to check") };
            var segs = SpecA.FileSegments(name);
            var shown = string.Join(" | ", segs);
            // The first segment is the discipline + version block ("jkrAR24", "jkrEL24-5a").
            // A name that does not open with it (e.g. "Copy of jkrAR24_…" from a download)
            // is not the guideline's format, however many segments follow (real Dewan
            // model, 16 Nov: it passed).
            if (segs.Count >= 3 && !SpecA.FileLead.IsMatch(name))
                return new[]
                {
                    Fail(id, $"Nama fail \"{name}\": tidak bermula dengan blok disiplin JKR (cth \"jkrAR24\") — segmen pertama \"{segs[0]}\"",
                         actual: segs[0], required: "jkr + disiplin + versi (cth jkrAR24)", key: "file")
                        .En($"File name \"{name}\": does not start with the JKR discipline block (e.g. \"jkrAR24\") — first segment \"{segs[0]}\""),
                };
            if (segs.Count >= 3)
                return new[] { Pass(id, $"Nama fail \"{name}\": {segs.Count} segmen ({shown})").En($"File name \"{name}\": {segs.Count} segments ({shown})") };
            return new[]
            {
                Fail(id, $"Nama fail \"{name}\": {segs.Count} segmen ({shown}) — perlu ≥ 3 segmen ikut garis panduan",
                     actual: segs.Count + " segmen", required: "≥ 3 segmen", key: "file")
                    .En($"File name \"{name}\": {segs.Count} segments ({shown}) — needs ≥ 3 segments per the guideline"),
            };
        }

        /// <summary>
        /// A2.2 (T 21:22): the model code read from the file name — W00 (single model) or
        /// WXnn (split model, e.g. segment "w-01" / "WX01"), carried to A3. Not readable →
        /// fail, as the spec says (<see cref="SpecA.ParseModelCode"/>).
        /// </summary>
        private static IEnumerable<SpecResult> A22ModelCode(SpecModel m)
        {
            const string id = "A2.2";
            var name = SpecA.StripExtension(m.FileName);
            if (name.Length == 0)
                return new[] { Unjudged(id, "Fail belum disimpan — kod model tidak dapat dibaca", key: "unsaved").En("File not saved — the model code cannot be read") };
            var code = SpecA.ParseModelCode(name);
            if (code == null)
                return new[]
                {
                    Fail(id, $"Kod model (W00 / WXnn) tidak dapat dibaca dari nama fail \"{name}\"", actual: name, required: "W00 / WXnn", key: "file")
                        .En($"Model code (W00 / WXnn) cannot be read from the file name \"{name}\""),
                };
            return new[]
            {
                code.IsSingle
                    ? Pass(id, $"Kod model dikesan: {code.Canonical} (model tunggal) — segmen \"{code.Raw}\"").En($"Model code found: {code.Canonical} (single model) — segment \"{code.Raw}\"")
                    : Pass(id, $"Kod model dikesan: {code.Canonical} (model pecah) — segmen \"{code.Raw}\"").En($"Model code found: {code.Canonical} (split model) — segment \"{code.Raw}\""),
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // A3 — model split (T 21:19–21:24). W00 complies outright; WXnn needs every part
        // linked. The full part list is in the BPEP, which only the backend has.
        // ════════════════════════════════════════════════════════════════════

        /// <summary>A3.1 (T 21:19, 21:22): code W00 → complies. A split model → not applicable.
        /// Code unreadable → a person (A2.2 fails on it).</summary>
        private static IEnumerable<SpecResult> A31Single(SpecModel m)
        {
            const string id = "A3.1";
            var name = SpecA.StripExtension(m.FileName);
            if (name.Length == 0) return Enumerable.Empty<SpecResult>();
            var code = SpecA.ParseModelCode(name);
            if (code == null)
                return new[] { Unjudged(id, "Kod model tidak dapat dibaca (lihat A2.2) — tunggal atau pecah tidak diketahui", key: "no-code").En("Model code not readable (see A2.2) — single or split unknown") };
            if (code.IsSingle)
            {
                // AI-504 (Q16): no JKR document gives a link-count rule, so there is no remark on how
                // many links a W00 model has. The verdict is the code alone.
                return new[] { Pass(id, "Model tunggal (W00) — patuh").En("Single model (W00) — complies") };
            }
            return new[] { NotApplicable(id, $"Model pecah ({code.Canonical}) — A3.1 tidak berkenaan").En($"Split model ({code.Canonical}) — A3.1 does not apply") };
        }

        /// <summary>
        /// A3.2 (T 21:19–21:21): every part of a split model is linked. The parts the
        /// model can show are the codes 1…max(own, highest linked) of the same discipline
        /// (links of another jkrXX discipline are not parts); each one missing from the
        /// links fails, as does a part link whose file is not found. FailOnly: parts above
        /// the highest code seen are known only from the BPEP (A3.3). No links at all and
        /// no missing part known → "link first", a person.
        /// </summary>
        private static IEnumerable<SpecResult> A32PartsLinked(SpecModel m)
        {
            const string id = "A3.2";
            var s = K.Split(m);
            if (s == null) return Enumerable.Empty<SpecResult>();
            if (s.Code.IsSingle) return new[] { NotApplicable(id, "Model tunggal (W00) — tiada pecahan untuk di-link").En("Single model (W00) — no parts to link") };
            var list = new List<SpecResult>();
            foreach (var p in s.Parts.Where(p => K.IsNotFound(p.Link)))
                list.Add(Fail(id, $"Link pecahan {p.Code.Canonical} tidak dijumpai (NotFound): {p.Link.Name}", actual: "NotFound", required: "Loaded", key: "notfound:" + p.Link.Name)
                    .En($"Part link {p.Code.Canonical} not found (NotFound): {p.Link.Name}"));
            var present = new HashSet<int>(s.Parts.Where(p => !K.IsNotFound(p.Link)).Select(p => p.Code.Number));
            var max = Math.Max(s.Code.Number, present.Count > 0 ? present.Max() : 0);
            var missing = Enumerable.Range(1, Math.Max(0, max)).Where(n => n != s.Code.Number && !present.Contains(n)).ToList();
            foreach (var n in missing)
                list.Add(Fail(id, $"Pecahan {SpecA.SplitCode(n)} tiada sebagai link dalam model", actual: "tiada link", required: "link " + SpecA.SplitCode(n), key: "missing:" + n)
                    .En($"Part {SpecA.SplitCode(n)} is not linked in the model"));
            if (list.Count > 0) return list;
            if (m.Links.Count == 0)
                return new[] { Unjudged(id, "Model perlu di-link dahulu sebelum semakan A3 boleh dijalankan", key: "no-links").En("The model must be linked first before the A3 checks can run") };
            if (s.Parts.Count == 0)
                return new[] { Unjudged(id, $"Tiada link pecahan dikenal pasti ({m.Links.Count} link lain) — sahkan pecahan model", key: "no-parts").En($"No part links identified ({m.Links.Count} other links) — confirm the model parts") };
            var linked = string.Join(", ", s.Parts.Select(p => p.Code.Canonical).Distinct());
            return new[]
            {
                Pass(id, $"{s.Code.Canonical} (fail ini); di-link: {linked} — senarai penuh pecahan perlu BPEP (A3.3)", s.Parts.Count, SpecCoverage.FailOnly)
                    .En($"{s.Code.Canonical} (this file); linked: {linked} — the full part list needs the BPEP (A3.3)"),
            };
        }

        /// <summary>A3.3 (T 22:23–22:25): split parts vs the BPEP file list. The BPEP is only in
        /// the backend, so the model side is "cannot be checked" (the spec's MANUAL text), never a pass.</summary>
        private static IEnumerable<SpecResult> A33Bpep(SpecModel m)
        {
            const string id = "A3.3";
            var s = K.Split(m);
            if (s == null) return Enumerable.Empty<SpecResult>();
            if (s.Code.IsSingle) return new[] { NotApplicable(id, "Model tunggal (W00) — tiada pecahan untuk dibanding dengan BPEP").En("Single model (W00) — no parts to compare with the BPEP") };
            return new[] { Unjudged(id, "BPEP tidak tersedia → tidak dapat disemak", key: "bpep").En("BPEP not available → cannot be checked") };
        }

        /// <summary>A3.4 (list only, T 21:22, 21:24): a split model with no links at all gets
        /// the spec's remark; with links, the link count is listed.</summary>
        private static IEnumerable<SpecResult> A34NoLinks(SpecModel m)
        {
            const string id = "A3.4";
            var s = K.Split(m);
            if (s == null) return Enumerable.Empty<SpecResult>();
            if (s.Code.IsSingle) return new[] { NotApplicable(id, "Model tunggal (W00) — A3.4 tidak berkenaan").En("Single model (W00) — A3.4 does not apply") };
            if (m.Links.Count == 0)
                return new[] { Listing(id, $"Model pecah ({s.Code.Canonical}) tanpa link: Model perlu di-link dahulu sebelum semakan A3 boleh dijalankan").En($"Split model ({s.Code.Canonical}) with no links: the model must be linked first before the A3 checks can run") };
            return new[] { Listing(id, $"Model pecah ({s.Code.Canonical}) dengan {m.Links.Count} link — semakan A3 dijalankan", m.Links.Count).En($"Split model ({s.Code.Canonical}) with {m.Links.Count} links — the A3 checks ran") };
        }

        /// <summary>A3.5 (list only, T 21:19–21:20): how the model is split — each part code
        /// with its file (this file and the part links), for a person to judge.</summary>
        private static IEnumerable<SpecResult> A35HowSplit(SpecModel m)
        {
            const string id = "A3.5";
            var s = K.Split(m);
            if (s == null) return Enumerable.Empty<SpecResult>();
            if (s.Code.IsSingle) return new[] { NotApplicable(id, "Model tunggal (W00) — tidak dipecahkan").En("Single model (W00) — not split") };
            var parts = new List<string> { $"{s.Code.Canonical} — {m.FileName} (fail ini)" };
            var partsEn = new List<string> { $"{s.Code.Canonical} — {m.FileName} (this file)" };
            foreach (var p in s.Parts.OrderBy(p => p.Code.Number))
            {
                parts.Add($"{p.Code.Canonical} — {p.Link.Name}");
                partsEn.Add($"{p.Code.Canonical} — {p.Link.Name}");
            }
            return new[] { Listing(id, "Model dipecahkan: " + string.Join("; ", parts), parts.Count).En("Model split: " + string.Join("; ", partsEn)) };
        }

        // ════════════════════════════════════════════════════════════════════
        // A4 — base point (F A.4).
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A4.1 (F A.4; Piawaian p.71-73, Form 0010 item 4, Desc 0012a (b)): the Project Base Point
        /// stands exactly on the Grid A/1 crossing. AI-504: JKR gives no tolerance (the old 10 mm came
        /// from this tool's own earlier output) and its wording is "preferably", so the documented
        /// position is compared exactly (<see cref="SpecUnits.CleanMm"/> only removes feet → mm float
        /// noise) and any other offset is listed for a person, never failed. "A" / "1" are read through
        /// the grid parser, but only a grid named plain A with a grid named plain 1 is Grid A/1: a
        /// project-prefixed pair (PT-A × PT1) is not in any JKR document, so it is listed for a person
        /// (spec #12). Where several plain pairs qualify, the nearest is judged, and named. Base point
        /// not read → no result (the backend and the BIM010 checker answer). No A or no 1, or no
        /// crossing to measure → a person.
        /// </summary>
        private static IEnumerable<SpecResult> A41BasePoint(SpecModel m)
        {
            const string id = "A4.1";
            var bp = m.ProjectBasePoint;
            if (bp == null) return Enumerable.Empty<SpecResult>();
            var labels = SpecA.ParseGrids(m.Grids.Select(g => g.Name));
            var pairs = SpecA.BaseGridPairs(labels);
            if (pairs.Count == 0)
            {
                var prefixed = SpecA.PrefixedBaseGridPairs(labels);
                if (prefixed.Count > 0)
                {
                    var shownPairs = string.Join(", ", prefixed.Take(3).Select(p => $"{m.Grids[p.A].Name.Trim()} / {m.Grids[p.One].Name.Trim()}")) + (prefixed.Count > 3 ? ", …" : "");
                    var firstP = prefixed[0];
                    return new[]
                    {
                        Context(id, $"Tiada grid bernama A dan 1; ada grid berawalan projek ({shownPairs}). JKR tidak menyatakan awalan sebagai Grid A/1 — manusia putuskan sama ada ia Grid A/1 dan semak kedudukan Project Base Point",
                                0, "Grid " + shownPairs, key: "prefixed-a1", elementIds: new[] { m.Grids[firstP.A].Id, m.Grids[firstP.One].Id })
                            .En($"No grid named A and 1; the model has project-prefixed grids ({shownPairs}). No JKR document names a prefix as Grid A/1 — a person decides whether they are Grid A/1 and checks the Project Base Point position"),
                    };
                }
                var names = string.Join(", ", m.Grids.Select(g => g.Name.Trim()).Take(6)) + (m.Grids.Count > 6 ? ", …" : "");
                return new[]
                {
                    m.Grids.Count == 0
                        ? Unjudged(id, "Tiada grid dalam model — kedudukan Project Base Point pada Grid A/1 tidak dapat disemak", key: "no-a1")
                            .En("No grids in the model — the Project Base Point position at Grid A/1 cannot be checked")
                        : Unjudged(id, $"Model ada {m.Grids.Count} grid ({names}) tetapi tiada grid A dan grid 1 — semak manual", key: "no-a1")
                            .En($"The model has {m.Grids.Count} grids ({names}) but no grid A and grid 1 — check by hand"),
                };
            }
            SpecGrid bestA = null, best1 = null;
            SpecPoint at = null;
            var bestD = double.MaxValue;
            foreach (var (ia, i1) in pairs)
            {
                var p = SpecA.IntersectXy(m.Grids[ia], m.Grids[i1]);
                if (p == null) continue;
                var d = Math.Sqrt((bp.X - p.X) * (bp.X - p.X) + (bp.Y - p.Y) * (bp.Y - p.Y));
                if (d < bestD) { bestD = d; bestA = m.Grids[ia]; best1 = m.Grids[i1]; at = p; }
            }
            if (at == null)
            {
                var first = $"{m.Grids[pairs[0].A].Name.Trim()} / {m.Grids[pairs[0].One].Name.Trim()}";
                return new[]
                {
                    Unjudged(id, $"Grid {first}: bukan garisan lurus atau selari — persilangan tidak dapat dihitung; semak manual", key: "no-crossing")
                        .En($"Grids {first}: not straight lines or parallel — the crossing cannot be computed; check by hand"),
                };
            }
            var pair = $"{bestA.Name.Trim()} / {best1.Name.Trim()}";
            var dx = K.Mm1(bp.X - at.X);
            var dy = K.Mm1(bp.Y - at.Y);
            var dist = K.Mm1(bestD);
            if (SpecUnits.CleanMm(bestD) == 0)
                return new[]
                {
                    Pass(id, $"Project Base Point tepat pada persilangan grid {pair} (Grid A/1)")
                        .En($"Project Base Point exactly at the crossing of grids {pair} (Grid A/1)"),
                };
            return new[]
            {
                Context(id, $"Project Base Point {dist} mm dari persilangan grid {pair} (Grid A/1): X {dx} mm, Y {dy} mm. Piawaian: Project Base Point \"sebaiknya\" di Grid A/1; JKR tiada had sisihan — manusia putuskan",
                        0, "Grid " + pair, key: "offset", elementIds: new[] { bestA.Id, best1.Id })
                    .En($"Project Base Point {dist} mm from the crossing of grids {pair} (Grid A/1): X {dx} mm, Y {dy} mm. The Piawaian says the Project Base Point is preferably at Grid A/1; JKR gives no offset limit — a person decides"),
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // A5 — grids and levels (F A.5; T 21:24–21:26). Grid names are read with the
        // project prefix stripped (SpecA.ParseGrids): each prefix is its own series.
        // ════════════════════════════════════════════════════════════════════

        /// <summary>A grid with its parsed name.</summary>
        private sealed class ParsedGrid
        {
            public SpecGrid Grid;
            public SpecGridLabel L;
        }

        private static List<ParsedGrid> ParseModelGrids(SpecModel m)
        {
            var labels = SpecA.ParseGrids(m.Grids.Select(g => g.Name));
            return m.Grids.Select((g, i) => new ParsedGrid { Grid = g, L = labels[i] }).ToList();
        }

        /// <summary>Series by prefix: no prefix first, then the project prefix.</summary>
        private static IEnumerable<IGrouping<string, ParsedGrid>> GridSeries(List<ParsedGrid> parsed) =>
            parsed.GroupBy(p => p.L.Prefix, StringComparer.Ordinal).OrderBy(g => g.Key.Length > 0 ? 1 : 0).ThenBy(g => g.Key, StringComparer.Ordinal);

        /// <summary>A5.1 (list only, F A.5): grid count and level count, with the grids in
        /// order — numbers | letters per series ("PT: …" for the prefixed ones), then the sub-grids.</summary>
        private static IEnumerable<SpecResult> A51Counts(SpecModel m)
        {
            if (m.Grids.Count == 0 && m.Levels.Count == 0) return Enumerable.Empty<SpecResult>();
            var parsed = ParseModelGrids(m);
            var parts = new List<string>();
            foreach (var g in GridSeries(parsed))
            {
                var nums = g.Where(p => p.L.Kind == SpecGridKind.Number).OrderBy(p => p.L.Main).Select(p => p.L.Core).Distinct().ToList();
                var lets = g.Where(p => p.L.Kind == SpecGridKind.Letter).OrderBy(p => p.L.Main).Select(p => p.L.Core).Distinct().ToList();
                if (nums.Count == 0 && lets.Count == 0) continue;
                var text = string.Join(",", nums) + " | " + string.Join(",", lets);
                parts.Add(g.Key.Length > 0 ? g.Key + ": " + text : text);
            }
            var order = parts.Count > 0 ? string.Join(" · ", parts) : " | ";
            var subs = parsed.Where(p => p.L.Kind == SpecGridKind.NumberSub || p.L.Kind == SpecGridKind.LetterSub)
                             .Select(p => p.L.Display).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var sub = subs.Count > 0 ? "; sub-grid: " + string.Join(", ", subs) : "";
            return new[]
            {
                Listing("A5.1", $"{m.Grids.Count} grid, {m.Levels.Count} aras — grid: {order}{sub}", m.Grids.Count + m.Levels.Count)
                    .En($"{m.Grids.Count} grids, {m.Levels.Count} levels — grids: {order}{sub}"),
            };
        }

        /// <summary>
        /// A5.2 (T 21:24–21:26): main gridlines run 1,2,3… and A,B,C… with no jump, in
        /// either direction. Sub-grids (1A, A1) are left out of the main sequence (A5.3).
        /// Letters I and O are conventionally skipped, so H→J or N→P is not a jump; AA
        /// follows Z. One fail per jump ("1,2,3 → 11"). Where every grid of a sequence
        /// runs the same way, they must also stand in that order on plan (1,3,2 fails).
        /// </summary>
        private static IEnumerable<SpecResult> A52GridSequence(SpecModel m)
        {
            const string id = "A5.2";
            var list = new List<SpecResult>();
            if (m.Grids.Count == 0)
            {
                list.Add(Unjudged(id, "Tiada grid dalam model — urutan tidak dapat disemak", key: "no-grids").En("No grids in the model — sequence cannot be checked"));
                return list;
            }
            var parsed = ParseModelGrids(m);
            if (!parsed.Any(p => p.L.Kind == SpecGridKind.Number || p.L.Kind == SpecGridKind.Letter))
            {
                var names = string.Join(", ", m.Grids.Select(g => g.Name).Take(10));
                list.Add(Unjudged(id, $"Tiada grid utama bernombor / berhuruf dikenal pasti: {names}", key: "no-main").En($"No numbered / lettered main grids identified: {names}"));
                return list;
            }
            var descs = new List<string>();
            var checkedCount = 0;
            // Each prefix is its own series: 1,2,3,4 beside PT1…PT8 is two runs, not one.
            foreach (var series in GridSeries(parsed))
            {
                var key = series.Key.Length > 0 ? series.Key + ":" : "";
                var nums = series.Where(p => p.L.Kind == SpecGridKind.Number).ToList();
                var lets = series.Where(p => p.L.Kind == SpecGridKind.Letter).ToList();
                if (nums.Count == 0 && lets.Count == 0) continue;
                checkedCount += nums.Count + lets.Count;
                var numPre = nums.Count > 0 ? nums[0].L.PrefixText : "";
                var letPre = lets.Count > 0 ? lets[0].L.PrefixText : "";
                string NumName(int n) => numPre + n.ToString(CultureInfo.InvariantCulture);
                string LetName(int n) => letPre + SpecA.LetterName(n);
                foreach (var gap in SpecA.NumberGaps(nums.Select(p => p.L.Main)))
                {
                    var run = SpecA.RunBefore(nums.Select(p => p.L.Main), gap.From, NumName);
                    list.Add(Fail(id, $"Grid {run} → {NumName(gap.To)}: urutan melompat ({SpecA.MissingNumbers(gap.From, gap.To)} tiada)",
                                  actual: $"{NumName(gap.From)} → {NumName(gap.To)}", required: "tanpa lompat", key: "num:" + key + gap.From + "-" + gap.To)
                        .En($"Grids {run} → {NumName(gap.To)}: sequence jumps ({SpecA.MissingNumbers(gap.From, gap.To)} missing)"));
                }
                foreach (var gap in SpecA.LetterGaps(lets.Select(p => p.L.Main)))
                {
                    var run = SpecA.RunBefore(lets.Select(p => p.L.Main), gap.From, LetName);
                    var miss = string.Join(",", gap.Missing.Take(6)) + (gap.Missing.Count > 6 ? "…" : "");
                    list.Add(Fail(id, $"Grid {run} → {LetName(gap.To)}: urutan melompat ({miss} tiada)",
                                  actual: $"{LetName(gap.From)} → {LetName(gap.To)}", required: "tanpa lompat",
                                  key: "let:" + key + gap.From + "-" + gap.To)
                        .En($"Grids {run} → {LetName(gap.To)}: sequence jumps ({miss} missing)"));
                }
                foreach (var seq in new[] { nums, lets })
                {
                    var order = SpecA.PlanOrderBroken(seq.Select(p => (p.L.Main, p.Grid.Offset, p.Grid.IsVertical)).ToList());
                    if (order == null) continue;
                    var shown = string.Join(",", order.Select(i => seq.First(p => p.L.Main == i).L.Display));
                    list.Add(Fail(id, $"Grid tidak tersusun ikut kedudukan pada pelan: {shown}", actual: shown, required: "tersusun",
                                  key: "order:" + shown, elementIds: seq.Select(p => p.Grid.Id))
                        .En($"Grids not in order on plan: {shown}"));
                }
                descs.Add(SpecA.SequenceText(nums.Select(p => p.L.Main), NumName) + " | " + SpecA.SequenceText(lets.Select(p => p.L.Main), LetName));
            }
            if (list.Count > 0) return list;
            var other = parsed.Where(p => p.L.Kind == SpecGridKind.Other).Select(p => p.Grid.Name).ToList();
            var desc = string.Join(" · ", descs);
            var extra = other.Count > 0 ? $"; grid lain tidak disemak: {string.Join(", ", other.Take(6))}" : "";
            var extraEn = other.Count > 0 ? $"; other grids not checked: {string.Join(", ", other.Take(6))}" : "";
            list.Add(Pass(id, $"Grid tersusun tanpa lompat: {desc}{extra}", checkedCount)
                .En($"Grids in sequence with no jump: {desc}{extraEn}"));
            return list;
        }

        /// <summary>
        /// A5.3 (T 21:25): sub-grids follow 1A, 1B… (under number 1) or A1, A2… (under
        /// letter A), listed under their parent. A sub-grid is never wrong for existing:
        /// the spec has no fail here. A sub-grid whose parent is missing, that stands
        /// outside its parent's bay on plan, or that skips (1A → 1C) goes to a person.
        /// No sub-grids → nothing breaks the pattern (pass, the grids were read).
        /// </summary>
        private static IEnumerable<SpecResult> A53SubGrids(SpecModel m)
        {
            const string id = "A5.3";
            var list = new List<SpecResult>();
            if (m.Grids.Count == 0) return list;
            var parsed = ParseModelGrids(m);
            var subs = parsed.Where(p => p.L.Kind == SpecGridKind.NumberSub || p.L.Kind == SpecGridKind.LetterSub).ToList();
            if (subs.Count == 0)
            {
                list.Add(Pass(id, "Tiada sub-grid dalam model — tiada pola untuk disemak").En("No sub-grids in the model — no pattern to check"));
                return list;
            }
            var valid = new List<string>();
            // The parent is looked for in the sub-grid's own series: PT4A under PT4, PT-A1 under
            // PT-A; K3 (no prefix) under a grid K.
            foreach (var grp in subs.GroupBy(p => (p.L.Prefix, p.L.Kind, p.L.ParentIndex))
                                    .OrderBy(g => g.Key.Prefix.Length > 0 ? 1 : 0).ThenBy(g => g.Key.Prefix, StringComparer.Ordinal)
                                    .ThenBy(g => g.Key.Kind).ThenBy(g => g.Key.ParentIndex))
            {
                var mainKind = grp.Key.Kind == SpecGridKind.NumberSub ? SpecGridKind.Number : SpecGridKind.Letter;
                var parentName = grp.First().L.ParentDisplay;
                var mains = parsed.Where(p => p.L.Kind == mainKind && p.L.Prefix == grp.Key.Prefix).ToList();
                var parent = mains.FirstOrDefault(p => p.L.Main == grp.Key.ParentIndex);
                if (parent == null)
                {
                    foreach (var s in grp)
                        list.Add(Unjudged(id, $"Sub-grid {s.Grid.Name}: grid induk {parentName} tiada — sahkan", s.Grid.Id, "Grid " + s.Grid.Name)
                            .En($"Sub-grid {s.Grid.Name}: parent grid {parentName} missing — confirm"));
                    continue;
                }
                var next = mains.Where(p => p.L.Main > grp.Key.ParentIndex).OrderBy(p => p.L.Main).FirstOrDefault();
                var okHere = new List<string>();
                foreach (var s in grp.OrderBy(p => p.L.Sub))
                {
                    if (next != null && !SpecA.WithinBay(s.Grid, parent.Grid, next.Grid))
                    {
                        list.Add(Unjudged(id, $"Sub-grid {s.Grid.Name} di luar petak {parent.Grid.Name}–{next.Grid.Name} — sahkan", s.Grid.Id, "Grid " + s.Grid.Name)
                            .En($"Sub-grid {s.Grid.Name} lies outside bay {parent.Grid.Name}–{next.Grid.Name} — confirm"));
                        continue;
                    }
                    okHere.Add(s.L.Display);
                }
                var subIdx = grp.Select(p => p.L.Sub).ToList();
                var gaps = grp.Key.Kind == SpecGridKind.NumberSub
                    ? SpecA.LetterGaps(subIdx).Select(g => (g.From, g.To, Miss: string.Join(",", g.Missing.Select(x => parentName + x)))).ToList()
                    : SpecA.NumberGaps(subIdx).Select(g => (g.From, g.To, Miss: string.Join(",", Enumerable.Range(g.From + 1, Math.Min(6, g.To - g.From - 1)).Select(x => parentName + x)))).ToList();
                foreach (var g in gaps)
                {
                    string Name(int i) => grp.Key.Kind == SpecGridKind.NumberSub ? parentName + SpecA.LetterName(i) : parentName + i.ToString(CultureInfo.InvariantCulture);
                    list.Add(Unjudged(id, $"Sub-grid {Name(g.From)} → {Name(g.To)}: {g.Miss} tiada — sahkan", key: "subgap:" + parentName + ":" + g.From + "-" + g.To)
                        .En($"Sub-grid {Name(g.From)} → {Name(g.To)}: {g.Miss} missing — confirm"));
                }
                if (okHere.Count > 0) valid.Add(parentName + ": " + string.Join(", ", okHere));
            }
            if (valid.Count > 0)
            {
                var n = valid.Sum(v => v.Split(',').Length);
                list.Add(Pass(id, "Sub-grid ikut pola: " + string.Join(" · ", valid), n).En("Sub-grids follow the pattern: " + string.Join(" · ", valid)));
            }
            return list;
        }

        // ════════════════════════════════════════════════════════════════════
        // A6 — rooms (T 21:26–21:34): every slab has a room; Department is information.
        // ════════════════════════════════════════════════════════════════════

        /// <summary>A6.1 (list only): rooms per level, as `Aras | Nama | Nombor | Department | Luas`,
        /// every level that has rooms. Rooms with no area are counted apart: not placed (no
        /// location) and placed but not enclosed / redundant (real model, 16 Nov 2026: Aras 01
        /// had 8 of the one and 44 of the other, shown as "52 not placed"). A snapshot that
        /// did not read placement counts them as not placed.</summary>
        private static IEnumerable<SpecResult> A61RoomsPerLevel(SpecModel m)
        {
            const string id = "A6.1";
            var list = new List<SpecResult>();
            if (m.Rooms.Count == 0) return list;
            var levels = K.LevelMap(m);
            foreach (var g in m.Rooms.GroupBy(r => r.LevelName.Trim(), StringComparer.OrdinalIgnoreCase).OrderBy(g => K.LevelElevation(levels, g.Key)))
            {
                var name = K.LevelLabel(g.Key);
                var placed = g.Where(r => r.Area > 0).OrderBy(r => r.Number, StringComparer.OrdinalIgnoreCase).ToList();
                var noArea = g.Where(r => !(r.Area > 0)).ToList();
                var notEnclosed = noArea.Count(r => r.Placed == true);
                var unplaced = noArea.Count - notEnclosed;
                var rows = placed.Take(8).Select(r => $"{K.Or(r.Name)} | {K.Or(r.Number)} | {K.Or(r.Department)} | {K.M2(r.Area)} m²").ToList();
                var more = placed.Count > 8 ? $"; … +{placed.Count - 8}" : "";
                var un = (unplaced > 0 ? $"; {unplaced} room tidak ditempatkan" : "")
                         + (notEnclosed > 0 ? $"; {notEnclosed} room tidak tertutup / berlebihan" : "");
                var unEn = (unplaced > 0 ? $"; {unplaced} rooms not placed" : "")
                           + (notEnclosed > 0 ? $"; {notEnclosed} rooms not enclosed / redundant" : "");
                var shown = rows.Count > 0 ? " — " + string.Join("; ", rows) + more : "";
                list.Add(Listing(id, $"Aras {name}: {placed.Count} room{shown}{un}", g.Count())
                    .En($"Level {name}: {placed.Count} rooms{shown}{unEn}"));
            }
            return list;
        }

        /// <summary>A6.2: sample points per slab footprint (a way of reading the geometry, not a JKR number).</summary>
        internal const int A62SamplesPerFloor = 400;

        /// <summary>
        /// A6.2 (T 21:28–21:33): where there is a slab there must be a room — lift, stair and apron
        /// included. AI-504: the meeting gives the principle and no number, so the invented 50 %
        /// coverage, the 1 m² pad rule and the 1500 mm level slack are gone. The automatic part is the
        /// principle: a slab with a placed room on it (at least one point sampled inside its top-face
        /// loops lies inside the boundary of a placed room that stands on it) complies. A slab with no
        /// room on it is listed for a person (the lift, stair and apron exceptions are theirs to
        /// judge), never failed, however small. A room stands on a slab when it is on the slab's
        /// level, or it is a stair or lift room whose own height holds the slab top (it rises through
        /// storeys and covers each landing inside it); compared exactly.
        /// Old snapshots (no loops): the floor's plan box stands in for its footprint, and a room with
        /// no boundary counts when its location point is on the slab — a pass then proves only part
        /// of the check (FailOnly). No floors → cannot be judged.
        /// </summary>
        private static IEnumerable<SpecResult> A62SlabHasRoom(SpecModel m)
        {
            const string id = "A6.2";
            var list = new List<SpecResult>();
            if (m.Floors.Count == 0)
            {
                var msg = m.Rooms.Count == 0 ? "Tiada lantai dan tiada room dalam model" : "Tiada lantai (slab) dimodelkan";
                var msgEn = m.Rooms.Count == 0 ? "No floors and no rooms in the model" : "No floors (slabs) modelled";
                list.Add(Unjudged(id, msg + " — tidak dapat disemak", key: "no-floors").En(msgEn + " — cannot be checked"));
                return list;
            }
            var rooms = m.Rooms.Where(r => r.Area > 0).ToList();
            var shapes = rooms.Where(r => r.Loops.Count > 0).ToDictionary(r => r, r => new SpecPlan.Shape<SpecRoom>(r, r.Loops));
            var roomsHaveLoops = shapes.Count > 0;
            int passedFull = 0, passedPartly = 0;
            foreach (var f in m.Floors)
            {
                var label = f.TypeName.Length > 0 ? f.TypeName : "Floor";
                var lv = f.LevelName.Trim();
                var hasBox = f.MaxX > f.MinX && f.MaxY > f.MinY;
                var footprint = f.Loops.Count > 0 ? f.Loops : hasBox ? SpecPlan.Rect(f.MinX, f.MinY, f.MaxX, f.MaxY) : null;
                if (footprint == null || (lv.Length == 0 && f.TopElevation == null))
                {
                    list.Add(Unjudged(id, $"Lantai {f.Id} ({label}): aras atau kedudukan tidak dibaca", f.Id, "Floors · " + label)
                        .En($"Floor {f.Id} ({label}): level or position not read"));
                    continue;
                }
                bool SameStorey(SpecRoom r)
                {
                    if (string.Equals(r.LevelName.Trim(), lv, StringComparison.OrdinalIgnoreCase)) return true;
                    if (f.TopElevation == null || r.BaseElevation == null || !(r.Height > 0) || !SpecA.IsVerticalCirculation(r.Name)) return false;
                    // A stair or lift room rising through storeys covers each landing whose top is inside
                    // its own height (the spec names "ruang lif … tangga"); any other room is on its level.
                    var top = SpecUnits.CleanMm(f.TopElevation.Value);
                    var rb = SpecUnits.CleanMm(r.BaseElevation.Value);
                    return top >= rb && top < SpecUnits.CleanMm(rb + r.Height.Value);
                }
                bool OnSlab(double x, double y) => f.Loops.Count > 0
                    ? SpecPlan.Inside(f.Loops, x, y)
                    : x >= f.MinX && x <= f.MaxX && y >= f.MinY && y <= f.MaxY;
                var cand = rooms.Where(SameStorey).ToList();
                var byPoint = cand.Any(r => r.Loops.Count == 0 && OnSlab(r.X, r.Y));
                var fb = new SpecPlan.Shape<SpecFloor>(f, footprint);
                var inside = cand.Where(r => r.Loops.Count > 0).Select(r => shapes[r]).Where(s => s.Overlaps(fb)).ToList();
                var samples = SpecPlan.Samples(footprint, A62SamplesPerFloor);
                var open = samples.Where(p => !inside.Any(s => s.Contains(p.X, p.Y))).ToList();
                var hasRoom = byPoint || open.Count < samples.Count;
                var geo = f.Loops.Count > 0 && roomsHaveLoops;
                if (hasRoom)
                {
                    if (geo) passedFull++; else passedPartly++;
                    continue;
                }
                // Locate it at the slab's middle.
                double cx = (f.MinX + f.MaxX) / 2, cy = (f.MinY + f.MaxY) / 2;
                if (samples.Count > 0) { cx = samples.Average(p => p.X); cy = samples.Average(p => p.Y); }
                var near = K.NearestGrids(m, cx, cy);
                var kind = SpecA.SpaceKind(f.TypeName);
                var at = near.Length > 0 ? $" | grid {near}" : "";
                var k = kind.Length > 0 ? $" | {kind}" : "";
                var lvName = K.LevelLabel(lv);
                list.Add(Context(id, $"{Aras(lvName)}{at}{k}: lantai {f.Id} ({label}) tiada room — manusia putuskan sama ada slab ini dikecualikan (lif, tangga, apron)",
                                 f.Id, "Floors · " + label, key: "noroom:" + f.Id)
                    .En($"Level {lvName}{at}{k}: floor {f.Id} ({label}) has no room — a person decides whether this slab is an exception (lift, stair, apron)"));
            }
            if (passedFull > 0)
                list.Add(Pass(id, $"{passedFull} lantai ada room di atasnya", passedFull)
                    .En($"{passedFull} floors have a room on them"));
            if (passedPartly > 0)
                list.Add(Pass(id, $"{passedPartly} lantai ada room di atasnya", passedPartly, SpecCoverage.FailOnly).En($"{passedPartly} floors have a room on them"));
            return list;
        }

        /// <summary>A6.3 (human-decides, T 21:26, 21:34): rooms with an empty Department, as
        /// information only — whether the project type needs departments is a person's call;
        /// an empty Department is never a fail.</summary>
        private static IEnumerable<SpecResult> A63Department(SpecModel m)
        {
            const string id = "A6.3";
            // Only rooms that exist on plan (placed, enclosed, area > 0): an unplaced or
            // not-enclosed room is A6.1's concern, and counting it here gave "2 of 140" on the
            // real model where 54 rooms are real (16 Nov).
            var rooms = m.Rooms.Where(r => r.Area > 0).ToList();
            if (rooms.Count == 0) return Enumerable.Empty<SpecResult>();
            var empty = rooms.Where(r => r.Department.Trim().Length == 0).ToList();
            if (empty.Count == 0)
                return new[] { Context(id, $"Semua {rooms.Count} room ada Department").En($"All {rooms.Count} rooms have a Department") };
            var names = string.Join(", ", empty.Take(8).Select(r => (K.Or(r.Number) + " " + r.Name).Trim()));
            var more = empty.Count > 8 ? ", …" : "";
            return new[]
            {
                Context(id, $"{empty.Count} daripada {rooms.Count} room tiada Department (maklumat sahaja; jenis projek menentukan): {names}{more}")
                    .En($"{empty.Count} of {rooms.Count} rooms have no Department (information only; the project type decides): {names}{more}"),
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // A7 — materials (T 21:40–21:46; PB p.35 Material Identity; NS §12.4.2).
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A7.1 (T 21:42–21:43): every element in every category has a material. Judged per
        /// element (AI-475): in each placed type of a category where a material applies
        /// (everything but the non-physical ones in <see cref="SpecA.NoMaterialCategories"/>,
        /// plus Planting and Entourage, whose render content carries no Revit material), the
        /// instances with no material fail, as `Kategori | Type | n daripada N elemen`; the
        /// failing result carries exactly those element ids. A snapshot without per-element data (old,
        /// hand-made) falls back to the type's materials, as before. A pass is FailOnly when a
        /// type has more instances than the reader's id cap (the rest were not read).
        /// </summary>
        private static IEnumerable<SpecResult> A71Materials(SpecModel m)
        {
            const string id = "A7.1";
            var list = new List<SpecResult>();
            var types = m.Types.Where(t => !SpecA.NoMaterialCategories.Contains(t.Category.Trim())).ToList();
            if (types.Count == 0)
            {
                list.Add(Unjudged(id, "Tiada elemen model dibaca — material tidak dapat disemak", key: "no-types").En("No model elements read — materials cannot be checked"));
                return list;
            }
            var passedElements = 0;
            var partly = false;
            foreach (var t in types)
            {
                var typeHas = t.Materials.Any(x => !string.IsNullOrWhiteSpace(x));
                if (!t.ElementMaterialsRead)
                {
                    // No per-element data (old snapshot): the type's list stands for its elements.
                    if (typeHas) { passedElements += Math.Max(1, t.InstanceCount); continue; }
                    var n0 = K.Count(t);
                    list.Add(Fail(id, $"{K.Or(t.Category)} | {K.TypeLabel(t)} | {n0} elemen: tiada material", elementName: K.ElementLabel(t),
                                  actual: "tiada material", required: "material", key: K.TypeKey(t), elementIds: t.InstanceIds)
                        .En($"{K.Or(t.Category)} | {K.TypeLabel(t)} | {n0} elements: no material"));
                    continue;
                }
                var judged = t.InstanceIds.Count;
                if (t.InstanceCount > judged) partly = true;   // more instances than the reader read
                var bare = t.MateriallessIds.Distinct().ToList();
                if (bare.Count == 0) { passedElements += Math.Max(judged, 0); continue; }
                passedElements += Math.Max(judged - bare.Count, 0);
                var total = Math.Max(t.InstanceCount, judged);
                list.Add(Fail(id, $"{K.Or(t.Category)} | {K.TypeLabel(t)} | {bare.Count} daripada {total} elemen: tiada material", elementName: K.ElementLabel(t),
                              actual: "tiada material", required: "material", key: K.TypeKey(t), elementIds: bare)
                    .En($"{K.Or(t.Category)} | {K.TypeLabel(t)} | {bare.Count} of {total} elements: no material"));
            }
            if (passedElements > 0)
                list.Add(Pass(id, $"{passedElements} elemen ada material", passedElements, partly ? SpecCoverage.FailOnly : SpecCoverage.Full)
                    .En($"{passedElements} elements have a material"));
            return list;
        }

        /// <summary>
        /// A7.2 (T 21:40–21:46, PB p.35 "Category: Material Identity"): used material names
        /// follow JKR naming — "jkr" + discipline + "_" … + a bracketed material code, as
        /// the PB examples "jkrAR_dor-b_(ab)-2 Aluminium Bronzed Anodised", "jkrAR_(kt)-
        /// Konkrit tetulang" (<see cref="SpecA.IsJkrMaterialName"/>). A Revit default
        /// name ("Default", "Glass", "Concrete - Cast-in-Place …") or any non-JKR name is a
        /// separate WARNING, one per material, with the categories that use it. Only used
        /// materials (UsedBy &gt; 0, or named on a placed type).
        /// Spec revision 2 (T 21:41, 21:46): human-decides — JKR itself cannot call a
        /// non-JKR material name wrong. So never a Fail and never a Pass: each warning is a
        /// Context result with an issue (key "mat:&lt;name&gt;") a person opens; all JKR-named →
        /// one Context note. The mandatory material coding is judged in Db.2.
        /// </summary>
        private static IEnumerable<SpecResult> A72MaterialNaming(SpecModel m)
        {
            const string id = "A7.2";
            var list = new List<SpecResult>();
            var used = K.UsedMaterials(m);
            if (used.Count == 0)
            {
                list.Add(Context(id, "Tiada material digunakan dibaca — naming tidak dapat disemak; sahkan secara manual", key: "no-materials")
                    .En("No used materials read — naming cannot be checked; confirm by hand"));
                return list;
            }
            var jkrNamed = 0;
            foreach (var u in used)
            {
                if (SpecA.IsJkrMaterialName(u.Name)) { jkrNamed++; continue; }
                var cats = u.Categories.Count > 0 ? " (" + string.Join(", ", u.Categories.Take(4)) + (u.Categories.Count > 4 ? ", …" : "") + ")" : "";
                var dflt = SpecA.IsRevitDefaultMaterial(u.Name);
                var why = dflt ? "nama default Revit" : "bukan naming JKR";
                var whyEn = dflt ? "Revit default name" : "not JKR naming";
                var r = Context(id, $"AMARAN: Material \"{u.Name}\"{cats}, {u.Count} elemen: {why}", elementName: u.Name, key: "mat:" + u.Name)
                    .En($"WARNING: Material \"{u.Name}\"{cats}, {u.Count} elements: {whyEn}");
                r.Actual = u.Name;
                r.Required = "jkrXX_…_(kod) nama";
                list.Add(r);
            }
            if (list.Count == 0)
                list.Add(Context(id, $"Semua {jkrNamed} material yang digunakan ikut naming JKR").En($"All {jkrNamed} used materials follow JKR naming"));
            return list;
        }

        /// <summary>A7.3 (human-decides, T 21:44): whether the chosen material is right is not
        /// judged in A7 (section D does) — the remark, plus the materials per category with how
        /// many are not JKR-named, for the person.</summary>
        private static IEnumerable<SpecResult> A73MaterialChoice(SpecModel m)
        {
            const string id = "A7.3";
            var list = new List<SpecResult>
            {
                Context(id, "Ketepatan pilihan material tidak dinilai di A7 — hanya kewujudan + naming; ketepatan disemak di seksyen D")
                    .En("Material choice is not judged in A7 — only presence and naming; correctness is checked in section D"),
            };
            var byCat = new SortedDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in m.Types)
                foreach (var name in t.Materials.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    var c = K.Or(t.Category);
                    if (!byCat.TryGetValue(c, out var set)) byCat[c] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(name.Trim());
                }
            foreach (var kv in byCat)
            {
                var bad = kv.Value.Count(x => !SpecA.IsJkrMaterialName(x));
                list.Add(Context(id, $"{kv.Key}: {kv.Value.Count} material, {bad} tidak ikut JKR").En($"{kv.Key}: {kv.Value.Count} materials, {bad} not JKR-named"));
            }
            return list;
        }
    }

    /// <summary>How a value stands against a documented minimum, see <see cref="SpecAKit.FitMin"/>.</summary>
    internal enum MinFit { Meets, PrintsAsMin, Below }

    /// <summary>Unit conversion fixes for the spec evaluators. Revit stores lengths in feet; the
    /// snapshot holds millimetres converted from them, so 900 mm typed in Revit can come back as
    /// 899.9999999999999. <see cref="CleanMm"/> removes that float noise (it rounds at 1e-6 mm, a
    /// nanometre) and nothing else: it is a conversion fix, not a tolerance. Two lengths are the
    /// same only when they are equal after it (AI-504: Dc.1 compares exactly).</summary>
    public static class SpecUnits
    {
        /// <summary>Decimals kept after a feet → mm conversion.</summary>
        public const int MmDecimals = 6;
        public static double CleanMm(double mm) => double.IsNaN(mm) || double.IsInfinity(mm) ? mm : Math.Round(mm, MmDecimals);
        public static bool SameMm(double a, double b) => CleanMm(a) == CleanMm(b);
    }

    /// <summary>Formatting and lookup helpers for the section A evaluators, kept out of
    /// the shared SpecChecks partial class so other sections' helpers cannot clash.</summary>
    internal static class SpecAKit
    {

        internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        internal static string Mm(double v) => Math.Round(v).ToString("0", Inv);
        internal static string Mm1(double v) => Math.Round(v, 1).ToString("0.#", Inv);

        /// <summary>How a measured value stands against a documented minimum (AI-504): no
        /// tolerance. <see cref="MinFit.Meets"/> = at or above the minimum, compared exactly (after
        /// <see cref="SpecUnits.CleanMm"/>, which only removes feet → mm float noise).
        /// <see cref="MinFit.PrintsAsMin"/> = below it, but the value shown at <paramref name="decimals"/>
        /// decimals reads as the minimum (2999.6 mm shows as 3000): JKR gives no rounding rule (Q1),
        /// so a person decides and the check never fails it. <see cref="MinFit.Below"/> = a real miss.</summary>
        internal static MinFit FitMin(double value, double min, int decimals = 0)
        {
            var v = SpecUnits.CleanMm(value);
            if (v >= min) return MinFit.Meets;
            return Math.Round(v, decimals, MidpointRounding.AwayFromZero) >= min ? MinFit.PrintsAsMin : MinFit.Below;
        }
        internal static string M2(double v) => v.ToString("0.0", Inv);
        internal static string Deg(double v) => v.ToString("0.#", Inv);
        internal static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
        internal static string LevelLabel(string name) => string.IsNullOrWhiteSpace(name) ? "(tiada aras)" : name.Trim();

        internal static bool IsCat(SpecType t, string cat) => string.Equals(t.Category.Trim(), cat, StringComparison.OrdinalIgnoreCase);
        internal static int Count(SpecType t) => Math.Max(t.InstanceCount, t.InstanceIds.Count);
        internal static long FirstId(SpecType t) => t.InstanceIds.FirstOrDefault(x => x > 0);
        internal static string TypeKey(SpecType t) => t.TypeId > 0 ? "type:" + t.TypeId : "type:" + t.Category + "|" + t.FamilyName + "|" + t.TypeName;
        internal static string ElementLabel(SpecType t) => K.Or(t.Category) + " · " + K.TypeLabel(t);

        internal static string TypeLabel(SpecType t)
        {
            var f = t.FamilyName.Trim();
            var n = t.TypeName.Trim();
            if (f.Length > 0 && n.Length > 0 && !string.Equals(f, n, StringComparison.OrdinalIgnoreCase)) return f + " : " + n;
            return n.Length > 0 ? n : f.Length > 0 ? f : "(tanpa nama)";
        }

        /// <summary>Family, type, text values and materials as one string, for word tests.</summary>
        internal static string TypeWords(SpecType t) =>
            t.FamilyName + " " + t.TypeName + " " + string.Join(" ", t.Texts.Values.Where(v => v != null)) + " " + string.Join(" ", t.Materials.Where(v => v != null));

        internal static bool TryNum(SpecType t, string key, out double v)
        {
            if (t.Numbers.TryGetValue(key, out v)) return !double.IsNaN(v) && !double.IsInfinity(v);
            foreach (var kv in t.Numbers)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { v = kv.Value; return !double.IsNaN(v) && !double.IsInfinity(v); }
            v = 0;
            return false;
        }

        internal static string Text(SpecType t, string key)
        {
            if (t.Texts.TryGetValue(key, out var v)) return v ?? "";
            foreach (var kv in t.Texts)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value ?? "";
            return "";
        }

        internal static Dictionary<string, SpecLevel> LevelMap(SpecModel m)
        {
            var d = new Dictionary<string, SpecLevel>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in m.Levels)
                if (!d.ContainsKey(l.Name.Trim())) d[l.Name.Trim()] = l;
            return d;
        }

        internal static SpecLevel LevelOf(Dictionary<string, SpecLevel> levels, string name) =>
            levels.TryGetValue((name ?? "").Trim(), out var l) ? l : null;

        internal static double LevelElevation(Dictionary<string, SpecLevel> levels, string name)
        {
            var l = K.LevelOf(levels, name);
            return l != null ? l.Elevation : double.MaxValue;
        }

        /// <summary>The link is a model of discipline <paramref name="d"/> ("ST"): its name holds
        /// "jkr" + d, or a name segment is the upper-case code + digits ("…_ST_…", "ST24"). The
        /// same reading as C5.1.1 (SpecBC.LinkIs), kept here so A3.1 does not lean on section C.</summary>
        internal static bool LinkOfDiscipline(SpecLink l, string d)
        {
            var name = l?.Name ?? "";
            if (name.IndexOf("jkr" + d, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var seg in name.Split(new[] { '_', '-', ' ', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
                if (seg.StartsWith(d, StringComparison.Ordinal) && seg.Substring(d.Length).All(char.IsDigit)) return true;
            return false;
        }

        internal static bool IsNotFound(SpecLink l) => string.Equals(l.Status.Trim(), "NotFound", StringComparison.OrdinalIgnoreCase);

        internal static bool ModelHasWaterproofing(SpecModel m) =>
            m.Types.Where(t => !K.IsCat(t, "Roofs")).Any(t => SpecA.IsWaterproofing(t.FamilyName + " " + t.TypeName) || t.Materials.Any(SpecA.IsWaterproofing))
            || m.Materials.Any(x => x.UsedBy > 0 && SpecA.IsWaterproofing(x.Name));

        /// <summary>"3/B": the vertical and horizontal grid nearest a plan point ("" with no grids).</summary>
        internal static string NearestGrids(SpecModel m, double x, double y)
        {
            var v = m.Grids.Where(g => g.IsVertical).OrderBy(g => Math.Abs(g.Offset - x)).FirstOrDefault();
            var h = m.Grids.Where(g => !g.IsVertical).OrderBy(g => Math.Abs(g.Offset - y)).FirstOrDefault();
            var parts = new[] { v?.Name, h?.Name }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return string.Join("/", parts);
        }

        internal sealed class UsedMaterial
        {
            public string Name = "";
            public int Count;
            public List<string> Categories = new List<string>();
        }

        private static readonly Regex CurtainWallRx = new Regex(@"curtain|storefront", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A Walls type that is a curtain wall (Revit's "Curtain Wall" family, storefronts).</summary>
        internal static bool IsCurtainWall(SpecType t) => K.IsCat(t, "Walls") && CurtainWallRx.IsMatch(t.FamilyName + " " + t.TypeName);

        /// <summary>
        /// Materials in use: UsedBy &gt; 0, plus any material named on a placed type
        /// (in case the reader could not count uses). With the categories that use each.
        /// Each element counts once, under its own category: the reader puts a curtain
        /// wall's panel and mullion materials on its Walls type too (so A7.1 sees the wall
        /// has a material), but the panels and mullions are placed elements of Curtain
        /// Panels / Curtain Wall Mullions and are counted there — not again under Walls
        /// (real model, 16 Nov 2026: Glass 54 with "Walls" where the model has 53).
        /// </summary>
        internal static List<UsedMaterial> UsedMaterials(SpecModel m)
        {
            var d = new Dictionary<string, UsedMaterial>(StringComparer.OrdinalIgnoreCase);
            var order = new List<UsedMaterial>();
            UsedMaterial Get(string name)
            {
                var key = name.Trim();
                if (!d.TryGetValue(key, out var u)) { d[key] = u = new UsedMaterial { Name = key }; order.Add(u); }
                return u;
            }
            foreach (var x in m.Materials.Where(x => x.UsedBy > 0 && x.Name.Trim().Length > 0)) Get(x.Name).Count = x.UsedBy;
            var countedByUse = new HashSet<string>(order.Select(u => u.Name), StringComparer.OrdinalIgnoreCase);
            var partMaterials = new HashSet<string>(
                m.Types.Where(t => K.IsCat(t, "Curtain Panels") || K.IsCat(t, "Curtain Wall Mullions"))
                       .SelectMany(t => t.Materials).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);
            var fromTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var twice = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in m.Types)
            {
                var curtain = IsCurtainWall(t);
                foreach (var name in t.Materials.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (curtain && partMaterials.Contains(name))
                    {
                        // UsedBy counted this wall's instances for the material: take them back out.
                        twice[name] = (twice.TryGetValue(name, out var w) ? w : 0) + t.InstanceCount;
                        continue;
                    }
                    var u = Get(name);
                    var c = K.Or(t.Category);
                    if (!u.Categories.Contains(c)) u.Categories.Add(c);
                    fromTypes[name] = (fromTypes.TryGetValue(name, out var n) ? n : 0) + K.Count(t);
                }
            }
            foreach (var u in order)
            {
                if (countedByUse.Contains(u.Name) && twice.TryGetValue(u.Name, out var w)) u.Count = Math.Max(0, u.Count - w);
                if (u.Count == 0 && fromTypes.TryGetValue(u.Name, out var n)) u.Count = n;
            }
            return order;
        }

        internal sealed class SplitInfo
        {
            public SpecModelCode Code;
            public List<(SpecLink Link, SpecModelCode Code)> Parts = new List<(SpecLink, SpecModelCode)>();
        }

        /// <summary>The file's model code and the links that are parts of the same model
        /// (a split code, same jkrXX discipline when both names carry one). Null when the
        /// file is unsaved or the code is unreadable (A2.2 / A3.1 report that).</summary>
        internal static SplitInfo Split(SpecModel m)
        {
            var name = SpecA.StripExtension(m.FileName);
            if (name.Length == 0) return null;
            var code = SpecA.ParseModelCode(name);
            if (code == null) return null;
            var s = new SplitInfo { Code = code };
            var disc = SpecA.Discipline(name);
            foreach (var l in m.Links)
            {
                var ln = SpecA.StripExtension(l.Name);
                var lc = SpecA.ParseModelCode(ln);
                if (lc == null || lc.IsSingle || lc.Number == code.Number) continue;
                var ld = SpecA.Discipline(ln);
                if (disc.Length > 0 && ld.Length > 0 && disc != ld) continue;
                s.Parts.Add((l, lc));
            }
            return s;
        }
    }

    /// <summary>Kind of a gridline name (A5.2 / A5.3).</summary>
    public enum SpecGridKind { Number, Letter, NumberSub, LetterSub, Other }

    /// <summary>A parsed gridline name.</summary>
    public sealed class SpecGridLabel
    {
        public SpecGridKind Kind = SpecGridKind.Other;
        /// <summary>The name without prefix, upper case ("1", "AA", "1A", "B2").</summary>
        public string Core = "";
        /// <summary>Number / letter index of a main grid (A = 1, Z = 26, AA = 27).</summary>
        public int Main;
        /// <summary>Sub-grid: the parent's name ("1", "B") and index.</summary>
        public string Parent = "";
        public int ParentIndex;
        /// <summary>Sub-grid: its own index (1A → 1 (A), B2 → 2).</summary>
        public int Sub;
        /// <summary>The project prefix shared by most of the model's grids ("PT" in PT-A, PT1,
        /// PT4A), stripped before the kind is read; "" when the grid has none.</summary>
        public string Prefix = "";
        /// <summary>The prefix as written, with its separator ("PT-" for PT-A, "PT" for PT1).</summary>
        public string PrefixText = "";
        /// <summary>The name as shown: prefix + core ("PT-A1", "PT4A"; "1A" with no prefix).</summary>
        public string Display => PrefixText + Core;
        /// <summary>Sub-grid: the parent grid's name as shown ("PT-A", "PT4"; "1" with no prefix).</summary>
        public string ParentDisplay => PrefixText + Parent;
    }

    /// <summary>A model code from a file name (A2.2): W00 single, WXnn split.</summary>
    public sealed class SpecModelCode
    {
        /// <summary>The text found ("w-01", "WX02", "W00").</summary>
        public string Raw = "";
        public int Number;
        public bool IsSingle => Number == 0;
        /// <summary>"W00" or "WX01".</summary>
        public string Canonical => IsSingle ? "W00" : SpecA.SplitCode(Number);
    }

    /// <summary>
    /// The small pure parsers behind section A (file name, model code, grid names,
    /// door / roof / fence / material words), public so they are tested on their own.
    /// </summary>
    public static class SpecA
    {
        /// <summary>A1.6: the NS clauses the spec's "Klausa NS" sheet marks "Tidak — semak manual".</summary>
        public static readonly IReadOnlyList<(string Clause, string Rule, string RuleEn)> ManualNsClauses = new[]
        {
            ("lain-lain", "Sustainability, IBS, landskap, konsep reka bentuk", "Sustainability, IBS, landscape, design concept"),
        };

        // ── file name / model code (A2) ──

        public static string StripExtension(string fileName)
        {
            var s = (fileName ?? "").Trim();
            var m = Regex.Match(s, @"\.(rvt|rfa|rte|rft)$", RegexOptions.IgnoreCase);
            return m.Success ? s.Substring(0, m.Index).Trim() : s;
        }

        /// <summary>Segments of a file name: split on '_' outside brackets; with fewer than
        /// three, split on '-' too (the F A.2 rule). Empty segments dropped.</summary>
        public static List<string> FileSegments(string name)
        {
            var s = StripExtension(name);
            var segs = SplitTop(s, '_');
            if (segs.Count < 3) segs = SplitTop(s, '_', '-');
            return segs;
        }

        private static List<string> SplitTop(string s, params char[] seps)
        {
            var list = new List<string>();
            var depth = 0;
            var cur = new System.Text.StringBuilder();
            foreach (var ch in s ?? "")
            {
                if (ch == '(' || ch == '[') depth++;
                else if ((ch == ')' || ch == ']') && depth > 0) depth--;
                if (depth == 0 && Array.IndexOf(seps, ch) >= 0)
                {
                    if (cur.ToString().Trim().Length > 0) list.Add(cur.ToString().Trim());
                    cur.Clear();
                    continue;
                }
                cur.Append(ch);
            }
            if (cur.ToString().Trim().Length > 0) list.Add(cur.ToString().Trim());
            return list;
        }

        private static readonly Regex CodeRx = new Regex(@"(?<![A-Za-z0-9])W(X)?[-\s]?(\d{2,3})(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The model code in a file name: "W00" / "w-00" → single; "w-01" / "WX01" /
        /// "W02" → split part nn. Bracketed groups are ignored. Null when none is found.</summary>
        public static SpecModelCode ParseModelCode(string name)
        {
            var s = Regex.Replace(StripExtension(name), @"\([^)]*\)", " ");
            var m = CodeRx.Match(s);
            if (!m.Success) return null;
            return new SpecModelCode { Raw = m.Value, Number = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) };
        }

        public static string SplitCode(int n) => "WX" + n.ToString("00", CultureInfo.InvariantCulture);

        /// <summary>"AR" from "jkrAR24_…"; "" when the name has no jkrXX prefix.</summary>
        public static string Discipline(string name)
        {
            var m = Regex.Match((name ?? "").Trim(), @"^jkr([A-Za-z]{2})", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }

        // ── grid names (A5) ──

        private static readonly Regex GridPrefix = new Regex(@"^(?:GRID|GL)[\s\-_.:]*(?=\S)|^G[\s\-_.:]+(?=\S)", RegexOptions.CultureInvariant);
        private static readonly Regex NumRx = new Regex(@"^0*(\d{1,4})$", RegexOptions.CultureInvariant);
        private static readonly Regex LetRx = new Regex(@"^[A-Z]{1,2}$", RegexOptions.CultureInvariant);
        private static readonly Regex NumSubRx = new Regex(@"^0*(\d{1,4})[\-_.\s]?([A-Z])$", RegexOptions.CultureInvariant);
        private static readonly Regex LetSubRx = new Regex(@"^([A-Z]{1,2})[\-_.\s]?0*(\d{1,3})$", RegexOptions.CultureInvariant);

        // A project prefix: two to six letters, then "-" or straight into a number ("PT-A", "PT1").
        private static readonly Regex ProjectPrefixRx = new Regex(@"^([A-Z]{2,6})(?:-(?=[A-Z0-9])|(?=\d))", RegexOptions.CultureInvariant);

        private static string CleanGridName(string name) => GridPrefix.Replace((name ?? "").Trim().ToUpperInvariant(), "").Trim();

        /// <summary>
        /// The project prefix of a model's grids, or "": letters (two or more) followed by
        /// "-" or directly by a number, shared by more than half of the named grids, with a
        /// readable grid name after it ("PT" for PT-A, PT-A1, PT1, PT4A … beside 1, 2, B,
        /// K3; real model, 16 Nov 2026). A single letter is never a prefix: "A1" / "B-2"
        /// stay sub-grids.
        /// </summary>
        public static string ProjectGridPrefix(IEnumerable<string> names)
        {
            var list = (names ?? Enumerable.Empty<string>()).Select(CleanGridName).Where(x => x.Length > 0).ToList();
            if (list.Count < 2) return "";
            var best = list.Select(x => (Name: x, M: ProjectPrefixRx.Match(x)))
                           .Where(x => x.M.Success && ParseCore("", "", x.Name.Substring(x.M.Length)).Kind != SpecGridKind.Other)
                           .GroupBy(x => x.M.Groups[1].Value, StringComparer.Ordinal)
                           .Select(g => (Prefix: g.Key, Count: g.Count()))
                           .OrderByDescending(x => x.Count).ThenBy(x => x.Prefix, StringComparer.Ordinal)
                           .FirstOrDefault();
            return best.Prefix != null && best.Count >= 2 && best.Count * 2 > list.Count ? best.Prefix : "";
        }

        /// <summary>Parse every grid name of a model, with the project prefix
        /// (<see cref="ProjectGridPrefix"/>) stripped where it is found. A4.1 and A5.1–A5.3
        /// read grids through this, so they agree on which grid is which.</summary>
        public static List<SpecGridLabel> ParseGrids(IEnumerable<string> names)
        {
            var list = (names ?? Enumerable.Empty<string>()).ToList();
            var prefix = ProjectGridPrefix(list);
            return list.Select(n => ParseGrid(n, prefix)).ToList();
        }

        /// <summary>
        /// Parse a gridline name. A "GRID" / "GL" prefix (or "G-") is dropped: "GL-1" = 1.
        /// "12" → number 12; "B" / "AA" → letter; "1A" / "1-A" → sub-grid A of number 1;
        /// "A1" / "B-2" → sub-grid of letter A / B. Anything else (primes, decimals,
        /// free text) is Other and stays out of the sequence. With a project
        /// <paramref name="prefix"/> ("PT"), a name that starts with it ("PT-A1", "PT4A") is
        /// read without it (A1 = sub-grid of letter A; 4A = sub-grid of number 4) and keeps
        /// the prefix in <see cref="SpecGridLabel.Prefix"/>.
        /// </summary>
        public static SpecGridLabel ParseGrid(string name, string prefix = "")
        {
            var s = CleanGridName(name);
            if (!string.IsNullOrEmpty(prefix))
            {
                var pm = ProjectPrefixRx.Match(s);
                if (pm.Success && pm.Groups[1].Value == prefix)
                    return ParseCore(prefix, pm.Value, s.Substring(pm.Length));
            }
            return ParseCore("", "", s);
        }

        private static SpecGridLabel ParseCore(string prefix, string prefixText, string s)
        {
            var g = new SpecGridLabel { Core = s, Prefix = prefix, PrefixText = prefixText };
            if (s.Length == 0) return g;
            Match m;
            if ((m = NumRx.Match(s)).Success)
            {
                g.Kind = SpecGridKind.Number; g.Main = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); g.Core = g.Main.ToString(CultureInfo.InvariantCulture);
            }
            else if (LetRx.IsMatch(s))
            {
                g.Kind = SpecGridKind.Letter; g.Main = LetterIndex(s);
            }
            else if ((m = NumSubRx.Match(s)).Success)
            {
                g.Kind = SpecGridKind.NumberSub; g.ParentIndex = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                g.Parent = g.ParentIndex.ToString(CultureInfo.InvariantCulture); g.Sub = LetterIndex(m.Groups[2].Value); g.Core = g.Parent + m.Groups[2].Value;
            }
            else if ((m = LetSubRx.Match(s)).Success)
            {
                g.Kind = SpecGridKind.LetterSub; g.Parent = m.Groups[1].Value; g.ParentIndex = LetterIndex(g.Parent);
                g.Sub = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); g.Core = g.Parent + g.Sub.ToString(CultureInfo.InvariantCulture);
            }
            return g;
        }

        /// <summary>A = 1 … Z = 26, AA = 27 … AZ = 52, BA = 53 (spreadsheet columns). 0 for non-letters.</summary>
        public static int LetterIndex(string letters)
        {
            var s = (letters ?? "").Trim().ToUpperInvariant();
            if (s.Length == 0) return 0;
            var n = 0;
            foreach (var ch in s)
            {
                if (ch < 'A' || ch > 'Z') return 0;
                n = n * 26 + (ch - 'A' + 1);
            }
            return n;
        }

        public static string LetterName(int index)
        {
            if (index <= 0) return "";
            var s = "";
            while (index > 0)
            {
                index--;
                s = (char)('A' + index % 26) + s;
                index /= 26;
            }
            return s;
        }

        /// <summary>Letters conventionally left out of a grid sequence (they read as 1 and 0).</summary>
        public static bool IsSkippableLetter(int index)
        {
            var n = LetterName(index);
            return n.IndexOf('I') >= 0 || n.IndexOf('O') >= 0;
        }

        /// <summary>Jumps in a number sequence, any input order: 1,2,3,11 → (3, 11).</summary>
        public static List<(int From, int To)> NumberGaps(IEnumerable<int> values)
        {
            var v = (values ?? Enumerable.Empty<int>()).Distinct().OrderBy(x => x).ToList();
            var gaps = new List<(int, int)>();
            for (var i = 1; i < v.Count; i++)
                if (v[i] - v[i - 1] > 1) gaps.Add((v[i - 1], v[i]));
            return gaps;
        }

        /// <summary>Jumps in a letter sequence (indices), I and O not counted as missing:
        /// A,B,D → (B, D, [C]); H,J → none; Z,AA → none.</summary>
        public static List<(int From, int To, List<string> Missing)> LetterGaps(IEnumerable<int> indices)
        {
            var v = (indices ?? Enumerable.Empty<int>()).Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
            var gaps = new List<(int, int, List<string>)>();
            for (var i = 1; i < v.Count; i++)
            {
                var missing = new List<string>();
                for (var k = v[i - 1] + 1; k < v[i] && missing.Count < 50; k++)
                    if (!IsSkippableLetter(k)) missing.Add(LetterName(k));
                if (missing.Count > 0) gaps.Add((v[i - 1], v[i], missing));
            }
            return gaps;
        }

        /// <summary>"4–10" / "4" for the numbers missing between two.</summary>
        public static string MissingNumbers(int from, int to)
        {
            if (to - from == 2) return (from + 1).ToString(CultureInfo.InvariantCulture);
            return (from + 1).ToString(CultureInfo.InvariantCulture) + "–" + (to - 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The up-to-three values before a jump: "1,2,3" (with "…," when more precede).</summary>
        public static string RunBefore(IEnumerable<int> values, int from, Func<int, string> name)
        {
            var v = values.Distinct().Where(x => x <= from).OrderBy(x => x).ToList();
            var tail = v.Skip(Math.Max(0, v.Count - 3)).Select(name);
            return (v.Count > 3 ? "…," : "") + string.Join(",", tail);
        }

        /// <summary>"1–8" / "A–F" for a gap-free sequence; "" for none.</summary>
        public static string SequenceText(IEnumerable<int> values, Func<int, string> name)
        {
            var v = values.Distinct().OrderBy(x => x).ToList();
            if (v.Count == 0) return "—";
            return v.Count == 1 ? name(v[0]) : name(v[0]) + "–" + name(v[v.Count - 1]);
        }

        /// <summary>
        /// Plan order: when every grid of a sequence runs the same way and their offsets
        /// are distinct, the indices sorted by offset must rise or fall throughout.
        /// Returns the indices in plan order when they do not, else null (in order, or
        /// not testable).
        /// </summary>
        public static List<int> PlanOrderBroken(IList<(int Index, double Offset, bool Vertical)> grids)
        {
            if (grids == null || grids.Count < 3) return null;
            if (grids.Select(g => g.Vertical).Distinct().Count() > 1) return null;
            var sorted = grids.OrderBy(g => g.Offset).ToList();
            for (var i = 1; i < sorted.Count; i++)
                if (Math.Abs(sorted[i].Offset - sorted[i - 1].Offset) < 1.0) return null;
            var idx = sorted.Select(g => g.Index).ToList();
            bool up = true, down = true;
            for (var i = 1; i < idx.Count; i++)
            {
                if (idx[i] <= idx[i - 1]) up = false;
                if (idx[i] >= idx[i - 1]) down = false;
            }
            return up || down ? null : idx;
        }

        /// <summary>A sub-grid stands between its parent and the next main grid on plan
        /// (true when the three do not run the same way, so it cannot be tested).</summary>
        public static bool WithinBay(SpecGrid sub, SpecGrid parent, SpecGrid next)
        {
            if (sub == null || parent == null || next == null) return true;
            if (sub.IsVertical != parent.IsVertical || parent.IsVertical != next.IsVertical) return true;
            if (Math.Abs(parent.Offset - next.Offset) < 1.0) return true;
            var lo = Math.Min(parent.Offset, next.Offset) - 1.0;
            var hi = Math.Max(parent.Offset, next.Offset) + 1.0;
            return sub.Offset > lo && sub.Offset < hi;
        }

        /// <summary>
        /// A4.1: the grid pairs that stand for "Grid A/1", as (index of A, index of 1) into
        /// <paramref name="labels"/> (from <see cref="ParseGrids"/>): a grid named plain A with a grid
        /// named plain 1, no project prefix. AI-504 (spec #12): a prefixed grid (PT-A, PT1) is not
        /// "Grid A/1" in any JKR document, so it is never a pair here; see
        /// <see cref="PrefixedBaseGridPairs"/>. Empty when the model has no plain A or no plain 1.
        /// </summary>
        public static List<(int A, int One)> BaseGridPairs(IList<SpecGridLabel> labels)
        {
            var pairs = new List<(int, int)>();
            if (labels == null) return pairs;
            for (var i = 0; i < labels.Count; i++)
            {
                if (labels[i].Kind != SpecGridKind.Letter || labels[i].Main != 1 || labels[i].Prefix.Length > 0) continue;
                for (var j = 0; j < labels.Count; j++)
                    if (labels[j].Kind == SpecGridKind.Number && labels[j].Main == 1 && labels[j].Prefix.Length == 0) pairs.Add((i, j));
            }
            return pairs;
        }

        /// <summary>A4.1: the A × 1 pairs that exist only with a project prefix (PT-A × PT1, A × PT1 …):
        /// listed for a person, never measured. Empty when a plain pair exists or there is no A or 1.</summary>
        public static List<(int A, int One)> PrefixedBaseGridPairs(IList<SpecGridLabel> labels)
        {
            var pairs = new List<(int, int)>();
            if (labels == null || BaseGridPairs(labels).Count > 0) return pairs;
            for (var i = 0; i < labels.Count; i++)
            {
                if (labels[i].Kind != SpecGridKind.Letter || labels[i].Main != 1) continue;
                for (var j = 0; j < labels.Count; j++)
                    if (labels[j].Kind == SpecGridKind.Number && labels[j].Main == 1) pairs.Add((i, j));
            }
            return pairs.OrderBy(p => labels[p.Item1].Prefix == labels[p.Item2].Prefix ? 0 : 1).ToList();
        }

        /// <summary>A4.1: where two straight grids cross on plan (lines extended), or null
        /// when either has no line or they are parallel.</summary>
        public static SpecPoint IntersectXy(SpecGrid a, SpecGrid b)
        {
            if (a == null || b == null || !a.HasLine || !b.HasLine) return null;
            double dax = a.X1 - a.X0, day = a.Y1 - a.Y0, dbx = b.X1 - b.X0, dby = b.Y1 - b.Y0;
            var den = dax * dby - day * dbx;
            var la = Math.Sqrt(dax * dax + day * day);
            var lb = Math.Sqrt(dbx * dbx + dby * dby);
            if (la < 1e-9 || lb < 1e-9 || Math.Abs(den) < 1e-6 * la * lb) return null;
            var t = ((b.X0 - a.X0) * dby - (b.Y0 - a.Y0) * dbx) / den;
            return new SpecPoint { X = a.X0 + t * dax, Y = a.Y0 + t * day };
        }

        // ── words (A1, A6, A7) ──

        private static readonly Regex BasementRx = new Regex(@"basement|bawah\s*tanah|(?<![A-Za-z0-9])B\d{1,2}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A1.1: a basement level — elevation below 0, or named basement / bawah tanah / B1.</summary>
        public static bool IsBasement(string levelName, SpecLevel level)
        {
            if (level != null && level.Elevation < 0) return true;
            return BasementRx.IsMatch(levelName ?? "");
        }

        private static readonly Regex DoubleRx = new Regex(
            @"double|dbl|(?<![0-9])2\s*-?\s*(leaf|leaves|daun|panel)|\bdua\b|(?<![0-9])2p(?![a-z])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DoubleNotLeafRx = new Regex(
            @"double[\s\-_]*(swing|acting|action|glaz|rebate|hung)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// A1.2: a double-leaf door, read from its family + type name: "double", "dbl",
        /// "2 leaf" / "2-leaf" / "2 daun" / "2 panel", "dua" (dua daun), or a JKR code
        /// with "2p" ("(PT2p600a)": two panels; "PT1p" is single). "Double swing / acting /
        /// glazed / rebate / hung" describe the swing or glass, not two leaves, and do not
        /// count. Everything else is single leaf (the stricter 900 mm then applies only to
        /// doors under 1500 mm, so a missed double can only fail at 900–1499 mm).
        /// </summary>
        public static bool IsDoubleLeaf(string family, string type)
        {
            var s = ((family ?? "") + " " + (type ?? "")).Trim();
            if (s.Length == 0) return false;
            var cleaned = DoubleNotLeafRx.Replace(s, " ");
            return DoubleRx.IsMatch(cleaned);
        }

        private static readonly Regex MainRx = new Regex(@"utama|entrance|masuk|(?<![a-z])main(?!t)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A1.3: the main door — "utama", "main", "entrance", "masuk" in the family,
        /// type or a text parameter ("maintenance" / "domain" do not count).</summary>
        public static bool IsMainDoor(SpecType t)
        {
            if (t == null) return false;
            var s = (t.FamilyName ?? "") + " " + (t.TypeName ?? "") + " " + string.Join(" ", (t.Texts ?? new Dictionary<string, string>()).Values.Where(v => v != null));
            return MainRx.IsMatch(s);
        }

        private static readonly Regex ConcreteRx = new Regex(@"concrete|konkrit|tetulang|reinforced|(?<![a-z])rc(?![a-z])|\(kt\d*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex WaterproofRx = new Regex(@"water\s*-?\s*proof|membrane|membran|kalis\s*air|bitum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A1.4: a concrete / reinforced-concrete name or material (JKR code "(kt…)" = konkrit tetulang, PB).</summary>
        public static bool IsConcrete(string name) => ConcreteRx.IsMatch(name ?? "");

        /// <summary>A1.4: a waterproofing name or material (waterproof, membrane, kalis air, bitumen).</summary>
        private static readonly Regex VerticalRx = new Regex(@"\b(tangga|stair\w*|lif|lift|elevator|eskalator|escalator)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A6.2: a stair / lift room, which rises through storeys.</summary>
        public static bool IsVerticalCirculation(string roomName) => VerticalRx.IsMatch(roomName ?? "");

        public static bool IsWaterproofing(string name) => WaterproofRx.IsMatch(name ?? "") && !DampProofRx.IsMatch(name ?? "");

        // A damp-proof membrane (DPM) under a ground slab is not roof waterproofing (real Dewan
        // model, 16 Nov: "jkrAR_flr-b_(LTdp01)-3 Damp Proof Membrane" in 2 floor types made
        // A1.4 say the model had a separate waterproofing element).
        private static readonly Regex DampProofRx = new Regex(@"damp\s*-?\s*proof|\bDPM\b|kalis\s*lembap", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex FenceRx = new Regex(@"pagar|fence|fencing", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex HandrailRx = new Regex(@"tangga|stair|balkoni|balcony|susur|handrail|koridor|corridor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DecorativeRx = new Regex(@"hiasan|decorative|ornamental", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A1.5: a fence by name (pagar / fence), not a stair / balcony / corridor handrail.</summary>
        public static bool IsFence(string name) => FenceRx.IsMatch(name ?? "") && !HandrailRx.IsMatch(name ?? "");

        public static bool IsDecorative(string name) => DecorativeRx.IsMatch(name ?? "");

        /// <summary>A6.2: lif / tangga / apron from a floor type name, else "". Apron also from
        /// the JKR space code "APR" ("jkrAR_flr_LFp_ls01_30_APR_ex_(LFp601a)-3", Dewan model).</summary>
        public static string SpaceKind(string typeName)
        {
            var s = typeName ?? "";
            if (Regex.IsMatch(s, @"(?<![a-z])(lif|lift)(?![a-z])", RegexOptions.IgnoreCase)) return "lif";
            if (Regex.IsMatch(s, @"tangga|stair", RegexOptions.IgnoreCase)) return "tangga";
            if (Regex.IsMatch(s, @"apron", RegexOptions.IgnoreCase) || Regex.IsMatch(s, @"(?<![A-Za-z])APR(?![A-Za-z])")) return "apron";
            return "";
        }

        /// <summary>A7.1: categories with no material to have (non-physical, or rendered content).</summary>
        public static readonly HashSet<string> NoMaterialCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "", "Rooms", "Areas", "Spaces", "Lines", "Detail Items", "Grids", "Levels", "Reference Planes", "Scope Boxes",
            "Cameras", "Views", "RVT Links", "Model Groups", "Detail Groups", "Shaft Openings", "Openings", "Curtain Grids",
            "Curtain Wall Grids", "Mass", "Planting", "Entourage", "Matchline", "Generic Annotations", "Raster Images",
            "Point Clouds", "Project Base Point", "Survey Point", "Imports in Families", "Analytical Nodes", "Room Separation Lines",
            "Area Boundary", "Space Separation", "Sun Path",
        };

        // "jkrAR_" and the material-library form "jkrARm_" (real Dewan model, 16 Nov:
        // "jkrARm_clg_(mt3) Aluminium Ceiling Carrier" was warned as not JKR).
        private static readonly Regex JkrMaterialRx = new Regex(@"^jkr[A-Za-z]{2}m?_.*\([A-Za-z0-9][A-Za-z0-9-]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A2.1: the file name opens with the JKR discipline + version block ("jkrAR24").</summary>
        internal static readonly Regex FileLead = new Regex(@"^jkr[A-Z]{2}\d{2}", RegexOptions.CultureInvariant);

        /// <summary>
        /// A7.2: JKR material naming (PB p.35, Category: Material Identity — "Name" =
        /// "jkrAR_dor-b_(ab)-2 Aluminium Bronzed Anodised"; also "jkrAR_(kt)- Konkrit
        /// tetulang", "jkrAR_wll-b_(bb02)-3 Batu Bata"): the "jkr" + two-letter discipline
        /// prefix and "_", and a bracketed material code.
        /// </summary>
        public static bool IsJkrMaterialName(string name) => JkrMaterialRx.IsMatch((name ?? "").Trim());

        private static readonly string[] RevitDefaultStems =
        {
            "Default", "Generic", "Glass", "Concrete", "Metal", "Steel", "Aluminum", "Aluminium", "Wood", "Masonry", "Brick",
            "Paint", "Plaster", "Plasterboard", "Gypsum", "Site", "Earth", "Grass", "Water", "Air", "Analytical", "Render",
            "Poche", "Ceramic", "Stone", "Carpet", "Laminate", "Plastic", "Fabric", "Leather", "Acoustic", "Asphalt",
            "Insulation", "Rigid insulation", "Vapor Retarder", "Damp-proofing", "Mirror", "Tile", "Iron", "Copper", "Vinyl",
            "Oak", "Cherry", "Maple", "Walnut", "Birch", "Cedar", "Pine", "Sand", "Gravel", "Mortar", "Cast Iron",
        };

        /// <summary>A7.2: a Revit template default name — "Default", "Generic", or a stock stem
        /// ("Glass", "Concrete - Cast-in-Place Concrete", "Metal - Steel", "Wood - Flooring").</summary>
        public static bool IsRevitDefaultMaterial(string name)
        {
            var s = (name ?? "").Trim();
            if (s.Length == 0) return false;
            foreach (var stem in RevitDefaultStems)
            {
                if (!s.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Length == stem.Length) return true;
                if (!char.IsLetter(s[stem.Length])) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Plan geometry for A1.1 and A6.2, in mm: point in polygon (even-odd over all loops, so
    /// holes and several faces work), sample points inside a footprint, area. Pure.
    /// </summary>
    internal static class SpecPlan
    {
        /// <summary>An item with its loops and plan bounds (the bounds only skip far-off tests).</summary>
        internal sealed class Shape<T>
        {
            public readonly T Item;
            public readonly List<List<SpecPoint>> Loops;
            public readonly double MinX, MinY, MaxX, MaxY;

            public Shape(T item, List<List<SpecPoint>> loops)
            {
                Item = item;
                Loops = loops ?? new List<List<SpecPoint>>();
                (MinX, MinY, MaxX, MaxY) = Bounds(Loops);
            }

            /// <summary>The plan bounds of the two overlap (a quick reject before point tests).</summary>
            public bool Overlaps<TOther>(Shape<TOther> o) =>
                MinX <= o.MaxX && o.MinX <= MaxX && MinY <= o.MaxY && o.MinY <= MaxY;

            public bool Contains(double x, double y) =>
                x >= MinX && x <= MaxX && y >= MinY && y <= MaxY && Inside(Loops, x, y);
        }

        internal static List<List<SpecPoint>> Rect(double x0, double y0, double x1, double y1) => new List<List<SpecPoint>>
        {
            new List<SpecPoint>
            {
                new SpecPoint { X = x0, Y = y0 }, new SpecPoint { X = x1, Y = y0 },
                new SpecPoint { X = x1, Y = y1 }, new SpecPoint { X = x0, Y = y1 },
            },
        };

        internal static (double MinX, double MinY, double MaxX, double MaxY) Bounds(List<List<SpecPoint>> loops)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var l in loops)
                foreach (var p in l)
                {
                    if (p.X < x0) x0 = p.X;
                    if (p.Y < y0) y0 = p.Y;
                    if (p.X > x1) x1 = p.X;
                    if (p.Y > y1) y1 = p.Y;
                }
            return x0 > x1 ? (0, 0, 0, 0) : (x0, y0, x1, y1);
        }

        /// <summary>The point is inside an odd number of the loops (ray casting).</summary>
        internal static bool Inside(List<List<SpecPoint>> loops, double x, double y)
        {
            var inside = false;
            foreach (var l in loops)
            {
                var n = l.Count;
                if (n < 3) continue;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    var a = l[i];
                    var b = l[j];
                    if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>Area inside the loops (mm²): each loop's area, added at an even nesting depth
        /// and taken away at an odd one (a hole), whatever the loops' direction.</summary>
        internal static double Area(List<List<SpecPoint>> loops)
        {
            double total = 0;
            for (var i = 0; i < loops.Count; i++)
            {
                var l = loops[i];
                if (l.Count < 3) continue;
                double s = 0;
                for (int a = 0, b = l.Count - 1; a < l.Count; b = a++) s += (l[b].X + l[a].X) * (l[b].Y - l[a].Y);
                var depth = 0;
                for (var j = 0; j < loops.Count; j++)
                    if (j != i && Inside(new List<List<SpecPoint>> { loops[j] }, l[0].X, l[0].Y)) depth++;
                total += (depth % 2 == 0 ? 1 : -1) * Math.Abs(s) / 2;
            }
            return Math.Max(0, total);
        }

        /// <summary>
        /// About <paramref name="target"/> points spread evenly inside the loops: the centres
        /// of a grid over the bounds, kept when inside. A thin or holed shape that keeps too
        /// few gets a finer grid (4×, then 16×), thinned back to at most 2 × target. Empty
        /// only for a shape with no area.
        /// </summary>
        internal static List<SpecPoint> Samples(List<List<SpecPoint>> loops, int target)
        {
            var result = new List<SpecPoint>();
            if (loops == null || loops.Count == 0 || target <= 0) return result;
            var (x0, y0, x1, y1) = Bounds(loops);
            double w = x1 - x0, h = y1 - y0;
            if (!(w > 0) || !(h > 0)) return result;
            foreach (var mult in new[] { 1, 4, 16 })
            {
                var n = (double)target * mult;
                var nx = Math.Min(512, Math.Max(1, (int)Math.Round(Math.Sqrt(n * w / h))));
                var ny = Math.Min(512, Math.Max(1, (int)Math.Ceiling(n / nx)));
                result = new List<SpecPoint>();
                for (var i = 0; i < nx; i++)
                    for (var j = 0; j < ny; j++)
                    {
                        var x = x0 + (i + 0.5) * w / nx;
                        var y = y0 + (j + 0.5) * h / ny;
                        if (Inside(loops, x, y)) result.Add(new SpecPoint { X = x, Y = y });
                    }
                if (result.Count >= Math.Max(1, target / 4)) break;
            }
            if (result.Count > 2 * target)
            {
                var step = (int)Math.Ceiling(result.Count / (2.0 * target));
                result = result.Where((_, i) => i % step == 0).ToList();
            }
            return result;
        }
    }
}
