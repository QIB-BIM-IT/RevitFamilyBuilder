using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitFamilyBuilder.Schema
{
    public class FamilyDefinition
    {
        public string SchemaVersion { get; set; }
        public string FamilyName { get; set; }
        public string FamilyTemplate { get; set; }
        public string Category { get; set; }
        public List<ParameterDefinition> Parameters { get; set; }
        public List<ReferencePlaneDefinition> ReferencePlanes { get; set; }
        public List<DimensionDefinition> Dimensions { get; set; }
        public List<SymbolicLineDefinition> SymbolicLines { get; set; }

        // Accept both the new array form (preferred) and the legacy single-object
        // form so older payloads keep parsing. See SingleOrArrayListConverter.
        [JsonConverter(typeof(SingleOrArrayListConverter<GeometryDefinition>))]
        public List<GeometryDefinition> Geometry { get; set; }
        public List<FormulaDefinition> Formulas { get; set; }
        public List<FamilyTypeDefinition> Types { get; set; }
        public List<ConnectorDefinition> Connectors { get; set; }
        public List<VoidDefinition> Voids { get; set; }
        public List<string> Warnings { get; set; }

        /// <summary>
        /// Build strategy requested by Claude.
        /// "single_type"    — no types, no lookup table.
        /// "explicit_types" — named types with explicit parameter values; no lookup table.
        /// "lookup_table"   — named types + size-lookup CSV embedded in the family.
        /// </summary>
        public string BuildStrategy { get; set; }

        public FamilyDefinition()
        {
            Parameters = new List<ParameterDefinition>();
            ReferencePlanes = new List<ReferencePlaneDefinition>();
            Dimensions = new List<DimensionDefinition>();
            SymbolicLines = new List<SymbolicLineDefinition>();
            Geometry = new List<GeometryDefinition>();
            Formulas = new List<FormulaDefinition>();
            Types = new List<FamilyTypeDefinition>();
            Connectors = new List<ConnectorDefinition>();
            Voids = new List<VoidDefinition>();
            Warnings = new List<string>();
        }
    }
}
