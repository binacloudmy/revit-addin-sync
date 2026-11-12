using Autodesk.Revit.DB;
using RevitWebAppSync.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// TEST-ONLY (see <see cref="JkrDevMode"/>): the Revit side of the JKR fixture.
    /// Read-only — never writes to the model. Run inside a RevitDispatcher job.
    ///  - <see cref="Scan"/> reads each fixture entry's live value and returns the
    ///    non-compliant ones in the backend's response shape, so the pane maps them
    ///    through the normal IssueMapper path.
    ///  - <see cref="Generate"/> builds a fixture from the open model with real
    ///    element IDs: fixes that will succeed (type renames, a level rename,
    ///    instance and type parameter writes) and fixes that will fail (read-only
    ///    parameter, duplicate type name, missing element, model-group member).
    /// </summary>
    public static class JkrFixtureScanner
    {
        public const long MissingElementId = 999999999;

        // ── Scan ──

        public static ModelCheckResponse Scan(Document doc, JkrFixture fixture)
        {
            var findings = JkrFixtureFile.Evaluate(fixture, i => Read(doc, i));
            return new ModelCheckResponse { ElementIssues = findings.Select(ToDto).ToList() };
        }

        /// <summary>The live value an entry checks: the type's (or Level/Grid's) name
        /// for a rename, the parameter's value for a parameter write.</summary>
        public static JkrFixtureObservation Read(Document doc, JkrFixtureIssue issue)
        {
            var elem = doc.GetElement(ElemIds.From(issue.ElementId));
            if (elem == null) return JkrFixtureObservation.Missing;

            if (issue.IsRename)
            {
                if (elem is Grid || elem is Level || elem is ElementType)
                    return new JkrFixtureObservation(true, elem.Name);
                var type = doc.GetElement(elem.GetTypeId()) as ElementType;
                return new JkrFixtureObservation(true, type?.Name ?? elem.Name);
            }

            var param = ResolveLikeApplicator(doc, elem, issue.FixParameter, issue.FixTarget);
            return new JkrFixtureObservation(true, param == null ? "(no parameter)" : ValueOf(param));
        }

        /// <summary>Same preference as JkrFixApplicator.ResolveParameter, so the scan
        /// reads the handle the fix writes.</summary>
        private static Parameter ResolveLikeApplicator(Document doc, Element elem, string name, string target)
        {
            var inst = elem.LookupParameter(name);
            var typeElem = elem is ElementType ? null : doc.GetElement(elem.GetTypeId());
            var typ = typeElem?.LookupParameter(name);
            bool preferType = string.Equals(target, "type", StringComparison.OrdinalIgnoreCase);
            if (preferType)
            {
                if (typ != null && !typ.IsReadOnly) return typ;
                if (inst != null && !inst.IsReadOnly) return inst;
                return typ ?? inst;
            }
            if (inst != null && !inst.IsReadOnly) return inst;
            if (typ != null && !typ.IsReadOnly) return typ;
            return inst ?? typ;
        }

        private static string ValueOf(Parameter p)
        {
            if (p.StorageType == StorageType.String) return p.AsString() ?? "";
            return p.AsValueString() ?? p.AsString() ?? "";
        }

        private static ComplianceIssueDto ToDto(JkrFixtureFinding f)
        {
            var i = f.Issue;
            if (i.IsCannotVerify)
            {
                return new ComplianceIssueDto
                {
                    IssueId = JkrComplianceService.ComputeIssueId(i.Category, i.Title, i.ElementId),
                    CheckId = "FIXTURE-" + i.Id,
                    Domain = "element",
                    ElementId = i.ElementId,
                    Category = i.Category,
                    TypeName = string.IsNullOrEmpty(i.RevitCategory) ? i.ElementName : $"{i.RevitCategory}: {i.ElementName}",
                    Status = "cannot_verify",
                    RevitCategory = i.RevitCategory ?? "",
                    Rule = i.Title,
                    Reason = i.Reason ?? "",
                    RequiredValue = i.Expected ?? "",
                    Locatable = i.ElementId > 0,
                    FixReference = "Test fixture " + i.Id,
                };
            }
            var found = f.Actual != JkrFixtureFile.NotFound;
            var reason = i.Reason ?? "";
            if (!string.IsNullOrEmpty(i.ExpectFailure))
                reason = (reason + " [Test fixture: this fix is expected to fail — " + i.ExpectFailure + "]").Trim();
            return new ComplianceIssueDto
            {
                IssueId = JkrComplianceService.ComputeIssueId(i.Category, i.Title, i.ElementId),
                CheckId = "FIXTURE-" + i.Id,
                Domain = "element",
                ElementId = i.ElementId,
                Category = i.Category,
                TypeName = string.IsNullOrEmpty(i.RevitCategory) ? i.ElementName : $"{i.RevitCategory}: {i.ElementName}",
                Status = "fail",
                RevitCategory = i.RevitCategory ?? "",
                Rule = i.Title,
                Reason = reason,
                RequiredValue = i.Expected,
                ActualValue = f.Actual,
                FixAction = i.IsRename ? "rename_type" : "set_parameter",
                FixParameterName = i.IsRename ? "" : i.FixParameter,
                FixValue = i.Expected,
                FixOldValue = found ? f.Actual : "",
                FixTarget = string.IsNullOrEmpty(i.FixTarget) ? "instance" : i.FixTarget,
                FixPriority = i.IsRename ? 1 : 10,
                Locatable = found,
                FixReference = "Test fixture " + i.Id,
            };
        }

        // ── Generate ──

        private static readonly BuiltInCategory[] RenameCategories =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows, BuiltInCategory.OST_Floors,
        };

        public static JkrFixture Generate(Document doc)
        {
            var fixture = new JkrFixture
            {
                Model = doc.Title ?? "",
                Generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            var issues = fixture.Issues;
            int n = 0;
            string NextId() => $"FX-{++n:00}";

            var usedTypeIds = new HashSet<long>();
            var allTypeNames = new HashSet<string>(
                new FilteredElementCollector(doc).WhereElementIsElementType().Select(t => t.Name ?? ""),
                StringComparer.OrdinalIgnoreCase);

            string UniqueName(string wanted)
            {
                var name = wanted;
                for (int k = 2; allTypeNames.Contains(name); k++) name = $"{wanted} ({k})";
                allTypeNames.Add(name);
                return name;
            }

            // Placed, ungrouped instances by category, ordered by id (deterministic).
            List<Element> Instances(BuiltInCategory bic) =>
                new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()
                    .Where(e => !InGroup(e) && e.GetTypeId() != ElementId.InvalidElementId)
                    .OrderBy(e => Id(e.Id)).ToList();

            // 1. Type renames that succeed: one per category, non-jkr type names.
            foreach (var bic in RenameCategories)
            {
                foreach (var inst in Instances(bic))
                {
                    var type = doc.GetElement(inst.GetTypeId()) as ElementType;
                    if (type == null || usedTypeIds.Contains(Id(type.Id))) continue;
                    if ((type.Name ?? "").StartsWith("jkr", StringComparison.OrdinalIgnoreCase)) continue;
                    var cat = inst.Category?.Name ?? bic.ToString();
                    usedTypeIds.Add(Id(type.Id));
                    issues.Add(new JkrFixtureIssue
                    {
                        Id = NextId(), Category = "Component Naming",
                        Title = "Type name does not follow JKR naming",
                        Reason = "Type names must follow jkr{Disc}_{prefix}-{subtype}_{description} (JKR Doc 03).",
                        RevitCategory = cat, ElementName = type.Name, ElementId = Id(inst.Id),
                        FixAction = "rename_type", FixTarget = "type",
                        Current = type.Name,
                        Expected = UniqueName(JkrRenameHandler.GenerateJkrName("AR", cat, type.Name)),
                    });
                    break;
                }
            }

            // 2. Level rename (High tier).
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).FirstOrDefault();
            if (level != null)
            {
                var levelNames = new HashSet<string>(
                    new FilteredElementCollector(doc).OfClass(typeof(Level)).Select(l => l.Name ?? ""),
                    StringComparer.OrdinalIgnoreCase);
                var wanted = level.Name + " (JKR)";
                for (int k = 2; levelNames.Contains(wanted); k++) wanted = $"{level.Name} (JKR {k})";
                issues.Add(new JkrFixtureIssue
                {
                    Id = NextId(), Category = "Levels", Title = "Level name does not follow JKR naming",
                    Reason = "Level names must follow the JKR convention.",
                    RevitCategory = "Levels", ElementName = level.Name, ElementId = Id(level.Id),
                    FixAction = "rename_type", FixTarget = "instance",
                    Current = level.Name, Expected = wanted,
                });
            }

            // 3. Large rename, off by default: the non-jkr MODEL type with the most
            //    instances (annotation/internal types such as dimension styles are
            //    not what JKR naming checks). Enable it to see the "Revit may pause"
            //    line (500+ affected elements).
            var biggest = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model
                            && e.GetTypeId() != ElementId.InvalidElementId)
                .GroupBy(e => Id(e.GetTypeId()))
                .Select(g => (TypeId: g.Key, Count: g.Count(), First: g.OrderBy(e => Id(e.Id)).First()))
                .Where(x => !usedTypeIds.Contains(x.TypeId))
                .Select(x => (x.TypeId, x.Count, x.First, Type: doc.GetElement(ElemIds.From(x.TypeId)) as ElementType))
                .Where(x => x.Type != null && !(x.Type.Name ?? "").StartsWith("jkr", StringComparison.OrdinalIgnoreCase)
                            && !InGroup(x.First))
                .OrderByDescending(x => x.Count).FirstOrDefault();
            if (biggest.Type != null)
            {
                usedTypeIds.Add(biggest.TypeId);
                var cat = biggest.First.Category?.Name ?? "";
                issues.Add(new JkrFixtureIssue
                {
                    Id = NextId(), Enabled = false, Category = "Component Naming",
                    Title = "Type name does not follow JKR naming (large type)",
                    Reason = $"This type has {biggest.Count} instances. Set \"enabled\": true to test the 500-element warning.",
                    RevitCategory = cat, ElementName = biggest.Type.Name, ElementId = Id(biggest.First.Id),
                    FixAction = "rename_type", FixTarget = "type",
                    Current = biggest.Type.Name,
                    Expected = UniqueName(JkrRenameHandler.GenerateJkrName("AR", cat, biggest.Type.Name)),
                });
            }

            // 4. Parameter writes that succeed: two instance Comments, two type-level
            //    Type Comments (one Low tier), and an empty JKR parameter if present.
            var hosts = RenameCategories.SelectMany(Instances).ToList();
            int comments = 0;
            foreach (var e in hosts)
            {
                if (comments == 2) break;
                var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p == null || p.IsReadOnly) continue;
                issues.Add(ParamIssue(NextId(), "Component Parameter", "Comments must be filled in", e, p.Definition.Name,
                    "instance", p.AsString() ?? "", $"JKR-TEST-{Id(e.Id)}"));
                comments++;
            }

            var typeCommentTypes = new HashSet<long>();
            int typeComments = 0;
            foreach (var e in hosts)
            {
                if (typeComments == 2) break;
                var type = doc.GetElement(e.GetTypeId()) as ElementType;
                if (type == null || !typeCommentTypes.Add(Id(type.Id))) continue;
                var p = type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
                if (p == null || p.IsReadOnly) continue;
                issues.Add(ParamIssue(NextId(), typeComments == 0 ? "Component Parameter" : "LOD 400/500 parameter",
                    "Type Comments must be filled in", e, p.Definition.Name, "type", p.AsString() ?? "",
                    $"JKR-TEST-T{Id(type.Id)}"));
                typeComments++;
            }

            foreach (var jkrName in new[] { "Kod_Jenis", "Sistem" })
            {
                var hit = hosts.Select(e => (e, Type: doc.GetElement(e.GetTypeId()) as ElementType))
                    .Select(x => (x.e, P: x.Type?.LookupParameter(jkrName)))
                    .FirstOrDefault(x => x.P != null && !x.P.IsReadOnly && x.P.StorageType == StorageType.String
                                         && string.IsNullOrEmpty(x.P.AsString()));
                if (hit.P == null) continue;
                issues.Add(ParamIssue(NextId(), "Component Parameter", $"{jkrName} must be filled in", hit.e, jkrName,
                    "type", "", "TEST"));
                break;
            }

            // 5. Fixes that will fail.
            //    a) Read-only parameter: a wall's computed Area.
            foreach (var wall in Instances(BuiltInCategory.OST_Walls))
            {
                var p = wall.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (p == null || !p.IsReadOnly) continue;
                var issue = ParamIssue(NextId(), "Component Parameter", "Area must match the room data sheet", wall,
                    p.Definition.Name, "instance", p.AsValueString() ?? "", "1");
                issue.ExpectFailure = "the parameter is read-only";
                issues.Add(issue);
                break;
            }

            //    b) Duplicate name: rename one type to the name of another type in the
            //       SAME family. Revit only requires type names to be unique within a
            //       family (Basic Wall vs Curtain Wall are different system families,
            //       so a cross-family rename succeeds). Prefer wall types.
            var sameFamily = new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>()
                .Where(t => t.Category != null && t.Category.CategoryType == CategoryType.Model
                            && !string.IsNullOrEmpty(t.FamilyName) && !usedTypeIds.Contains(Id(t.Id)))
                .GroupBy(t => (Cat: Id(t.Category.Id), t.FamilyName))
                .Where(g => g.Count() >= 2)
                .OrderBy(g => g.First() is WallType ? 0 : 1).ThenBy(g => g.Key.Cat).ThenBy(g => g.Key.FamilyName)
                .Select(g => g.OrderBy(t => Id(t.Id)).Take(2).ToList())
                .FirstOrDefault();
            if (sameFamily != null)
            {
                var from = sameFamily[0];
                var taken = sameFamily[1];
                usedTypeIds.Add(Id(from.Id));
                issues.Add(new JkrFixtureIssue
                {
                    Id = NextId(), Category = "Component Naming", Title = "Type name must match the JKR library name",
                    Reason = $"The required name is already used by another '{from.FamilyName}' type.",
                    RevitCategory = from.Category.Name, ElementName = from.Name, ElementId = Id(from.Id),
                    FixAction = "rename_type", FixTarget = "type",
                    Current = from.Name, Expected = taken.Name,
                    ExpectFailure = "the name is already used by another type in the same family",
                });
            }

            // 6. Items the AI cannot judge (cannot_verify → needs a person; never a
            //    failure, never in the %). Listed on every scan, with no fix.
            var link = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).FirstElement();
            issues.Add(new JkrFixtureIssue
            {
                Id = NextId(), Status = "cannot_verify", Category = "Project Information",
                Title = "Link files are latest version",
                Reason = link != null
                    ? "Links are loaded. Version status cannot be confirmed: no CDE access. Check for the latest version manually."
                    : "No CDE access. Check for the latest version of each linked file manually.",
                RevitCategory = link != null ? "RVT Links" : "Project", ElementName = link?.Name ?? "Project",
                ElementId = link != null ? Id(link.Id) : 0,
                Expected = "Latest version",
            });
            var firstDoor = Instances(BuiltInCategory.OST_Doors).FirstOrDefault();
            if (firstDoor != null)
            {
                issues.Add(new JkrFixtureIssue
                {
                    Id = NextId(), Status = "cannot_verify", Category = "Component Parameter",
                    Title = "Door fire rating suits its location",
                    Reason = "The spec gives no rule for this door type, so the AI cannot judge it. Check the fire rating against the fire plan.",
                    RevitCategory = firstDoor.Category?.Name ?? "Doors", ElementName = firstDoor.Name ?? "",
                    ElementId = Id(firstDoor.Id),
                    Expected = "Per fire plan",
                });
            }

            //    c) Element that does not exist.
            issues.Add(new JkrFixtureIssue
            {
                Id = NextId(), Category = "Component Naming", Title = "Type name does not follow JKR naming (missing element)",
                Reason = "Points at an element id that is not in the model.",
                RevitCategory = "—", ElementName = "missing element", ElementId = MissingElementId,
                FixAction = "rename_type", FixTarget = "type",
                Current = "", Expected = "jkrAR_missing",
                ExpectFailure = "the element is not in the model",
            });

            //    d) Instance write on a model-group member (only if the model has groups).
            var grouped = RenameCategories
                .SelectMany(bic => new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                .Where(InGroup).OrderBy(e => Id(e.Id))
                .Select(e => (e, P: e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)))
                .FirstOrDefault(x => x.P != null);
            if (grouped.P != null)
            {
                var issue = ParamIssue(NextId(), "Component Parameter", "Comments must be filled in (grouped element)",
                    grouped.e, grouped.P.Definition.Name, "instance", grouped.P.AsString() ?? "",
                    $"JKR-TEST-{Id(grouped.e.Id)}");
                issue.ExpectFailure = "the element is inside a model group";
                issues.Add(issue);
            }

            return fixture;
        }

        private static JkrFixtureIssue ParamIssue(string id, string category, string title, Element e, string param,
                                                  string target, string current, string expected) =>
            new JkrFixtureIssue
            {
                Id = id, Category = category, Title = title,
                Reason = $"'{param}' should be '{expected}'.",
                RevitCategory = e.Category?.Name ?? "", ElementName = e.Name ?? "", ElementId = Id(e.Id),
                FixAction = "set_parameter", FixParameter = param, FixTarget = target,
                Current = current, Expected = expected,
            };

        private static bool InGroup(Element e) =>
            e.GroupId != null && e.GroupId != ElementId.InvalidElementId;

        private static long Id(ElementId id)
        {
#if REVIT2023_24
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }
    }
}
