using System;
using System.Collections.Generic;
using System.IO;

namespace RevitFamilyBuilder.FamilyBuilder
{
    /// <summary>
    /// Maps canonical template keys to localized .rft file candidates
    /// and resolves the first matching file under the Revit template root.
    /// </summary>
    public static class FamilyTemplateResolver
    {
        private static readonly Dictionary<string, string[]> _candidates =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GenericModelMetric", new[]
                    {
                        "Metric Generic Model.rft",
                        "Generic Model.rft",
                        "Mod\u00e8le g\u00e9n\u00e9rique m\u00e9trique.rft"
                    }
                }
            };

        /// <summary>
        /// Returns the full path of the first candidate file found under templateRoot.
        /// Throws InvalidOperationException with a clear message on failure.
        /// </summary>
        public static string Resolve(string key, string templateRoot)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Family template key must not be empty.");

            string[] candidates;
            if (!_candidates.TryGetValue(key, out candidates))
                throw new InvalidOperationException(
                    "Unknown family template key: \"" + key + "\".\n"
                    + "Supported keys: " + string.Join(", ", _candidates.Keys) + ".");

            // Revit's FamilyTemplatePath often points to a language subfolder such as
            // "...\Family Templates\French". Moving up one level gives the root that
            // contains all language subfolders, allowing a cross-language file search.
            string searchRoot = NormalizeTemplateRoot(templateRoot);

            foreach (string candidate in candidates)
            {
                string[] matches = Directory.GetFiles(
                    searchRoot, candidate, SearchOption.AllDirectories);

                if (matches.Length > 0)
                    return matches[0];
            }

            throw new InvalidOperationException(
                "Template not found for key \"" + key + "\".\n"
                + "Tried: " + string.Join(", ", candidates) + "\n"
                + "Searched in: " + searchRoot);
        }

        /// <summary>
        /// If templateRoot looks like a language subfolder (its parent exists and is
        /// a valid directory), returns the parent so the recursive search covers all
        /// language subfolders. Otherwise returns templateRoot unchanged.
        /// </summary>
        private static string NormalizeTemplateRoot(string templateRoot)
        {
            string parent = Path.GetDirectoryName(templateRoot);

            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                return parent;

            return templateRoot;
        }
    }
}
