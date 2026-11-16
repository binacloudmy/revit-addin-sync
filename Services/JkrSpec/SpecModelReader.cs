using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services.JkrSpec
{
    /// <summary>
    /// Reads the <see cref="SpecModel"/> snapshot from a Revit document — the only
    /// Revit-dependent part of the spec checks. Runs on Revit's thread; never throws:
    /// whatever it cannot read goes to SpecModel.ReadErrors.
    /// </summary>
    public static class SpecModelReader
    {
        public static SpecModel Read(Document doc, string discipline, int lod)
        {
            var m = new SpecModel { Discipline = discipline ?? "AR", Lod = lod };
            if (doc == null) { m.ReadErrors.Add("No document"); return m.Normalize(); }
            m.FileName = System.IO.Path.GetFileName(doc.PathName ?? "");
            return m.Normalize();
        }
    }
}
