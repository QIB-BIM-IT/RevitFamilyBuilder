using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using RevitFamilyBuilder.Schema;

namespace RevitFamilyBuilder.Services
{
    public class JsonSchemaService
    {
        private static readonly JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            },
            Converters = { new StringEnumConverter(new SnakeCaseNamingStrategy(), false) },
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        public FamilyDefinition Parse(string json)
        {
            return JsonConvert.DeserializeObject<FamilyDefinition>(json, _settings);
        }

        public ValidationResult Validate(FamilyDefinition definition)
        {
            var errors = new List<string>();

            // --- Structural checks (must pass before business rules) ---

            if (string.IsNullOrEmpty(definition.SchemaVersion))
                errors.Add("schema_version is required.");

            if (string.IsNullOrEmpty(definition.FamilyName))
                errors.Add("family_name is required.");

            if (string.IsNullOrEmpty(definition.FamilyTemplate))
                errors.Add("family_template is required.");

            if (string.IsNullOrEmpty(definition.Category))
                errors.Add("category is required.");

            if (definition.Parameters == null)
                errors.Add("parameters list must not be null.");

            if (definition.ReferencePlanes == null)
                errors.Add("reference_planes list must not be null.");

            if (definition.Geometry == null)
                errors.Add("geometry list must not be null.");

            // Return early — business rules cannot run safely on null lists.
            if (errors.Count > 0)
                return ValidationResult.Fail(errors.ToArray());

            // --- Build lookup sets for cross-reference checks ---

            var parameterNames = new HashSet<string>();
            var planeNames = new HashSet<string>();

            // --- Parameter rules ---

            foreach (ParameterDefinition param in definition.Parameters)
            {
                if (string.IsNullOrWhiteSpace(param.Name))
                {
                    errors.Add("A parameter has an empty name.");
                    continue;
                }

                if (!parameterNames.Add(param.Name))
                    errors.Add("Duplicate parameter name: \"" + param.Name + "\".");
            }

            // --- Reference plane rules ---

            foreach (ReferencePlaneDefinition plane in definition.ReferencePlanes)
            {
                if (string.IsNullOrWhiteSpace(plane.Name))
                {
                    errors.Add("A reference plane has an empty name.");
                    continue;
                }

                if (!planeNames.Add(plane.Name))
                    errors.Add("Duplicate reference plane name: \"" + plane.Name + "\".");
            }

            // --- Dimension rules ---

            if (definition.Dimensions != null)
            {
                foreach (DimensionDefinition dim in definition.Dimensions)
                {
                    if (!planeNames.Contains(dim.ReferencePlane1))
                        errors.Add("Dimension references unknown reference plane: \"" + dim.ReferencePlane1 + "\".");

                    if (dim.ReferencePlane2 != null && !planeNames.Contains(dim.ReferencePlane2))
                        errors.Add("Dimension references unknown reference plane: \"" + dim.ReferencePlane2 + "\".");

                    if (!string.IsNullOrWhiteSpace(dim.ParameterName) && !parameterNames.Contains(dim.ParameterName))
                        errors.Add("Dimension references unknown parameter: \"" + dim.ParameterName + "\".");
                }
            }

            // --- Geometry rules ---

            if (definition.Geometry.Count == 0)
            {
                errors.Add("geometry must contain at least one item.");
            }
            else
            {
                // Each geometry declares (explicitly or by default) the six
                // reference planes its faces will be locked to. A missing plane
                // must be reported against the geometry that needs it, so two
                // geometries bounded by different planes can both be checked
                // in isolation.
                string[] canonicalDefaults =
                    { "Left", "Right", "Front", "Back", "Base", "Top" };
                string[] slotLabels =
                    { "left", "right", "front", "back", "base", "top" };

                var geometryIds = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < definition.Geometry.Count; i++)
                {
                    GeometryDefinition geo = definition.Geometry[i];
                    string label = "geometry[" + i + "]";

                    // "id" is mandatory and must be unique across the array.
                    // "subcategory" is intentionally NOT validated here — it is
                    // optional and resolved at build time by the engine.
                    if (string.IsNullOrWhiteSpace(geo.Id))
                    {
                        errors.Add(label + ": id must not be empty.");
                    }
                    else if (!geometryIds.Add(geo.Id))
                    {
                        errors.Add(label + ": duplicate geometry id \""
                            + geo.Id + "\".");
                    }

                    if (string.IsNullOrWhiteSpace(geo.Profile))
                        errors.Add(label + ": profile must not be empty.");

                    if (!parameterNames.Contains(geo.WidthParameter))
                        errors.Add(label + ": references unknown width parameter: \""
                            + geo.WidthParameter + "\".");

                    if (!parameterNames.Contains(geo.DepthParameter))
                        errors.Add(label + ": references unknown depth parameter: \""
                            + geo.DepthParameter + "\".");

                    if (!parameterNames.Contains(geo.HeightParameter))
                        errors.Add(label + ": references unknown height parameter: \""
                            + geo.HeightParameter + "\".");

                    // Resolve the six effective plane names (override or
                    // canonical default) and make sure each one exists in the
                    // reference-plane pool.
                    string[] effectiveNames =
                    {
                        string.IsNullOrWhiteSpace(geo.LeftPlane)
                            ? canonicalDefaults[0] : geo.LeftPlane.Trim(),
                        string.IsNullOrWhiteSpace(geo.RightPlane)
                            ? canonicalDefaults[1] : geo.RightPlane.Trim(),
                        string.IsNullOrWhiteSpace(geo.FrontPlane)
                            ? canonicalDefaults[2] : geo.FrontPlane.Trim(),
                        string.IsNullOrWhiteSpace(geo.BackPlane)
                            ? canonicalDefaults[3] : geo.BackPlane.Trim(),
                        string.IsNullOrWhiteSpace(geo.BasePlane)
                            ? canonicalDefaults[4] : geo.BasePlane.Trim(),
                        string.IsNullOrWhiteSpace(geo.TopPlane)
                            ? canonicalDefaults[5] : geo.TopPlane.Trim()
                    };

                    var reportedPerGeo = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    for (int s = 0; s < effectiveNames.Length; s++)
                    {
                        string planeName = effectiveNames[s];
                        if (planeNames.Contains(planeName)) continue;
                        if (!reportedPerGeo.Add(planeName)) continue;

                        errors.Add(label + ": " + slotLabels[s]
                            + "_plane references unknown reference plane \""
                            + planeName + "\".");
                    }
                }
            }

