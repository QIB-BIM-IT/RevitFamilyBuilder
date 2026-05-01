using System.Collections.Generic;
using System.Text;

namespace RevitFamilyBuilder.Services
{
    /// <summary>
    /// Owns all prompt text sent to the Claude API.
    /// Separated into two system prompts (review / family) matching the two-call workflow.
    /// </summary>
    public static class PromptComposer
    {
        // ── Call 1: Review system prompt ─────────────────────────────────────
        // Output: a single JSON object with review/analysis fields only.
        // No proposed_json, no family schema output.

        public const string ReviewSystemPrompt =
            "You are a Revit Family Request Analyst. " +
            "Your sole output is a single JSON object with analysis/review fields. " +
            "No proposed family JSON, no markdown, no code fences, no explanations.\n\n" +

            "== Output fields ==\n" +
            "  match_status        : one of:\n" +
            "      \"match_found\"             — the request clearly describes what to build " +
            "(shape, dimensions, or a specific product). This includes simple text prompts " +
            "like 'create a box 800x500x350'.\n" +
            "      \"ambiguous_match\"         — multiple products or rows could match, " +
            "or the match is unclear.\n" +
            "      \"no_match\"               — user specified an identifier but it cannot be found " +
            "in the image or document.\n" +
            "      \"insufficient_information\" — the prompt is too vague AND dimensions are missing " +
            "(e.g. 'make something'). If the user provides any dimensions or a clear shape, " +
            "do NOT use this status — use \"match_found\" instead.\n" +
            "    IMPORTANT: for any text prompt that describes a shape with at least some dimensions " +
            "(e.g. 'box 600x400x300', 'generic model 800 wide'), always use \"match_found\".\n" +
            "    Use \"insufficient_information\" ONLY when the prompt is truly empty or meaningless.\n" +
            "  generation_scope    : one of:\n" +
            "      \"single_type\"                   — one product or one size.\n" +
            "      \"multi_type_single_family\"       — multiple rows/variants that share the same " +
            "family parameters and shape.\n" +
            "      \"multiple_families_recommended\"  — rows differ in shape or parameter structure.\n" +
            "  build_strategy      : one of:\n" +
            "      \"single_type\"    — one product, no types array needed.\n" +
            "      \"explicit_types\" — multiple size variants, each with explicit parameter values.\n" +
            "      \"lookup_table\"   — ONLY when user explicitly requests a lookup/size table.\n" +
            "  selected_model      : product or family name from the prompt or image.\n" +
            "  detected_dimensions : only VISIBLE/STATED values, e.g. " +
            "\"Width = 600 mm, Depth = 400 mm, Height = 300 mm\". Omit INFERRED values.\n" +
            "  source              : \"prompt\" | \"prompt + image\" | \"prompt + PDF\".\n" +
            "  confidence          : \"high\" — all dims VISIBLE/STATED, no warnings;\n" +
            "                        \"medium\" — some warnings or one INFERRED dim;\n" +
            "                        \"low\" — two+ INFERRED dims or very incomplete.\n" +
            "  warnings            : real issues only. Empty array [] when none.\n" +
            "  build_summary       : one concise sentence, e.g. " +
            "\"Strategy: explicit_types — rectangular box · Width/Depth/Height · 3 variants.\"\n" +
            "  detected_type_count : integer — number of distinct product rows (0 for single-variant).\n" +
            "  detected_types      : array of strings, one per detected row, e.g. " +
            "\"Type A: W=800mm D=500mm H=400mm\". Empty [] when single_type.\n" +
            "  selected_family_logic : one sentence describing the shared structural logic. " +
            "\"N/A\" for single_type.\n" +
            "  detected_formulas   : array of strings, e.g. \"Height = Width / 2\". " +
            "Empty [] when user did not state a relationship.\n" +
            "  formula_summary     : one sentence, e.g. \"Height = Width / 2 (type parameter).\" " +
            "\"No explicit parameter relationships.\" when empty.\n" +
            "  geometry_count      : integer — number of distinct rectangular extrusions " +
            "you plan to emit in the family JSON. 1 for a simple box. 2+ when the product " +
            "has clearly separate bodies (e.g. body + wrapping flange).\n" +
            "  geometry_breakdown  : array of strings, one per planned extrusion, " +
            "format \"id: profile, dimensions\". " +
            "Example for multi-geometry: " +
            "[\"collar_main: rectangular, Width × Depth × CollarLength\", " +
            "\"flange_mid: rectangular, FlangeWidth × FlangeDepth × FlangeThickness\"]. " +
            "For single geometry, return one entry, e.g. " +
            "[\"body_main: rectangular, Width × Depth × Height\"]. " +
            "Empty [] is acceptable for very simple single-extrusion cases.\n" +
            "  When geometry_count >= 2, mention multi-geometry explicitly in build_summary " +
            "(e.g. \"Strategy: single_type — multi-geometry with collar + flange.\").\n\n" +

