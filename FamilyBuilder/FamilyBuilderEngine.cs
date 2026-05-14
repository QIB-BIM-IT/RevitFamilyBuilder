using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using RevitFamilyBuilder.Config;
using RevitFamilyBuilder.Schema;

namespace RevitFamilyBuilder.FamilyBuilder
{
    public class FamilyBuilderEngine
    {
        // Thin overload kept for back-compat with any caller that does not
        // need the template-source diagnostic. The coordinator uses the
        // descriptor-returning overload below.
        public Document CreateFamilyDocument(FamilyDefinition definition, UIApplication uiApp)
        {
            string ignoredLabel;
            return CreateFamilyDocument(definition, uiApp, null, out ignoredLabel);
        }

        /// <summary>
        /// Creates the family document, preferring the company template stored
        /// on ACC over the stock Revit template.
        ///
        /// <para>Resolution order:
        /// <list type="number">
        ///   <item><description><see cref="CompanyPaths.GetCompanyTemplatePath"/>
        ///         — used when the file exists on disk;</description></item>
        ///   <item><description>otherwise <see cref="FamilyTemplateResolver.Resolve(string, string)"/>
        ///         against <c>uiApp.Application.FamilyTemplatePath</c>, with a
        ///         non-blocking warning explaining the fallback.</description></item>
        /// </list></para>
        ///
        /// <para><paramref name="templateLabel"/> is a human-readable string
        /// suitable for the build report (e.g. <c>"Metric Generic Model (entreprise)"</c>
        /// or <c>"GenericModelMetric (stock - fallback)"</c>).</para>
        /// </summary>
        public Document CreateFamilyDocument(
            FamilyDefinition definition,
            UIApplication uiApp,
            IList<string> warnings,
            out string templateLabel)
        {
            // 1) Try the company template first (deterministic path on ACC).
            string companyTemplatePath = CompanyPaths.GetCompanyTemplatePath();
            if (File.Exists(companyTemplatePath))
            {
                Document companyDoc = uiApp.Application.NewFamilyDocument(companyTemplatePath);
                if (companyDoc == null)
                    throw new InvalidOperationException(
                        "Revit returned a null document for company template: \""
                        + companyTemplatePath + "\".");

                templateLabel = Path.GetFileNameWithoutExtension(companyTemplatePath)
                    + " (entreprise)";
                return companyDoc;
            }

            // 2) Fall back to the stock template; warn explicitly so the
            //    operator knows the company asset is missing on this machine.
            if (warnings != null)
            {
                warnings.Add(
                    "Template d'entreprise introuvable au chemin "
                    + companyTemplatePath
                    + ". Utilisation du template stock GenericModelMetric en fallback.");
            }

            string templateRoot = uiApp.Application.FamilyTemplatePath;
            if (string.IsNullOrWhiteSpace(templateRoot) || !Directory.Exists(templateRoot))
                throw new InvalidOperationException(
                    "Revit family template path is not configured or does not exist: \""
                    + templateRoot + "\".");

            string stockPath = FamilyTemplateResolver.Resolve(
                definition.FamilyTemplate, templateRoot);

            Document familyDoc = uiApp.Application.NewFamilyDocument(stockPath);
            if (familyDoc == null)
                throw new InvalidOperationException(
                    "Revit returned a null document for template: \"" + stockPath + "\".");

            templateLabel = (definition.FamilyTemplate ?? "GenericModelMetric")
                + " (stock - fallback)";
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

        // Adds the parameters declared in <paramref name="definition"/> to the
        // family document. Each parameter is first looked up in the company
        // shared-parameters file (<c>CompanyPaths.GetSharedParametersPath()</c>);
        // when it is present AND its spec type matches the JSON's declared
        // type, the parameter is created as a SHARED parameter (federated by
        // GUID across families). Any other case — missing file, missing entry,
        // or spec mismatch — falls back to a LOCAL parameter and adds a
        // warning explaining why. The "happy path" (SHARED) emits no warning;
        // a quiet warnings list means every parameter was properly federated.
        public int AddParameters(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            if (definition.Parameters == null || definition.Parameters.Count == 0)
                return 0;

            FamilyManager fm = familyDoc.FamilyManager;

            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter existing in fm.GetParameters())
                existingNames.Add(existing.Definition.Name);

            // Load the shared-parameters file once per batch. May return null
            // in degraded mode (file missing, malformed, or unreachable on
            // ACC) — that's not blocking: every parameter just falls back to
            // local. TryLoadSharedDefinitions emits a SINGLE global warning
            // in that case, which is enough — we don't repeat "shared file
            // unavailable" once per parameter.
            DefinitionFile sharedFile =
                TryLoadSharedDefinitions(familyDoc.Application, warnings);

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

                    // ── Attempt SHARED creation first ─────────────────────
                    ExternalDefinition extDef =
                        FindSharedDefinition(sharedFile, paramDef.Name);

                    bool   createdAsShared = false;
                    string fallbackReason  = null;

                    if (extDef != null)
                    {
                        ForgeTypeId sharedSpec = extDef.GetDataType();
                        if (sharedSpec != null && sharedSpec.Equals(specType))
                        {
                            try
                            {
                                FamilyParameter fp = fm.AddParameter(
                                    extDef, groupType, paramDef.IsInstance);
                                if (fp != null)
                                {
                                    TrySetDefaultValue(fm, fp, paramDef);
                                    existingNames.Add(paramDef.Name);
                                    created++;
                                    createdAsShared = true;
                                    Debug.WriteLine(
                                        "[SharedParam] \"" + paramDef.Name
                                        + "\" created as SHARED.");
                                }
                            }
                            catch (Exception ex)
                            {
                                fallbackReason = "shared creation failed ("
                                    + ex.Message + ")";
                            }
                        }
                        else
                        {
                            string sharedSpecId = sharedSpec != null
                                ? sharedSpec.TypeId : "<unknown>";
                            fallbackReason = "spec mismatch (JSON declares "
                                + paramDef.Type + ", shared file declares "
                                + sharedSpecId + ")";
                        }
                    }
                    else if (sharedFile != null)
                    {
                        fallbackReason = "not found in shared parameters file";
                    }
                    // else: sharedFile == null → global warning already
                    // emitted; do not repeat per parameter.

                    // ── LOCAL fallback ────────────────────────────────────
                    if (!createdAsShared)
                    {
                        try
                        {
                            FamilyParameter fp = fm.AddParameter(
                                paramDef.Name, groupType, specType, paramDef.IsInstance);

                            if (fp != null)
                            {
                                TrySetDefaultValue(fm, fp, paramDef);
                                existingNames.Add(paramDef.Name);
                                created++;
                                if (fallbackReason != null)
                                    warnings.Add("Parameter \""
                                        + paramDef.Name + "\" created as LOCAL: "
                                        + fallbackReason + ".");
                            }
                        }
                        catch (Exception ex)
                        {
                            warnings.Add("Parameter \"" + paramDef.Name
                                + "\" creation failed: " + ex.Message + ".");
                        }
                    }
                }

