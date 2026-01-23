using System;

namespace RevitWebAppSync
{
    public class BimDisciplineFile
    {
        public string DisciplineType { get; set; }
        public string FileUrl { get; set; }
        public string FileName { get; set; }
    }

    public class BimDisciplineResponse
    {
        public BimDisciplineFile Structure { get; set; }
        public BimDisciplineFile Architecture { get; set; }
        public BimDisciplineFile Mechanical { get; set; }
        public BimDisciplineFile Electrical { get; set; }
        public BimDisciplineFile MainFile { get; set; }
    }
}