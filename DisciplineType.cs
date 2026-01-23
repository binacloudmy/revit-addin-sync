namespace RevitWebAppSync
{
    /// <summary>
    /// Discipline types for BIM files in BINA Cloud
    /// </summary>
    public enum DisciplineType
    {
        Architecture,
        Structure,
        Mechanical,
        Electrical,
        MainFile
    }

    public static class DisciplineTypeExtensions
    {
        public static string ToDisplayName(this DisciplineType disciplineType)
        {
            return disciplineType switch
            {
                DisciplineType.Architecture => "Architecture",
                DisciplineType.Structure => "Structure",
                DisciplineType.Mechanical => "Mechanical",
                DisciplineType.Electrical => "Electrical",
                DisciplineType.MainFile => "Main File",
                _ => disciplineType.ToString()
            };
        }

        public static string ToDescription(this DisciplineType disciplineType)
        {
            return disciplineType switch
            {
                DisciplineType.Architecture => "Architectural design elements, walls, doors, windows, rooms, etc.",
                DisciplineType.Structure => "Structural elements, beams, columns, foundations, framing, etc.",
                DisciplineType.Mechanical => "Mechanical systems, HVAC, ducts, pipes, equipment, etc.",
                DisciplineType.Electrical => "Electrical systems, lighting, power distribution, circuits, etc.",
                DisciplineType.MainFile => "General or federated model containing multiple disciplines.",
                _ => ""
            };
        }

        public static string ToIcon(this DisciplineType disciplineType)
        {
            return disciplineType switch
            {
                DisciplineType.Architecture => "A",
                DisciplineType.Structure => "S",
                DisciplineType.Mechanical => "M",
                DisciplineType.Electrical => "E",
                DisciplineType.MainFile => "F",
                _ => "?"
            };
        }

        public static string ToValue(this DisciplineType disciplineType)
        {
            return disciplineType.ToString();
        }
    }
}
