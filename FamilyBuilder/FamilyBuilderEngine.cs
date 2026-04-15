using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using RevitFamilyBuilder.Schema;

namespace RevitFamilyBuilder.FamilyBuilder
{
    public class FamilyBuilderEngine
    {
        public Document CreateFamilyDocument(FamilyDefinition definition, UIApplication uiApp)
        {
            string templateRoot = uiApp.Application.FamilyTemplatePath;

            if (string.IsNullOrWhiteSpace(templateRoot) || !Directory.Exists(templateRoot))
                throw new InvalidOperationException(
                    "Revit family template path is not configured or does not exist: \""
                    + templateRoot + "\".");

            string templatePath = FamilyTemplateResolver.Resolve(definition.FamilyTemplate, templateRoot);

            Document familyDoc = uiApp.Application.NewFamilyDocument(templatePath);

            if (familyDoc == null)
                throw new InvalidOperationException(
                    "Revit returned a null document for template: \"" + templatePath + "\".");

            return familyDoc;
        }

        // Ensures the family document has a valid CurrentType before parameters or formulas are applied.
        // Returns true if a new type was created, false if one already existed.
        public bool EnsureFamilyType(Document familyDoc)
        {
            FamilyManager fm = familyDoc.FamilyManager;

            if (fm.CurrentType != null)
                return false;

            using (Transaction tx = new Transaction(familyDoc, "Create Default Family Type"))
            {
                tx.Start();
                fm.NewType("Default");
                tx.Commit();
            }

            return true;
        }

        public string ApplyCategory(Document familyDoc, FamilyDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(definition.Category))
                return "skipped (no category specified)";

            BuiltInCategory bic = MapCategoryKey(definition.Category);
            if (bic == BuiltInCategory.INVALID)
                return "unsupported category: \"" + definition.Category + "\"";

            Category targetCategory = familyDoc.Settings.Categories.get_Item(bic);
            if (targetCategory == null)
                return "category not available in Revit: \"" + definition.Category + "\"";

            Family ownerFamily = familyDoc.OwnerFamily;
            if (ownerFamily == null)
                return "could not access the family owner element";

            if (ownerFamily.FamilyCategory != null
                && ownerFamily.FamilyCategory.Id == targetCategory.Id)
                return "already correct";

            using (Transaction tx = new Transaction(familyDoc, "Set Family Category"))
            {
                tx.Start();
                ownerFamily.FamilyCategory = targetCategory;
                tx.Commit();
            }

            return "applied";
        }

        private static BuiltInCategory MapCategoryKey(string category)
        {
            switch (category.Trim().ToUpperInvariant())
            {
                case "GENERIC MODELS":
                case "GENERIC MODEL":
                case "OST_GENERICMODEL":
                    return BuiltInCategory.OST_GenericModel;
                default:
                    return BuiltInCategory.INVALID;
            }
        }

        public int AddParameters(Document familyDoc, FamilyDefinition definition)
        {
            if (definition.Parameters == null || definition.Parameters.Count == 0)
                return 0;

            FamilyManager fm = familyDoc.FamilyManager;

            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter existing in fm.GetParameters())
                existingNames.Add(existing.Definition.Name);

            int created = 0;

            using (Transaction tx = new Transaction(familyDoc, "Add Family Parameters"))
            {
                tx.Start();

                foreach (ParameterDefinition paramDef in definition.Parameters)
                {
                    if (string.IsNullOrWhiteSpace(paramDef.Name)) continue;
                    if (existingNames.Contains(paramDef.Name)) continue;

                    ForgeTypeId specType  = MapSpecTypeId(paramDef.Type);
                    ForgeTypeId groupType = MapGroupTypeId(paramDef.Group);

                    try
                    {
                        FamilyParameter fp = fm.AddParameter(
                            paramDef.Name, groupType, specType, paramDef.IsInstance);

                        if (fp != null)
                        {
                            TrySetDefaultValue(fm, fp, paramDef);
                            existingNames.Add(paramDef.Name);
                            created++;
                        }
                    }
                    catch
                    {
                        // Skip any parameter that fails; continue with the rest.
                    }
                }

                tx.Commit();
            }