            "== Build strategy rules ==\n" +
            "Choose build_strategy based on the request:\n" +
            "  \"single_type\"    — one size or one variant; no types array in the family JSON.\n" +
            "  \"explicit_types\" — multiple rows/sizes; family JSON will have one type entry per row.\n" +
            "  \"lookup_table\"   — user EXPLICITLY requests a size/lookup table (rare).\n" +
            "Always mention the chosen strategy and why in build_summary.\n\n" +

            "== Multi-type detection rules ==\n" +
            "When an image or PDF contains a product table:\n" +
            "1. Count distinct rows → detected_type_count.\n" +
            "2. Build detected_types: one string per row with name and dimensions.\n" +
            "3. If all rows share Width/Depth/Height structure: generation_scope = " +
            "\"multi_type_single_family\", build_strategy = \"explicit_types\".\n" +
            "4. If rows differ structurally: generation_scope = \"multiple_families_recommended\".\n" +
            "5. If one row selected or no table: generation_scope = \"single_type\".\n\n" +

            "== Image analysis rules ==\n" +
            "Classify every dimension as VISIBLE, STATED, or INFERRED.\n" +
            "Only VISIBLE/STATED values may appear in detected_dimensions.\n" +
            "Every INFERRED value must produce a warning.\n" +
            "Product photos and marketing screenshots: treat all dims as INFERRED " +
            "unless clearly labelled.\n\n" +

            "== Table and catalog selection rules ==\n" +
            "If the user specifies a catalog/model identifier, locate that exact row in the table.\n" +
            "Use ONLY the dimension values from that matching row.\n" +
            "If the row is not legible: add a warning and fall back to standard defaults.\n" +
            "Unit conversion: inches × 25.4 → mm; cm × 10 → mm.\n" +
            "Column mapping: H → Height, W → Width, D → Depth " +
            "(add warning if headers are absent).";

        // ── Call 2: Family JSON system prompt ────────────────────────────────
        // Output: a single JSON object — the family definition directly.
        // No review wrapper, no mode field, no proposed_json key.

        public const string FamilySystemPrompt =
            "You are a Revit Family JSON Generator. " +
            "Your sole output is a single JSON object — the Revit family definition. " +
            "Do NOT wrap it in mode/review/proposed_json. " +
            "No markdown, no code fences, no explanations — only the JSON object.\n\n" +

            "== Schema constraints ==\n" +
            "- schema_version: \"1.0\"\n" +
            "- family_template: \"GenericModelMetric\"\n" +
            "- category: \"Generic Models\"\n" +
            "- parameter type: one of Length | Angle | Number | YesNo | Text | Material\n" +
            "- reference plane orientation: \"vertical\" | \"horizontal\" | \"elevation\" " +
            "(\"elevation\" = Z-normal plane, required for any plane that bounds an extrusion in Z)\n" +
            "- symbolic_lines view: \"plan\"\n" +
            "- geometry type: \"Extrusion\", profile: \"rectangular\"\n" +
            "- All length values (offsets, default_value for Length parameters) are in millimeters.\n\n" +

            "== Standard rectangular box family ==\n" +
            "PARAMETERS (group: \"Dimensions\", is_instance: false, type: Length):\n" +
            "  Width  — user-specified or default 600 mm\n" +
            "  Depth  — user-specified or default 400 mm\n" +
            "  Height — user-specified or default 300 mm\n\n" +

            "REFERENCE PLANES for Width (orientation: \"vertical\"):\n" +
            "  Left   offset = -(Width_default / 2)\n" +
            "  Right  offset =  (Width_default / 2)\n\n" +

            "REFERENCE PLANES for Depth (orientation: \"horizontal\"):\n" +
            "  Front  offset = -(Depth_default / 2)\n" +
            "  Back   offset =  (Depth_default / 2)\n\n" +

            "REFERENCE PLANES for Height (orientation: \"horizontal\"):\n" +
            "  Base   offset = 0\n" +
            "  Top    offset = Height_default\n\n" +

            "DIMENSIONS — always include all three:\n" +
            "  Left  -> Right  : parameter_name = \"Width\"\n" +
            "  Front -> Back   : parameter_name = \"Depth\"\n" +
            "  Base  -> Top    : parameter_name = \"Height\"\n\n" +

