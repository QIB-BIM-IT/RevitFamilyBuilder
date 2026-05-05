using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitFamilyBuilder.Schema
{
    /// <summary>
    /// Transit container used ONLY at deserialization time to receive the
    /// <c>plane_overrides</c> sub-object emitted by the Call 2 Anthropic
    /// schema. The six plane names are grouped into this object (rather
    /// than exposed as 8 independent optional fields on the geometry item)
    /// to keep the compiled grammar small enough to compile within
    /// Anthropic's time budget — see <c>ClaudeService.BuildFamilySchema</c>
    /// for the full rationale.
    ///
    /// <para>After <c>JsonSchemaService.Parse</c> runs, the values from
    /// this sub-object are copied to the corresponding flat properties on
    /// <see cref="GeometryDefinition"/> (<c>LeftPlane</c>, <c>RightPlane</c>,
    /// etc.), which remain the stable read API consumed by the validator
    /// and the engine. This class is therefore not consulted in any code
    /// path downstream of <c>Parse</c>.</para>
    /// </summary>
    public class PlaneOverrides
    {
        public string LeftPlane  { get; set; }
        public string RightPlane { get; set; }
        public string FrontPlane { get; set; }
        public string BackPlane  { get; set; }
        public string BasePlane  { get; set; }
        public string TopPlane   { get; set; }
    }

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
        ///
        /// <para>When a <see cref="Convention"/> is also declared, the
        /// convention's <c>SubcategoryName</c> WINS and this field is
        /// ignored — keep them aligned in the JSON to avoid confusion.</para>
        /// </summary>
        public string Subcategory { get; set; }

        /// <summary>
        /// Optional company-standard preset name resolved through
        /// <c>RevitFamilyBuilder.Config.ConventionLibrary</c>. When set, the
        /// convention drives the subcategory and (in future PRs) line
        /// colour, line pattern, projection weight and default material.
        ///
        /// <para>Omit to keep the legacy behaviour where <see cref="Subcategory"/>
        /// is applied directly. Unknown names are rejected by the JSON
        /// validator with the list of available conventions.</para>
        /// </summary>
        public string Convention { get; set; }

        public GeometryType Type { get; set; }
        public string Profile { get; set; }
        public string WidthParameter { get; set; }
        public string DepthParameter { get; set; }
        public string HeightParameter { get; set; }

        // ── Per-geometry bounding plane overrides ────────────────────────
        //
        // Each slot names the reference plane that the corresponding face of
        // this extrusion will be locked to. All six slots are OPTIONAL: when
        // null or empty, the engine falls back to the canonical default name
        // ("Left", "Right", "Front", "Back", "Base", "Top") so legacy JSON
        // that built a single centred box keeps working unchanged.
        //
        // Typical use-case (this PR's goal): two side-by-side extrusions
        // sharing a vertical plane in the middle.
        //
        //   geometry[0] body_primary   — right_plane = "Mid_LR"  (occupies left half)
        //   geometry[1] body_secondary — left_plane  = "Mid_LR"  (occupies right half)
        //
        // The extrusion's initial extent is derived from the actual offsets
        // of these reference planes in the family document, and each face is
        // then aligned to its declared plane, so Width / Depth / Height flex
        // propagates correctly through the shared plane.
        public string LeftPlane  { get; set; }
        public string RightPlane { get; set; }
        public string FrontPlane { get; set; }
        public string BackPlane  { get; set; }
        public string BasePlane  { get; set; }
        public string TopPlane   { get; set; }

        /// <summary>
        /// Transit container populated by the Anthropic Call 2 schema when
        /// the AI emits per-geometry plane overrides. Mapped to the flat
        /// <c>LeftPlane</c>/<c>RightPlane</c>/etc. properties above by
        /// <c>JsonSchemaService.Parse</c>. The validator and engine never
        /// read this property directly — they always consume the flat
        /// fields. Decorated with <c>NullValueHandling.Ignore</c> so the
        /// server-side sample serializer (which never sets it) does not
        /// emit a noisy <c>"plane_overrides": null</c> in its preview output.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public PlaneOverrides PlaneOverrides { get; set; }

        // Axes on which the extrusion should be symmetric around a centre plane.
        // Valid values: "LR" (Left-Right symmetry, requires a shared centre plane + EQ dim),
        //               "FB" (Front-Back symmetry, requires Center_FB plane + EQ dim).
        // This field is informational for the AI contract; the actual EQ constraint
        // is created through DimensionDefinition.IsEqual entries in "dimensions".
        public List<string> Symmetry { get; set; }
    }
}
