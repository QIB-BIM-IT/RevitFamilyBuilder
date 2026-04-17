using System.Collections.Generic;

namespace RevitFamilyBuilder.Schema
{
    public class GeometryDefinition
    {
        /// <summary>
        /// Unique name for this extrusion inside the family document.
        /// Required for multi-geometry builds (array of geometry) so each
        /// extrusion can be identified individually in Revit and in logs.
        /// </summary>
        public string Name { get; set; }

        public GeometryType Type { get; set; }
        public string Profile { get; set; }
        public string WidthParameter { get; set; }
        public string DepthParameter { get; set; }
        public string HeightParameter { get; set; }

        // Axes on which the extrusion should be symmetric around a centre plane.
        // Valid values: "LR" (Left-Right symmetry, requires Center_LR plane + EQ dim),
        //               "FB" (Front-Back symmetry, requires Center_FB plane + EQ dim).
        // This field is informational for the AI contract; the actual EQ constraint
        // is created through DimensionDefinition.IsEqual entries in "dimensions".
        public List<string> Symmetry { get; set; }
    }
}