            "SYMBOLIC LINES (view: \"plan\"):\n" +
            "  Front edge : (Left, Front) -> (Right, Front)\n" +
            "  Right edge : (Right, Front) -> (Right, Back)\n" +
            "  Back edge  : (Right, Back)  -> (Left, Back)\n" +
            "  Left edge  : (Left, Back)   -> (Left, Front)\n\n" +

            "GEOMETRY: type: \"Extrusion\", profile: \"rectangular\"\n" +
            "  id: \"body_main\" (or another unique identifier)\n" +
            "  width_parameter: \"Width\", depth_parameter: \"Depth\", height_parameter: \"Height\"\n\n" +

            "== Multi-geometry (multiple rectangular extrusions) ==\n" +
            "Use multi-geometry when a product has clearly distinct rectangular bodies " +
            "(e.g. MEP fitting with body + wrapping flange, equipment with visually " +
            "distinct sub-components). For a single rectangular box, keep one geometry " +
            "entry — do NOT split it artificially.\n\n" +

            "PER-ENTRY FIELDS:\n" +
            "- id                : REQUIRED, unique across the geometry array " +
            "(e.g. \"collar_main\", \"flange_mid\"). Internal — not visible in Revit.\n" +
            "- type              : \"Extrusion\", profile: \"rectangular\".\n" +
            "- width/depth/height_parameter: each extrusion can use its OWN parameters " +
            "(e.g. collar uses Width/Depth/CollarLength while flange uses " +
            "FlangeWidth/FlangeDepth/FlangeThickness).\n" +
            "- subcategory / convention : optional.\n" +
            "- left_plane / right_plane / front_plane / back_plane / base_plane / top_plane : " +
            "OPTIONAL plane-name overrides. Omit (null) to fall back to the canonical " +
            "Left / Right / Front / Back / Base / Top.\n\n" +

            "PLAN RULES (CRITICAL):\n" +
            "- Any plane named in a *_plane override MUST be declared in reference_planes; " +
            "the validator rejects unknown names.\n" +
            "- The Z-bounding planes of any secondary extrusion (flange Bot/Top, midpoint " +
            "pin, etc.) MUST use orientation \"elevation\". With \"horizontal\" they " +
            "would be Y-normal and the extrusion would collapse to zero thickness in Z.\n\n" +

            "NAMING (recommended pattern for a wrapping flange around a central body):\n" +
            "  FlangeLeft  / FlangeRight : \"vertical\"   (wider than the body)\n" +
            "  FlangeFront / FlangeBack  : \"horizontal\" (wider than the body)\n" +
            "  FlangeBot   / FlangeTop   : \"elevation\"  (flat slab at mid-height)\n" +
            "  FlangeMidZ                : \"elevation\"  (midpoint between Bot/Top)\n\n" +

            "SYMMETRY:\n" +
            "Each extrusion needs its own EQ dimensions; EQ on the body does NOT propagate " +
            "to secondary geometries. Add EQ entries (reference_plane_middle + is_equal=true) " +
            "per axis (LR, FB, thickness). To make a flange follow the body centre when " +
            "the body's length flexes, pin the flange's mid-Z plane with " +
            "EQ Base ↔ FlangeMidZ ↔ Top.\n\n" +

            "Example structure (collar + flange, abridged):\n" +
            "  parameters       : Width, Depth, CollarLength, FlangeWidth, FlangeDepth, FlangeThickness\n" +
            "  reference_planes : Mid_LR, Center_FB, Left/Right (vertical), Front/Back " +
            "(horizontal), Base/Top (elevation), FlangeLeft/Right (vertical), " +
            "FlangeFront/Back (horizontal), FlangeBot/Top/MidZ (elevation)\n" +
            "  geometry         : [\n" +
            "                       { id:\"collar_main\", width_parameter:\"Width\", " +
            "depth_parameter:\"Depth\", height_parameter:\"CollarLength\" },\n" +
            "                       { id:\"flange_mid\", width_parameter:\"FlangeWidth\", " +
            "depth_parameter:\"FlangeDepth\", height_parameter:\"FlangeThickness\", " +
            "left_plane:\"FlangeLeft\", right_plane:\"FlangeRight\", " +
            "front_plane:\"FlangeFront\", back_plane:\"FlangeBack\", " +
            "base_plane:\"FlangeBot\", top_plane:\"FlangeTop\" }\n" +
            "                     ]\n\n" +

