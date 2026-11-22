#nullable enable
// Safety gap found by the WP-1 spike pack dry-compile (S8c): ParameterBindings.ReInsert compiled under the
// code policy and was classified READ, so a run_revit_code that widens or narrows a project parameter's
// category binding ran in the rolled-back preview group and could skip the server's write question.
// Every call that changes a binding or a parameter definition must be a WRITE. The mode decision must
// come from the real policy (CodePolicySet.Default), not from a test copy.

using System;
using System.Linq;
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class BindingWritesModeTests
    {
        private static ModeDecision Classify(string code)
        {
            var a = CodeTestSupport.AnalyzeProduction(code);
            Assert.True(a.Allowed, CodeTestSupport.Describe(a));
            return a.Mode!;
        }

        private const string Prelude =
            "var gm = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);\n" +
            "var it = doc.ParameterBindings.ForwardIterator();\n" +
            "it.MoveNext();\n" +
            "var def = (InternalDefinition)it.Key;\n" +
            "var bind = (ElementBinding)it.Current;\n";

        [Theory]
        [InlineData("doc.ParameterBindings.ReInsert(def, bind, def.GetGroupTypeId());", "ReInsert")]
        [InlineData("doc.ParameterBindings.Insert(def, bind, def.GetGroupTypeId());", "Insert")]
        [InlineData("doc.ParameterBindings.Remove(def);", "Remove")]
        [InlineData("var map = doc.ParameterBindings;\nmap.ReInsert(def, bind);", "ReInsert")]
        [InlineData("doc.ParameterBindings.ReInsert(def, bind);", "ReInsert")]
        [InlineData("doc.ParameterBindings.Clear();", "Clear")]
        public void BindingChangesAreWrites(string call, string symbol)
        {
            var d = Classify(Prelude + call);
            Assert.True(d.Mode == CodeMode.Write, "expected Write for: " + call);
            Assert.Contains(d.Reasons, r => r.Symbol.Contains(symbol));
        }

        /// <summary>The exact S8c snippet from docs/spikes/WP-1-spike-pack.md.</summary>
        [Fact]
        public void SpikeS8c_IsWrite()
        {
            const string s8c =
                "var gm = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);\n" +
                "var it = doc.ParameterBindings.ForwardIterator();\n" +
                "InternalDefinition def = null;\nElementBinding bind = null;\n" +
                "while (it.MoveNext())\n{\n    var d = it.Key as InternalDefinition;\n    var b = it.Current as ElementBinding;\n" +
                "    if (d == null || b == null) continue;\n    def = d; bind = b; break;\n}\n" +
                "if (def != null)\n{\n    var grp = def.GetGroupTypeId();\n    try\n    {\n" +
                "        bind.Categories.Insert(gm);\n        var ok1 = doc.ParameterBindings.ReInsert(def, bind, grp);\n" +
                "        bind.Categories.Erase(gm);\n        var ok2 = doc.ParameterBindings.ReInsert(def, bind, grp);\n" +
                "    }\n    catch (Exception e) { Print(e.Message); }\n}";
            var d = Classify(s8c);
            Assert.Equal(CodeMode.Write, d.Mode);
            Assert.Contains(d.Reasons, r => r.Symbol.Contains("ReInsert"));
        }

        /// <summary>Definition changes. Shared-parameter files are out of reach for snippets (Application access is denied by the policy), so
        /// Definitions.Create and SharedParameterElement.Create cannot compile; their members stay on the verb rules (Create, Remove).</summary>
        [Theory]
        [InlineData("var gp = (GlobalParameter)doc.GetElement(new ElementId(1L));\ngp.LabelDimension(new ElementId(2L));", "LabelDimension")]
        [InlineData("var gp = (GlobalParameter)doc.GetElement(new ElementId(1L));\ngp.UnlabelDimension(new ElementId(2L));", "UnlabelDimension")]
        [InlineData("var d = (InternalDefinition)doc.ParameterBindings.ForwardIterator().Key;\nd.SetAllowVaryBetweenGroups(doc, true);", "SetAllowVaryBetweenGroups")]
        [InlineData("var e = doc.GetElement(new ElementId(1L)) as ParameterElement;\ndoc.Delete(e.Id);", "Delete")]
        public void DefinitionChangesAreWrites(string code, string symbol)
        {
            var d = Classify(code);
            Assert.True(d.Mode == CodeMode.Write, "expected Write for: " + code + " (" + string.Join("; ", d.Reasons) + ")");
            Assert.Contains(d.Reasons, r => r.Symbol.Contains(symbol));
        }

        /// <summary>Reading bindings and definitions stays a read (S0 style counts, name lookups).</summary>
        [Theory]
        [InlineData("var n = 0;\nvar it = doc.ParameterBindings.ForwardIterator();\nwhile (it.MoveNext()) n++;\nPrint(n + \" \" + doc.ParameterBindings.Size);")]
        [InlineData("var it = doc.ParameterBindings.ForwardIterator();\nwhile (it.MoveNext())\n{\n    var d = it.Key as InternalDefinition;\n    var b = it.Current as ElementBinding;\n    if (d != null && b != null) Print(d.Name + \" \" + b.Categories.Size + \" \" + (b is InstanceBinding));\n}")]
        [InlineData("var gm = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);\nvar it = doc.ParameterBindings.ForwardIterator();\nwhile (it.MoveNext()) { var b = it.Current as ElementBinding; if (b != null) Print(b.Categories.Contains(gm).ToString()); }")]
        [InlineData("var bm = doc.ParameterBindings;\nvar key = bm.Contains(null) ? 1 : 0;\nPrint(key);")]
        public void ReadingBindingsStaysRead(string code)
        {
            var d = Classify(code);
            Assert.True(d.Mode == CodeMode.Read, "expected Read, got write because " + string.Join("; ", d.Reasons));
        }

        /// <summary>The spike pack's own snippets keep their verdict: S0 stays a read, S1, S7d, S8c and S9 are writes.</summary>
        [Theory]
        [InlineData("// S0 ", CodeMode.Read)]
        [InlineData("// S1 ", CodeMode.Write)]
        [InlineData("// S7d ", CodeMode.Write)]
        [InlineData("// S8c ", CodeMode.Write)]
        [InlineData("// S9 ", CodeMode.Write)]
        public void SpikePackSnippetsKeepTheirMode(string marker, CodeMode expected)
        {
            var doc = System.IO.File.ReadAllText(CodeTestSupport.RepoFile("docs/spikes/WP-1-spike-pack.md")).Replace("\r\n", "\n");
            var start = doc.IndexOf("```csharp\n" + marker, StringComparison.Ordinal);
            Assert.True(start >= 0, "snippet " + marker + " not found");
            start += "```csharp\n".Length;
            var code = doc.Substring(start, doc.IndexOf("\n```", start, StringComparison.Ordinal) - start);
            var a = CodeTestSupport.AnalyzeProduction(code);
            Assert.True(a.Allowed, CodeTestSupport.Describe(a));
            Assert.True(a.Mode!.Mode == expected, marker + " expected " + expected + ": " + string.Join("; ", a.Mode.Reasons));
        }

        /// <summary>The write-mode list is the real policy file: every binding member is in it.</summary>
        [Theory]
        [InlineData("Autodesk.Revit.DB.BindingMap.ReInsert")]
        [InlineData("Autodesk.Revit.DB.BindingMap.Insert")]
        [InlineData("Autodesk.Revit.DB.BindingMap.Remove")]
        [InlineData("Autodesk.Revit.DB.DefinitionBindingMap.ReInsert")]
        [InlineData("Autodesk.Revit.DB.DefinitionBindingMap.Insert")]
        [InlineData("Autodesk.Revit.DB.DefinitionBindingMap.Remove")]
        public void BindingMembersAreListedAsWrites(string key) =>
            Assert.Contains(key, CodePolicySet.Default.WriteMembers);
    }
}