                tx.Commit();
            }

            return created;
        }

        // Loads the company shared-parameters file (path resolved through
        // <c>CompanyPaths.GetSharedParametersPath()</c>) and returns the
        // resulting <c>DefinitionFile</c>. Returns <c>null</c> when the file
        // is missing, malformed, or the Revit API refuses to open it; in
        // every null-return case, a single descriptive warning is appended to
        // <paramref name="warnings"/> so the caller can render it in the
        // build report. Callers should treat null as "degraded mode — every
        // parameter must be created locally" and proceed.
        private static DefinitionFile TryLoadSharedDefinitions(
            Application app, IList<string> warnings)
        {
            string path = CompanyPaths.GetSharedParametersPath();

            if (!File.Exists(path))
            {
                warnings.Add("Shared parameters file not found at " + path
                    + ". All parameters will be created as local.");
                return null;
            }

            try
            {
                app.SharedParametersFilename = path;
                DefinitionFile file = app.OpenSharedParameterFile();
                if (file == null)
                {
                    warnings.Add("Failed to open shared parameters file at "
                        + path + " (OpenSharedParameterFile returned null). "
                        + "All parameters will be created as local.");
                }
                return file;
            }
            catch (Exception ex)
            {
                warnings.Add("Error opening shared parameters file at " + path
                    + ": " + ex.Message
                    + ". All parameters will be created as local.");
                return null;
            }
        }

        // Looks up a single shared-parameter definition by name across every
        // group in the loaded <see cref="DefinitionFile"/>. Case-insensitive
        // match — Revit shared-parameter names are conventionally case
        // sensitive, but accepting case-insensitive matches here surfaces
        // typos as obvious "spec mismatch" or "not found" warnings rather
        // than silent local creation. Returns the first
        // <c>ExternalDefinition</c> found, or <c>null</c> if absent.
        private static ExternalDefinition FindSharedDefinition(
            DefinitionFile sharedFile, string parameterName)
        {
            if (sharedFile == null || string.IsNullOrWhiteSpace(parameterName))
                return null;

            foreach (DefinitionGroup grp in sharedFile.Groups)
            {
                foreach (Definition def in grp.Definitions)
                {
                    if (string.Equals(def.Name, parameterName,
                            StringComparison.OrdinalIgnoreCase))
                        return def as ExternalDefinition;
                }
            }
            return null;
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

        // Mapping from GeometryDefinition.Id → ElementId of the extrusion
        // created by the most recent AddGeometry call. Exposed so future
        // coordinator steps (e.g. connector / void targeting by geometry id)
        // can resolve which solid they are working on. Not consumed yet.
        private readonly Dictionary<string, ElementId> _geometryIdMap =
            new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, ElementId> GeometryIdMap
        {
            get { return _geometryIdMap; }
        }

        public int AddGeometry(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            List<string> ignoredDescriptors;
            int ignoredCreated, ignoredReused;
            return AddGeometry(
                familyDoc, definition, warnings,
                out ignoredDescriptors,
                out ignoredCreated,
                out ignoredReused);
        }

        /// <summary>
        /// Multi-extrusion aware: iterates the <c>geometry[]</c> array, creates
        /// one independent solid extrusion per entry, optionally assigns a
        /// family subcategory (creating or reusing it as needed), and stores
        /// the resulting <c>id → ElementId</c> map on the engine.
        ///
        /// <para>Extrusions are deliberately NOT renamed inside Revit — that
        /// approach was removed along with the "Name could not be applied"
        /// warning. Visual distinction between geometries is done through
        /// subcategories (Object Styles), which is the Revit-native way.</para>
        ///
        /// After the loop, a lightweight <c>Regenerate()</c> pass captures any
        /// Revit failure message about coincident / overlapping / duplicate
        /// geometry and routes it to <paramref name="warnings"/> (non-blocking).
        /// </summary>
        public int AddGeometry(
            Document familyDoc,
            FamilyDefinition definition,
            IList<string> warnings,
            out List<string> geometryDescriptors,
            out int subcategoriesCreated,
            out int subcategoriesReused)
        {
            geometryDescriptors = new List<string>();
            subcategoriesCreated = 0;
            subcategoriesReused = 0;

            // Reset the map so a fresh build never leaks entries from a
            // previous run against another document.
            _geometryIdMap.Clear();

            if (definition.Geometry == null || definition.Geometry.Count == 0)
                return 0;

            int count = 0;

            // Pre-fetch views and planes once (read-only — no transaction needed).
            View planView     = FindWorkingView(familyDoc);
            View elevView     = FindElevationView(familyDoc);
            var  planesByName = BuildReferencePlaneLookup(familyDoc);

            // Track subcategories we have already resolved in this build so
            // two geometries requesting the same name share one category and
            // only the first triggers a "created" count.
            var resolvedSubcats = new Dictionary<string, Category>(
                StringComparer.OrdinalIgnoreCase);

            foreach (GeometryDefinition geoDef in definition.Geometry)
            {
                if (geoDef.Type != GeometryType.Extrusion)
                {
                    warnings.Add("Geometry type \""
                        + geoDef.Type + "\" is not supported in v1; skipped.");
                    continue;
                }

                // Resolve the six bounding reference planes for this
                // geometry. Each slot falls back to the canonical name
                // when the geometry does not override it, so legacy JSON
                // that only declares a single centred box keeps working.
                string leftName  = string.IsNullOrWhiteSpace(geoDef.LeftPlane)
                    ? "Left"  : geoDef.LeftPlane.Trim();
                string rightName = string.IsNullOrWhiteSpace(geoDef.RightPlane)
                    ? "Right" : geoDef.RightPlane.Trim();
                string frontName = string.IsNullOrWhiteSpace(geoDef.FrontPlane)
                    ? "Front" : geoDef.FrontPlane.Trim();
                string backName  = string.IsNullOrWhiteSpace(geoDef.BackPlane)
                    ? "Back"  : geoDef.BackPlane.Trim();
                string baseName  = string.IsNullOrWhiteSpace(geoDef.BasePlane)
                    ? "Base"  : geoDef.BasePlane.Trim();
                string topName   = string.IsNullOrWhiteSpace(geoDef.TopPlane)
                    ? "Top"   : geoDef.TopPlane.Trim();

                ReferencePlane leftRp, rightRp, frontRp, backRp, baseRp, topRp;
                if (!planesByName.TryGetValue(leftName,  out leftRp)
                    || !planesByName.TryGetValue(rightName, out rightRp)
                    || !planesByName.TryGetValue(frontName, out frontRp)
                    || !planesByName.TryGetValue(backName,  out backRp)
                    || !planesByName.TryGetValue(baseName,  out baseRp)
                    || !planesByName.TryGetValue(topName,   out topRp))
                {
                    warnings.Add("Geometry \"" + (geoDef.Id ?? "<no-id>")
                        + "\" skipped: one or more bounding reference planes "
                        + "were not found in the family document.");
                    continue;
                }

                Extrusion ext = BuildRectangularExtrusion(
                    familyDoc,
                    leftRp, rightRp, frontRp, backRp, baseRp, topRp,
                    planView, elevView, warnings);

                if (ext == null) continue;

                count++;

                // Record the id → ElementId mapping even if subcategory
                // assignment fails below — the extrusion exists and future
                // steps must be able to target it.
                string id = (geoDef.Id ?? string.Empty).Trim();
                if (id.Length > 0 && !_geometryIdMap.ContainsKey(id))
                    _geometryIdMap[id] = ext.Id;

                // Resolve the company convention first (if any). The
                // convention's SubcategoryName intentionally overrides the
                // legacy "subcategory" field — they should normally be aligned
                // in the JSON, so a divergence is worth a warning.
                string subcatRequested = (geoDef.Subcategory ?? string.Empty).Trim();
                string conventionName = (geoDef.Convention ?? string.Empty).Trim();
                GeometryConvention convention = null;
                if (conventionName.Length > 0)
                {
                    try
                    {
                        convention = ConventionLibrary.Get(conventionName);
                    }
                    catch (ArgumentException ex)
                    {
                        // Defensive: validation already rejects unknown names.
                        warnings.Add("Geometry \"" + (geoDef.Id ?? "<no-id>")
                            + "\": " + ex.Message);
                    }

                    if (convention != null)
                    {
                        string conventionSubcat =
                            (convention.SubcategoryName ?? string.Empty).Trim();

                        if (conventionSubcat.Length > 0
                            && subcatRequested.Length > 0
                            && !string.Equals(conventionSubcat, subcatRequested,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            warnings.Add("Geometry \"" + (geoDef.Id ?? "<no-id>")
                                + "\": convention \"" + convention.Name
                                + "\" overrides subcategory \"" + subcatRequested
                                + "\" with \"" + conventionSubcat + "\".");
                        }

                        // Convention wins.
                        if (conventionSubcat.Length > 0)
                            subcatRequested = conventionSubcat;

                        // Other convention slots (LineColorRgb, LineProjection,
                        // LinePatternName, DefaultMaterialName) are all null in
                        // this PR's library, so there is nothing else to apply.
                        // When a future PR sets non-null values, this is the
                        // place to wire them up to the corresponding Revit
                        // calls (Category line graphics, material assignment).
                    }
                }

                string subcatApplied = null;
                if (subcatRequested.Length > 0)
                {
                    Category subCat;
                    if (!resolvedSubcats.TryGetValue(subcatRequested, out subCat))
                    {
                        bool created;
                        subCat = EnsureSubcategory(
                            familyDoc, subcatRequested, warnings, out created);
                        if (subCat != null)
                        {
                            resolvedSubcats[subcatRequested] = subCat;
                            if (created) subcategoriesCreated++;
                            else         subcategoriesReused++;
                        }
                    }
                    else
                    {
                        // Same subcategory requested by a previous geometry
                        // in this same build — already resolved, counts as reuse.
                        subcategoriesReused++;
                    }

                    if (subCat != null
                        && TryAssignSubcategory(familyDoc, ext, subCat, warnings))
                    {
                        subcatApplied = subCat.Name;
                    }
                }

                geometryDescriptors.Add(
                    (id.Length > 0 ? id : "<no-id>")
                    + (subcatApplied != null ? " (" + subcatApplied + ")" : "")
                    + " : [" + leftName + ", " + rightName
                    + ", " + frontName + ", " + backName
                    + ", " + baseName + ", " + topName + "]");
            }

            // Run a final regenerate to surface Revit warnings about coincident /
            // overlapping / duplicate geometry. Non-blocking: we capture them
            // into the build-report warnings list and continue.
            if (count > 0)
            {
                try
                {
                    using (Transaction tx = new Transaction(
                        familyDoc, "Check geometry overlaps"))
                    {
                        FailureHandlingOptions fho = tx.GetFailureHandlingOptions();
                        fho.SetFailuresPreprocessor(
                            new CoincidentGeometryWarningCollector(warnings));
                        fho.SetClearAfterRollback(true);
                        tx.SetFailureHandlingOptions(fho);

                        tx.Start();
                        familyDoc.Regenerate();
                        tx.Commit();
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("Coincident-geometry check skipped: " + ex.Message);
                }
            }

            return count;
        }

        // Resolves or creates a family subcategory under the family's own
        // category. Returns the matching <see cref="Category"/> on success,
        // <c>null</c> if the family owner or its category cannot be accessed.
        private static Category EnsureSubcategory(
            Document familyDoc,
            string subcategoryName,
            IList<string> warnings,
            out bool created)
        {
            created = false;

            Family ownerFamily = familyDoc.OwnerFamily;
            Category parent = ownerFamily != null ? ownerFamily.FamilyCategory : null;
            if (parent == null)
            {
                warnings.Add("Subcategory \"" + subcategoryName
                    + "\" skipped: family category is not available.");
                return null;
            }

            // Reuse if a subcategory with the same name (case-insensitive)
            // already lives under this family's category.
            foreach (Category existing in parent.SubCategories)
            {
                if (string.Equals(existing.Name, subcategoryName,
                        StringComparison.OrdinalIgnoreCase))
                    return existing;
            }

            try
            {
                using (Transaction tx = new Transaction(
                    familyDoc, "Create Subcategory: " + subcategoryName))
                {
                    tx.Start();
                    Category fresh = familyDoc.Settings.Categories.NewSubcategory(
                        parent, subcategoryName);
                    tx.Commit();
                    created = true;
                    return fresh;
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Subcategory \"" + subcategoryName
                    + "\" could not be created: " + ex.Message);
                return null;
            }
        }

        private static bool TryAssignSubcategory(
            Document familyDoc, Extrusion ext, Category subCat, IList<string> warnings)
        {
            try
            {
                using (Transaction tx = new Transaction(
                    familyDoc, "Assign Subcategory"))
                {
                    tx.Start();
                    ext.Subcategory = subCat;
                    tx.Commit();
                }
                return true;
            }
            catch (Exception ex)
            {
                warnings.Add("Subcategory \"" + subCat.Name
                    + "\" could not be assigned to extrusion: " + ex.Message);
                return false;
            }
        }

        // Captures Revit failure messages that indicate coincident / overlapping
        // / duplicate solids OR unsatisfied constraints between geometries and
        // reference planes, routes them to the build-report warnings, and tells
        // Revit to swallow them so the build is never blocked by a modal.
        //
        // Constraint-related keywords are caught here (rather than in a separate
        // collector) so a single regenerate pass at the end of AddGeometry can
        // surface both "two extrusions overlap" and "constraints not satisfied"
        // issues in one place.
        private sealed class CoincidentGeometryWarningCollector : IFailuresPreprocessor
        {
            private readonly IList<string> _warnings;

            public CoincidentGeometryWarningCollector(IList<string> warnings)
            {
                _warnings = warnings;
            }

            public FailureProcessingResult PreprocessFailures(
                FailuresAccessor accessor)
            {
                foreach (FailureMessageAccessor msg in accessor.GetFailureMessages())
                {
                    string desc = msg.GetDescriptionText();
                    if (string.IsNullOrWhiteSpace(desc)) continue;

                    string lower = desc.ToLowerInvariant();

                    bool overlap =
                        lower.Contains("coincid")   || lower.Contains("overlap")
                     || lower.Contains("identical") || lower.Contains("duplicate")
                     || lower.Contains("joined");

                    bool constraint =
                        lower.Contains("constraint")  || lower.Contains("consistent")
                     || lower.Contains("satisf")      || lower.Contains("over-constrain");

                    if (overlap)
                    {
                        _warnings.Add("Geometry overlap warning: " + desc);
                        accessor.DeleteWarning(msg);
                    }
                    else if (constraint)
                    {
                        _warnings.Add("Constraint warning: " + desc);
                        accessor.DeleteWarning(msg);
                    }
                }
                return FailureProcessingResult.Continue;
            }
        }

        // ── Unified rectangular extrusion builder ────────────────────────────────
        //
        // This is the SINGLE code path for creating a locked rectangular extrusion.
        // All geometry requests of type "Extrusion" must go through here.
        //
        // The initial profile and extrusion depth are derived from the ACTUAL
        // positions of the six bounding reference planes passed in by the caller.
        // This allows two geometries to share a reference plane in the middle
        // (e.g. body_primary bounded by [Left, Mid_LR, ...] and body_secondary
        // bounded by [Mid_LR, Right, ...]) and each be locked to its own plane
        // set.
        //
        // Guarantees:
        //   • Rectangular profile spans [leftRp..rightRp] × [frontRp..backRp]
        //     at Z = baseRp, extruded up to Z = topRp
        //   • 4 lateral faces locked to the declared left/right/front/back planes
        //     (via NewAlignment in plan view)
        //   • Bottom face locked to the declared base plane in elevation view
        //   • Top face locked to the declared top plane in elevation view
        //   • The two alignment transactions are independent so a failed face lock
        //     never rolls back the committed solid
        //
        // For the Top/Bottom locks to succeed the "base" and "top" reference planes
        // MUST have been created with Orientation = "elevation" (Z-normal planes).
        private static Extrusion BuildRectangularExtrusion(
            Document familyDoc,
            ReferencePlane leftRp, ReferencePlane rightRp,
            ReferencePlane frontRp, ReferencePlane backRp,
            ReferencePlane baseRp, ReferencePlane topRp,
            View planView, View elevView,
            IList<string> warnings)
        {
            // Read the actual positions of the bounding planes in the live
            // family document. Each plane's origin projected onto the relevant
            // axis gives the coordinate of the face it will drive.
            double xLeft  = leftRp.GetPlane().Origin.X;
            double xRight = rightRp.GetPlane().Origin.X;
            double yFront = frontRp.GetPlane().Origin.Y;
            double yBack  = backRp.GetPlane().Origin.Y;
            double zBase  = baseRp.GetPlane().Origin.Z;
            double zTop   = topRp.GetPlane().Origin.Z;

            double xMin = Math.Min(xLeft,  xRight);
            double xMax = Math.Max(xLeft,  xRight);
            double yMin = Math.Min(yFront, yBack);
            double yMax = Math.Max(yFront, yBack);
            double zMin = Math.Min(zBase,  zTop);
            double zMax = Math.Max(zBase,  zTop);

            double height = zMax - zMin;
            if ((xMax - xMin) < 0.001 || (yMax - yMin) < 0.001 || height < 0.001)
            {
                warnings.Add("Extrusion skipped: one or more bounding reference "
                    + "planes are coincident (zero-thickness box).");
                return null;
            }

            Extrusion ext = null;

            // ── Step 1: create the solid extrusion ────────────────────────────────
            // Extrusion naming is intentionally NOT attempted: Revit extrusions
            // do not expose a user-facing name and any attempt to set one
            // triggers a "Name could not be applied" warning. Visual distinction
            // between geometries is done via subcategories, assigned by the
            // caller after this method returns.
            using (Transaction tx = new Transaction(familyDoc, "Add Rectangular Extrusion"))
            {
                tx.Start();
                try
                {
                    XYZ p1 = new XYZ(xMin, yMin, zMin);
                    XYZ p2 = new XYZ(xMax, yMin, zMin);
                    XYZ p3 = new XYZ(xMax, yMax, zMin);
                    XYZ p4 = new XYZ(xMin, yMax, zMin);

                    CurveArray loop = new CurveArray();
                    loop.Append(Line.CreateBound(p1, p2));
                    loop.Append(Line.CreateBound(p2, p3));
                    loop.Append(Line.CreateBound(p3, p4));
                    loop.Append(Line.CreateBound(p4, p1));

                    CurveArrArray profile = new CurveArrArray();
                    profile.Append(loop);

                    // Sketch plane sits on the base elevation so the extrusion
                    // grows from zMin (base) up to zMax (top).
                    SketchPlane sketchPlane = SketchPlane.Create(
                        familyDoc,
                        Plane.CreateByNormalAndOrigin(
                            XYZ.BasisZ, new XYZ(0, 0, zMin)));

                    ext = familyDoc.FamilyCreate.NewExtrusion(
                        true, profile, sketchPlane, height);
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    warnings.Add("Rectangular extrusion creation failed: " + ex.Message);
                    return null;
                }
            }

            // ── Step 2: lock every face to its named reference plane ─────────────
            // Kept in a separate transaction so a failed alignment never rolls back
            // the already-committed solid geometry.
            try
            {
                using (Transaction txAlign = new Transaction(
                    familyDoc, "Lock Extrusion Faces to Reference Planes"))
                {
                    txAlign.Start();
                    TryAlignExtrusionToReferencePlanes(
                        familyDoc, ext, planView, elevView,
                        leftRp, rightRp, frontRp, backRp, baseRp, topRp,
                        warnings);
                    txAlign.Commit();
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Extrusion face-locking transaction failed: " + ex.Message);
            }

            return ext;
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
        // FlexTest verifies that the parametric constraints added by AddGeometry are
        // correct by driving Width / Depth / Height to two test values (×1.5 and ×0.7
        // of the current defaults) and checking that the solid geometry regenerates
        // without errors.
        //
        // Each test pass runs inside a transaction that is ROLLED BACK afterwards so
        // the family document is left exactly as it was before the test.  The method
        // is safe to call between AddGeometry and SaveAndActivateDocument.
        //
        // Formula-driven parameters cannot be set directly; FlexTest will log which
        // parameters were skipped and still count the pass as valid.
        //
        // Returns a multi-line summary string; also appends items to warnings.
        public string FlexTest(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("── FlexTest ──────────────────────────────────────────");

            FamilyManager fm = familyDoc.FamilyManager;

            // Locate Width, Depth, Height parameters (any that exist).
            var dimParamNames = new[] { "Width", "Depth", "Height" };
            var dimParams = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter fp in fm.GetParameters())
                if (Array.IndexOf(dimParamNames, fp.Definition.Name) >= 0)
                    dimParams[fp.Definition.Name] = fp;

            if (dimParams.Count == 0)
            {
                string msg = "FlexTest skipped: no Width / Depth / Height parameters found.";
                sb.AppendLine(msg);
                warnings.Add(msg);
                return sb.ToString();
            }

            // Record current values from the active family type.
            var origValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            FamilyType activeType = fm.CurrentType;
            foreach (var kv in dimParams)
            {
                double? val = activeType != null ? activeType.AsDouble(kv.Value) : null;
                if (!val.HasValue || val.Value < 1e-6)
                {
                    // Fall back to the JSON default (mm → ft).
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
                                    System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                            {
                                fallback = UnitUtils.ConvertToInternalUnits(
                                    parsed, UnitTypeId.Millimeters);
                            }
                            break;
                        }
                    }
                    origValues[kv.Key] = fallback > 1e-6 ? fallback : 0.3; // 300 mm default
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

            bool allPassed = pass1 && pass2;
            sb.AppendLine("FlexTest result: " + (allPassed ? "PASSED ✓" : "FAILED — see warnings above"));
            sb.AppendLine("──────────────────────────────────────────────────────");

            if (!allPassed)
                warnings.Add("FlexTest: one or more flex passes failed. Check constraints.");

            return sb.ToString();
        }

        // Runs a single flex pass at a given multiplier.  The transaction is rolled back
        // so no permanent change is made to the family document.
        // Returns true if the solid geometry survived the regeneration without errors.
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

            using (Transaction tx = new Transaction(familyDoc, "FlexTest " + label))
            {
                tx.Start();

                var skipped = new List<string>();
                var applied = new List<string>();

                foreach (var kv in dimParams)
                {
                    double newVal = origValues[kv.Key] * multiplier;
                    if (newVal < 0.001) newVal = 0.001; // guard against zero
                    try
                    {
                        fm.Set(kv.Value, newVal);
                        applied.Add(kv.Key + "="
                            + UnitUtils.ConvertFromInternalUnits(newVal, UnitTypeId.Millimeters)
                                .ToString("0.0") + " mm");
                    }
                    catch
                    {
                        // Formula-driven parameters cannot be set; skip and continue.
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

                tx.RollBack(); // restore — the pass is purely a validation step
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
                string idLabel = string.IsNullOrWhiteSpace(geo.Id)
                    ? "<no-id>" : geo.Id;
                string subLabel = string.IsNullOrWhiteSpace(geo.Subcategory)
                    ? string.Empty : " [" + geo.Subcategory + "]";
                sb.AppendLine("Create geometry: " + idLabel + subLabel
                    + " — " + geo.Type + " (" + geo.Profile + ")"
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
        //
        // Each connector targets a SPECIFIC extrusion by its geometry id
        // (populated into _geometryIdMap by AddGeometry). This is the first
        // consumer of that mapping — before this PR, AddConnectors fell back
        // to extrusions[0], which silently picked a host regardless of what
        // the JSON asked for. Now the JSON is authoritative.

        // Thin overload kept for back-compat with callers that do not need
        // the per-connector descriptor list.
        public int AddConnectors(
            Document familyDoc, FamilyDefinition definition, IList<string> warnings)
        {
            List<string> ignored;
            return AddConnectors(familyDoc, definition, warnings, out ignored);
        }

        /// <summary>
        /// Creates the MEP connectors declared in <paramref name="definition"/>
        /// and returns the number of successful creations. Each successful
        /// connector produces one human-readable entry in
        /// <paramref name="connectorDescriptors"/>, e.g.
        /// <c>connector[0]: body_primary.back (In, Global, Round, ⌀Diameter=200mm)</c>.
        /// </summary>
        public int AddConnectors(
            Document familyDoc,
            FamilyDefinition definition,
            IList<string> warnings,
            out List<string> connectorDescriptors)
        {
            connectorDescriptors = new List<string>();

            if (definition.Connectors == null || definition.Connectors.Count == 0)
                return 0;

            FamilyManager fm = familyDoc.FamilyManager;
            var paramsByName =
                new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyParameter fp in fm.GetParameters())
                paramsByName[fp.Definition.Name] = fp;

            // Count In / Out / Bidirectional per target geometry so we can
            // flag a convention warning after the loop (non-blocking).
            var flowsPerGeometry =
                new Dictionary<string, Dictionary<FlowDirectionType, int>>(
                    StringComparer.OrdinalIgnoreCase);

            int created = 0;

            for (int i = 0; i < definition.Connectors.Count; i++)
            {
                ConnectorDefinition connDef = definition.Connectors[i];
                string label = "connector[" + i + "]";

                // ── Resolve target geometry via the id → ElementId map ────────
                string geoId = (connDef.TargetGeometryId ?? string.Empty).Trim();
                if (geoId.Length == 0)
                {
                    warnings.Add(label + ": target_geometry_id is empty; skipped.");
                    continue;
                }

                ElementId extId;
                if (!_geometryIdMap.TryGetValue(geoId, out extId)
                    || extId == ElementId.InvalidElementId)
                {
                    warnings.Add(label + ": target_geometry_id \"" + geoId
                        + "\" does not match any geometry built in this family "
                        + "(check AddGeometry ran successfully); skipped.");
                    continue;
                }

                Extrusion host = familyDoc.GetElement(extId) as Extrusion;
                if (host == null || !host.IsSolid)
                {
                    warnings.Add(label + ": target geometry \"" + geoId
                        + "\" is not a solid extrusion; skipped.");
                    continue;
                }

                // ── Profile guard: Round | Rectangular ────────────────────────
                string profile      = (connDef.Profile ?? string.Empty).Trim();
                bool   isRound      = string.Equals(profile, "Round",
                                          StringComparison.OrdinalIgnoreCase);
                bool   isRectangular= string.Equals(profile, "Rectangular",
                                          StringComparison.OrdinalIgnoreCase);

                if (!isRound && !isRectangular)
                {
                    warnings.Add(label + ": profile \"" + connDef.Profile
                        + "\" is not supported; use \"Round\" or \"Rectangular\".");
                    continue;
                }

                // ── Face resolution on THIS specific extrusion ────────────────
                string faceName = (connDef.TargetFace ?? string.Empty).Trim();
                XYZ    faceCentre;
                Reference faceRef = FindNamedFaceReference(
                    host, faceName, label, out faceCentre, warnings);
                if (faceRef == null)
                    continue; // helper already emitted a warning

                // ── Enum parsing ──────────────────────────────────────────────
                DuctSystemType    sysType = ParseDuctSystemType(connDef.SystemClassification);
                FlowDirectionType flow    = ParseFlowDirection(connDef.FlowDirection);

                // ── Create the connector and apply flow / size association ────
                using (Transaction tx = new Transaction(
                    familyDoc, "Add Connector: " + geoId + "." + faceName))
                {
                    tx.Start();
                    try
                    {
                        ConnectorProfileType profileType = isRound
                            ? ConnectorProfileType.Round
                            : ConnectorProfileType.Rectangular;

                        ConnectorElement ce = ConnectorElement.CreateDuctConnector(
                            familyDoc, sysType, profileType, faceRef);

                        // Flow direction: ConnectorElement.Direction is
                        // get-only, so drive it through the BuiltInParameter.
                        TrySetFlowDirection(ce, flow, label, warnings);

                        // Extra diagnostic bits (only used for Rectangular).
                        string sizeDesc = null;

                        if (isRound)
                        {
                            // ── Round path — unchanged (PR 4 behaviour) ──────
                            if (!string.IsNullOrWhiteSpace(connDef.DiameterParameter))
                            {
                                FamilyParameter diamFp;
                                if (paramsByName.TryGetValue(
                                        connDef.DiameterParameter.Trim(), out diamFp))
                                    TryBindConnectorSizeParameter(
                                        fm, ce, diamFp, label, warnings);
                                else
                                    warnings.Add(label + ": diameter_parameter \""
                                        + connDef.DiameterParameter + "\" not found.");
                            }
                        }
                        else // isRectangular
                        {
                            // ── Rectangular path — strict ordering ───────────
                            //
                            // The Revit API exposes CONNECTOR_WIDTH /
                            // CONNECTOR_HEIGHT on a newly-created rectangular
                            // connector, but those parameters are only usable
                            // AFTER a document regenerate. Likewise, the
                            // family-parameter association only propagates
                            // the size to the connector after a second
                            // regenerate. Both steps are captured by rules
                            // added to AGENTS.md.
                            familyDoc.Regenerate();

                            // Access the connector's own Width / Height parameters.
                            Parameter ceWidth  = ce.get_Parameter(
                                BuiltInParameter.CONNECTOR_WIDTH);
                            Parameter ceHeight = ce.get_Parameter(
                                BuiltInParameter.CONNECTOR_HEIGHT);

                            // Resolve target family parameters.
                            FamilyParameter widthFp  = null;
                            FamilyParameter heightFp = null;

                            if (!string.IsNullOrWhiteSpace(connDef.WidthParameter))
                            {
                                if (!paramsByName.TryGetValue(
                                        connDef.WidthParameter.Trim(), out widthFp))
                                    warnings.Add(label + ": width_parameter \""
                                        + connDef.WidthParameter + "\" not found.");
                            }
                            if (!string.IsNullOrWhiteSpace(connDef.HeightParameter))
                            {
                                if (!paramsByName.TryGetValue(
                                        connDef.HeightParameter.Trim(), out heightFp))
                                    warnings.Add(label + ": height_parameter \""
                                        + connDef.HeightParameter + "\" not found.");
                            }

                            // Associate Width and Height independently so a
                            // failure on one does not prevent the other.
                            bool widthOk = TryAssociateConnectorBuiltIn(
                                fm, ceWidth, widthFp, "Width", label, warnings);
                            bool heightOk = TryAssociateConnectorBuiltIn(
                                fm, ceHeight, heightFp, "Height", label, warnings);

                            // Second regenerate so Revit pushes the associated
                            // family-parameter VALUES onto the connector. Without
                            // this, selecting the connector in Revit may show
                            // the association mark but a stale default size.
                            familyDoc.Regenerate();

                            sizeDesc = "W="
                                + (widthOk ? connDef.WidthParameter : "?")
                                + ", H="
                                + (heightOk ? connDef.HeightParameter : "?");
                        }

                        tx.Commit();
                        created++;

                        // Bookkeeping for the post-loop convention check.
                        Dictionary<FlowDirectionType, int> flowCounts;
                        if (!flowsPerGeometry.TryGetValue(geoId, out flowCounts))
                        {
                            flowCounts = new Dictionary<FlowDirectionType, int>();
                            flowsPerGeometry[geoId] = flowCounts;
                        }
                        if (!flowCounts.ContainsKey(flow)) flowCounts[flow] = 0;
                        flowCounts[flow]++;

                        // Diagnostic line — proves the connector sits on the
                        // targeted geometry's face, not the family centre.
                        string profileLabel = isRound ? "Round" : "Rectangular";
                        string sizeSuffix;
                        if (isRound)
                        {
                            string diamTxt = FormatBoundDiameter(
                                fm, connDef.DiameterParameter);
                            sizeSuffix = (diamTxt != null) ? ", " + diamTxt : "";
                        }
                        else
                        {
                            sizeSuffix = ", " + sizeDesc;
                        }

                        connectorDescriptors.Add(
                            label + ": " + geoId + "." + faceName.ToLowerInvariant()
                            + " (" + flow + ", " + sysType + ", " + profileLabel
                            + sizeSuffix
                            + ") @ " + FormatFaceCentreMm(faceCentre));
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        warnings.Add(label + ": connector creation failed on "
                            + geoId + "." + faceName + ": " + ex.Message);
                    }
                }
            }

            // ── Convention check: for a through-fitting, one In + one Out is
            // the expected pattern. Not blocking — just surfaced as a warning.
            foreach (var kv in flowsPerGeometry)
            {
                int inCount, outCount, biCount;
                kv.Value.TryGetValue(FlowDirectionType.In,            out inCount);
                kv.Value.TryGetValue(FlowDirectionType.Out,           out outCount);
                kv.Value.TryGetValue(FlowDirectionType.Bidirectional, out biCount);

                int total = inCount + outCount + biCount;
                if (total >= 2 && !(inCount == 1 && outCount == 1 && biCount == 0))
                {
                    warnings.Add("Connector convention: geometry \""
                        + kv.Key + "\" has "
                        + inCount + " In / " + outCount + " Out / "
                        + biCount + " Bidirectional — through-fittings usually "
                        + "declare exactly 1 In + 1 Out.");
                }
            }

            return created;
        }

        // Returns a stable Reference to the planar face of an extrusion whose
        // outward normal aligns with the direction implied by faceName, along
        // with the geometric centre of that face (used purely for diagnostics).
        //
        // Face-normal mapping is independent of extrusion position: a box
        // bounded by [Left, Mid_LR, Front, Back, Base, Top] still has a face
        // whose normal is +Y (back) even though the box is off-centre in X.
        //   Top    →  (0,  0, +1)    Bottom → (0,  0, -1)
        //   Front  →  (0, -1,  0)    Back   → (0, +1,  0)
        //   Left   → (-1,  0,  0)    Right  → (+1,  0,  0)
        private static Reference FindNamedFaceReference(
            Extrusion extrusion,
            string faceName,
            string diagnosticLabel,
            out XYZ faceCentre,
            IList<string> warnings)
        {
            faceCentre = XYZ.Zero;

            XYZ expected = FaceNameToNormal(faceName);
            if (expected == null)
            {
                warnings.Add(diagnosticLabel + ": target_face \""
                    + faceName + "\" is not recognised; valid values are "
                    + "front, back, left, right, top, bottom.");
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
                warnings.Add(diagnosticLabel
                    + ": could not find matching face — no geometry "
                    + "returned from target extrusion.");
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
                    {
                        faceCentre = ComputePlanarFaceCentre(pf);
                        return pf.Reference;
                    }
                }
            }

            warnings.Add(diagnosticLabel + ": could not find matching face \""
                + faceName + "\" on target extrusion (normal "
                + expected.X.ToString("0.##") + ", "
                + expected.Y.ToString("0.##") + ", "
                + expected.Z.ToString("0.##") + ").");
            return null;
        }

        // Returns the geometric centre of a PlanarFace by averaging the
        // extremes of its UV bounding box and evaluating the face there.
        private static XYZ ComputePlanarFaceCentre(PlanarFace pf)
        {
            BoundingBoxUV bb = pf.GetBoundingBox();
            UV mid = new UV(
                (bb.Min.U + bb.Max.U) / 2.0,
                (bb.Min.V + bb.Max.V) / 2.0);
            return pf.Evaluate(mid);
        }

        // Formats a face-centre XYZ in millimetres for the diagnostic log.
        private static string FormatFaceCentreMm(XYZ ptFt)
        {
            double xMm = UnitUtils.ConvertFromInternalUnits(ptFt.X, UnitTypeId.Millimeters);
            double yMm = UnitUtils.ConvertFromInternalUnits(ptFt.Y, UnitTypeId.Millimeters);
            double zMm = UnitUtils.ConvertFromInternalUnits(ptFt.Z, UnitTypeId.Millimeters);
            return "("
                + xMm.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ", "
                + yMm.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ", "
                + zMm.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ") mm";
        }

        // Returns "⌀<paramName>=<value>mm" for the log, or null if the
        // parameter cannot be read at this time.
        private static string FormatBoundDiameter(
            FamilyManager fm, string paramName)
        {
            if (string.IsNullOrWhiteSpace(paramName) || fm.CurrentType == null)
                return null;

            FamilyParameter fp = null;
            foreach (FamilyParameter candidate in fm.GetParameters())
            {
                if (string.Equals(candidate.Definition.Name, paramName.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    fp = candidate;
                    break;
                }
            }
            if (fp == null) return null;

            double? val = fm.CurrentType.AsDouble(fp);
            if (!val.HasValue) return null;

            double mm = UnitUtils.ConvertFromInternalUnits(val.Value, UnitTypeId.Millimeters);
            return "⌀" + paramName + "="
                + mm.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                + "mm";
        }

        private static DuctSystemType ParseDuctSystemType(string raw)
        {
            switch ((raw ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "SUPPLYAIR":  return DuctSystemType.SupplyAir;
                case "RETURNAIR":  return DuctSystemType.ReturnAir;
                case "EXHAUSTAIR": return DuctSystemType.ExhaustAir;
                case "FITTING":    return DuctSystemType.Fitting;
                case "GLOBAL":
                default:           return DuctSystemType.Global;
            }
        }

        private static FlowDirectionType ParseFlowDirection(string raw)
        {
            switch ((raw ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "IN":            return FlowDirectionType.In;
                case "OUT":           return FlowDirectionType.Out;
                case "BIDIRECTIONAL":
                default:              return FlowDirectionType.Bidirectional;
            }
        }

        // Sets the connector's flow direction through the BuiltInParameter
        // because ConnectorElement.Direction is get-only in the public API.
        private static void TrySetFlowDirection(
            ConnectorElement ce,
            FlowDirectionType flow,
            string diagnosticLabel,
            IList<string> warnings)
        {
            try
            {
                Parameter p = ce.get_Parameter(
                    BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM);
                if (p == null || p.IsReadOnly)
                {
                    warnings.Add(diagnosticLabel
                        + ": flow_direction parameter not writable on connector "
                        + "(Direction stays at the Revit default).");
                    return;
                }
                p.Set((int)flow);
            }
            catch (Exception ex)
            {
                warnings.Add(diagnosticLabel
                    + ": could not apply flow_direction: " + ex.Message);
            }
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
            string diagnosticLabel,
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

            warnings.Add(diagnosticLabel
                + ": could not bind diameter parameter ["
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

        // Associates a single named BuiltInParameter on a rectangular connector
        // (CONNECTOR_WIDTH or CONNECTOR_HEIGHT) to the target family parameter.
        //
        // Unlike the Round path — where CONNECTOR_RADIUS is sometimes shadowed
        // by a renamed / version-specific BIP and requires keyword fallback —
        // CONNECTOR_WIDTH and CONNECTOR_HEIGHT are consistently exposed on a
        // rectangular connector AFTER a doc.Regenerate() (see AGENTS.md). No
        // fallback sweep is needed; if the BIP is null or read-only we treat
        // it as a non-blocking warning rather than an exception.
        private static bool TryAssociateConnectorBuiltIn(
            FamilyManager fm,
            Parameter elementParam,
            FamilyParameter familyParam,
            string slotLabel,
            string diagnosticLabel,
            IList<string> warnings)
        {
            if (familyParam == null)
                return false; // missing-parameter warning was already logged.

            if (elementParam == null)
            {
                warnings.Add(diagnosticLabel + ": " + slotLabel
                    + " built-in parameter not accessible on connector "
                    + "(missing doc.Regenerate after creation?).");
                return false;
            }

            if (elementParam.IsReadOnly)
            {
                warnings.Add(diagnosticLabel + ": " + slotLabel
                    + " built-in parameter is read-only; association skipped.");
                return false;
            }

            Exception lastEx;
            if (TryAssociateParameter(fm, elementParam, familyParam, out lastEx))
                return true;

            warnings.Add(diagnosticLabel + ": association failed for "
                + slotLabel + " → family parameter \""
                + familyParam.Definition.Name + "\": "
                + (lastEx != null ? lastEx.Message : "unknown error"));
            return false;
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

        // Creates NewAlignment constraints between the six planar faces of a
        // rectangular extrusion and the six reference planes passed in by the
        // caller. The face-normal → plane map is slot-based (±X, ±Y, ±Z), NOT
        // keyed on canonical names, so two geometries can lock their adjacent
        // faces to the same shared reference plane while keeping their opposite
        // faces locked to different planes.
        //
        // Each alignment is attempted independently so one failure does not
        // abort the others. Caller must supply an open transaction.
        private static void TryAlignExtrusionToReferencePlanes(
            Document familyDoc,
            Extrusion extrusion,
            View planView,
            View elevView,
            ReferencePlane leftRp, ReferencePlane rightRp,
            ReferencePlane frontRp, ReferencePlane backRp,
            ReferencePlane baseRp, ReferencePlane topRp,
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

                        XYZ            n         = pf.FaceNormal;
                        ReferencePlane rp        = null;
                        View           alignView = null;
                        string         slotLabel = null;

                        if      (n.X < -0.9) { rp = leftRp;  alignView = planView; slotLabel = "left";  }
                        else if (n.X >  0.9) { rp = rightRp; alignView = planView; slotLabel = "right"; }
                        else if (n.Y < -0.9) { rp = frontRp; alignView = planView; slotLabel = "front"; }
                        else if (n.Y >  0.9) { rp = backRp;  alignView = planView; slotLabel = "back";  }
                        else if (n.Z < -0.9) { rp = baseRp;  alignView = elevView; slotLabel = "base";  }
                        else if (n.Z >  0.9) { rp = topRp;   alignView = elevView; slotLabel = "top";   }

                        if (rp == null || alignView == null) continue;

                        try
                        {
                            familyDoc.FamilyCreate.NewAlignment(
                                alignView, rp.GetReference(), pf.Reference);
                        }
                        catch (Exception ex)
                        {
                            warnings.Add("Alignment (" + slotLabel + " -> \""
                                + rp.Name + "\"): " + ex.Message);
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

            // Delete any stale CSV from a previous run so the header annotations
            // (##OTHER##, ##LENGTH##MILLIMETERS) always match what size_lookup expects.
            if (File.Exists(csvPath))
                File.Delete(csvPath);

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
        // Formula pattern:
        //   Width = size_lookup("TableName", "Width##LENGTH##MILLIMETERS", 600 mm, LookupKey)
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
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out d)
                        && d > 0)
                        defaults[pd.Name] = d;
                }
            }

            // Logical column names used in size_lookup formulas.
            // The CSV header has the full annotation (e.g. "Width##LENGTH##MILLIMETERS")
            // but the formula references only the logical name before "##".
            string[] lookupColumns = { "Width", "Depth", "Height" };

            int applied = 0;

            foreach (string colName in lookupColumns)
            {
                FamilyParameter fp;
                if (!paramsByName.TryGetValue(colName, out fp)) continue;
                if (fp.IsInstance) continue; // size_lookup only works on type params
                if (fp.IsDeterminedByFormula) continue; // already driven by another formula

                double defaultMm = defaults.ContainsKey(colName) ? defaults[colName] : 600;
                string formula =
                    "size_lookup(\""
                    + tableName + "\", \""
                    + colName + "##LENGTH##MILLIMETERS\", "
                    + defaultMm.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
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