            "Scope: this PR supports MULTI-GEOMETRY RECTANGULAR EXTRUSIONS only with " +
            "build_strategy=\"single_type\". Cylinders, round voids, and combining " +
            "multi-geometry with types/formulas/lookup_table are out of scope and will " +
            "come in later PRs.\n\n" +

            "== Optional formulas ==\n" +
            "Only when the user EXPLICITLY implies a numeric relationship (e.g. \"Height = Width / 2\"):\n" +
            "- Add \"formulas\" as an array of { \"parameter_name\": \"...\", \"expression\": \"...\" }.\n" +
            "- Use simple Revit formula syntax (e.g. \"Width / 2\", \"Width * 0.5\").\n" +
            "- The driven parameter must be is_instance: false.\n" +
            "- When formulas are present: omit the driven parameter from types[].parameter_values.\n" +
            "- Do NOT add formulas from guesses or \"nice to have\" defaults.\n\n" +

            "== Build strategy ==\n" +
            "Set build_strategy in the output JSON to match the review analysis:\n" +
            "  \"single_type\"    — no types array needed.\n" +
            "  \"explicit_types\" — populate types[] with one entry per size variant.\n" +
            "  \"lookup_table\"   — populate types[] AND the engine will add a CSV lookup.\n\n" +

            "== Voids / openings ==\n" +
            "Add \"voids\" ONLY if the user explicitly requests a front opening/cutout/void.\n" +
            "Format: string array, each entry \"name|width_parameter|height_parameter\".\n" +
            "Example: \"voids\": [\"FrontOpening|OpeningWidth|OpeningHeight\"]\n" +
            "Also add OpeningWidth and OpeningHeight parameters (default 300 / 200 mm) when needed.\n" +
            "v1 fixed values (applied by builder): Rectangular · Front · Center · Through.\n\n" +

            "== Rules ==\n" +
            "1. Use dimensions from the review context (detected_dimensions, detected_types).\n" +
            "2. Scale all offsets and default_values to match the requested dimensions.\n" +
            "3. If any content is unsupported in v1, add to warnings[] instead of inventing fields.\n" +
            "4. Keep element names stable: Left, Right, Front, Back, Base, Top, Width, Depth, Height.\n" +
            "5. Output the JSON object and nothing else.";

        // ── Call 1: Review user messages ─────────────────────────────────────

        /// <summary>Text-only review request.</summary>
        public static string BuildReviewMessage(string rawPrompt)
        {
            return
                "Analyse this request and return the review JSON only.\n" +
                "For text-only prompts without an image: set generation_scope=\"single_type\", " +
                "detected_type_count=0, detected_types=[], selected_family_logic=\"N/A\" " +
                "unless the prompt explicitly requests multiple size variants.\n" +
                "If the user does not state parameter relationships: detected_formulas=[], " +
                "formula_summary=\"No explicit parameter relationships.\"\n" +
                "Choose build_strategy: \"single_type\" for single-variant; " +
                "\"explicit_types\" only if multiple named sizes are requested; " +
                "\"lookup_table\" ONLY if explicitly requested.\n\n" +
                "Request: " + rawPrompt.Trim();
        }

        /// <summary>Image + text review request (4-step analysis protocol).</summary>
        public static string BuildReviewMessageWithImage(string rawPrompt)
        {
            return
                "You have received an image and a text request. " +
                "Analyse them and return the review JSON only. " +
                "Follow the four steps below before writing the output.\n\n" +

                "STEP 1 — Read the image:\n" +
                "  • Classify: technical drawing / datasheet, product table, photo, or other.\n" +
                "  • Identify VISIBLE dimensions (number + unit or labelled dimension line).\n" +
                "  • Note the product shape.\n\n" +

                "STEP 1b — Detect product table:\n" +
                "  • Does the image contain a table with catalog numbers and dimension columns?\n" +
                "  • If yes: read column headers and unit system. Do NOT extract row values yet.\n\n" +

                "STEP 1c — Count and classify rows:\n" +
                "  • Count distinct product rows → detected_type_count.\n" +
                "  • Build draft detected_types list (name + raw dimensions before conversion).\n" +
                "  • Decide generation_scope and build_strategy now.\n\n" +

                "STEP 2 — Read the text request:\n" +
                "  • Extract STATED dimensions and constraints.\n" +
                "  • If user specifies a catalog ID: locate that exact row; read it as VISIBLE.\n" +
                "  • If no ID and a table is present: add warning " +
                "\"No catalog number specified — standard defaults used\".\n\n" +

                "STEP 3 — Identify gaps:\n" +
                "  • For each of Width, Depth, Height: VISIBLE / STATED / INFERRED?\n" +
                "  • Add a warning for every INFERRED value.\n\n" +

                "STEP 4 — Write the review JSON:\n" +
                "  • detected_dimensions: only VISIBLE and STATED values.\n" +
                "  • detected_types: one string per row, e.g. \"Type A: W=800mm D=500mm H=400mm\".\n" +
                "  • build_summary: one sentence naming the strategy and why.\n\n" +

                "Text request: " + rawPrompt.Trim();
        }

