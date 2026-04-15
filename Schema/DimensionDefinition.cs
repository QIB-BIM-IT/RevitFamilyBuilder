namespace RevitFamilyBuilder.Schema
{
    public class DimensionDefinition
    {
        public string ReferencePlane1 { get; set; }
        public string ReferencePlane2 { get; set; }
        public string ParameterName { get; set; }

        // Optional: name of an intermediate reference plane.
        // When set together with IsEqual = true, creates a 3-reference EQ dimension
        // that constrains ReferencePlane1 <-> ReferencePlaneMiddle <-> ReferencePlane2
        // as equal segments (symmetry constraint).
        public string ReferencePlaneMiddle { get; set; }

        // When true (and ReferencePlaneMiddle is set), all dimension segments are
        // marked as IsEqualDriven, locking the middle plane to the midpoint.
        public bool IsEqual { get; set; }
    }
}
