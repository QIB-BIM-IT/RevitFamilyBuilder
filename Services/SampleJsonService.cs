using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using RevitFamilyBuilder.Schema;

namespace RevitFamilyBuilder.Services
{
    public class SampleJsonService
    {
        private static readonly JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            },
            Converters = { new StringEnumConverter(new SnakeCaseNamingStrategy(), false) },
            Formatting = Formatting.Indented
        };

        public string GetSampleJson()
        {
            var definition = BuildSampleDefinition();
            return JsonConvert.SerializeObject(definition, _settings);
        }

        private static FamilyDefinition BuildSampleDefinition()
        {
            return new FamilyDefinition
            {
                SchemaVersion = "1.0",
                FamilyName    = "Generic_Box",
                FamilyTemplate = "GenericModelMetric",
                Category       = "Generic Models",

                // Named types with explicit per-type values + a classical formula
                // on Height (Width / 2). No lookup-table machinery is exercised here;
                // the lookup path remains available in the engine for future use.
                BuildStrategy  = "explicit_types",

                // ── Parameters ──────────────────────────────────────────────────
                // Only the parameters actually consumed by the current sample are
                // declared: Width/Depth/Height drive the two-extrusion split.
                // Connector/void-specific parameters were dropped together with
                // the connector+void sections for this PR — they will come back
                // in later PRs when connectors and voids target a specific
                // geometry by id.
                Parameters = new List<ParameterDefinition>
                {
                    new ParameterDefinition { Name = "Width",  Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "600" },
                    new ParameterDefinition { Name = "Depth",  Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "400" },
                    new ParameterDefinition { Name = "Height", Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "300" }
                },

                // ── Reference planes ────────────────────────────────────────────
                // Lateral planes (Left/Right/Front/Back) use "vertical" or "horizontal"
                // orientation and live in the plan view (X-normal or Y-normal planes).
                //
                // Elevation planes (Base/Top) use "elevation" orientation, which creates
                // Z-normal planes visible in the front-elevation view.  These are the
                // ONLY planes that can correctly constrain the extrusion top/bottom faces.
                //
                // Mid_LR is the central vertical plane shared by the two extrusions
                // body_primary and body_secondary. Placing it at offset 0 puts it
                // exactly midway between Left (-300) and Right (+300), which is the
                // precondition Revit requires before an EQ constraint can lock it
                // to the midpoint. Center_FB stays as the Y-axis symmetry anchor.
                ReferencePlanes = new List<ReferencePlaneDefinition>
                {
                    new ReferencePlaneDefinition { Name = "Center_FB", Orientation = "horizontal", Offset =    0.0 },
                    new ReferencePlaneDefinition { Name = "Mid_LR",    Orientation = "vertical",   Offset =    0.0 },

                    // Lateral bounding planes.
                    new ReferencePlaneDefinition { Name = "Left",  Orientation = "vertical",   Offset = -300.0 },
                    new ReferencePlaneDefinition { Name = "Right", Orientation = "vertical",   Offset =  300.0 },
                    new ReferencePlaneDefinition { Name = "Front", Orientation = "horizontal", Offset = -200.0 },
                    new ReferencePlaneDefinition { Name = "Back",  Orientation = "horizontal", Offset =  200.0 },

                    // Elevation planes (Z-normal) — REQUIRED for height flex to work.
                    new ReferencePlaneDefinition { Name = "Base", Orientation = "elevation", Offset =   0.0 },
                    new ReferencePlaneDefinition { Name = "Top",  Orientation = "elevation", Offset = 300.0 }
                },

                // ── Dimensions ──────────────────────────────────────────────────
                // Three labelled dimensions drive Width, Depth, Height.
                // Two EQ dimensions enforce symmetry:
                //   • Left ↔ Mid_LR ↔ Right  locks Mid_LR to the LR midpoint
                //     so the two extrusions sharing Mid_LR always occupy
                //     equal halves of Width.
                //   • Front ↔ Center_FB ↔ Back locks the family's Y-axis
                //     symmetry as before.
                //
                // EQ dimension rules:
                //   • ReferencePlaneMiddle must name the middle plane.
                //   • IsEqual = true marks every segment as IsEqualDriven.
                //   • Leave ParameterName empty on EQ dims (no label, only constraint).
                Dimensions = new List<DimensionDefinition>
                {
                    new DimensionDefinition { ReferencePlane1 = "Left",  ReferencePlane2 = "Right", ParameterName = "Width"  },
                    new DimensionDefinition { ReferencePlane1 = "Front", ReferencePlane2 = "Back",  ParameterName = "Depth"  },
                    new DimensionDefinition { ReferencePlane1 = "Base",  ReferencePlane2 = "Top",   ParameterName = "Height" },

                    new DimensionDefinition { ReferencePlane1 = "Left",  ReferencePlaneMiddle = "Mid_LR",    ReferencePlane2 = "Right", IsEqual = true },
                    new DimensionDefinition { ReferencePlane1 = "Front", ReferencePlaneMiddle = "Center_FB", ReferencePlane2 = "Back",  IsEqual = true }
                },

                // ── Symbolic lines ──────────────────────────────────────────────
                // Plan-view footprint outline.  Coordinates match the default
                // Width = 600 mm (±300) and Depth = 400 mm (±200) values.
                SymbolicLines = new List<SymbolicLineDefinition>
                {
                    new SymbolicLineDefinition { StartX = -300.0, StartY = -200.0, EndX =  300.0, EndY = -200.0, View = "plan" },
                    new SymbolicLineDefinition { StartX =  300.0, StartY = -200.0, EndX =  300.0, EndY =  200.0, View = "plan" },
                    new SymbolicLineDefinition { StartX =  300.0, StartY =  200.0, EndX = -300.0, EndY =  200.0, View = "plan" },
                    new SymbolicLineDefinition { StartX = -300.0, StartY =  200.0, EndX = -300.0, EndY = -200.0, View = "plan" }
                },

                // ── Geometry ────────────────────────────────────────────────────
                // First real geometric differentiation: two rectangular extrusions
                // sitting SIDE BY SIDE, sharing Mid_LR as their inner boundary.
                //
                //   body_primary   bounded by [Left,   Mid_LR, Front, Back, Base, Top]
                //                  → occupies the LEFT half of Width.
                //   body_secondary bounded by [Mid_LR, Right,  Front, Back, Base, Top]
                //                  → occupies the RIGHT half of Width.
                //
                // The "Left ↔ Mid_LR ↔ Right" EQ dimension keeps Mid_LR centred
                // while Width flexes, so each half always renders at Width / 2.
                //
                // Both geometries request the Body subcategory to exercise the
                // create-once / reuse path from the previous PR.
                Geometry = new List<GeometryDefinition>
                {
                    new GeometryDefinition
                    {
                        Id              = "body_primary",
                        Subcategory     = "Body",
                        Type            = GeometryType.Extrusion,
                        Profile         = "rectangular",
                        WidthParameter  = "Width",
                        DepthParameter  = "Depth",
                        HeightParameter = "Height",
                        LeftPlane       = "Left",
                        RightPlane      = "Mid_LR",
                        Symmetry        = new List<string> { "FB" }
                    },
                    new GeometryDefinition
                    {
                        Id              = "body_secondary",
                        Subcategory     = "Body",
                        Type            = GeometryType.Extrusion,
                        Profile         = "rectangular",
                        WidthParameter  = "Width",
                        DepthParameter  = "Depth",
                        HeightParameter = "Height",
                        LeftPlane       = "Mid_LR",
                        RightPlane      = "Right",
                        Symmetry        = new List<string> { "FB" }
                    }
                },

                // ── Formulas ────────────────────────────────────────────────────
                // Height is driven by Width / 2.  Formula-driven parameters cannot be
                // directly set in FlexTest; the engine will skip them and log the reason.
                Formulas = new List<FormulaDefinition>
                {
                    new FormulaDefinition
                    {
                        ParameterName = "Height",
                        Expression    = "Width / 2"
                    }
                },

                // ── Types ───────────────────────────────────────────────────────
                // Explicit types with per-type Width and Depth values.
                // Height is computed from the formula above (Width / 2).
                Types = new List<FamilyTypeDefinition>
                {
                    new FamilyTypeDefinition
                    {
                        Name = "Type A",
                        ParameterValues = new Dictionary<string, string>
                        {
                            { "Width", "800" },
                            { "Depth", "500" }
                        }
                    },
                    new FamilyTypeDefinition
                    {
                        Name = "Type B",
                        ParameterValues = new Dictionary<string, string>
                        {
                            { "Width", "600" },
                            { "Depth", "300" }
                        }
                    }
                },

                // ── Voids ───────────────────────────────────────────────────────
                // Intentionally empty in this PR. A void cuts a specific extrusion
                // face; now that there are two extrusions, void targeting must be
                // expressed by geometry_id. That work is scheduled for a later PR.
                Voids = new List<VoidDefinition>(),

                // ── Connectors ──────────────────────────────────────────────────
                // Intentionally empty for the same reason as Voids above — the
                // connector must declare which geometry it sits on. Re-introduced
                // in a later PR with geometry_id targeting.
                Connectors = new List<ConnectorDefinition>(),

                Warnings = new List<string>
                {
                    "Sample JSON only — not generated by AI.",
                    "Sample JSON — exercises explicit_types strategy with formula.",
                    "Height is formula-driven (Width / 2); FlexTest will skip it when testing directly.",
                    "Two extrusions share the Mid_LR reference plane — EQ(Left, Mid_LR, Right) keeps it centred during flex.",
                    "Connectors and voids are intentionally empty in this sample until geometry-id targeting ships."
                }
            };
        }
    }
}
