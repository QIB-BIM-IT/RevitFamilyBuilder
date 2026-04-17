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
                // All dimensional parameters are type parameters (IsInstance = false)
                // so that size_lookup() formulas and dimension labels work correctly.
                Parameters = new List<ParameterDefinition>
                {
                    new ParameterDefinition { Name = "Width",         Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "600" },
                    new ParameterDefinition { Name = "Depth",         Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "400" },
                    new ParameterDefinition { Name = "Height",        Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "300" },
                    new ParameterDefinition { Name = "Diameter",      Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "200" },
                    new ParameterDefinition { Name = "OpeningWidth",  Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "300" },
                    new ParameterDefinition { Name = "OpeningHeight", Type = ParameterType.Length, Group = "Dimensions", IsInstance = false, DefaultValue = "200" }
                },

                // ── Reference planes ────────────────────────────────────────────
                // Lateral planes (Left/Right/Front/Back) use "vertical" or "horizontal"
                // orientation and live in the plan view (X-normal or Y-normal planes).
                //
                // Elevation planes (Base/Top) use "elevation" orientation, which creates
                // Z-normal planes visible in the front-elevation view.  These are the
                // ONLY planes that can correctly constrain the extrusion top/bottom faces.
                //
                // Centre planes at offset 0 are used for EQ symmetry dimensions below.
                ReferencePlanes = new List<ReferencePlaneDefinition>
                {
                    // Symmetry centres — must exist before EQ dimensions are created.
                    new ReferencePlaneDefinition { Name = "Center_LR", Orientation = "vertical",   Offset =    0.0 },
                    new ReferencePlaneDefinition { Name = "Center_FB", Orientation = "horizontal", Offset =    0.0 },

                    // Lateral bounding planes.
                    new ReferencePlaneDefinition { Name = "Left",  Orientation = "vertical",   Offset = -300.0 },
                    new ReferencePlaneDefinition { Name = "Right", Orientation = "vertical",   Offset =  300.0 },
                    new ReferencePlaneDefinition { Name = "Front", Orientation = "horizontal", Offset = -200.0 },
                    new ReferencePlaneDefinition { Name = "Back",  Orientation = "horizontal", Offset =  200.0 },

                    // Elevation planes (Z-normal) — REQUIRED for height flex to work.
                    // "elevation" orientation creates planes with normal (0,0,1) at the
                    // given Z offset.  Without this, the extrusion top/bottom faces
                    // cannot be locked and Height will not drive the geometry.
                    new ReferencePlaneDefinition { Name = "Base", Orientation = "elevation", Offset =   0.0 },
                    new ReferencePlaneDefinition { Name = "Top",  Orientation = "elevation", Offset = 300.0 }
                },

                // ── Dimensions ──────────────────────────────────────────────────
                // Three labelled dimensions drive Width, Depth, Height parameters.
                // Two additional EQ dimensions lock the centre planes to the midpoints,
                // ensuring Left-Right and Front-Back symmetry around the origin.
                //
                // EQ dimension rules:
                //   • ReferencePlaneMiddle must name the centre plane.
                //   • IsEqual = true marks every segment as IsEqualDriven.
                //   • Leave ParameterName empty on EQ dims (no label, only constraint).
                Dimensions = new List<DimensionDefinition>
                {
                    // Labelled driving dimensions.
                    new DimensionDefinition { ReferencePlane1 = "Left",  ReferencePlane2 = "Right", ParameterName = "Width"  },
                    new DimensionDefinition { ReferencePlane1 = "Front", ReferencePlane2 = "Back",  ParameterName = "Depth"  },
                    new DimensionDefinition { ReferencePlane1 = "Base",  ReferencePlane2 = "Top",   ParameterName = "Height" },

                    // EQ symmetry constraints — no parameter label, only constraint.
                    new DimensionDefinition { ReferencePlane1 = "Left",  ReferencePlaneMiddle = "Center_LR", ReferencePlane2 = "Right", IsEqual = true },
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
                // Two rectangular extrusions sharing the same eight reference
                // planes — geometrically superposed on purpose. This is the
                // smallest possible test of the multi-geometry foundation:
                // only the names differ. All six faces of each extrusion are
                // locked to Left / Right / Front / Back / Base / Top.
                //
                // "symmetry" declares the axes on which the AI intends symmetry.
                // The actual EQ constraints come from the "dimensions" entries above;
                // this field is used by the AI layer to know which centre planes to
                // declare and which EQ dims to include.
                Geometry = new List<GeometryDefinition>
                {
                    new GeometryDefinition
                    {
                        Name            = "Body_Primary",
                        Type            = GeometryType.Extrusion,
                        Profile         = "rectangular",
                        WidthParameter  = "Width",
                        DepthParameter  = "Depth",
                        HeightParameter = "Height",
                        Symmetry        = new List<string> { "LR", "FB" }
                    },
                    new GeometryDefinition
                    {
                        Name            = "Body_Secondary",
                        Type            = GeometryType.Extrusion,
                        Profile         = "rectangular",
                        WidthParameter  = "Width",
                        DepthParameter  = "Depth",
                        HeightParameter = "Height",
                        Symmetry        = new List<string> { "LR", "FB" }
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
                Voids = new List<VoidDefinition>
                {
                    new VoidDefinition
                    {
                        Name            = "FrontOpening",
                        Shape           = "Rectangular",
                        Face            = "Front",
                        Location        = "Center",
                        WidthParameter  = "OpeningWidth",
                        HeightParameter = "OpeningHeight",
                        Cut             = "Through"
                    }
                },

                // ── Connectors ──────────────────────────────────────────────────
                Connectors = new List<ConnectorDefinition>
                {
                    new ConnectorDefinition
                    {
                        Name              = "Primary",
                        Domain            = "HVAC",
                        Shape             = "Round",
                        Face              = "Back",
                        Location          = "Center",
                        DiameterParameter = "Diameter"
                    }
                },

                Warnings = new List<string>
                {
                    "Sample JSON only — not generated by AI.",
                    "Sample JSON — exercises explicit_types strategy with formula.",
                    "Height is formula-driven (Width / 2); FlexTest will skip it when testing directly.",
                    "EQ dimensions for Center_LR and Center_FB require those planes to exist before AddDimensions runs."
                }
            };
        }
    }
}
