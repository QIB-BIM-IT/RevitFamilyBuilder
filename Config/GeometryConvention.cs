namespace RevitFamilyBuilder.Config
{
    /// <summary>
    /// Preset bundle of company drafting standards applied to a geometry.
    ///
    /// <para>Each field is intentionally nullable: a <c>null</c> value means
    /// "the engine must not modify this aspect of the family", which lets a
    /// convention express only the bits it actually owns and leaves the rest
    /// at the family-template defaults.</para>
    ///
    /// <para>This PR introduces only the <c>SubcategoryName</c> dimension —
    /// the colour / projection / pattern / material slots are placeholders
    /// that will be wired up to actual Revit calls in a later PR once the
    /// company values are confirmed by the BIM team.</para>
    /// </summary>
    public class GeometryConvention
    {
        /// <summary>Stable identifier of the convention preset (e.g. "Body").</summary>
        public string Name { get; set; }

        /// <summary>
        /// Family subcategory (Object Style) this convention applies to its
        /// host geometry. Required — every convention must at least name a
        /// subcategory, otherwise the convention has no effect.
        /// </summary>
        public string SubcategoryName { get; set; }

        /// <summary>RGB triplet for the subcategory's projection line colour. <c>null</c> = leave as-is.</summary>
        public int[] LineColorRgb { get; set; }

        /// <summary>Projection line weight (1..16). <c>null</c> = leave as-is.</summary>
        public int? LineProjection { get; set; }

        /// <summary>Line pattern name to apply to the subcategory. <c>null</c> = leave as-is.</summary>
        public string LinePatternName { get; set; }

        /// <summary>Default material name to apply to the geometry. <c>null</c> = leave as-is.</summary>
        public string DefaultMaterialName { get; set; }
    }
}
