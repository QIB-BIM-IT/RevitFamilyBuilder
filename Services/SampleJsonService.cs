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
                SchemaVersion  = "1.0",
                FamilyName     = "Generic_Box",
                FamilyTemplate = "GenericModelMetric",
                Category       = "Generic Models",

                // Single type. Connectors / formulas / types are all empty in
                // this PR so the multi-geometry pipeline is exercised in
                // isolation, without interaction from per-type values or
                // formula-driven parameters.
                BuildStrategy  = "single_type",

                // ── Parameters ──────────────────────────────────────────────────
                // Six parameters drive two independent extrusions:
                //   collar : Width × Depth × CollarLength
                //   flange : FlangeWidth × FlangeDepth × FlangeThickness
                // The flange's XY footprint is wider than the collar's so the
                // flange wraps the collar at mid-height.
                Parameters = new List<ParameterDefinition>
                {
                    new ParameterDefinition { Name = "Width",           Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "600" },
                    new ParameterDefinition { Name = "Depth",           Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "400" },
                    new ParameterDefinition { Name = "CollarLength",    Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "300" },
                    new ParameterDefinition { Name = "FlangeWidth",     Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "644" },
                    new ParameterDefinition { Name = "FlangeDepth",     Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "444" },
                    new ParameterDefinition { Name = "FlangeThickness", Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "25"  }
                },

                // ── Reference planes ────────────────────────────────────────────
                // Two plane families:
                //   (a) Canonical Left/Right/Front/Back/Base/Top + Mid_LR /
                //       Center_FB symmetry planes — drive the collar.
                //   (b) Flange planes (FlangeLeft/Right/Front/Back) plus two
                //       elevation planes (FlangeBot/FlangeTop) that sandwich a
                //       flat slab at mid-collar.
                //
                // FlangeBot / FlangeTop offsets are computed once from default
                // values:
                //   FlangeBot = CollarLength/2 − FlangeThickness/2 = 137.5
                //   FlangeTop = CollarLength/2 + FlangeThickness/2 = 162.5
                // They are NOT driven by CollarLength — flexing CollarLength
                // does not auto-recenter the flange. See warnings.
                ReferencePlanes = new List<ReferencePlaneDefinition>
                {
                    // Symmetry planes (collar).
                    new ReferencePlaneDefinition { Name = "Center_FB", Orientation = "horizontal", Offset =    0.0 },
                    new ReferencePlaneDefinition { Name = "Mid_LR",    Orientation = "vertical",   Offset =    0.0 },

                    // Collar lateral bounding planes.
                    new ReferencePlaneDefinition { Name = "Left",  Orientation = "vertical",   Offset = -300.0 },
                    new ReferencePlaneDefinition { Name = "Right", Orientation = "vertical",   Offset =  300.0 },
                    new ReferencePlaneDefinition { Name = "Front", Orientation = "horizontal", Offset = -200.0 },
                    new ReferencePlaneDefinition { Name = "Back",  Orientation = "horizontal", Offset =  200.0 },

                    // Collar elevation planes (Z-normal).
                    new ReferencePlaneDefinition { Name = "Base", Orientation = "elevation", Offset =   0.0 },
                    new ReferencePlaneDefinition { Name = "Top",  Orientation = "elevation", Offset = 300.0 },

                    // Flange lateral bounding planes (wider than the collar).
                    new ReferencePlaneDefinition { Name = "FlangeLeft",  Orientation = "vertical",   Offset = -322.0 },
                    new ReferencePlaneDefinition { Name = "FlangeRight", Orientation = "vertical",   Offset =  322.0 },
                    new ReferencePlaneDefinition { Name = "FlangeFront", Orientation = "horizontal", Offset = -222.0 },
                    new ReferencePlaneDefinition { Name = "FlangeBack",  Orientation = "horizontal", Offset =  222.0 },

                    // Flange elevation planes — flat slab at mid-collar.
                    new ReferencePlaneDefinition { Name = "FlangeBot", Orientation = "elevation", Offset = 137.5 },
                    new ReferencePlaneDefinition { Name = "FlangeTop", Orientation = "elevation", Offset = 162.5 }
                },

                // ── Dimensions ──────────────────────────────────────────────────
                // Collar: 3 labelled dimensions (Width, Depth, CollarLength) +
                //         2 EQ dimensions for LR / FB symmetry.
                // Flange: 3 labelled dimensions (FlangeWidth, FlangeDepth,
                //         FlangeThickness). No EQ on the flange — the collar's
                //         Mid_LR / Center_FB already lock the family centre and
                //         the flange sits inside that centred frame.
                Dimensions = new List<DimensionDefinition>
                {
                    // Collar.
                    new DimensionDefinition { ReferencePlane1 = "Left",  ReferencePlane2 = "Right", ParameterName = "Width"        },
                    new DimensionDefinition { ReferencePlane1 = "Front", ReferencePlane2 = "Back",  ParameterName = "Depth"        },
                    new DimensionDefinition { ReferencePlane1 = "Base",  ReferencePlane2 = "Top",   ParameterName = "CollarLength" },

                    new DimensionDefinition { ReferencePlane1 = "Left",  ReferencePlaneMiddle = "Mid_LR",    ReferencePlane2 = "Right", IsEqual = true },
                    new DimensionDefinition { ReferencePlane1 = "Front", ReferencePlaneMiddle = "Center_FB", ReferencePlane2 = "Back",  IsEqual = true },

                    // Flange.
                    new DimensionDefinition { ReferencePlane1 = "FlangeLeft",  ReferencePlane2 = "FlangeRight", ParameterName = "FlangeWidth"     },
                    new DimensionDefinition { ReferencePlane1 = "FlangeFront", ReferencePlane2 = "FlangeBack",  ParameterName = "FlangeDepth"     },
                    new DimensionDefinition { ReferencePlane1 = "FlangeBot",  ReferencePlane2 = "FlangeTop",   ParameterName = "FlangeThickness" }
                },

                // ── Symbolic lines ──────────────────────────────────────────────
                // Plan-view footprint of the COLLAR only (-300/+300 × -200/+200).
                // No flange outline in this PR — the focus is on the geometry
                // pipeline rather than 2D annotations.
                SymbolicLines = new List<SymbolicLineDefinition>
                {
                    new SymbolicLineDefinition { StartX = -300.0, StartY = -200.0, EndX =  300.0, EndY = -200.0, View = "plan" },
                    new SymbolicLineDefinition { StartX =  300.0, StartY = -200.0, EndX =  300.0, EndY =  200.0, View = "plan" },
                    new SymbolicLineDefinition { StartX =  300.0, StartY =  200.0, EndX = -300.0, EndY =  200.0, View = "plan" },
                    new SymbolicLineDefinition { StartX = -300.0, StartY =  200.0, EndX = -300.0, EndY = -200.0, View = "plan" }
                },

                // ── Geometry ────────────────────────────────────────────────────
                // Two extrusions exercising the multi-geometry pipeline:
                //   collar_main — uses canonical Left/Right/Front/Back/Base/Top
                //                 (no plane overrides → engine fallback).
                //   flange_mid  — overrides all six planes to Flange*.
                //
                // The two solids intersect geometrically at the flange's
                // Z-band (137.5..162.5). The engine's
                // CoincidentGeometryWarningCollector handles any Revit warning
                // non-blockingly.
                Geometry = new List<GeometryDefinition>
                {
                    new GeometryDefinition
                    {
                        Id              = "collar_main",
                        Subcategory     = "Body",
                        Convention      = "Body",
                        Type            = GeometryType.Extrusion,
                        Profile         = "rectangular",
                        WidthParameter  = "Width",
                        DepthParameter  = "Depth",
                        HeightParameter = "CollarLength",
                        // No plane overrides — falls back to canonical
                        // Left / Right / Front / Back / Base / Top.
                        Symmetry        = new List<string> { "LR", "FB" }
                    },
                    new GeometryDefinition
                    {
                        Id              = "flange_mid",
                        Subcategory     = "Body",
                        Convention      = "Body",
                        Type            = GeometryType.Extrusion,
                        Profile         = "rectangular",
                        WidthParameter  = "FlangeWidth",
                        DepthParameter  = "FlangeDepth",
                        HeightParameter = "FlangeThickness",
                        LeftPlane       = "FlangeLeft",
                        RightPlane      = "FlangeRight",
                        FrontPlane      = "FlangeFront",
                        BackPlane       = "FlangeBack",
                        BasePlane       = "FlangeBot",
                        TopPlane        = "FlangeTop",
                        Symmetry        = new List<string> { "LR", "FB" }
                    }
                },

                // ── Formulas ────────────────────────────────────────────────────
                // Intentionally empty — the multi-geometry pipeline is tested
                // in isolation, without formula machinery.
                Formulas = new List<FormulaDefinition>(),

                // ── Types ───────────────────────────────────────────────────────
                // Intentionally empty — single_type strategy.
                Types = new List<FamilyTypeDefinition>(),

                // ── Voids ───────────────────────────────────────────────────────
                // Intentionally empty in this PR.
                Voids = new List<VoidDefinition>(),

                // ── Connectors ──────────────────────────────────────────────────
                // Intentionally empty in this PR. Connectors will return in a
                // follow-up PR once the multi-geometry baseline is validated.
                Connectors = new List<ConnectorDefinition>(),

                Warnings = new List<string>
                {
                    "Sample JSON only — not generated by AI.",
                    "Multi-geometry minimal test: rectangular approximation of a fire damper Collar + Flange.",
                    "Two extrusions: collar_main (uses canonical Left/Right/Front/Back planes) and flange_mid (uses FlangeLeft/FlangeRight/FlangeFront/FlangeBack overrides).",
                    "Collar and flange intersect geometrically at the flange position; this exercises the engine's coincident-geometry handling without being a blocker.",
                    "Flange Z position is statically offset, not driven by CollarLength. If CollarLength flexes, the flange does not auto-recenter. Acceptable limitation for this PR.",
                    "Connectors, voids, types, formulas intentionally omitted from this sample to isolate the multi-geometry pipeline test."
                }
            };
        }
    }
}