            return created;
        }

        // Applies formula expressions to family parameters using Revit's family parameter API.
        // Each formula runs in its own transaction so a single failure does not abort the rest.
        public int ApplyFormulas(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            if (definition.Formulas == null || definition.Formulas.Count == 0)
                return 0;

            FamilyManager fm = familyDoc.FamilyManager;

            var paramsByName = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter fp in fm.GetParameters())
                paramsByName[fp.Definition.Name] = fp;

            int applied = 0;

            foreach (FormulaDefinition formulaDef in definition.Formulas)
            {
                if (string.IsNullOrWhiteSpace(formulaDef.ParameterName)
                    || string.IsNullOrWhiteSpace(formulaDef.Expression))
                    continue;

                FamilyParameter fp;
                if (!paramsByName.TryGetValue(formulaDef.ParameterName, out fp))
                {
                    warnings.Add("Formula skipped: parameter \""
                        + formulaDef.ParameterName + "\" not found in the family document.");
                    continue;
                }

                // Revit does not support formulas that target or reference instance parameters.
                if (HasInstanceConflict(fp, formulaDef.Expression, paramsByName))
                {
                    warnings.Add("Formula skipped (\""
                        + formulaDef.ParameterName + " = " + formulaDef.Expression
                        + "\"): formulas are only supported on type-parameter workflows in this builder.");
                    continue;
                }

                using (Transaction tx = new Transaction(
                    familyDoc, "Apply Formula: " + formulaDef.ParameterName))
                {
                    tx.Start();
                    try
                    {
                        string formulaActiveType = fm.CurrentType != null
                            ? fm.CurrentType.Name : "(none)";
                        Debug.WriteLine("[TypeDiag] FORMULA ActiveType=\"" + formulaActiveType
                            + "\" Param=\"" + formulaDef.ParameterName
                            + "\" Expr=\"" + formulaDef.Expression + "\"");

                        fm.SetFormula(fp, formulaDef.Expression);
                        applied++;
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        warnings.Add("Formula \""
                            + formulaDef.ParameterName + " = " + formulaDef.Expression
                            + "\" could not be applied: " + ex.Message);
                    }
                }
            }

            return applied;
        }

        // Unified type-management entry point called by the coordinator.
        //
        // • No explicit types (Types null/empty): falls back to EnsureFamilyType + ApplyFormulas.
        // • Explicit types provided: creates each type, sets it current, applies its parameter
        //   values, then applies formulas in that type's context.
        //
        // Returns the number of types created; formulaCount receives the total formulas applied.
        public int ApplyTypes(
            Document familyDoc, FamilyDefinition definition,
            IList<string> warnings, out int formulaCount)
        {
            formulaCount = 0;

            bool hasExplicitTypes = definition.Types != null && definition.Types.Count > 0;

            if (!hasExplicitTypes)
            {
                bool created = EnsureFamilyType(familyDoc);
                formulaCount = ApplyFormulas(familyDoc, definition, warnings);
                return created ? 1 : 0;
            }

            FamilyManager fm = familyDoc.FamilyManager;

            // Build parameter lookup once — it does not change as types are added.
            var paramsByName = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter fp in fm.GetParameters())
                paramsByName[fp.Definition.Name] = fp;

            int typesCreated = 0;

            foreach (FamilyTypeDefinition typeDef in definition.Types)
            {
                if (string.IsNullOrWhiteSpace(typeDef.Name)) continue;

                // Create the type and capture the returned FamilyType handle.
                FamilyType createdType = null;
                using (Transaction tx = new Transaction(
                    familyDoc, "Create Family Type: " + typeDef.Name))
                {
                    tx.Start();
                    try
                    {
                        createdType = fm.NewType(typeDef.Name);
                        typesCreated++;
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        warnings.Add("Type \"" + typeDef.Name
                            + "\" could not be created: " + ex.Message);
                        continue;
                    }
                }

                // Explicitly activate this type so that fm.Set() calls below target
                // the correct type. FamilyManager.CurrentType requires an open Transaction
                // because it triggers an internal SubTransaction in the Revit API.
                if (createdType != null)
                {
                    using (Transaction txActivate = new Transaction(
                        familyDoc, "Activate Type: " + typeDef.Name))
                    {
                        txActivate.Start();
                        fm.CurrentType = createdType;
                        txActivate.Commit();
                    }
                }

                // Apply this type's parameter values in one transaction.
                if (typeDef.ParameterValues != null && typeDef.ParameterValues.Count > 0)
                    ApplyTypeParameterValues(familyDoc, fm, typeDef, paramsByName, definition, warnings);
            }

            // Apply formulas once, after all types and their explicit values are set.
            // fm.SetFormula() is a family-wide (not per-type) operation. Calling it inside
            // the type loop caused a re-evaluation on every iteration, making Revit overwrite
            // earlier types' computed values with the last active type's parameter context.
            formulaCount = ApplyFormulas(familyDoc, definition, warnings);

            // Post-loop diagnostic: log final parameter values for every type.
            // FamilyType.AsDouble(fp) reads values without changing CurrentType.
            foreach (FamilyType ft in fm.Types)
            {
                var diagSb = new StringBuilder("[TypeDiag] FINAL Type=\"" + ft.Name + "\"");
                foreach (FamilyParameter diagFp in fm.GetParameters())
                {
                    try
                    {
                        double? val = ft.AsDouble(diagFp);
                        if (val.HasValue)
                            diagSb.Append(" " + diagFp.Definition.Name
                                + "=" + val.Value.ToString("F4") + "ft");
                    }
                    catch { }
                }
                Debug.WriteLine(diagSb.ToString());
            }

            return typesCreated;
        }

        // Sets per-type parameter values for the type that is currently active in FamilyManager.
        private static void ApplyTypeParameterValues(
            Document familyDoc,
            FamilyManager fm,
            FamilyTypeDefinition typeDef,
            Dictionary<string, FamilyParameter> paramsByName,
            FamilyDefinition definition,
            IList<string> warnings)
        {
            using (Transaction tx = new Transaction(
                familyDoc, "Set Parameter Values: " + typeDef.Name))
            {
                tx.Start();

                foreach (KeyValuePair<string, string> kv in typeDef.ParameterValues)
                {
                    FamilyParameter fp;
                    if (!paramsByName.TryGetValue(kv.Key, out fp))
                    {
                        warnings.Add("Type \"" + typeDef.Name + "\": parameter \""
                            + kv.Key + "\" not found; value skipped.");
                        continue;
                    }

                    // Instance parameters hold one value shared across all types.
                    // Setting them per-type overwrites whatever a previous type wrote.
                    if (fp.IsInstance)
                    {
                        warnings.Add("Type \"" + typeDef.Name + "\": parameter \""
                            + kv.Key + "\" is an instance parameter — its value is shared "
                            + "across all types and will reflect the last type processed.");
                    }

                    // Locate the schema definition so we know the parameter type (Length, Number, …).
                    ParameterDefinition schemaDef = null;
                    foreach (ParameterDefinition pd in definition.Parameters)
                    {
                        if (string.Equals(pd.Name, kv.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            schemaDef = pd;
                            break;
                        }
                    }

                    if (schemaDef == null)
                    {
                        warnings.Add("Type \"" + typeDef.Name
                            + "\": no schema definition for \"" + kv.Key + "\"; value skipped.");
                        continue;
                    }

                    string setActiveType = fm.CurrentType != null
                        ? fm.CurrentType.Name : "(none)";
                    Debug.WriteLine("[TypeDiag] SET ActiveType=\"" + setActiveType
                        + "\" Param=\"" + kv.Key + "\" Value=\"" + kv.Value + "\""
                        + (fp.IsInstance ? " [INSTANCE — shared]" : ""));

                    // Reuse TrySetDefaultValue with a temporary def carrying the type-specific value.
                    var tempDef = new ParameterDefinition
                    {
                        Name         = schemaDef.Name,
                        Type         = schemaDef.Type,
                        DefaultValue = kv.Value
                    };
                    TrySetDefaultValue(fm, fp, tempDef);
                }

                tx.Commit();
            }
        }

        // Returns true if the target parameter or any directly referenced parameter is an instance param.
        private static bool HasInstanceConflict(
            FamilyParameter targetParam,
            string expression,
            Dictionary<string, FamilyParameter> paramsByName)
        {
            if (targetParam.IsInstance) return true;

            foreach (KeyValuePair<string, FamilyParameter> kvp in paramsByName)
            {
                if (kvp.Value.IsInstance && ExpressionReferencesParam(expression, kvp.Key))
                    return true;
            }

            return false;
        }

        // True when paramName appears as a whole word inside expression (case-insensitive).
        private static bool ExpressionReferencesParam(string expression, string paramName)
        {
            return Regex.IsMatch(
                expression,
                @"\b" + Regex.Escape(paramName) + @"\b",
                RegexOptions.IgnoreCase);
        }

        private static ForgeTypeId MapSpecTypeId(Schema.ParameterType type)
        {
            switch (type)
            {
                case Schema.ParameterType.Length:   return SpecTypeId.Length;
                case Schema.ParameterType.Angle:    return SpecTypeId.Angle;
                case Schema.ParameterType.Number:   return SpecTypeId.Number;
                case Schema.ParameterType.YesNo:    return SpecTypeId.Boolean.YesNo;
                case Schema.ParameterType.Text:     return SpecTypeId.String.Text;
                case Schema.ParameterType.Material: return SpecTypeId.Reference.Material;
                default:                            return SpecTypeId.Number;
            }
        }

        private static ForgeTypeId MapGroupTypeId(string group)
        {
            if (string.IsNullOrWhiteSpace(group))
                return GroupTypeId.Data;

            switch (group.Trim().ToUpperInvariant())
            {
                case "DIMENSIONS":    return GroupTypeId.Geometry;
                case "TEXT":          return GroupTypeId.Text;
                case "DATA":          return GroupTypeId.Data;
                case "IDENTITY":
                case "IDENTITYDATA":  return GroupTypeId.IdentityData;
                default:              return GroupTypeId.Data;
            }
        }

        private static void TrySetDefaultValue(
            FamilyManager fm, FamilyParameter fp, ParameterDefinition paramDef)
        {
            if (string.IsNullOrWhiteSpace(paramDef.DefaultValue)) return;

            try
            {
                double d;
                switch (paramDef.Type)
                {
                    case Schema.ParameterType.Length:
                        if (double.TryParse(paramDef.DefaultValue,
                            NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                        {
                            fm.Set(fp, UnitUtils.ConvertToInternalUnits(d, UnitTypeId.Millimeters));
                        }
                        break;

                    case Schema.ParameterType.Number:
                        if (double.TryParse(paramDef.DefaultValue,
                            NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                        {
                            fm.Set(fp, d);
                        }
                        break;

                    case Schema.ParameterType.YesNo:
                        string v = paramDef.DefaultValue.Trim().ToLowerInvariant();
                        fm.Set(fp, (v == "1" || v == "true" || v == "yes") ? 1 : 0);
                        break;

                    case Schema.ParameterType.Text:
                        fm.Set(fp, paramDef.DefaultValue);
                        break;

                    // Angle and Material: skip — units/references need further context
                }
            }
            catch
            {
                // Best-effort; skip default value on any failure.
            }
        }

        public int AddReferencePlanes(Document familyDoc, FamilyDefinition definition)
        {
            if (definition.ReferencePlanes == null || definition.ReferencePlanes.Count == 0)
                return 0;

            View workingView = FindWorkingView(familyDoc);
            if (workingView == null)
                throw new InvalidOperationException(
                    "No suitable view found in the family document for reference plane creation.");

            // Needed for "elevation" orientation planes (Z-normal, shown in elevation view).
            View elevationView = FindElevationView(familyDoc);

            // Collect names already present (template built-ins + any previously created).
            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Element elem in new FilteredElementCollector(familyDoc)
                .OfClass(typeof(ReferencePlane)))
            {
                ReferencePlane rp = elem as ReferencePlane;
                if (rp != null && !string.IsNullOrEmpty(rp.Name))
                    existingNames.Add(rp.Name);
            }

            int count = 0;

            using (Transaction tx = new Transaction(familyDoc, "Add Reference Planes"))
            {
                tx.Start();

                foreach (ReferencePlaneDefinition planeDef in definition.ReferencePlanes)
                {
                    if (string.IsNullOrWhiteSpace(planeDef.Name)) continue;

                    // Reuse an existing plane that already has this name.
                    if (existingNames.Contains(planeDef.Name))
                    {
                        count++;
                        continue;
                    }

                    string orientation = (planeDef.Orientation ?? string.Empty)
                        .Trim().ToLowerInvariant();

                    if (orientation != "vertical"
                        && orientation != "horizontal"
                        && orientation != "elevation")
                        continue;

                    try
                    {
                        double offsetFt = UnitUtils.ConvertToInternalUnits(
                            planeDef.Offset, UnitTypeId.Millimeters);

                        const double halfLen = 2.0; // feet — only affects visual extent

                        XYZ cutVector, bubbleEnd, freeEnd;
                        View viewForPlane;

                        if (orientation == "vertical")
                        {
                            // Line along Y at X = offsetFt  →  normal = (1,0,0)  (YZ-plane)
                            cutVector = new XYZ(0, 0, 1);
                            bubbleEnd = new XYZ(offsetFt, -halfLen, 0);
                            freeEnd   = new XYZ(offsetFt,  halfLen, 0);
                            viewForPlane = workingView;
                        }
                        else if (orientation == "horizontal")
                        {
                            // Line along X at Y = offsetFt  →  normal = (0,-1,0)  (XZ-plane)
                            cutVector = new XYZ(0, 0, 1);
                            bubbleEnd = new XYZ(-halfLen, offsetFt, 0);
                            freeEnd   = new XYZ( halfLen, offsetFt, 0);
                            viewForPlane = workingView;
                        }
                        else
                        {
                            // "elevation": line along X at Z = offsetFt  →  normal = (0,0,1)  (XY-plane)
                            // These are the correct planes for constraining extrusion Top/Bottom faces.
                            // Cross product: (1,0,0) × (0,1,0) = (0,0,1) ✓
                            cutVector = new XYZ(0, 1, 0);
                            bubbleEnd = new XYZ(-halfLen, 0, offsetFt);
                            freeEnd   = new XYZ( halfLen, 0, offsetFt);
                            viewForPlane = elevationView ?? workingView;
                        }

                        ReferencePlane rp = familyDoc.FamilyCreate.NewReferencePlane(
                            bubbleEnd, freeEnd, cutVector, viewForPlane);

                        rp.Name = planeDef.Name;
                        existingNames.Add(planeDef.Name);
                        count++;
                    }
                    catch
                    {
                        // Skip any plane that fails; continue with the rest.
                    }
                }

                tx.Commit();
            }

            return count;
        }

        private static View FindWorkingView(Document familyDoc)
        {
            FilteredElementCollector views = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(View));

            // Prefer a floor plan view (the standard working view in a family)
            foreach (Element elem in views)
            {
                View v = elem as View;
                if (v != null && v.ViewType == ViewType.FloorPlan && !v.IsTemplate)
                    return v;
            }

            // Fall back to any non-template view
            foreach (Element elem in new FilteredElementCollector(familyDoc).OfClass(typeof(View)))
            {
                View v = elem as View;
                if (v != null && !v.IsTemplate)
                    return v;
            }

            return null;
        }

        public int AddDimensions(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            if (definition.Dimensions == null || definition.Dimensions.Count == 0)
                return 0;

            View planView = FindWorkingView(familyDoc);
            View elevView = FindElevationView(familyDoc);

            if (planView == null)
            {
                warnings.Add("No suitable view found for dimension creation.");
                return 0;
            }

            // Build name → ReferencePlane lookup from the live document
            var planesByName = new Dictionary<string, ReferencePlane>(StringComparer.OrdinalIgnoreCase);
            foreach (Element elem in new FilteredElementCollector(familyDoc).OfClass(typeof(ReferencePlane)))
            {
                ReferencePlane rp = elem as ReferencePlane;
                if (rp != null && !string.IsNullOrEmpty(rp.Name))
                    planesByName[rp.Name] = rp;
            }

            // Build name → FamilyParameter lookup
            var paramsByName = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter fp in familyDoc.FamilyManager.GetParameters())
                paramsByName[fp.Definition.Name] = fp;

            int created = 0;

            using (Transaction tx = new Transaction(familyDoc, "Add Dimensions"))
            {
                tx.Start();

                foreach (DimensionDefinition dimDef in definition.Dimensions)
                {
                    if (string.IsNullOrWhiteSpace(dimDef.ReferencePlane1)
                        || string.IsNullOrWhiteSpace(dimDef.ReferencePlane2))
                    {
                        warnings.Add("Dimension skipped: both reference plane names are required.");
                        continue;
                    }

                    ReferencePlane rp1, rp2;
                    if (!planesByName.TryGetValue(dimDef.ReferencePlane1, out rp1))
                    {
                        warnings.Add("Dimension skipped: plane not found: \""
                            + dimDef.ReferencePlane1 + "\".");
                        continue;
                    }
                    if (!planesByName.TryGetValue(dimDef.ReferencePlane2, out rp2))
                    {
                        warnings.Add("Dimension skipped: plane not found: \""
                            + dimDef.ReferencePlane2 + "\".");
                        continue;
                    }

                    // Resolve optional middle plane for EQ dimensions.
                    ReferencePlane rpMiddle = null;
                    if (!string.IsNullOrWhiteSpace(dimDef.ReferencePlaneMiddle))
                    {
                        if (!planesByName.TryGetValue(dimDef.ReferencePlaneMiddle, out rpMiddle))
                        {
                            warnings.Add("EQ middle plane not found: \""
                                + dimDef.ReferencePlaneMiddle
                                + "\"; dimension will be created without EQ constraint.");
                        }
                    }

                    try
                    {
                        Plane geom1 = rp1.GetPlane();
                        Plane geom2 = rp2.GetPlane();

                        XYZ normal = geom1.Normal.Normalize();

                        double d1   = geom1.Origin.DotProduct(normal);
                        double d2   = geom2.Origin.DotProduct(normal);
                        double low  = Math.Min(d1, d2);
                        double high = Math.Max(d1, d2);

                        if (Math.Abs(high - low) < 0.001)
                        {
                            warnings.Add("Dimension skipped: planes \""
                                + dimDef.ReferencePlane1 + "\" and \""
                                + dimDef.ReferencePlane2 + "\" appear coincident.");
                            continue;
                        }

                        const double pad = 0.5; // feet — extends line past each plane

                        double s = low  - pad;
                        double e = high + pad;

                        // For Z-normal planes (elevation orientation) the line must be
                        // a vertical segment; include the Z component in the calculation.
                        XYZ start = new XYZ(normal.X * s, normal.Y * s, normal.Z * s);
                        XYZ end   = new XYZ(normal.X * e, normal.Y * e, normal.Z * e);

                        // Choose the view that matches the plane orientation.
                        bool isElevationDim = Math.Abs(normal.Z) > 0.9;
                        View dimView = isElevationDim ? (elevView ?? planView) : planView;

                        ReferenceArray refs = new ReferenceArray();
                        refs.Append(rp1.GetReference());
                        if (rpMiddle != null) refs.Append(rpMiddle.GetReference());
                        refs.Append(rp2.GetReference());

                        Dimension dim = familyDoc.FamilyCreate.NewDimension(
                            dimView, Line.CreateBound(start, end), refs);

                        // Bind a family parameter label when requested.
                        if (!string.IsNullOrWhiteSpace(dimDef.ParameterName))
                        {
                            FamilyParameter fp;
                            if (paramsByName.TryGetValue(dimDef.ParameterName, out fp))
                            {
                                string labelActiveType = familyDoc.FamilyManager.CurrentType != null
                                    ? familyDoc.FamilyManager.CurrentType.Name : "(none)";
                                Debug.WriteLine("[TypeDiag] LABEL ActiveType=\"" + labelActiveType
                                    + "\" Param=\"" + dimDef.ParameterName
                                    + "\" DimBetween=\"" + dimDef.ReferencePlane1
                                    + "\" and \"" + dimDef.ReferencePlane2 + "\"");

                                try { dim.FamilyLabel = fp; }
                                catch (Exception ex)
                                {
                                    warnings.Add("Could not bind parameter \""
                                        + dimDef.ParameterName + "\": " + ex.Message);
                                }
                            }
                            else
                            {
                                warnings.Add("Dimension parameter not found: \""
                                    + dimDef.ParameterName + "\".");
                            }
                        }

                        // Apply EQ (equal-segment) constraint when requested.
                        // Requires a middle reference plane and at least 2 segments.
                        // Dimension.AreSegmentsEqual drives all segments as equal in one call.
                        if (dimDef.IsEqual && rpMiddle != null
                            && dim.NumberOfSegments > 1)
                        {
                            try
                            {
                                dim.AreSegmentsEqual = true;
                            }
                            catch (Exception ex)
                            {
                                warnings.Add("EQ constraint failed on dimension (\""
                                    + dimDef.ReferencePlane1 + "\" - \""
                                    + dimDef.ReferencePlane2 + "\"): " + ex.Message);
                            }
                        }

                        created++;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Dimension creation failed (\""
                            + dimDef.ReferencePlane1 + "\" - \""
                            + dimDef.ReferencePlane2 + "\"): " + ex.Message);
                    }
                }

                tx.Commit();
            }

            return created;
        }

        public int AddSymbolicLines(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            if (definition.SymbolicLines == null || definition.SymbolicLines.Count == 0)
                return 0;

            int count = 0;

            // Pre-fetch for alignment (read-only queries — no transaction needed).
            View planViewSL     = FindWorkingView(familyDoc);
            var  planesBySLName = BuildReferencePlaneLookup(familyDoc);

            using (Transaction tx = new Transaction(familyDoc, "Add Symbolic Lines"))
            {
                tx.Start();

                // One shared sketch plane on the XY plane (Z=0) covers all plan-view lines.
                SketchPlane sketchPlane;
                try
                {
                    sketchPlane = SketchPlane.Create(
                        familyDoc,
                        Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                }
                catch (Exception ex)
                {
                    warnings.Add("Could not create sketch plane for symbolic lines: " + ex.Message);
                    tx.RollBack();
                    return 0;
                }

                foreach (SymbolicLineDefinition lineDef in definition.SymbolicLines)
                {
                    string viewKey = (lineDef.View ?? string.Empty).Trim().ToLowerInvariant();
                    if (viewKey != "plan")
                    {
                        warnings.Add("Symbolic line view \""
                            + lineDef.View + "\" is not supported in v1; line skipped.");
                        continue;
                    }

                    try
                    {
                        double x1 = UnitUtils.ConvertToInternalUnits(
                            lineDef.StartX, UnitTypeId.Millimeters);
                        double y1 = UnitUtils.ConvertToInternalUnits(
                            lineDef.StartY, UnitTypeId.Millimeters);
                        double x2 = UnitUtils.ConvertToInternalUnits(
                            lineDef.EndX, UnitTypeId.Millimeters);
                        double y2 = UnitUtils.ConvertToInternalUnits(
                            lineDef.EndY, UnitTypeId.Millimeters);

                        XYZ start = new XYZ(x1, y1, 0);
                        XYZ end   = new XYZ(x2, y2, 0);

                        if (start.DistanceTo(end) < 0.001)
                        {
                            warnings.Add("Symbolic line skipped: start and end points are identical.");
                            continue;
                        }

                        SymbolicCurve symCurve = familyDoc.FamilyCreate.NewSymbolicCurve(
                            Line.CreateBound(start, end), sketchPlane);

                        // Lock the curve to the reference plane it lies on.
                        if (symCurve != null && planViewSL != null)
                            TryAlignSymbolicCurveToPlane(
                                familyDoc, symCurve, planViewSL, planesBySLName, warnings);

                        count++;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Symbolic line creation failed: " + ex.Message);
                    }
                }

                tx.Commit();
            }

            return count;
        }

        public int AddGeometry(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            if (definition.Geometry == null || definition.Geometry.Count == 0)
                return 0;

            int count = 0;

            // Pre-fetch views and planes once (read-only — no transaction needed).
            View planView     = FindWorkingView(familyDoc);
            View elevView     = FindElevationView(familyDoc);
            var  planesByName = BuildReferencePlaneLookup(familyDoc);

            foreach (GeometryDefinition geoDef in definition.Geometry)
            {
                if (geoDef.Type != GeometryType.Extrusion)
                {
                    warnings.Add("Geometry type \""
                        + geoDef.Type + "\" is not supported in v1; skipped.");
                    continue;
                }

                double? widthFt  = ResolveParameterValueFt(
                    familyDoc, definition, geoDef.WidthParameter,  warnings);
                double? depthFt  = ResolveParameterValueFt(
                    familyDoc, definition, geoDef.DepthParameter,  warnings);
                double? heightFt = ResolveParameterValueFt(
                    familyDoc, definition, geoDef.HeightParameter, warnings);

                if (widthFt == null || depthFt == null || heightFt == null)
                    continue;

                double w = widthFt.Value;
                double d = depthFt.Value;
                double h = heightFt.Value;

                if (w < 0.001 || d < 0.001 || h < 0.001)
                {
                    warnings.Add("Extrusion skipped: one or more resolved dimensions are zero or too small.");
                    continue;
                }

                // All rectangular extrusion creation flows through this single method.
                bool built = BuildRectangularExtrusion(
                    familyDoc, w, d, h, planView, elevView, planesByName, warnings);

                if (built) count++;
            }

            return count;
        }

        // ── Unified rectangular extrusion builder ────────────────────────────────
        //
        // This is the SINGLE code path for creating a locked rectangular extrusion.
        // All geometry requests of type "Extrusion" must go through here.
        //
        // Guarantees:
        //   • Rectangular profile (w × d) centred at origin, extruded along +Z to h
        //   • 4 lateral faces locked to Left / Right / Front / Back reference planes
        //     (via NewAlignment in plan view)
        //   • Bottom (Z = 0): extrusion StartOffset = 0, sketch plane at Z = 0
        //     The sketch plane coincides with the built-in Lower Ref Level at Z = 0,
        //     which is sufficient for the base constraint in GenericModel templates.
        //   • Top (Z = h): the extrusion EndOffset is associated to the "Height"
        //     family parameter via EXTRUSION_END_PARAM.  This is the standard Revit
        //     pattern for parametric height — it avoids the brittle face-reference
        //     approach that fails with "The reference is not valid" on Z-faces.
        //   • The alignment transaction is independent so a failed face lock never
        //     rolls back the committed solid.
        private static bool BuildRectangularExtrusion(
            Document familyDoc,
            double w, double d, double h,
            View planView, View elevView,
            Dictionary<string, ReferencePlane> planesByName,
            IList<string> warnings)
        {
            Extrusion ext = null;

            // ── Step 1: create the solid extrusion ────────────────────────────────
            using (Transaction tx = new Transaction(familyDoc, "Add Rectangular Extrusion"))
            {
                tx.Start();
                try
                {
                    double halfW = w / 2.0;
                    double halfD = d / 2.0;

                    XYZ p1 = new XYZ(-halfW, -halfD, 0);
                    XYZ p2 = new XYZ( halfW, -halfD, 0);
                    XYZ p3 = new XYZ( halfW,  halfD, 0);
                    XYZ p4 = new XYZ(-halfW,  halfD, 0);

                    CurveArray loop = new CurveArray();
                    loop.Append(Line.CreateBound(p1, p2));
                    loop.Append(Line.CreateBound(p2, p3));
                    loop.Append(Line.CreateBound(p3, p4));
                    loop.Append(Line.CreateBound(p4, p1));

                    CurveArrArray profile = new CurveArrArray();
                    profile.Append(loop);

                    SketchPlane sketchPlane = SketchPlane.Create(
                        familyDoc,
                        Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));

                    ext = familyDoc.FamilyCreate.NewExtrusion(true, profile, sketchPlane, h);
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    warnings.Add("Rectangular extrusion creation failed: " + ex.Message);
                    return false;
                }
            }

            // ── Step 2: lock lateral faces (Left/Right/Front/Back) ────────────────
            try
            {
                using (Transaction txAlign = new Transaction(
                    familyDoc, "Lock Extrusion Faces to Reference Planes"))
                {
                    txAlign.Start();
                    TryAlignExtrusionToReferencePlanes(
                        familyDoc, ext, planView, elevView, planesByName, warnings);
                    txAlign.Commit();
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Extrusion face-locking transaction failed: " + ex.Message);
            }

            // ── Step 3: lock height via extrusion offset parameters ───────────────
            // This is the standard Revit pattern: associate the extrusion's built-in
            // EXTRUSION_END_PARAM (top distance from sketch plane) with the "Height"
            // family parameter.  EXTRUSION_START_PARAM stays at 0 (base = sketch plane).
            //
            // This replaces the face-based NewAlignment approach for Z-normals which
            // fails with "The reference is not valid" in most family templates.
            try
            {
                using (Transaction txHeight = new Transaction(
                    familyDoc, "Associate Extrusion Height"))
                {
                    txHeight.Start();
                    TryAssociateExtrusionHeight(familyDoc, ext, warnings);
                    txHeight.Commit();
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Extrusion height association failed: " + ex.Message);
            }

            return true;
        }

        // Associates the extrusion's EXTRUSION_END_PARAM with the "Height" family
        // parameter so the top of the extrusion tracks the Height value directly.
        // This is more reliable than face-to-plane alignment for Z-normal faces.
        // Caller must supply an open transaction.
        private static void TryAssociateExtrusionHeight(
            Document familyDoc,
            Extrusion extrusion,
            IList<string> warnings)
        {
            FamilyManager fm = familyDoc.FamilyManager;

            FamilyParameter heightParam = null;
            foreach (FamilyParameter fp in fm.GetParameters())
            {
                if (string.Equals(fp.Definition.Name, "Height", StringComparison.OrdinalIgnoreCase))
                {
                    heightParam = fp;
                    break;
                }
            }

            if (heightParam == null)
            {
                warnings.Add("Height parameter not found; extrusion top is not parametrically constrained.");
                return;
            }

            // Associate EXTRUSION_END_PARAM → Height
            Parameter endParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            if (endParam != null && !endParam.IsReadOnly)
            {
                try
                {
                    fm.AssociateElementParameterToFamilyParameter(endParam, heightParam);
                }
                catch (Exception ex)
                {
                    warnings.Add("Could not associate extrusion EndOffset to Height: " + ex.Message);
                }
            }
            else
            {
                warnings.Add("EXTRUSION_END_PARAM is null or read-only; height not constrained.");
            }
        }

        // Reads a family parameter's current value (in internal feet).
        // Level 1: live family type via FamilyManager.CurrentType.AsDouble().
        // Level 2: parses DefaultValue from the JSON schema definition (mm → ft).
        private static double? ResolveParameterValueFt(
            Document familyDoc,
            FamilyDefinition definition,
            string paramName,
            IList<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(paramName))
            {
                warnings.Add("Geometry parameter name is empty; dimension skipped.");
                return null;
            }

            FamilyManager fm = familyDoc.FamilyManager;
            FamilyType currentType = fm.CurrentType;

            if (currentType != null)
            {
                foreach (FamilyParameter fp in fm.GetParameters())
                {
                    if (!string.Equals(fp.Definition.Name, paramName,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        double? val = currentType.AsDouble(fp);
                        if (val.HasValue && val.Value > 0.0001) return val.Value;
                    }
                    catch { }
                    break;
                }
            }

            // Fallback: convert DefaultValue mm → ft from the JSON schema.
            if (definition.Parameters != null)
            {
                foreach (ParameterDefinition pd in definition.Parameters)
                {
                    if (!string.Equals(pd.Name, paramName,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    double parsed;
                    if (!string.IsNullOrWhiteSpace(pd.DefaultValue)
                        && double.TryParse(pd.DefaultValue,
                            NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)
                        && parsed > 0.0)
                    {
                        return UnitUtils.ConvertToInternalUnits(parsed, UnitTypeId.Millimeters);
                    }
                    break;
                }
            }

            warnings.Add("Could not resolve value for parameter \""
                + paramName + "\"; geometry skipped.");
            return null;
        }

        // ── Void openings ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates void extrusions that cut the solid geometry.
        /// v1: only rectangular, centered on the Front face, cut-through.
        /// </summary>
        public int AddVoids(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            if (definition.Voids == null || definition.Voids.Count == 0)
                return 0;

            int count = 0;

            foreach (VoidDefinition voidDef in definition.Voids)
            {
                try
                {
                    if (!string.Equals(voidDef.Shape, "Rectangular", StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add("Void \"" + voidDef.Name
                            + "\": shape \"" + voidDef.Shape
                            + "\" not supported in v1; skipped.");
                        continue;
                    }
                    if (!string.Equals(voidDef.Face, "Front", StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add("Void \"" + voidDef.Name
                            + "\": face \"" + voidDef.Face
                            + "\" not supported in v1; skipped.");
                        continue;
                    }

                    double? openingW = ResolveParameterValueFt(
                        familyDoc, definition, voidDef.WidthParameter, warnings);
                    double? openingH = ResolveParameterValueFt(
                        familyDoc, definition, voidDef.HeightParameter, warnings);

                    if (openingW == null || openingH == null)
                        continue;

                    double ow = openingW.Value;
                    double oh = openingH.Value;
                    if (ow < 0.001 || oh < 0.001)
                    {
                        warnings.Add("Void \"" + voidDef.Name
                            + "\" skipped: resolved dimensions are zero or too small.");
                        continue;
                    }

                    // Resolve the box Depth so the void can cut fully through.
                    double? depthFt = ResolveParameterValueFt(
                        familyDoc, definition, "Depth", warnings);
                    double cutDepth = (depthFt.HasValue && depthFt.Value > 0.001)
                        ? depthFt.Value * 1.1
                        : UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);

                    using (Transaction tx = new Transaction(familyDoc, "Add Void " + voidDef.Name))
                    {
                        tx.Start();

                        // Rectangular profile centred at origin on the XZ plane (Front face).
                        // The void extrudes along Y (negative — from Front toward Back).
                        double halfW = ow / 2.0;
                        double halfH = oh / 2.0;
                        double midH  = oh / 2.0; // base at Z = 0, top at Z = oh

                        XYZ p1 = new XYZ(-halfW, 0, midH - halfH);
                        XYZ p2 = new XYZ( halfW, 0, midH - halfH);
                        XYZ p3 = new XYZ( halfW, 0, midH + halfH);
                        XYZ p4 = new XYZ(-halfW, 0, midH + halfH);

                        CurveArray loop = new CurveArray();
                        loop.Append(Line.CreateBound(p1, p2));
                        loop.Append(Line.CreateBound(p2, p3));
                        loop.Append(Line.CreateBound(p3, p4));
                        loop.Append(Line.CreateBound(p4, p1));

                        CurveArrArray profile = new CurveArrArray();
                        profile.Append(loop);

                        // Sketch plane on the Front face (XZ plane at Y = -Depth/2 roughly,
                        // but we draw at origin and let the extrusion depth cover the full cut).
                        SketchPlane sketchPlane = SketchPlane.Create(
                            familyDoc,
                            Plane.CreateByNormalAndOrigin(XYZ.BasisY, XYZ.Zero));

                        Extrusion voidExt = familyDoc.FamilyCreate.NewExtrusion(
                            false, profile, sketchPlane, cutDepth);

                        // The void extrudes in +Y by default (from the sketch plane).
                        // Shift it so it starts before the front face and cuts all the way through.
                        double halfDepth = cutDepth / 2.0;
                        ElementTransformUtils.MoveElement(
                            familyDoc, voidExt.Id, new XYZ(0, -halfDepth, 0));

                        count++;
                        tx.Commit();
                    }

                    // CombineElements (cut) in a separate transaction for safety.
                    try
                    {
                        using (Transaction txCut = new Transaction(familyDoc, "Cut Void " + voidDef.Name))
                        {
                            txCut.Start();

                            // Find the first solid extrusion to cut.
                            Extrusion solidExt = null;
                            Extrusion voidExt  = null;
                            using (FilteredElementCollector col =
                                new FilteredElementCollector(familyDoc))
                            {
                                foreach (GenericForm gf in
                                    col.OfClass(typeof(Extrusion)).Cast<GenericForm>())
                                {
                                    Extrusion ex = gf as Extrusion;
                                    if (ex == null) continue;
                                    if (ex.IsSolid && solidExt == null) solidExt = ex;
                                    if (!ex.IsSolid && voidExt == null) voidExt  = ex;
                                }
                            }

                            if (solidExt != null && voidExt != null)
                            {
                                CombinableElementArray arr = new CombinableElementArray();
                                arr.Append(solidExt);
                                arr.Append(voidExt);
                                familyDoc.CombineElements(arr);
                            }
                            else
                            {
                                warnings.Add("Void \"" + voidDef.Name
                                    + "\": could not find solid/void pair to cut.");
                            }

                            txCut.Commit();
                        }
                    }
                    catch (Exception cutEx)
                    {
                        warnings.Add("Void \"" + voidDef.Name
                            + "\": cut operation failed: " + cutEx.Message);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("Void \"" + voidDef.Name
                        + "\" creation failed: " + ex.Message);
                }
            }

            return count;
        }

        // ── Flex validation ──────────────────────────────────────────────────────
        //
        // FlexTest verifies that the parametric constraints added by the build
        // pipeline are correct.  It performs three categories of checks:
        //
        //  A) Pre-flex structural checks (before changing any value):
        //     • Width and Depth must each have a labelled Dimension in the plan view.
        //     • Height must be driven by EXTRUSION_END_PARAM association (not by a
        //       labelled dimension — Revit cannot label Z-normal plane dimensions).
        //
        //  B) Flex passes (×1.5 and ×0.7 of baseline):
        //     • Set parameter values, Regenerate, capture Revit failure messages
        //       via IFailuresPreprocessor, then verify solid volume is non-zero.
        //     • Any Revit warning or error during regeneration → FAIL.
        //
        //  C) Summary: all checks must pass for overall PASSED result.
        //
        // Each flex pass runs inside a transaction that is ROLLED BACK afterwards so
        // the family document is left exactly as it was before the test.
        //
        // Returns a multi-line summary string; also appends items to warnings.
        public string FlexTest(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("── FlexTest ──────────────────────────────────────────");

            FamilyManager fm = familyDoc.FamilyManager;
            bool structuralOk = true;

            // ── A) Pre-flex structural checks ─────────────────────────────────────

            // A1. Verify labelled dimensions exist for Width and Depth.
            //     Height is NOT constrained via a labelled dimension (Revit cannot
            //     label dimensions between Z-normal planes via the API).  It is
            //     constrained instead by EXTRUSION_END_PARAM association — checked
            //     separately in A2 below.
            var expectedLabels = new[] { "Width", "Depth" };
            var labelledDims = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in expectedLabels)
                labelledDims[name] = false;

            foreach (Element elem in new FilteredElementCollector(familyDoc)
                .OfClass(typeof(Dimension)))
            {
                Dimension dim = elem as Dimension;
                if (dim == null || dim.FamilyLabel == null) continue;
                string labelName = dim.FamilyLabel.Definition.Name;
                if (labelledDims.ContainsKey(labelName))
                    labelledDims[labelName] = true;
            }

            foreach (var kv in labelledDims)
            {
                if (!kv.Value)
                {
                    string msg = "FlexTest structural: no dimension labelled with \"" + kv.Key + "\" found.";
                    sb.AppendLine("  FAIL — " + msg);
                    warnings.Add(msg);
                    structuralOk = false;
                }
                else
                {
                    sb.AppendLine("  OK — Dimension labelled \"" + kv.Key + "\" exists.");
                }
            }

            // A2. Verify extrusion exists and Height is driven by parameter association.
            //     The standard Revit pattern for parametric height: associate
            //     EXTRUSION_END_PARAM to the "Height" family parameter.
            var solidExtrusions = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(Extrusion))
                .Cast<Extrusion>()
                .Where(e => e.IsSolid)
                .ToList();

            if (solidExtrusions.Count == 0)
            {
                string msg = "FlexTest structural: no solid extrusion found.";
                sb.AppendLine("  FAIL — " + msg);
                warnings.Add(msg);
                structuralOk = false;
            }
            else
            {
                Extrusion mainExt = solidExtrusions[0];
                Parameter endParam = mainExt.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
                bool heightAssociated = false;
                if (endParam != null)
                {
                    FamilyParameter assoc = fm.GetAssociatedFamilyParameter(endParam);
                    heightAssociated = assoc != null
                        && string.Equals(assoc.Definition.Name, "Height",
                            StringComparison.OrdinalIgnoreCase);
                }
                if (heightAssociated)
                {
                    sb.AppendLine("  OK — Extrusion EndOffset associated to \"Height\".");
                }
                else
                {
                    string msg = "FlexTest structural: extrusion EndOffset is NOT associated to \"Height\".";
                    sb.AppendLine("  FAIL — " + msg);
                    warnings.Add(msg);
                    structuralOk = false;
                }
            }

            // A3. Count lateral alignment constraints (Left/Right/Front/Back).
            int alignmentCount = 0;
            try
            {
                foreach (Element elem in new FilteredElementCollector(familyDoc)
                    .OfCategory(BuiltInCategory.OST_Constraints)
                    .WhereElementIsNotElementType())
                {
                    alignmentCount++;
                }
            }
            catch { }
            sb.AppendLine("  Alignment constraints found: " + alignmentCount);
            if (alignmentCount < 4)
            {
                string msg = "FlexTest structural: expected at least 4 alignment constraints "
                    + "(Left/Right/Front/Back), found " + alignmentCount + ".";
                sb.AppendLine("  WARNING — " + msg);
                warnings.Add(msg);
            }

            // ── B) Flex passes ────────────────────────────────────────────────────

            var dimParamNames = new[] { "Width", "Depth", "Height" };
            var dimParams = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter fp in fm.GetParameters())
                if (Array.IndexOf(dimParamNames, fp.Definition.Name) >= 0)
                    dimParams[fp.Definition.Name] = fp;

            bool flexOk = true;

            if (dimParams.Count == 0)
            {
                string msg = "FlexTest skipped flex passes: no Width / Depth / Height parameters found.";
                sb.AppendLine(msg);
                warnings.Add(msg);
                flexOk = false;
            }
            else
            {
                var origValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                FamilyType activeType = fm.CurrentType;
                foreach (var kv in dimParams)
                {
                    double? val = activeType != null ? activeType.AsDouble(kv.Value) : null;
                    if (!val.HasValue || val.Value < 1e-6)
                    {
                        double fallback = 0.0;
                        if (definition.Parameters != null)
                        {
                            foreach (ParameterDefinition pd in definition.Parameters)
                            {
                                if (!string.Equals(pd.Name, kv.Key, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                double parsed;
                                if (!string.IsNullOrWhiteSpace(pd.DefaultValue)
                                    && double.TryParse(pd.DefaultValue,
                                        NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                                {
                                    fallback = UnitUtils.ConvertToInternalUnits(
                                        parsed, UnitTypeId.Millimeters);
                                }
                                break;
                            }
                        }
                        origValues[kv.Key] = fallback > 1e-6 ? fallback : 0.3;
                    }
                    else
                    {
                        origValues[kv.Key] = val.Value;
                    }
                }

                sb.AppendLine("Parameters found: " + string.Join(", ", origValues.Keys));
                foreach (var kv in origValues)
                    sb.AppendLine("  " + kv.Key + " baseline = "
                        + UnitUtils.ConvertFromInternalUnits(kv.Value, UnitTypeId.Millimeters)
                            .ToString("0.0") + " mm");

                bool pass1 = RunFlexPass(familyDoc, fm, dimParams, origValues, 1.5, "×1.5", sb, warnings);
                bool pass2 = RunFlexPass(familyDoc, fm, dimParams, origValues, 0.7, "×0.7", sb, warnings);
                flexOk = pass1 && pass2;
            }

            // ── C) Summary ────────────────────────────────────────────────────────
            bool allPassed = structuralOk && flexOk;
            sb.AppendLine("FlexTest result: " + (allPassed ? "PASSED" : "FAILED — see warnings above"));
            sb.AppendLine("──────────────────────────────────────────────────────");

            if (!allPassed)
                warnings.Add("FlexTest: one or more checks failed. Review constraint setup.");

            return sb.ToString();
        }

        // ── Failures preprocessor for flex passes ─────────────────────────────────
        // Captures Revit warning/error messages during Regenerate() inside a
        // transaction so FlexTest can report them.  All failures are continued
        // (not rolled back) to let the pass finish before we decide pass/fail.
        private class FlexFailuresPreprocessor : IFailuresPreprocessor
        {
            public readonly List<string> Messages = new List<string>();

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                IList<FailureMessageAccessor> failures = accessor.GetFailureMessages();
                foreach (FailureMessageAccessor fma in failures)
                {
                    Messages.Add("[" + fma.GetSeverity() + "] " + fma.GetDescriptionText());
                    if (fma.GetSeverity() == FailureSeverity.Warning)
                        accessor.DeleteWarning(fma);
                }
                return FailureProcessingResult.Continue;
            }
        }

        // Runs a single flex pass at a given multiplier.  The transaction is rolled back
        // so no permanent change is made to the family document.
        // Returns true if the solid geometry survived the regeneration without errors
        // AND no Revit warnings were raised.
        private static bool RunFlexPass(
            Document familyDoc,
            FamilyManager fm,
            Dictionary<string, FamilyParameter> dimParams,
            Dictionary<string, double> origValues,
            double multiplier,
            string label,
            StringBuilder sb,
            IList<string> warnings)
        {
            bool passed = false;
            var failuresCaptured = new FlexFailuresPreprocessor();

            using (Transaction tx = new Transaction(familyDoc, "FlexTest " + label))
            {
                var failureOpts = tx.GetFailureHandlingOptions();
                failureOpts.SetFailuresPreprocessor(failuresCaptured);
                tx.SetFailureHandlingOptions(failureOpts);

                tx.Start();

                var skipped = new List<string>();
                var applied = new List<string>();

                foreach (var kv in dimParams)
                {
                    double newVal = origValues[kv.Key] * multiplier;
                    if (newVal < 0.001) newVal = 0.001;
                    try
                    {
                        fm.Set(kv.Value, newVal);
                        applied.Add(kv.Key + "="
                            + UnitUtils.ConvertFromInternalUnits(newVal, UnitTypeId.Millimeters)
                                .ToString("0.0") + " mm");
                    }
                    catch
                    {
                        skipped.Add(kv.Key + " (formula-driven)");
                    }
                }

                try
                {
                    familyDoc.Regenerate();
                    passed = CheckSolidGeometry(familyDoc);
                }
                catch (Exception ex)
                {
                    warnings.Add("FlexTest " + label + ": regeneration threw: " + ex.Message);
                    passed = false;
                }

                // Revit failures captured during Regenerate / Commit preparation.
                if (failuresCaptured.Messages.Count > 0)
                {
                    passed = false;
                    foreach (string msg in failuresCaptured.Messages)
                    {
                        warnings.Add("FlexTest " + label + " Revit warning: " + msg);
                        sb.AppendLine("  " + label + " warning: " + msg);
                    }
                }

                if (skipped.Count > 0)
                    sb.AppendLine("  " + label + " skipped: " + string.Join(", ", skipped.ToArray()));

                tx.RollBack();
            }

            sb.AppendLine("Pass " + label + ": " + (passed ? "OK" : "FAIL"));
            return passed;
        }

        // Returns true when every solid extrusion in the family document has a
        // non-zero volume after the last Regenerate() call.
        private static bool CheckSolidGeometry(Document familyDoc)
        {
            var solidExtrusions = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(Extrusion))
                .Cast<Extrusion>()
                .Where(e => e.IsSolid)
                .ToList();

            if (solidExtrusions.Count == 0) return false;

            Options opts = new Options { DetailLevel = ViewDetailLevel.Fine };
            foreach (Extrusion ext in solidExtrusions)
            {
                GeometryElement geomEl = ext.get_Geometry(opts);
                if (geomEl == null) return false;

                bool hasVolume = false;
                foreach (GeometryObject obj in geomEl)
                {
                    Solid solid = obj as Solid;
                    if (solid != null && solid.Volume > 1e-9) { hasVolume = true; break; }
                }
                if (!hasVolume) return false;
            }
            return true;
        }

        public string DryRun(FamilyDefinition definition)
        {
            var sb = new StringBuilder();

            sb.AppendLine("[Dry Run] Family: " + definition.FamilyName);
            sb.AppendLine("Create family document from template: " + definition.FamilyTemplate);
            sb.AppendLine("Set category: " + definition.Category);
            sb.AppendLine();

            foreach (ParameterDefinition param in definition.Parameters)
            {
                string scope = param.IsInstance ? "Instance" : "Type";
                sb.AppendLine("Create parameter: " + param.Name
                    + " (" + param.Type + ", " + scope + ", Default: " + param.DefaultValue + ")");
            }

            sb.AppendLine();

            foreach (ReferencePlaneDefinition plane in definition.ReferencePlanes)
            {
                sb.AppendLine("Create reference plane: " + plane.Name
                    + " (" + plane.Orientation + ", offset: " + plane.Offset.ToString("0.0") + " mm)");
            }

            sb.AppendLine();

            if (definition.Dimensions != null)
            {
                foreach (DimensionDefinition dim in definition.Dimensions)
                {
                    string driven = string.IsNullOrWhiteSpace(dim.ParameterName)
                        ? "(annotation only)"
                        : "-> " + dim.ParameterName;
                    string plane2 = dim.ReferencePlane2 ?? "(none)";
                    sb.AppendLine("Create dimension between: " + dim.ReferencePlane1
                        + " and " + plane2 + " " + driven);
                }

                sb.AppendLine();
            }

            if (definition.SymbolicLines != null)
            {
                foreach (SymbolicLineDefinition line in definition.SymbolicLines)
                {
                    sb.AppendLine("Create symbolic line in view: " + line.View
                        + " [(" + line.StartX.ToString("0.0") + ", " + line.StartY.ToString("0.0") + ")"
                        + " -> (" + line.EndX.ToString("0.0") + ", " + line.EndY.ToString("0.0") + ")]");
                }

                sb.AppendLine();
            }

            foreach (GeometryDefinition geo in definition.Geometry)
            {
                sb.AppendLine("Create geometry: " + geo.Type + " (" + geo.Profile + ")"
                    + " driven by " + geo.WidthParameter
                    + " x " + geo.DepthParameter
                    + " x " + geo.HeightParameter);
            }

            return sb.ToString().TrimEnd();
        }

        public string SaveAndActivateDocument(Document familyDoc, FamilyDefinition definition, UIApplication uiApp)
        {
            const string outputFolder = @"C:\RevitFamilles";

            if (!Directory.Exists(outputFolder))
                Directory.CreateDirectory(outputFolder);

            string safeName = SanitizeFileName(definition.FamilyName);
            string savedPath = Path.Combine(outputFolder, safeName + ".rfa");

            var saveOptions = new SaveAsOptions { OverwriteExistingFile = true };
            familyDoc.SaveAs(savedPath, saveOptions);
            familyDoc.Close(false);

            try
            {
                uiApp.OpenAndActivateDocument(savedPath);
            }
            catch
            {
                // Opening in the UI is optional; the save already succeeded.
            }

            return savedPath;
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        // ── MEP Connectors ────────────────────────────────────────────────────

        public int AddConnectors(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            if (definition.Connectors == null || definition.Connectors.Count == 0)
                return 0;

            // Only solid extrusions can host connectors — void extrusions have
            // different face geometry (e.g. XZ-plane profile) and must be excluded.
            var solidExtrusions = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(Extrusion))
                .Cast<Extrusion>()
                .Where(e => e.IsSolid)
                .ToList();

            if (solidExtrusions.Count == 0)
            {
                warnings.Add("Connector skipped: no solid extrusion found to host the connector.");
                return 0;
            }

            Extrusion host = solidExtrusions[0];

            // Build parameter lookup.
            FamilyManager fm = familyDoc.FamilyManager;
            var paramsByName =
                new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter fp in fm.GetParameters())
                paramsByName[fp.Definition.Name] = fp;

            int created = 0;

            foreach (ConnectorDefinition connDef in definition.Connectors)
            {
                if (string.IsNullOrWhiteSpace(connDef.Name)) continue;

                string domain = (connDef.Domain ?? string.Empty).Trim().ToUpperInvariant();
                string shape  = (connDef.Shape  ?? string.Empty).Trim().ToUpperInvariant();

                if (domain != "HVAC")
                {
                    warnings.Add("Connector \"" + connDef.Name
                        + "\": domain \"" + connDef.Domain
                        + "\" is not supported in v1; skipped.");
                    continue;
                }
                if (shape != "ROUND")
                {
                    warnings.Add("Connector \"" + connDef.Name
                        + "\": shape \"" + connDef.Shape
                        + "\" is not supported in v1; skipped.");
                    continue;
                }

                // Resolve the named face on the extrusion solid.
                Reference faceRef = FindNamedFaceReference(host, connDef.Face, warnings);
                if (faceRef == null)
                    continue; // warning already added by the helper

                using (Transaction tx = new Transaction(
                    familyDoc, "Add Connector: " + connDef.Name))
                {
                    tx.Start();
                    try
                    {
                        ConnectorElement ce = ConnectorElement.CreateDuctConnector(
                            familyDoc,
                            DuctSystemType.SupplyAir,
                            ConnectorProfileType.Round,
                            faceRef);

                        // Bind the connector size to the named family parameter.
                        if (!string.IsNullOrWhiteSpace(connDef.DiameterParameter))
                        {
                            FamilyParameter diamFp;
                            if (paramsByName.TryGetValue(connDef.DiameterParameter, out diamFp))
                                TryBindConnectorSizeParameter(
                                    fm, ce, diamFp, connDef.Name, warnings);
                            else
                                warnings.Add("Connector \"" + connDef.Name
                                    + "\": diameter_parameter \""
                                    + connDef.DiameterParameter + "\" not found.");
                        }

                        created++;
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        warnings.Add("Connector \"" + connDef.Name
                            + "\" creation failed: " + ex.Message);
                    }
                }
            }

            return created;
        }

        // Returns a stable Reference to the planar face of an extrusion whose outward
        // normal aligns with the direction implied by faceName.
        //
        // Coordinate conventions for the centered-at-origin rectangular extrusion
        // created by AddGeometry (profile in XY, extruded along +Z):
        //   Top    →  (0,  0, +1)    Bottom → (0,  0, -1)
        //   Front  →  (0, -1,  0)    Back   → (0, +1,  0)
        //   Left   → (-1,  0,  0)    Right  → (+1,  0,  0)
        private static Reference FindNamedFaceReference(
            Extrusion extrusion, string faceName, IList<string> warnings)
        {
            XYZ expected = FaceNameToNormal(faceName);
            if (expected == null)
            {
                warnings.Add("Connector: face name \""
                    + faceName
                    + "\" is not recognised; valid values are Front, Back, Left, Right, Top, Bottom.");
                return null;
            }

            Options opts = new Options
            {
                ComputeReferences = true,
                DetailLevel       = ViewDetailLevel.Fine
            };

            GeometryElement geomElem = extrusion.get_Geometry(opts);
            if (geomElem == null)
            {
                warnings.Add("Connector: could not retrieve geometry from extrusion.");
                return null;
            }

            foreach (GeometryObject geomObj in geomElem)
            {
                Solid solid = geomObj as Solid;
                if (solid == null || solid.Faces.Size == 0) continue;

                foreach (Face face in solid.Faces)
                {
                    PlanarFace pf = face as PlanarFace;
                    if (pf == null) continue;

                    if (pf.FaceNormal.DotProduct(expected) > 0.9)
                        return pf.Reference;
                }
            }

            warnings.Add("Connector: no face matching \""
                + faceName + "\" found on extrusion solid.");
            return null;
        }

        // Maps a face name to its expected outward-normal direction.
        private static XYZ FaceNameToNormal(string faceName)
        {
            switch ((faceName ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "TOP":    return new XYZ( 0,  0,  1);
                case "BOTTOM": return new XYZ( 0,  0, -1);
                case "FRONT":  return new XYZ( 0, -1,  0);
                case "BACK":   return new XYZ( 0,  1,  0);
                case "LEFT":   return new XYZ(-1,  0,  0);
                case "RIGHT":  return new XYZ( 1,  0,  0);
                default:       return null;
            }
        }

        // Associates the size parameter of a round ConnectorElement to a family parameter.
        //
        // Revit exposes the connector's dimension through an element Parameter, but the
        // exact built-in parameter ID that is associatable varies by Revit version and
        // connector type.  This helper tries three strategies in order:
        //
        //   1. CONNECTOR_RADIUS — the canonical BIP; often works, sometimes read-only.
        //   2. Any writable double parameter whose name contains "radius", "diameter",
        //      or "size" — picks up renamed or version-specific parameters.
        //   3. Any remaining writable double parameter — last-resort sweep.
        //
        // On success it returns without adding a warning.
        // On total failure it emits a diagnostic that lists every parameter attempted
        // and the exception message from the last attempt.
        private static void TryBindConnectorSizeParameter(
            FamilyManager fm,
            ConnectorElement ce,
            FamilyParameter familyParam,
            string connectorName,
            IList<string> warnings)
        {
            // Strategy 1: CONNECTOR_RADIUS by built-in parameter ID.
            Parameter bip = ce.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
            if (TryAssociateParameter(fm, bip, familyParam))
                return;

            // Strategies 2 & 3: iterate the connector's full parameter set.
            // Pass 1 — prefer parameters whose name suggests a size dimension.
            // Pass 2 — any remaining writable double parameter.
            var sizeKeywords = new[] { "radius", "diameter", "size" };
            var skipped      = new List<Parameter>(); // deferred to pass 2
            var tried        = new List<string>();    // for the diagnostic
            Exception lastEx = null;

            foreach (Parameter p in ce.Parameters)
            {
                if (p == null || p.IsReadOnly) continue;
                if (p.StorageType != StorageType.Double) continue;

                string name = (p.Definition?.Name ?? string.Empty).ToLowerInvariant();

                bool isSizeHint = false;
                foreach (string kw in sizeKeywords)
                    if (name.Contains(kw)) { isSizeHint = true; break; }

                if (!isSizeHint) { skipped.Add(p); continue; }

                tried.Add(p.Definition?.Name ?? "(unnamed)");
                if (TryAssociateParameter(fm, p, familyParam, out lastEx))
                    return;
            }

            // Pass 2: non-size-hinted parameters.
            foreach (Parameter p in skipped)
            {
                tried.Add(p.Definition?.Name ?? "(unnamed)");
                if (TryAssociateParameter(fm, p, familyParam, out lastEx))
                    return;
            }

            // All strategies exhausted — emit a precise diagnostic.
            string detail = lastEx != null
                ? lastEx.Message
                : "no writable double-storage parameters found on connector";

            string triedStr = tried.Count > 0
                ? "tried: " + string.Join(", ", tried.ToArray())
                : "CONNECTOR_RADIUS was null or read-only; no other candidates found";

            warnings.Add("Connector \"" + connectorName
                + "\": could not bind diameter parameter ["
                + triedStr + "]: " + detail);
        }

        // Attempts a single AssociateElementParameterToFamilyParameter call.
        // Returns true on success.  The overload without the out parameter is used
        // for the initial CONNECTOR_RADIUS attempt where we don't need lastEx.
        private static bool TryAssociateParameter(
            FamilyManager fm, Parameter elementParam, FamilyParameter familyParam)
        {
            Exception ignored;
            return TryAssociateParameter(fm, elementParam, familyParam, out ignored);
        }

        private static bool TryAssociateParameter(
            FamilyManager fm,
            Parameter elementParam,
            FamilyParameter familyParam,
            out Exception lastException)
        {
            lastException = null;

            if (elementParam == null || elementParam.IsReadOnly
                || elementParam.StorageType != StorageType.Double)
                return false;

            try
            {
                fm.AssociateElementParameterToFamilyParameter(elementParam, familyParam);
                return true;
            }
            catch (Exception ex)
            {
                lastException = ex;
                return false;
            }
        }

        // ── Parametric alignment helpers ─────────────────────────────────────────

        // Returns a case-insensitive name → ReferencePlane map for the family document.
        private static Dictionary<string, ReferencePlane> BuildReferencePlaneLookup(
            Document familyDoc)
        {
            var dict = new Dictionary<string, ReferencePlane>(StringComparer.OrdinalIgnoreCase);
            foreach (Element e in new FilteredElementCollector(familyDoc)
                         .OfClass(typeof(ReferencePlane)))
            {
                ReferencePlane rp = e as ReferencePlane;
                if (rp != null && !string.IsNullOrEmpty(rp.Name))
                    dict[rp.Name] = rp;
            }
            return dict;
        }

        // Returns the first non-template Elevation view in the family document.
        // Used for Z-axis (Top / Base) alignment constraints.
        private static View FindElevationView(Document familyDoc)
        {
            foreach (Element elem in new FilteredElementCollector(familyDoc)
                         .OfClass(typeof(View)))
            {
                View v = elem as View;
                if (v != null && !v.IsTemplate && v.ViewType == ViewType.Elevation)
                    return v;
            }
            return null;
        }

        // Creates NewAlignment constraints between the four lateral planar faces of
        // a rectangular extrusion and the named reference planes
        // (Left, Right, Front, Back — all in plan view).
        //
        // Top / Bottom (Z-normal faces) are NOT constrained here.  They use the
        // extrusion's built-in EXTRUSION_END_PARAM / EXTRUSION_START_PARAM approach
        // instead (see TryAssociateExtrusionHeight), which is the standard Revit
        // pattern and avoids the "The reference is not valid" error that face-based
        // NewAlignment produces on Z-normal faces in most family templates.
        //
        // Each alignment is attempted independently so one failure does not abort the
        // others.  Caller must supply an open transaction.
        private static void TryAlignExtrusionToReferencePlanes(
            Document familyDoc,
            Extrusion extrusion,
            View planView,
            View elevView,
            Dictionary<string, ReferencePlane> planesByName,
            IList<string> warnings)
        {
            if (planView == null)
            {
                warnings.Add("Extrusion alignment skipped: no plan view found in family document.");
                return;
            }

            try
            {
                Options opts = new Options
                {
                    ComputeReferences = true,
                    DetailLevel       = ViewDetailLevel.Fine
                };

                GeometryElement geomEl = extrusion.get_Geometry(opts);

                foreach (GeometryObject obj in geomEl)
                {
                    Solid solid = obj as Solid;
                    if (solid == null || solid.Faces.Size == 0) continue;

                    foreach (Face face in solid.Faces)
                    {
                        PlanarFace pf = face as PlanarFace;
                        if (pf == null) continue;

                        XYZ    n         = pf.FaceNormal;
                        string planeName = null;

                        // Only lateral faces (X/Y normals) are aligned here.
                        // Z-normal faces (Top/Bottom) are handled by parameter
                        // association in TryAssociateExtrusionHeight.
                        if      (n.X < -0.9) { planeName = "Left";  }
                        else if (n.X >  0.9) { planeName = "Right"; }
                        else if (n.Y < -0.9) { planeName = "Front"; }
                        else if (n.Y >  0.9) { planeName = "Back";  }

                        if (planeName == null) continue;

                        ReferencePlane rp;
                        if (!planesByName.TryGetValue(planeName, out rp)) continue;

                        try
                        {
                            familyDoc.FamilyCreate.NewAlignment(
                                planView, rp.GetReference(), pf.Reference);
                        }
                        catch (Exception ex)
                        {
                            warnings.Add("Alignment (" + planeName + "): " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Extrusion alignment failed: " + ex.Message);
            }
        }

        // Aligns a plan-view symbolic curve to the reference plane it lies on.
        //
        // Horizontal lines (constant Y) are matched to horizontal reference planes
        // (normal ≈ Y-axis).  Vertical lines (constant X) are matched to vertical
        // reference planes (normal ≈ X-axis).  Diagonal lines are silently skipped.
        //
        // A missing plane match or any Revit exception adds a warning; this method
        // never throws.  Caller must supply an open transaction.
        private static void TryAlignSymbolicCurveToPlane(
            Document familyDoc,
            SymbolicCurve curve,
            View planView,
            Dictionary<string, ReferencePlane> planesByName,
            IList<string> warnings)
        {
            try
            {
                Line line = curve.GeometryCurve as Line;
                if (line == null) return;

                XYZ p0 = line.GetEndPoint(0);
                XYZ p1 = line.GetEndPoint(1);

                const double tol    = 0.002;                   // ~0.6 mm in internal feet
                bool         constY = Math.Abs(p0.Y - p1.Y) < tol;
                bool         constX = Math.Abs(p0.X - p1.X) < tol;

                if (!constY && !constX) return;                // diagonal — no single-plane lock

                ReferencePlane matchPlane = null;
                foreach (ReferencePlane rp in planesByName.Values)
                {
                    Plane geom = rp.GetPlane();

                    if (constY
                        && Math.Abs(geom.Normal.Y) > 0.9
                        && Math.Abs(geom.Origin.Y - p0.Y) < tol)
                    {
                        matchPlane = rp;
                        break;
                    }
                    if (constX
                        && Math.Abs(geom.Normal.X) > 0.9
                        && Math.Abs(geom.Origin.X - p0.X) < tol)
                    {
                        matchPlane = rp;
                        break;
                    }
                }

                if (matchPlane == null) return;

                // Retrieve the curve's stable reference through computed geometry.
                Reference curveRef = null;
                try
                {
                    Options opts = new Options { ComputeReferences = true };
                    foreach (GeometryObject obj in curve.get_Geometry(opts))
                    {
                        Curve c = obj as Curve;
                        if (c != null && c.Reference != null)
                        {
                            curveRef = c.Reference;
                            break;
                        }
                    }
                }
                catch { }

                if (curveRef == null) return;

                try
                {
                    familyDoc.FamilyCreate.NewAlignment(
                        planView, matchPlane.GetReference(), curveRef);
                }
                catch (Exception ex)
                {
                    warnings.Add("Symbolic line alignment skipped: " + ex.Message);
                }
            }
            catch
            {
                // Best effort — alignment is non-critical for symbolic lines.
            }
        }

        public void Build(FamilyDefinition definition, UIApplication uiApp)
        {
            // TODO: implement full Revit family generation
        }

        // ── Output path ──────────────────────────────────────────────────────────

        // Returns the intended RFA path and creates the output folder if needed.
        // Called before SaveAndActivateDocument so the CSV can be written and
        // embedded while the family document is still open.
        public string GetOutputPath(FamilyDefinition definition)
        {
            const string outputFolder = @"C:\RevitFamilles";
            if (!Directory.Exists(outputFolder))
                Directory.CreateDirectory(outputFolder);
            return Path.Combine(outputFolder, SanitizeFileName(definition.FamilyName) + ".rfa");
        }

        // ── Lookup CSV export ────────────────────────────────────────────────────

        // Exports a Revit-compatible lookup CSV next to the RFA when the family
        // has more than one explicit type.  Returns the CSV path, or null.
        //
        // Revit size_lookup CSV structure:
        //   Column A : description-only (header BLANK, ignored by size_lookup)
        //   Column B : lookup key       (LookupKey##OTHER##)
        //   Column C+: return columns   (Width##LENGTH##MILLIMETERS, …)
        //
        // Data rows:  <description>,<key>,<width>,<depth>,<height>
        //
        // size_lookup("TableName", "Width##LENGTH##MILLIMETERS", 600 mm, LookupKey)
        //   → searches column B for LookupKey's value, returns column C.
        public string ExportLookupCsv(FamilyDefinition definition, string rfaPath)
        {
            if (definition.Types == null || definition.Types.Count <= 1)
                return null;

            // Determine which standard columns are actually populated across all types.
            var columnOrder = new[] { "Width", "Depth", "Height" };
            var activeColumns = new List<string>();
            foreach (string col in columnOrder)
            {
                foreach (FamilyTypeDefinition t in definition.Types)
                {
                    if (t.ParameterValues != null && t.ParameterValues.ContainsKey(col))
                    {
                        activeColumns.Add(col);
                        break;
                    }
                }
            }

            string folder   = Path.GetDirectoryName(rfaPath);
            string safeName = SanitizeFileName(definition.FamilyName);
            string csvPath  = Path.Combine(folder, safeName + "_Lookup.csv");

            var sb = new StringBuilder();

            // Header: column A blank (description), column B = key, columns C+ = values.
            sb.Append(",LookupKey##OTHER##");
            foreach (string col in activeColumns)
                sb.Append(",").Append(col).Append("##LENGTH##MILLIMETERS");
            sb.AppendLine();

            // One data row per type.
            foreach (FamilyTypeDefinition t in definition.Types)
            {
                if (string.IsNullOrWhiteSpace(t.Name)) continue;

                // Column A: human-readable description (ignored by size_lookup).
                sb.Append(EscapeCsvField(t.Name));
                // Column B: lookup key value (must match the LookupKey family parameter).
                sb.Append(",").Append(EscapeCsvField(t.Name));
                foreach (string col in activeColumns)
                {
                    string value = string.Empty;
                    if (t.ParameterValues != null)
                        t.ParameterValues.TryGetValue(col, out value);
                    sb.Append(",").Append(EscapeCsvField(value ?? string.Empty));
                }
                sb.AppendLine();
            }

            // UTF-8 WITHOUT BOM — Revit's ImportSizeTable rejects files that start with a BOM.
            File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return csvPath;
        }

        // ── Lookup parameter setup ───────────────────────────────────────────────

        // Ensures a Text type parameter named "LookupKey" exists in the family.
        // Returns true if it was created, false if it already existed or failed.
        public bool EnsureLookupKeyParameter(Document familyDoc, IList<string> warnings)
        {
            FamilyManager fm = familyDoc.FamilyManager;

            foreach (FamilyParameter fp in fm.GetParameters())
            {
                if (string.Equals(fp.Definition.Name, "LookupKey",
                        StringComparison.OrdinalIgnoreCase))
                    return false; // already present
            }

            using (Transaction tx = new Transaction(familyDoc, "Add LookupKey Parameter"))
            {
                tx.Start();
                try
                {
                    fm.AddParameter("LookupKey",
                        GroupTypeId.IdentityData, SpecTypeId.String.Text,
                        isInstance: false);
                    tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    warnings.Add("LookupKey parameter could not be added: " + ex.Message);
                    return false;
                }
            }
        }

        // Sets LookupKey = type name for every type in the family, one Transaction each.
        // Must run after ApplyTypes so the Revit types already exist.
        public int SetLookupKeysForTypes(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            if (definition.Types == null || definition.Types.Count == 0)
                return 0;

            FamilyManager fm = familyDoc.FamilyManager;

            FamilyParameter lookupKeyParam = null;
            foreach (FamilyParameter fp in fm.GetParameters())
            {
                if (string.Equals(fp.Definition.Name, "LookupKey",
                        StringComparison.OrdinalIgnoreCase))
                {
                    lookupKeyParam = fp;
                    break;
                }
            }

            if (lookupKeyParam == null)
            {
                warnings.Add("SetLookupKeysForTypes: LookupKey parameter not found.");
                return 0;
            }

            // Build name → FamilyType from the live document.
            var typesByName = new Dictionary<string, FamilyType>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyType ft in fm.Types)
                typesByName[ft.Name] = ft;

            int count = 0;

            foreach (FamilyTypeDefinition typeDef in definition.Types)
            {
                if (string.IsNullOrWhiteSpace(typeDef.Name)) continue;

                FamilyType ft;
                if (!typesByName.TryGetValue(typeDef.Name, out ft)) continue;

                // fm.CurrentType requires an open Transaction (triggers internal SubTransaction).
                using (Transaction tx = new Transaction(
                    familyDoc, "Set LookupKey: " + typeDef.Name))
                {
                    tx.Start();
                    try
                    {
                        fm.CurrentType = ft;
                        fm.Set(lookupKeyParam, typeDef.Name);
                        tx.Commit();
                        count++;
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        warnings.Add("LookupKey not set for type \""
                            + typeDef.Name + "\": " + ex.Message);
                    }
                }
            }

            return count;
        }

        // ── Lookup table embedding ───────────────────────────────────────────────

        // Embeds the CSV as a Revit family size table.
        // Uses FamilySizeTableManager.CreateFamilySizeTableManager when none exists yet,
        // then ImportSizeTable.  Returns true on success; adds warnings and returns false
        // on any failure so the caller can continue without crashing.
        public bool TryEmbedLookupTable(
            Document familyDoc, string csvPath, IList<string> warnings)
        {
            try
            {
                Family ownerFamily = familyDoc.OwnerFamily;
                if (ownerFamily == null)
                {
                    warnings.Add("Lookup embed: OwnerFamily is null; not a family document.");
                    return false;
                }

                ElementId familyId = ownerFamily.Id;

                // Get the manager, or create it if this family has never had size tables.
                FamilySizeTableManager manager =
                    FamilySizeTableManager.GetFamilySizeTableManager(familyDoc, familyId);

                if (manager == null)
                {
                    using (Transaction txCreate = new Transaction(
                        familyDoc, "Create Size Table Manager"))
                    {
                        txCreate.Start();
                        try
                        {
                            FamilySizeTableManager.CreateFamilySizeTableManager(
                                familyDoc, familyId);
                            txCreate.Commit();
                        }
                        catch (Exception ex)
                        {
                            txCreate.RollBack();
                            warnings.Add("Lookup embed: could not create FamilySizeTableManager: "
                                + ex.Message);
                            return false;
                        }
                    }

                    manager = FamilySizeTableManager.GetFamilySizeTableManager(
                        familyDoc, familyId);
                }

                if (manager == null)
                {
                    warnings.Add("Lookup embed: FamilySizeTableManager unavailable after creation.");
                    return false;
                }

                using (Transaction txImport = new Transaction(familyDoc, "Import Size Table"))
                {
                    txImport.Start();
                    try
                    {
                        using (FamilySizeTableErrorInfo errorInfo = new FamilySizeTableErrorInfo())
                        {
                            bool ok = manager.ImportSizeTable(familyDoc, csvPath, errorInfo);
                            if (ok)
                            {
                                txImport.Commit();
                                return true;
                            }
                            else
                            {
                                txImport.RollBack();

                                // Build a detailed diagnostic message from every
                                // FamilySizeTableErrorInfo field so the cause is
                                // visible directly in the build result warning list.
                                var diag = new StringBuilder(
                                    "Lookup embed: ImportSizeTable failed.");
                                try
                                {
                                    diag.Append(" ErrorType=")
                                        .Append(errorInfo.FamilySizeTableErrorType);
                                    if (!string.IsNullOrEmpty(errorInfo.InvalidHeaderText))
                                        diag.Append(", InvalidHeader=\"")
                                            .Append(errorInfo.InvalidHeaderText)
                                            .Append("\"");
                                    diag.Append(", Column=")
                                        .Append(errorInfo.InvalidColumnIndex);
                                    diag.Append(", Row=")
                                        .Append(errorInfo.InvalidRowIndex);
                                    if (!string.IsNullOrEmpty(errorInfo.FilePath))
                                        diag.Append(", File=\"")
                                            .Append(errorInfo.FilePath)
                                            .Append("\"");
                                }
                                catch
                                {
                                    diag.Append(" (error info unavailable)");
                                }

                                warnings.Add(diag.ToString());
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        txImport.RollBack();
                        warnings.Add("Lookup embed: ImportSizeTable threw: " + ex.Message);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Lookup embed: unexpected error: " + ex.Message);
                return false;
            }
        }

        // ── Lookup formulas ──────────────────────────────────────────────────────

        // Applies size_lookup() formulas to Width, Depth, and Height (whichever are
        // present as type parameters).  Each formula runs in its own Transaction so a
        // single failure does not abort the others.  Returns the number applied.
        //
        // Revit size_lookup syntax:
        //   size_lookup("TableName", "ColumnHeader", defaultValue, LookupKeyParam)
        //
        // IMPORTANT: the column header in the formula must match the FULL annotated
        // CSV header including the ##TYPE##UNIT suffix.  For example:
        //   size_lookup("Tbl", "Width##LENGTH##MILLIMETERS", 600 mm, LookupKey)
        //
        // The previous implementation used bare column names ("Width") which caused
        // Revit to reject the formula as invalid.
        public int TryApplyLookupFormulas(
            Document familyDoc,
            FamilyDefinition definition,
            string tableName,
            IList<string> warnings)
        {
            FamilyManager fm = familyDoc.FamilyManager;

            var paramsByName =
                new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter fp in fm.GetParameters())
                paramsByName[fp.Definition.Name] = fp;

            // Standard defaults in mm — overridden by JSON default_value when found.
            var defaults = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "Width", 600 }, { "Depth", 400 }, { "Height", 300 }
            };

            if (definition.Parameters != null)
            {
                foreach (ParameterDefinition pd in definition.Parameters)
                {
                    if (!defaults.ContainsKey(pd.Name)
                        || string.IsNullOrWhiteSpace(pd.DefaultValue))
                        continue;

                    double d;
                    if (double.TryParse(pd.DefaultValue,
                            NumberStyles.Any, CultureInfo.InvariantCulture, out d)
                        && d > 0)
                        defaults[pd.Name] = d;
                }
            }

            string[] lookupColumns = { "Width", "Depth", "Height" };

            int applied = 0;

            foreach (string colName in lookupColumns)
            {
                FamilyParameter fp;
                if (!paramsByName.TryGetValue(colName, out fp)) continue;
                if (fp.IsInstance) continue;

                // Skip parameters that already have a formula (e.g. Height = Width / 2).
                // Applying size_lookup on top would conflict with the existing formula.
                if (fp.IsDeterminedByFormula) continue;

                double defaultMm = defaults.ContainsKey(colName) ? defaults[colName] : 600;

                // The column header MUST include the full ##TYPE##UNIT annotation to
                // match the CSV header that was imported by ImportSizeTable.
                string columnHeader = colName + "##LENGTH##MILLIMETERS";

                string formula =
                    "size_lookup(\""
                    + tableName + "\", \""
                    + columnHeader + "\", "
                    + defaultMm.ToString("0", CultureInfo.InvariantCulture)
                    + " mm, LookupKey)";

                using (Transaction tx = new Transaction(
                    familyDoc, "Apply Lookup Formula: " + colName))
                {
                    tx.Start();
                    try
                    {
                        fm.SetFormula(fp, formula);
                        applied++;
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        warnings.Add("Lookup formula \""
                            + colName + "\" could not be applied: " + ex.Message
                            + " | Formula: " + formula);
                    }
                }
            }

            return applied;
        }

        // Wraps a CSV field in quotes if it contains a comma, quote, or newline.
        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return string.Empty;
            if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }
    }
}
