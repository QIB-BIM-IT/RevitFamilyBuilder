using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitFamilyBuilder.FamilyBuilder;
using RevitFamilyBuilder.Schema;

namespace RevitFamilyBuilder.Services
{
    public class FamilyGenerationCoordinator
    {
        private readonly FamilyBuilderEngine _engine;

        public FamilyGenerationCoordinator()
        {
            _engine = new FamilyBuilderEngine();
        }

        public string Run(FamilyDefinition definition, UIApplication uiApp)
        {
            var warnings = new List<string>();

            // Lookup integration is active only when Claude explicitly chose the lookup_table strategy.
            // explicit_types and single_type build family types without any lookup-table machinery.
            bool hasLookup = string.Equals(
                definition.BuildStrategy, "lookup_table", StringComparison.OrdinalIgnoreCase);

            Document familyDoc    = _engine.CreateFamilyDocument(definition, uiApp);
            string categoryStatus = _engine.ApplyCategory(familyDoc, definition);
            int paramCount        = _engine.AddParameters(familyDoc, definition);

            // Add the LookupKey text parameter before any type work so it exists
            // when we set per-type values later.
            if (hasLookup)
                _engine.EnsureLookupKeyParameter(familyDoc, warnings);

            int planeCount = _engine.AddReferencePlanes(familyDoc, definition);
            int dimCount   = _engine.AddDimensions(familyDoc, definition, warnings);
            int lineCount  = _engine.AddSymbolicLines(familyDoc, definition, warnings);

            // ApplyTypes MUST run after AddDimensions. Setting dim.FamilyLabel
            // establishes a constraint between a dimension and a parameter. If types
            // with per-type values already exist at that point, Revit resets ALL types'
            // values to match the fixed reference-plane geometry, destroying the
            // per-type values. By creating types afterward, per-type fm.Set() calls
            // flex the already-constrained planes to the correct positions.
            int formulaCount;
            int typesCreated = _engine.ApplyTypes(
                familyDoc, definition, warnings, out formulaCount);

            // Set LookupKey = type name for every Revit type so size_lookup() can
            // resolve the correct CSV row when the type is active.
            if (hasLookup)
                _engine.SetLookupKeysForTypes(familyDoc, definition, warnings);

            List<string> extrusionNames;
            int geoCount       = _engine.AddGeometry(
                familyDoc, definition, warnings, out extrusionNames);

            // Flex validation — runs immediately after geometry is built, before saving.
            // Each test is rolled back so the family document is unchanged afterward.
            string flexReport  = _engine.FlexTest(familyDoc, definition, warnings);

            // Connectors must be placed before voids — a "Through" void
            // destroys the planar face that the connector needs as host.
            int connectorCount = _engine.AddConnectors(familyDoc, definition, warnings);
            int voidCount      = _engine.AddVoids(familyDoc, definition, warnings);

            // ── Lookup table setup (before save so the family doc is still open) ──

            // Resolve the intended save path first — ExportLookupCsv writes the CSV
            // there, and TryEmbedLookupTable reads it from that same path.
            string intendedPath = _engine.GetOutputPath(definition);
            string csvPath      = _engine.ExportLookupCsv(definition, intendedPath);

            string lookupStatus = "—";
            if (csvPath != null)
            {
                bool embedded = _engine.TryEmbedLookupTable(familyDoc, csvPath, warnings);
                if (embedded)
                {
                    string tableName     = Path.GetFileNameWithoutExtension(csvPath);
                    int lookupFormulas   = _engine.TryApplyLookupFormulas(
                        familyDoc, definition, tableName, warnings);
                    lookupStatus = "embedded · " + lookupFormulas + " formula(s) applied";
                }
                else
                {
                    lookupStatus = "CSV exported · not embedded (see warnings)";
                }
            }

            string savedPath = _engine.SaveAndActivateDocument(familyDoc, definition, uiApp);

            var msg = new StringBuilder();
            msg.Append("Family saved successfully.");
            msg.Append("\nFamily:           " + definition.FamilyName);
            msg.Append("\nTemplate:         " + definition.FamilyTemplate);
            msg.Append("\nTypes:            " + typesCreated);
            msg.Append("\nCategory:         " + categoryStatus);
            msg.Append("\nParameters:       " + paramCount);
            msg.Append("\nFormulas:         " + formulaCount);
            msg.Append("\nReference planes: " + planeCount);
            msg.Append("\nDimensions:       " + dimCount);
            msg.Append("\nSymbolic lines:   " + lineCount);
            msg.Append("\nGeometry:         " + geoCount);
            if (extrusionNames != null && extrusionNames.Count > 0)
                msg.Append("\nExtrusions:       " + string.Join(", ", extrusionNames));
            msg.Append("\nVoids:            " + voidCount);
            msg.Append("\nConnectors:       " + connectorCount);
            msg.Append("\nLookup table:     " + lookupStatus);
            msg.Append("\nSaved to:         " + savedPath);
            if (csvPath != null)
                msg.Append("\nLookup CSV:       " + csvPath);

            msg.Append("\n\n" + flexReport.TrimEnd());

            if (warnings.Count > 0)
            {
                msg.Append("\n\nWarnings:");
                foreach (string w in warnings)
                    msg.Append("\n  - " + w);
            }

            return msg.ToString();
        }

        public string RunDryRun(FamilyDefinition definition)
        {
            return _engine.DryRun(definition);
        }
    }
}
