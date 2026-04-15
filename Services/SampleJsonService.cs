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
                BuildStrategy  = "lookup_table",

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
                // Height is NOT constrained via reference planes.  Instead, the engine
                // associates the extrusion's EXTRUSION_END_PARAM directly with the
                // "Height" family parameter.  This is the standard Revit pattern and
                // avoids the "dimension can not be labeled" error that occurs when
                // trying to label dimensions between Z-normal planes in elevation view.
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
                    new ReferencePlaneDefinition { Name = "Back",  Orientation = "horizontal", Offset =  200.0 }
                },

                // ── Dimensions ──────────────────────────────────────────────────
                // Width and Depth are driven by labelled dimensions between reference
                // planes in the plan view (the standard Revit family pattern).
                //
                // Height is NOT driven by a labelled dimension.  Revit does not allow
                // labelling dimensions between Z-normal planes in the elevation view
                // via the API ("This dimension can not be labeled").  Instead, Height
                // is constrained by associating the extrusion's EXTRUSION_END_PARAM
                // directly with the Height family parameter (done in AddGeometry).
                //
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
                // A single rectangular extrusion built by BuildRectangularExtrusion.
                // The 4 lateral faces are locked to Left/Right/Front/Back reference
                // planes via NewAlignment in the plan view.
                // The extrusion height is driven by associating EXTRUSION_END_PARAM
                // directly with the "Height" family parameter (sketch plane at Z = 0).
                //
                // "symmetry" declares the axes on which the AI intends symmetry.
                // The actual EQ constraints come from the "dimensions" entries above;
                // this field is used by the AI layer to know which centre planes to
                // declare and which EQ dims to include.
                Geometry = new List<GeometryDefinition>
                {
                    new GeometryDefinition
                    {
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
                    "Height is formula-driven (Width / 2); FlexTest will skip it when testing directly.",
                    "Height is constrained via EXTRUSION_END_PARAM association (not by a labelled dimension).",
                    "EQ dimensions for Center_LR and Center_FB require those planes to exist before AddDimensions runs.",
                    "BuildStrategy = lookup_table exercises LookupKey parameter, CSV export, embed, and size_lookup formulas.",
                    "size_lookup formulas skip Height because it is already formula-driven (Width / 2)."
                }
            };
        }
    }
}