            // --- Type rules (optional — null or empty list is valid) ---

            if (definition.Types != null && definition.Types.Count > 0)
            {
                var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < definition.Types.Count; i++)
                {
                    FamilyTypeDefinition t = definition.Types[i];
                    string label = "types[" + i + "]";

                    if (string.IsNullOrWhiteSpace(t.Name))
                    {
                        errors.Add(label + ": name must not be empty.");
                        continue;
                    }

                    if (!typeNames.Add(t.Name))
                    {
                        errors.Add(label + ": duplicate type name \"" + t.Name + "\".");
                        continue;
                    }

                    if (t.ParameterValues != null)
                    {
                        foreach (string key in t.ParameterValues.Keys)
                        {
                            if (!parameterNames.Contains(key))
                                errors.Add(label + " (\"" + t.Name
                                    + "\"): parameter_values key \""
                                    + key + "\" does not match any declared parameter.");
                        }
                    }
                }
            }

            // --- Formula rules (optional — null or empty list is valid) ---

            if (definition.Formulas != null && definition.Formulas.Count > 0)
            {
                for (int i = 0; i < definition.Formulas.Count; i++)
                {
                    FormulaDefinition f = definition.Formulas[i];
                    string label = "formulas[" + i + "]";

                    if (string.IsNullOrWhiteSpace(f.ParameterName))
                    {
                        errors.Add(label + ": parameter_name must not be empty.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(f.Expression))
                    {
                        errors.Add(label + " (\"" + f.ParameterName
                            + "\"): expression must not be empty.");
                        continue;
                    }

                    if (!parameterNames.Contains(f.ParameterName))
                    {
                        errors.Add(label + ": parameter_name \""
                            + f.ParameterName
                            + "\" does not match any declared parameter.");
                    }
                }
            }

            // --- Connector rules (optional — null or empty list is valid) ---

            var validFaces = new HashSet<string>(
                new[] { "FRONT", "BACK", "LEFT", "RIGHT", "TOP", "BOTTOM" },
                StringComparer.OrdinalIgnoreCase);

            if (definition.Connectors != null && definition.Connectors.Count > 0)
            {
                for (int i = 0; i < definition.Connectors.Count; i++)
                {
                    ConnectorDefinition c = definition.Connectors[i];
                    string label = "connectors[" + i + "]";

                    if (string.IsNullOrWhiteSpace(c.Name))
                    {
                        errors.Add(label + ": name must not be empty.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(c.Domain))
                        errors.Add(label + " (\"" + c.Name + "\"): domain is required.");

                    // v1: shape is required and must be Round.
                    if (string.IsNullOrWhiteSpace(c.Shape))
                    {
                        errors.Add(label + " (\"" + c.Name + "\"): shape is required.");
                    }
                    else if (!string.Equals(c.Shape.Trim(), "Round", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(label + " (\"" + c.Name
                            + "\"): shape \"" + c.Shape
                            + "\" is not supported in v1; use \"Round\".");
                    }

                    // v1: face is required and must be one of the six named faces.
                    if (string.IsNullOrWhiteSpace(c.Face))
                    {
                        errors.Add(label + " (\"" + c.Name + "\"): face is required.");
                    }
                    else if (!validFaces.Contains(c.Face.Trim()))
                    {
                        errors.Add(label + " (\"" + c.Name
                            + "\"): face \"" + c.Face
                            + "\" is not valid; must be Front, Back, Left, Right, Top, or Bottom.");
                    }

                    // v1: location is required and must be Center.
                    if (string.IsNullOrWhiteSpace(c.Location))
                    {
                        errors.Add(label + " (\"" + c.Name + "\"): location is required.");
                    }
                    else if (!string.Equals(c.Location.Trim(), "Center", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(label + " (\"" + c.Name
                            + "\"): location \"" + c.Location
                            + "\" is not supported in v1; use \"Center\".");
                    }

                    // diameter_parameter must reference an existing parameter.
                    if (!string.IsNullOrWhiteSpace(c.DiameterParameter)
                        && !parameterNames.Contains(c.DiameterParameter))
                    {
                        errors.Add(label + " (\"" + c.Name
                            + "\"): diameter_parameter \""
                            + c.DiameterParameter
                            + "\" does not match any declared parameter.");
                    }
                }
            }

            // --- Void rules (optional — null or empty list is valid) ---

            if (definition.Voids != null && definition.Voids.Count > 0)
            {
                for (int i = 0; i < definition.Voids.Count; i++)
                {
                    VoidDefinition v = definition.Voids[i];
                    string label = "voids[" + i + "]";

                    if (string.IsNullOrWhiteSpace(v.Name))
                    {
                        errors.Add(label + ": name must not be empty.");
                        continue;
                    }

                    // v1: shape must be Rectangular.
                    if (string.IsNullOrWhiteSpace(v.Shape))
                    {
                        errors.Add(label + " (\"" + v.Name + "\"): shape is required.");
                    }
                    else if (!string.Equals(v.Shape.Trim(), "Rectangular", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(label + " (\"" + v.Name
                            + "\"): shape \"" + v.Shape
                            + "\" is not supported in v1; use \"Rectangular\".");
                    }

                    // v1: face must be Front.
                    if (string.IsNullOrWhiteSpace(v.Face))
                    {
                        errors.Add(label + " (\"" + v.Name + "\"): face is required.");
                    }
                    else if (!string.Equals(v.Face.Trim(), "Front", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(label + " (\"" + v.Name
                            + "\"): face \"" + v.Face
                            + "\" is not supported in v1; use \"Front\".");
                    }

                    // v1: location must be Center.
                    if (string.IsNullOrWhiteSpace(v.Location))
                    {
                        errors.Add(label + " (\"" + v.Name + "\"): location is required.");
                    }
                    else if (!string.Equals(v.Location.Trim(), "Center", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(label + " (\"" + v.Name
                            + "\"): location \"" + v.Location
                            + "\" is not supported in v1; use \"Center\".");
                    }

                    // v1: cut must be Through.
                    if (string.IsNullOrWhiteSpace(v.Cut))
                    {
                        errors.Add(label + " (\"" + v.Name + "\"): cut is required.");
                    }
                    else if (!string.Equals(v.Cut.Trim(), "Through", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(label + " (\"" + v.Name
                            + "\"): cut \"" + v.Cut
                            + "\" is not supported in v1; use \"Through\".");
                    }

                    // width_parameter must reference an existing parameter.
                    if (string.IsNullOrWhiteSpace(v.WidthParameter))
                    {
                        errors.Add(label + " (\"" + v.Name + "\"): width_parameter is required.");
                    }
                    else if (!parameterNames.Contains(v.WidthParameter))
                    {
                        errors.Add(label + " (\"" + v.Name
                            + "\"): width_parameter \""
                            + v.WidthParameter
                            + "\" does not match any declared parameter.");
                    }

                    // height_parameter must reference an existing parameter.
                    if (string.IsNullOrWhiteSpace(v.HeightParameter))
                    {
                        errors.Add(label + " (\"" + v.Name + "\"): height_parameter is required.");
                    }
                    else if (!parameterNames.Contains(v.HeightParameter))
                    {
                        errors.Add(label + " (\"" + v.Name
                            + "\"): height_parameter \""
                            + v.HeightParameter
                            + "\" does not match any declared parameter.");
                    }
                }
            }

            if (errors.Count > 0)
                return ValidationResult.Fail(errors.ToArray());

            return ValidationResult.Ok();
        }
    }
}