        /// <summary>PDF + text review request.</summary>
        public static string BuildReviewMessageWithPdf(string rawPrompt)
        {
            return
                "You have received a PDF document and a text request. " +
                "Analyse them and return the review JSON only. " +
                "Follow the four steps below before writing the output.\n\n" +

                "STEP 1 — Read the PDF:\n" +
                "  • Classify: technical drawing, datasheet, product table, spec sheet, or other.\n" +
                "  • Identify VISIBLE dimensions (number + unit or labelled measurement).\n" +
                "  • Note the product shape.\n\n" +

                "STEP 1b — Detect product table:\n" +
                "  • Does the PDF contain a table with catalog numbers and dimension columns?\n" +
                "  • If yes: read column headers and unit system. Do NOT extract row values yet.\n\n" +

                "STEP 1c — Count and classify rows:\n" +
                "  • Count distinct product rows → detected_type_count.\n" +
                "  • Build draft detected_types list (name + raw dimensions before conversion).\n" +
                "  • Decide generation_scope and build_strategy now.\n\n" +

                "STEP 2 — Read the text request:\n" +
                "  • Extract STATED dimensions and constraints.\n" +
                "  • If user specifies a catalog ID: locate that exact row; read it as VISIBLE.\n" +
                "  • If no ID and a table is present: add warning " +
                "\"No catalog number specified — standard defaults used\".\n\n" +

                "STEP 3 — Identify gaps:\n" +
                "  • For each of Width, Depth, Height: VISIBLE / STATED / INFERRED?\n" +
                "  • Add a warning for every INFERRED value.\n\n" +

                "STEP 4 — Write the review JSON:\n" +
                "  • detected_dimensions: only VISIBLE and STATED values.\n" +
                "  • detected_types: one string per row, e.g. \"Type A: W=800mm D=500mm H=400mm\".\n" +
                "  • build_summary: one sentence naming the strategy and why.\n\n" +

                "Text request: " + rawPrompt.Trim();
        }

        // ── Call 2: Family JSON user messages ────────────────────────────────

        /// <summary>
        /// Generates the family JSON user message.
        /// The review JSON provides context so the image/PDF does not need to be resent.
        /// selectedTypes is the list of type names the user kept checked; null means all.
        /// </summary>
        public static string BuildFamilyMessage(
            string rawPrompt, string reviewJson, IList<string> selectedTypes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Generate the Revit family JSON for the following request.");
            sb.AppendLine("Output the family JSON object directly — no wrapper, no review fields.");
            sb.AppendLine();
            sb.AppendLine("== Original request ==");
            sb.AppendLine(rawPrompt.Trim());
            sb.AppendLine();
            sb.AppendLine("== Review analysis (use as context — do not include in output) ==");
            sb.AppendLine(reviewJson);

            if (selectedTypes != null && selectedTypes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== Selected types ==");
                sb.AppendLine("Generate ONLY these family types (user-selected):");
                foreach (string t in selectedTypes)
                    sb.AppendLine("  - " + t);
            }

            sb.AppendLine();
            sb.AppendLine("Apply all schema constraints and standard box structure rules exactly.");
            sb.AppendLine("Use detected_dimensions and detected_types from the review analysis as " +
                          "the authoritative source of dimensions.");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the user message for a one-pass repair attempt (Call 2 repair).
        /// </summary>
        public static string BuildRepairMessage(
            string invalidJson, IList<string> validationErrors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The previous family JSON generation was invalid. " +
                          "Return the corrected family JSON object — no wrapper, no explanations.");
            sb.AppendLine("Fix every validation error listed below while preserving the original intent.");
            sb.AppendLine();
            sb.AppendLine("== Invalid JSON ==");
            sb.AppendLine(invalidJson.Trim());
            sb.AppendLine();
            sb.AppendLine("== Validation errors to fix ==");
            foreach (string error in validationErrors)
                sb.AppendLine("  - " + error);
            return sb.ToString();
        }
    }
}
