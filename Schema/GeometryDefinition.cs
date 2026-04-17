using System.Collections.Generic;

namespace RevitFamilyBuilder.Schema
{
    public class GeometryDefinition
    {
        /// <summary>
        /// Stable internal identifier for this geometry entry, used ONLY by
        /// the engine to map a geometry definition to the ElementId of the
        /// extrusion it creates in Revit. This id is never written to Revit
        /// itself — Revit extrusions do not expose a user-facing name field
        /// and any attempt to set one triggers "Name could not be applied"
        /// warnings. To distinguish geometries visually in the family editor
        /// use <see cref="Subcategory"/> instead.
        ///
        /// Required. Must be unique across the geometry array.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Optional family subcategory name (Object Style) assigned to the
        /// extrusion. If several geometry entries reference the same
        /// subcategory, the engine creates it once and reuses it for the
        /// others. Omit to keep the default subcategory of the family.
        /// </summary>
        public string Subcategory { get; set; }

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
