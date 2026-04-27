using System;
using System.Collections.Generic;

namespace RevitFamilyBuilder.Config
{
    /// <summary>
    /// Static catalogue of company drafting-standard presets.
    ///
    /// <para>This PR seeds the library with a single, deliberately neutral
    /// convention named <c>"Body"</c> whose only effective field is
    /// <c>SubcategoryName = "Body"</c>. All other slots are <c>null</c> so
    /// the visual output of the existing sample stays IDENTICAL — this PR
    /// is a non-regression test for the convention-resolution machinery.</para>
    ///
    /// <para>Adding new presets is a future PR concern; lookup is
    /// case-insensitive so JSON authors can write <c>"body"</c> or <c>"Body"</c>
    /// interchangeably.</para>
    /// </summary>
    public static class ConventionLibrary
    {
        public static readonly IReadOnlyDictionary<string, GeometryConvention>
            Conventions = BuildConventions();

        private static IReadOnlyDictionary<string, GeometryConvention> BuildConventions()
        {
            var dict = new Dictionary<string, GeometryConvention>(
                StringComparer.OrdinalIgnoreCase);

            // ── Body ──────────────────────────────────────────────────────
            // Placeholder values: only SubcategoryName is set so that this
            // PR's sample produces the exact same family as before. Colour,
            // projection, pattern and material are intentionally null; they
            // will be filled in once the BIM team confirms the company values.
            dict["Body"] = new GeometryConvention
            {
                Name                = "Body",
                SubcategoryName     = "Body",
                LineColorRgb        = null,
                LineProjection      = null,
                LinePatternName     = null,
                DefaultMaterialName = null
            };

            return dict;
        }

        /// <summary>
        /// Returns the convention preset registered under <paramref name="name"/>.
        /// Throws <see cref="ArgumentException"/> when the name is unknown — by
        /// the time the engine reaches this call, the JSON validator has
        /// already rejected unknown names with a clearer per-geometry error
        /// message, so this exception is a defensive last line of defence.
        /// </summary>
        public static GeometryConvention Get(string name)
        {
            GeometryConvention conv;
            if (name == null || !Conventions.TryGetValue(name, out conv))
            {
                throw new ArgumentException(
                    "Convention '" + (name ?? "<null>")
                    + "' is unknown. Available conventions: "
                    + string.Join(", ", AvailableNames()) + ".");
            }
            return conv;
        }

        /// <summary>
        /// Returns the registered convention names in declaration order,
        /// formatted for the build-report's "Standards d'entreprise" section.
        /// </summary>
        public static IEnumerable<string> AvailableNames()
        {
            return Conventions.Keys;
        }
    }
}
