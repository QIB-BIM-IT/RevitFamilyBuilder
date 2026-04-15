using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RevitFamilyBuilder.Schema;

namespace RevitFamilyBuilder.Services
{
    public class AiReviewSummary
    {
        public string Model       { get; set; }
        public string Dimensions  { get; set; }
        public string Source      { get; set; }
        public string Confidence  { get; set; }
        public string Warnings    { get; set; }
        public string BuildSummary { get; set; }
    }

    public static class AiReviewSummaryService
    {
        // Matches "Cat No", "Model", "SKU", "Part No", "Item No", "Ref" followed by an identifier token.
        private static readonly Regex _catalogPattern = new Regex(
            @"\b(cat\s*no|model|sku|part\s*no|item\s*no|ref)\b[:\s#]*([A-Z0-9][A-Z0-9\-_]{1,20})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] _mainDimNames = { "Width", "Depth", "Height" };

        public static AiReviewSummary Build(
            FamilyDefinition definition,
            string rawPrompt,
            bool hasImage)
        {
            return new AiReviewSummary
            {
                Model        = BuildModel(definition, rawPrompt),
                Dimensions   = BuildDimensions(definition),
                Source       = hasImage
                                 ? "Claude analysis from prompt + image"
                                 : "Claude analysis from prompt",
                Confidence   = BuildConfidence(definition),
                Warnings     = BuildWarnings(definition),
                BuildSummary = BuildSummaryText(definition)
            };
        }

        // ── Field builders ────────────────────────────────────────────────────

        private static string BuildModel(FamilyDefinition definition, string rawPrompt)
        {
            string name = string.IsNullOrWhiteSpace(definition.FamilyName)
                ? null : definition.FamilyName.Trim();

            string token = ExtractCatalogToken(rawPrompt ?? string.Empty);

            if (name != null && token != null
                && name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return name + "  (" + token + ")";
            }

            return name ?? "—";
        }

        private static string ExtractCatalogToken(string prompt)
        {
            Match m = _catalogPattern.Match(prompt);
            return m.Success ? m.Groups[2].Value.Trim() : null;
        }

        private static string BuildDimensions(FamilyDefinition definition)
        {
            if (definition.Parameters == null || definition.Parameters.Count == 0)
                return "—";

            var parts = new List<string>();
            foreach (string target in _mainDimNames)
            {
                foreach (ParameterDefinition p in definition.Parameters)
                {
                    if (string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase))
                    {
                        string val = string.IsNullOrWhiteSpace(p.DefaultValue)
                            ? "?" : p.DefaultValue + " mm";
                        parts.Add(target + " = " + val);
                        break;
                    }
                }
            }

            return parts.Count > 0 ? string.Join(",  ", parts) : "—";
        }

        private static string BuildConfidence(FamilyDefinition definition)
        {
            bool hasWarnings = definition.Warnings != null && definition.Warnings.Count > 0;
            int  dimCount    = CountPresentMainDims(definition);
            bool hasParams   = definition.Parameters != null && definition.Parameters.Count > 0;
            bool hasGeometry = definition.Geometry   != null && definition.Geometry.Count   > 0;

            // Very incomplete output
            if (!hasParams || !hasGeometry || dimCount == 0)
                return "Low";

            // Two or more main dimensions absent
            if (dimCount <= 1)
                return "Low";

            // Warnings present or one main dimension missing
            if (hasWarnings || dimCount == 2)
                return "Medium";

            return "High";
        }

        private static int CountPresentMainDims(FamilyDefinition definition)
        {
            if (definition.Parameters == null) return 0;

            int count = 0;
            foreach (string target in _mainDimNames)
            {
                foreach (ParameterDefinition p in definition.Parameters)
                {
                    if (string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(p.DefaultValue))
                    {
                        count++;
                        break;
                    }
                }
            }
            return count;
        }

        private static string BuildWarnings(FamilyDefinition definition)
        {
            if (definition.Warnings == null || definition.Warnings.Count == 0)
                return "None";

            return string.Join("\n", definition.Warnings);
        }

        private static string BuildSummaryText(FamilyDefinition definition)
        {
            var parts = new List<string>();

            // Family category / type
            parts.Add(string.IsNullOrWhiteSpace(definition.Category)
                ? "Generic Model" : definition.Category.Trim());

            // Named dimension parameters (Width / Depth / Height present in definition)
            var foundDims = new List<string>();
            if (definition.Parameters != null)
            {
                foreach (string target in _mainDimNames)
                {
                    foreach (ParameterDefinition p in definition.Parameters)
                    {
                        if (string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase))
                        {
                            foundDims.Add(target);
                            break;
                        }
                    }
                }
            }

            if (foundDims.Count > 0)
                parts.Add(string.Join(" / ", foundDims) + " parameters");
            else if (definition.Parameters != null && definition.Parameters.Count > 0)
                parts.Add(definition.Parameters.Count + " parameter(s)");
            else
                parts.Add("no parameters");

            // Reference planes
            int planeCount = definition.ReferencePlanes != null ? definition.ReferencePlanes.Count : 0;
            if (planeCount > 0)
                parts.Add(planeCount + " reference plane" + (planeCount == 1 ? "" : "s"));

            // Geometry
            if (definition.Geometry != null && definition.Geometry.Count > 0)
                parts.Add("rectangular extrusion");
            else
                parts.Add("no geometry");

            return string.Join("  ·  ", parts);
        }
    }
}
