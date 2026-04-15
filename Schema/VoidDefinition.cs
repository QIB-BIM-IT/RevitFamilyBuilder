namespace RevitFamilyBuilder.Schema
{
    public class VoidDefinition
    {
        public string Name { get; set; }

        /// <summary>v1: must be "Rectangular".</summary>
        public string Shape { get; set; }

        /// <summary>v1: must be "Front".</summary>
        public string Face { get; set; }

        /// <summary>v1: must be "Center".</summary>
        public string Location { get; set; }

        /// <summary>References a Length parameter for the opening width.</summary>
        public string WidthParameter { get; set; }

        /// <summary>References a Length parameter for the opening height.</summary>
        public string HeightParameter { get; set; }

        /// <summary>v1: must be "Through".</summary>
        public string Cut { get; set; }
    }
}
