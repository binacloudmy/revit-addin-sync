using System;
using System.Collections.Generic;

namespace RevitWebAppSync
{
    public class LatestFileInfo
    {
        public int Id { get; set; }
        public string FileName { get; set; }
        public string FileUrl { get; set; }
        public string Version { get; set; }
    }

    public class DisciplineFolderInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public LatestFileInfo LatestFile { get; set; }
    }

    public class DisciplineData
    {
        public List<DisciplineFolderInfo> Folders { get; set; } = new List<DisciplineFolderInfo>();
    }

    public class BimDisciplineResponse
    {
        public DisciplineData Structure { get; set; }
        public DisciplineData Architecture { get; set; }
        public DisciplineData Mechanical { get; set; }
        public DisciplineData Electrical { get; set; }
    }
}
