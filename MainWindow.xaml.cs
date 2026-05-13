using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitFamilyBuilder.Schema;
using RevitFamilyBuilder.Services;

namespace RevitFamilyBuilder
{
    public partial class MainWindow : Window
    {
        private readonly UIApplication _uiApp;

        // Pending state between Call 1 (Generate JSON) and Call 2 (Confirm and Generate).
        private JObject _pendingReview;       // review JSON from Call 1
        private string  _pendingPrompt;       // original user prompt for Call 2

        // Attachment data from Call 1, re-sent in Call 2 so Claude sees the original source.
        private string _pendingImageBase64;
        private string _pendingImageMediaType;
        private string _pendingPdfBase64;

        // Holds the match_status from the last Claude review (drives Confirm button state).
        private string _pendingMatchStatus;

        // Tracks the CheckBox controls added to TypeSelectionPanel for multi-type selection.
        private readonly List<CheckBox> _typeCheckBoxes = new List<CheckBox>();

        public MainWindow()
        {
            _uiApp = null;
            InitializeComponent();
        }

        public MainWindow(UIApplication uiApp)
        {
            _uiApp = uiApp;
            InitializeComponent();
        }

        private void BrowseImageButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title  = "Select an image or PDF";
            dialog.Filter = "Image and PDF Files|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.pdf|All Files|*.*";

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                ImagePathTextBox.Text = dialog.FileName;
                StatusTextBlock.Text  = "File selected.";
            }
        }

        // ── Call 1: Generate JSON button ─────────────────────────────────────
        // Calls Claude for the review/analysis only.
        // No family JSON is produced here. Confirm and Generate handles Call 2.

        private async void GenerateJsonButton_Click(object sender, RoutedEventArgs e)
        {
            string prompt = PromptTextBox.Text ?? string.Empty;

            // Empty prompt — use sample fallback.
            if (string.IsNullOrWhiteSpace(prompt))
            {
                JsonPreviewTextBox.Text = new SampleJsonService().GetSampleJson();
                StatusTextBlock.Text = "Sample JSON generated (no prompt entered).";
                return;
            }

            // Resolve optional file (image or PDF).
            string filePath = ImagePathTextBox.Text ?? string.Empty;
            bool   hasFile  = !string.IsNullOrWhiteSpace(filePath);
            bool   isPdf    = hasFile && string.Equals(
                                  Path.GetExtension(filePath), ".pdf",
                                  StringComparison.OrdinalIgnoreCase);
            bool   hasImage = hasFile && !isPdf;

            if (hasFile)
            {
                if (!File.Exists(filePath))
                {
                    TaskDialog.Show("Generate JSON", "File not found:\n" + filePath);
                    StatusTextBlock.Text = "Review failed: file not found.";
                    return;
                }

                if (isPdf)
                {
                    const long MaxPdfBytes = 25L * 1024L * 1024L;
                    long fileSize = new FileInfo(filePath).Length;
                    if (fileSize > MaxPdfBytes)
                    {
                        TaskDialog.Show("Generate JSON",
                            "PDF file is too large ("
                            + (fileSize / (1024 * 1024)) + " MB). "
                            + "Maximum supported size is 25 MB.\n"
                            + "File: " + filePath);
                        StatusTextBlock.Text = "Review failed: PDF too large.";
                        return;
                    }
                }
                else
                {
                    string mediaType = GetImageMediaType(filePath);
                    if (mediaType == null)
                    {
                        TaskDialog.Show("Generate JSON",
                            "Unsupported file format. Use .png, .jpg, .jpeg, .webp, .gif, or .pdf.\n"
                            + "File: " + filePath);
                        StatusTextBlock.Text = "Review failed: unsupported file format.";
                        return;
                    }
                }
            }

            StatusTextBlock.Text = isPdf    ? "Analysing request (PDF + text)…"
                                 : hasImage ? "Analysing request (image + text)…"
                                 :            "Analysing request…";

            // Read attachment once; store for re-use in Call 2.
            string imgB64  = null;
            string imgType = null;
            string pdfB64  = null;

            if (isPdf)
                pdfB64 = Convert.ToBase64String(File.ReadAllBytes(filePath));
            else if (hasImage)
            {
                imgB64  = Convert.ToBase64String(File.ReadAllBytes(filePath));
                imgType = GetImageMediaType(filePath);
            }

            string reviewJson;
            try
            {
                if (isPdf)
                    reviewJson = await ClaudeService.GetReviewWithPdfAsync(prompt, pdfB64);
                else if (hasImage)
                    reviewJson = await ClaudeService.GetReviewWithImageAsync(prompt, imgB64, imgType);
                else
                    reviewJson = await ClaudeService.GetReviewAsync(prompt);
            }
            catch (Exception ex)
            {
                string fullError = ex.Message;
                if (fullError.Length > 600)
                    fullError = fullError.Substring(0, 600) + "\n…(truncated)";
                TaskDialog.Show("Generate JSON",
                    "Claude review error:\n\n" + fullError
                    + "\n\nModel: " + Config.AppConfig.ClaudeModel);
                StatusTextBlock.Text = "Review failed.";
                return;
            }

            JObject reviewObj;
            try
            {
                reviewObj = JObject.Parse(reviewJson);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Generate JSON",
                    "Claude returned an unexpected review format.\n\nRaw response:\n"
                    + reviewJson + "\n\nError: " + ex.Message);
                StatusTextBlock.Text = "Review failed: unexpected response format.";
                return;
            }

            // Store context needed for Call 2 (Confirm and Generate).
            _pendingReview         = reviewObj;
            _pendingPrompt         = prompt;
            _pendingImageBase64    = imgB64;
            _pendingImageMediaType = imgType;
            _pendingPdfBase64      = pdfB64;

            PresentForReview(reviewObj);
        }

        // Maps a file extension to the Anthropic media_type string.
        // Returns null for unsupported formats.
        private static string GetImageMediaType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png":            return "image/png";
                case ".jpg":
                case ".jpeg":           return "image/jpeg";
                case ".webp":           return "image/webp";
                case ".gif":            return "image/gif";
                default:                return null;
            }
        }

        // Tries to parse and validate JSON. Returns an empty list on success,
        // or a list containing the validation errors (or a parse error) on failure.
        private static List<string> TryParseAndValidate(JsonSchemaService service, string json)
        {
            try
            {
                var definition = service.Parse(json);
                if (definition == null)
                    return new List<string> { "Parse error: JSON could not be deserialized into a FamilyDefinition (null)." };
                var result = service.Validate(definition);
                return result.IsValid ? new List<string>() : result.Errors;
            }
            catch (Exception ex)
            {
                return new List<string> { "Parse error: " + ex.Message };
            }
        }

        // ── AI Review card ──────────────────────────────────────────────────────

        private void ShowAiReviewCard()
        {
            AiReviewCard.Visibility = System.Windows.Visibility.Visible;
        }

        private void HideAiReviewCard()
        {
            AiReviewCard.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void FillAiReviewCard(
            string matchStatus,
            string model,
            string dimensions,
            string source,
            string confidence,
            string warnings,
            string buildSummary,
            string generationScope,
            string geometryCount,
            string geometryBreakdown,
            string capabilities,
            string typeCount,
            string familyLogic)
        {
            // Match status badge — human-readable label + colour.
            AiReviewMatchStatusText.Text       = FormatMatchStatus(matchStatus);
            AiReviewMatchStatusText.Foreground = MatchStatusBrush(matchStatus);
            AiReviewCard.BorderBrush           = MatchStatusBrush(matchStatus);

            AiReviewModelText.Text        = string.IsNullOrWhiteSpace(model)        ? "—" : model;
            AiReviewDimensionsText.Text   = string.IsNullOrWhiteSpace(dimensions)   ? "—" : dimensions;
            AiReviewSourceText.Text       = string.IsNullOrWhiteSpace(source)       ? "—" : source;
            AiReviewConfidenceText.Text   = string.IsNullOrWhiteSpace(confidence)   ? "—" : confidence;
            AiReviewWarningsText.Text     = string.IsNullOrWhiteSpace(warnings)     ? "—" : warnings;
            AiReviewBuildSummaryText.Text = string.IsNullOrWhiteSpace(buildSummary) ? "—" : buildSummary;

            AiReviewGenerationScopeText.Text = string.IsNullOrWhiteSpace(generationScope)
                ? "—" : FormatGenerationScope(generationScope);
            AiReviewGeometryCountText.Text     = string.IsNullOrWhiteSpace(geometryCount)     ? "—" : geometryCount;
            AiReviewGeometryBreakdownText.Text = string.IsNullOrWhiteSpace(geometryBreakdown) ? "—" : geometryBreakdown;
            AiReviewCapabilitiesText.Text      = string.IsNullOrWhiteSpace(capabilities)      ? "—" : capabilities;
            AiReviewTypeCountText.Text = string.IsNullOrWhiteSpace(typeCount) ? "—" : typeCount;
            // TypeSelectionPanel is populated separately by PopulateTypeSelection().
            AiReviewFamilyLogicText.Text = string.IsNullOrWhiteSpace(familyLogic) ? "—" : familyLogic;
        }

        // Converts a snake_case match_status token to a human-readable label.
        private static string FormatMatchStatus(string raw)
        {
            switch (raw)
            {
                case "match_found":             return "Match found";
                case "ambiguous_match":         return "Ambiguous match";
                case "no_match":                return "No match";
                case "insufficient_information": return "Insufficient information";
                default: return string.IsNullOrWhiteSpace(raw) ? "—" : raw;
            }
        }

        // Converts a snake_case build_strategy token to a short display label.
        private static string FormatBuildStrategy(string raw)
        {
            switch (raw)
            {
                case "single_type":    return "Strategy: Single type";
                case "explicit_types": return "Strategy: Explicit types";
                case "lookup_table":   return "Strategy: Lookup table";
                default: return string.IsNullOrWhiteSpace(raw) ? string.Empty : "Strategy: " + raw;
            }
        }

        // Converts a snake_case generation_scope token to a human-readable label.
        private static string FormatGenerationScope(string raw)
        {
            switch (raw)
            {
                case "single_type":                   return "Single type";
                case "multi_type_single_family":      return "Multi-type · single family";
                case "multiple_families_recommended": return "Multiple families recommended";
                default: return string.IsNullOrWhiteSpace(raw) ? "—" : raw;
            }
        }

        // Returns a colour brush that reflects the match quality at a glance.
        private static SolidColorBrush MatchStatusBrush(string raw)
        {
            switch (raw)
            {
                case "match_found":             return new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)); // green
                case "ambiguous_match":         return new SolidColorBrush(Color.FromRgb(0xC8, 0x7A, 0x14)); // amber
                case "no_match":                return new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)); // red
                case "insufficient_information": return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)); // grey
                default:                        return new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)); // blue (default)
            }
        }

        // ── Call 2: Confirm and Generate button ──────────────────────────────
        // Calls Claude for the family JSON using the review from Call 1 as context.
        // Validates, repairs once if needed, then fills the JSON preview.

        private async void ConfirmAndGenerateButton_Click(object sender, RoutedEventArgs e)
        {
            // Defensive guard — button is already disabled for these statuses.
            if (_pendingMatchStatus == "no_match" || _pendingMatchStatus == "insufficient_information")
            {
                TaskDialog.Show("AI Review",
                    "Cannot confirm: Claude reported \""
                    + FormatMatchStatus(_pendingMatchStatus) + "\".\n"
                    + "Please regenerate with a clearer prompt or image.");
                return;
            }

            if (_pendingReview == null || string.IsNullOrWhiteSpace(_pendingPrompt))
            {
                TaskDialog.Show("AI Review", "No review data available. Please generate first.");
                return;
            }

            // Capture selected type names before disabling UI.
            List<string> selectedTypes = CollectSelectedTypeNames();

            // Disable confirm button while generating to prevent double-clicks.
            ConfirmAndGenerateButton.IsEnabled  = false;
            ConfirmAndGenerateButton.Background =
                new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            StatusTextBlock.Text = "Generating family JSON…";

            string reviewJson = JsonConvert.SerializeObject(_pendingReview, Formatting.Indented);

            // Build the family context from the review JObject so Call 2's
            // schema stays minimal for prompts that don't need every section
            // (avoids the Anthropic 503 grammar_compilation overload).
            // Defensive defaults keep this safe if any field is missing from
            // the review — the worst case is a smaller schema than Claude
            // would have liked, which is recoverable via Regenerate.
            var familyContext = new FamilyContext
            {
                GeometryCount       = _pendingReview?["geometry_count"]?.Value<int>()         ?? 1,
                BuildStrategy       = _pendingReview?["build_strategy"]?.Value<string>()      ?? "single_type",
                RequiresTypes       = _pendingReview?["requires_types"]?.Value<bool>()        ?? false,
                RequiresFormulas    = _pendingReview?["requires_formulas"]?.Value<bool>()     ?? false,
                RequiresVoids       = _pendingReview?["requires_voids"]?.Value<bool>()        ?? false,
                RequiresConnectors  = _pendingReview?["requires_connectors"]?.Value<bool>()   ?? false,
                RequiresLookupTable = _pendingReview?["requires_lookup_table"]?.Value<bool>() ?? false
            };

            // Defensive normalisation: a lookup table is always backed by
            // types[] in the engine (TryEmbedLookupTable reads from the
            // generated types), so an inconsistent review where
            // requires_lookup_table = true but requires_types = false would
            // produce a schema without types[] and a build_strategy that
            // immediately fails. Force the implication client-side rather
            // than trusting Claude's review to be self-consistent.
            if (familyContext.RequiresLookupTable)
                familyContext.RequiresTypes = true;

            // First attempt.
            string firstJson;
            try
            {
                firstJson = await ClaudeService.GetProposedJsonAsync(
                    _pendingPrompt, reviewJson, selectedTypes, familyContext,
                    _pendingImageBase64, _pendingImageMediaType, _pendingPdfBase64);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Confirm and Generate", "Claude API error:\n" + ex.Message);
                StatusTextBlock.Text = "Family JSON generation failed.";
                ResetConfirmButton();
                return;
            }

            firstJson = ExpandVoidShorthand(firstJson);

            var schemaService = new JsonSchemaService();
            List<string> firstErrors = TryParseAndValidate(schemaService, firstJson);

            if (firstErrors.Count == 0)
            {
                CommitGeneratedJson(firstJson, selectedTypes, wasRepaired: false);
                return;
            }

            // One repair attempt.
            StatusTextBlock.Text = "Validation failed ("
                + firstErrors.Count + " error(s)). Attempting repair…";

            string repairedJson;
            try
            {
                // Repair MUST use the same context so Anthropic compiles the
                // identical schema and Claude is asked to fix the same JSON shape.
                repairedJson = await ClaudeService.RepairProposedJsonAsync(
                    firstJson, firstErrors, familyContext);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Confirm and Generate", "Repair API error:\n" + ex.Message);
                StatusTextBlock.Text = "Family JSON generation failed (repair error).";
                ResetConfirmButton();
                return;
            }

            repairedJson = ExpandVoidShorthand(repairedJson);
            List<string> repairedErrors = TryParseAndValidate(schemaService, repairedJson);

            if (repairedErrors.Count == 0)
            {
                CommitGeneratedJson(repairedJson, selectedTypes, wasRepaired: true);
                return;
            }

            // Repair still failed — report and stay on review card.
            var sb = new StringBuilder();
            sb.AppendLine("Repair attempt did not fix all issues.");
            sb.AppendLine("Remaining errors (" + repairedErrors.Count + "):");
            foreach (string error in repairedErrors)
                sb.AppendLine("  - " + error);
            TaskDialog.Show("Confirm and Generate", sb.ToString());
            StatusTextBlock.Text = "Family JSON generation failed after repair ("
                + repairedErrors.Count + " error(s)).";
            ResetConfirmButton();
        }

        // Collects the names from checked type checkboxes (Tag holds the parsed name).
        // Returns an empty list when no checkboxes are present (single-type or 1 type).
        private List<string> CollectSelectedTypeNames()
        {
            var names = new List<string>();
            foreach (CheckBox cb in _typeCheckBoxes)
            {
                if (cb.IsChecked == true)
                    names.Add((cb.Tag as string) ?? string.Empty);
            }
            return names;
        }

        // Applies type filtering, writes to preview, and resets review state.
        private void CommitGeneratedJson(
            string proposedJson, List<string> selectedTypes, bool wasRepaired)
        {
            try
            {
                JsonPreviewTextBox.Text = ApplyTypeSelection(proposedJson);
                _pendingReview         = null;
                _pendingPrompt         = null;
                _pendingMatchStatus    = null;
                _pendingImageBase64    = null;
                _pendingImageMediaType = null;
                _pendingPdfBase64      = null;
                ClearTypeSelection();
                ResetConfirmButton();
                HideAiReviewCard();
                StatusTextBlock.Text = wasRepaired
                    ? "JSON confirmed (repaired) — ready to validate and build."
                    : "JSON confirmed — ready to validate and build.";
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Confirm Error",
                    "An error occurred writing the JSON:\n"
                    + ex.GetType().Name + "\n" + ex.Message);
                StatusTextBlock.Text = "Confirm failed.";
                ResetConfirmButton();
            }
        }

        private void RegenerateButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingReview         = null;
            _pendingPrompt         = null;
            _pendingMatchStatus    = null;
            _pendingImageBase64    = null;
            _pendingImageMediaType = null;
            _pendingPdfBase64      = null;
            ClearTypeSelection();
            ResetConfirmButton();
            HideAiReviewCard();
            // Re-run Call 1 with the current prompt and image.
            GenerateJsonButton_Click(sender, e);
        }

        private void CancelAiReviewButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingReview         = null;
            _pendingPrompt         = null;
            _pendingMatchStatus    = null;
            _pendingImageBase64    = null;
            _pendingImageMediaType = null;
            _pendingPdfBase64      = null;
            ClearTypeSelection();
            ResetConfirmButton();
            HideAiReviewCard();
            StatusTextBlock.Text = "AI Review cancelled.";
        }

        // Restores the Confirm button to its default enabled/green state for the next session.
        private void ResetConfirmButton()
        {
            ConfirmAndGenerateButton.IsEnabled  = true;
            ConfirmAndGenerateButton.Background =
                new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
        }

        // Populates the AI Review card from Call 1's review JObject.
        // No proposed JSON at this stage — Confirm will trigger Call 2.
        private void PresentForReview(JObject reviewObj)
        {
            _pendingMatchStatus = FillCardFromReview(reviewObj);

            // Populate type checkboxes from review.detected_types (string array).
            JArray detectedTypes = reviewObj?["detected_types"] as JArray;
            PopulateTypeSelection(detectedTypes);

            // Enable Confirm only for match_found and ambiguous_match.
            bool confirmAllowed = _pendingMatchStatus == "match_found"
                               || _pendingMatchStatus == "ambiguous_match";
            ConfirmAndGenerateButton.IsEnabled  = confirmAllowed;
            ConfirmAndGenerateButton.Background =
                confirmAllowed
                    ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10))
                    : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

            ShowAiReviewCard();
            StatusTextBlock.Text = "Request analysed — review and confirm to generate JSON.";
        }

        // Fills the AI Review card from Call 1's review JObject.
        // Returns the raw match_status string so the caller can configure button state.
        // build_strategy is now a required field in the review schema (Call 1 returns it directly).
        private string FillCardFromReview(JObject review)
        {
            string matchStatus     = review?["match_status"]?.Value<string>()        ?? string.Empty;
            string generationScope = review?["generation_scope"]?.Value<string>()    ?? string.Empty;
            string model           = review?["selected_model"]?.Value<string>()      ?? string.Empty;
            string dimensions      = review?["detected_dimensions"]?.Value<string>() ?? string.Empty;
            string source          = review?["source"]?.Value<string>()              ?? string.Empty;
            string confidence      = review?["confidence"]?.Value<string>()          ?? string.Empty;
            string summary         = review?["build_summary"]?.Value<string>()       ?? string.Empty;
            string familyLogic     = review?["selected_family_logic"]?.Value<string>() ?? string.Empty;
            string formulaSummary  = review?["formula_summary"]?.Value<string>()     ?? string.Empty;
            string buildStrategy   = review?["build_strategy"]?.Value<string>()      ?? string.Empty;
            var    detectedFormulas = review?["detected_formulas"] as JArray;

            // Prepend build strategy badge into build summary (no XAML change needed).
            if (!string.IsNullOrWhiteSpace(buildStrategy))
            {
                string strategyLabel = FormatBuildStrategy(buildStrategy);
                summary = string.IsNullOrWhiteSpace(summary)
                    ? strategyLabel
                    : strategyLabel + "\n" + summary;
            }

            // Append formula review lines into build summary (no XAML change).
            if (!string.IsNullOrWhiteSpace(formulaSummary)
                || (detectedFormulas != null && detectedFormulas.Count > 0))
            {
                var sbSum = new StringBuilder(summary);
                if (!string.IsNullOrWhiteSpace(formulaSummary))
                {
                    if (sbSum.Length > 0) sbSum.AppendLine();
                    sbSum.Append(formulaSummary);
                }
                if (detectedFormulas != null && detectedFormulas.Count > 0)
                {
                    if (sbSum.Length > 0) sbSum.AppendLine();
                    sbSum.Append("Formulas:");
                    foreach (JToken f in detectedFormulas)
                        sbSum.AppendLine().Append("  • ").Append(f.Value<string>() ?? "?");
                }
                summary = sbSum.ToString();
            }

            // Void info is included in build_summary by prompt instruction — no separate fields needed.

            // Title-case confidence for display (API returns lowercase).
            if (!string.IsNullOrEmpty(confidence))
                confidence = char.ToUpper(confidence[0]) + confidence.Substring(1);

            var warningsArr = review?["warnings"] as JArray;
            string warnings = warningsArr != null && warningsArr.Count > 0
                ? string.Join("\n", warningsArr.Values<string>())
                : "None";

            // detected_type_count: display as "N type(s)" or "—".
            int typeCountInt = review?["detected_type_count"]?.Value<int>() ?? 0;
            string typeCount = typeCountInt > 0 ? typeCountInt + " type(s)" : "—";

            // geometry_count / geometry_breakdown — populated by Call 1 when the
            // schema includes them. Fallback gracefully when absent so older
            // review responses (or hand-crafted JSON in the preview) still display.
            int geometryCountInt = review?["geometry_count"]?.Value<int>() ?? 1;
            string geometryCount = geometryCountInt == 1
                ? "1 extrusion"
                : geometryCountInt + " extrusions";

            var    geometryBreakdownArr = review?["geometry_breakdown"] as JArray;
            string geometryBreakdown = geometryBreakdownArr != null
                                       && geometryBreakdownArr.Count > 0
                ? string.Join("\n", geometryBreakdownArr.Values<string>())
                : string.Empty;

            // Capability flags — collect the labels for whichever requires_*
            // flag the review set to true. Display as a short comma-separated
            // string; "None" when no extra section will be generated. Older
            // reviews without these fields render "None" without crashing.
            var capLabels = new List<string>();
            if (review?["requires_types"]?.Value<bool>()        ?? false) capLabels.Add("Types");
            if (review?["requires_formulas"]?.Value<bool>()     ?? false) capLabels.Add("Formulas");
            if (review?["requires_voids"]?.Value<bool>()        ?? false) capLabels.Add("Voids");
            if (review?["requires_connectors"]?.Value<bool>()   ?? false) capLabels.Add("Connectors");
            if (review?["requires_lookup_table"]?.Value<bool>() ?? false) capLabels.Add("Lookup table");
            string capabilities = capLabels.Count > 0
                ? string.Join(", ", capLabels)
                : "None";

            FillAiReviewCard(
                matchStatus, model, dimensions, source, confidence, warnings, summary,
                generationScope, geometryCount, geometryBreakdown, capabilities,
                typeCount, familyLogic);
            return matchStatus;
        }

        // Populates TypeSelectionPanel from review.detected_types (string array from Call 1).
        //   0 types → "—" label
        //   1 type  → type name label (no interaction needed)
        //   2+ types → one CheckBox per type, all checked by default
        // The CheckBox Tag holds the short name (before ":") used for type filtering in Call 2.
        private void PopulateTypeSelection(JArray detectedTypes)
        {
            TypeSelectionPanel.Children.Clear();
            _typeCheckBoxes.Clear();

            if (detectedTypes == null || detectedTypes.Count == 0)
            {
                TypeSelectionPanel.Children.Add(
                    new TextBlock { Text = "—", Foreground = new SolidColorBrush(Colors.White) });
                return;
            }

            if (detectedTypes.Count == 1)
            {
                string entry = detectedTypes[0]?.Value<string>() ?? "1 type";
                TypeSelectionPanel.Children.Add(
                    new TextBlock { Text = entry, Foreground = new SolidColorBrush(Colors.White) });
                return;
            }

            // Multiple types — interactive checkboxes.
            foreach (JToken token in detectedTypes)
            {
                string entry    = token.Value<string>() ?? "?";
                string typeName = ExtractTypeName(entry); // short name for Tag / filtering
                var cb = new CheckBox
                {
                    Content    = entry,     // full string for display
                    IsChecked  = true,
                    Tag        = typeName,  // short name for ApplyTypeSelection
                    Foreground = new SolidColorBrush(Colors.White),
                    Margin     = new Thickness(0, 0, 0, 4),
                    Cursor     = System.Windows.Input.Cursors.Hand
                };
                _typeCheckBoxes.Add(cb);
                TypeSelectionPanel.Children.Add(cb);
            }
        }

        // Extracts the short type name from a detected_types string.
        // "Type A: W=800mm D=500mm H=400mm" → "Type A"
        // "SingleVariant" → "SingleVariant"
        private static string ExtractTypeName(string detectedTypeString)
        {
            if (string.IsNullOrWhiteSpace(detectedTypeString)) return "?";
            int colonIdx = detectedTypeString.IndexOf(':');
            return colonIdx > 0
                ? detectedTypeString.Substring(0, colonIdx).Trim()
                : detectedTypeString.Trim();
        }

        // Returns a filtered copy of proposedJson where proposed_json.types contains only
        // the types whose CheckBox is checked. If there are no checkboxes (0/1 type), passes
        // proposedJson through unchanged.
        private string ApplyTypeSelection(string proposedJson)
        {
            if (_typeCheckBoxes.Count == 0)
                return proposedJson;

            try
            {
                JObject doc   = JObject.Parse(proposedJson);
                JArray  types = doc["types"] as JArray;
                if (types == null || types.Count == 0)
                    return proposedJson;

                var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CheckBox cb in _typeCheckBoxes)
                {
                    if (cb.IsChecked == true)
                        selected.Add((cb.Tag as string) ?? string.Empty);
                }

                // DeepClone each kept token. Adding a JToken to a new JArray detaches
                // it from its current parent, which mutates the source array mid-iteration
                // and throws InvalidOperationException.
                var filtered = new JArray();
                foreach (JToken t in types)
                {
                    string name = t["name"]?.Value<string>() ?? string.Empty;
                    if (selected.Contains(name))
                        filtered.Add(t.DeepClone());
                }
                doc["types"] = filtered;

                // Do not use JToken.ToString(Formatting) — Revit often binds an older
                // Newtonsoft.Json at runtime where that overload is missing (MissingMethodException).
                return JsonConvert.SerializeObject(doc, Formatting.Indented);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[TypeSelection] ApplyTypeSelection failed: " + ex.Message);
                return proposedJson;
            }
        }

        // Clears the type selection panel back to its blank state.
        private void ClearTypeSelection()
        {
            TypeSelectionPanel.Children.Clear();
            _typeCheckBoxes.Clear();
        }

        // TryExtractWrapper was removed: the two-call workflow no longer uses a combined wrapper.
        // Call 1 returns a review JSON directly; Call 2 returns the family JSON directly.

        // Converts pipe-delimited void strings ("name|width_param|height_param") produced
        // by the Anthropic schema (string array — grammar-size discipline) into the full
        // VoidDefinition object array that JsonSchemaService expects.
        // v1 defaults: shape=Rectangular, face=Front, location=Center, cut=Through.
        private static string ExpandVoidShorthand(string proposedJson)
        {
            try
            {
                JObject doc = JObject.Parse(proposedJson);
                JArray voids = doc["voids"] as JArray;
                if (voids == null || voids.Count == 0)
                    return proposedJson;

                if (voids[0].Type != JTokenType.String)
                    return proposedJson;

                var expanded = new JArray();
                foreach (JToken item in voids)
                {
                    string s = item.Value<string>();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    string[] parts = s.Split('|');
                    if (parts.Length < 3) continue;

                    expanded.Add(new JObject
                    {
                        ["name"]             = parts[0].Trim(),
                        ["shape"]            = "Rectangular",
                        ["face"]             = "Front",
                        ["location"]         = "Center",
                        ["width_parameter"]  = parts[1].Trim(),
                        ["height_parameter"] = parts[2].Trim(),
                        ["cut"]              = "Through"
                    });
                }

                doc["voids"] = expanded;
                return JsonConvert.SerializeObject(doc, Formatting.Indented);
            }
            catch
            {
                return proposedJson;
            }
        }

        // ── Validate / Build ────────────────────────────────────────────────────

        private void ValidateJsonButton_Click(object sender, RoutedEventArgs e)
        {
            string json = JsonPreviewTextBox.Text;

            if (string.IsNullOrWhiteSpace(json))
            {
                TaskDialog.Show("Validate JSON", "No JSON to validate. Generate a sample first.");
                StatusTextBlock.Text = "Validation failed: empty input.";
                return;
            }

            try
            {
                var service = new JsonSchemaService();
                var definition = service.Parse(json);
                var result = service.Validate(definition);

                if (result.IsValid)
                {
                    TaskDialog.Show("Validate JSON", "Validation passed. Family: " + definition.FamilyName);
                    StatusTextBlock.Text = "Validation passed.";
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Validation failed:");
                    foreach (string error in result.Errors)
                        sb.AppendLine("  - " + error);
                    TaskDialog.Show("Validate JSON", sb.ToString());
                    StatusTextBlock.Text = "Validation failed (" + result.Errors.Count + " error(s)).";
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Validate JSON", "Parse error: " + ex.Message);
                StatusTextBlock.Text = "Validation failed: invalid JSON.";
            }
        }

        private void BuildFamilyButton_Click(object sender, RoutedEventArgs e)
        {
            string json = JsonPreviewTextBox.Text;

            if (string.IsNullOrWhiteSpace(json))
            {
                TaskDialog.Show("Build Family", "No JSON to build. Generate and validate a sample first.");
                StatusTextBlock.Text = "Build failed: empty input.";
                return;
            }

            try
            {
                var schemaService = new JsonSchemaService();
                var definition = schemaService.Parse(json);
                var validation = schemaService.Validate(definition);

                if (!validation.IsValid)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Validation failed. Fix errors before building:");
                    foreach (string error in validation.Errors)
                        sb.AppendLine("  - " + error);
                    TaskDialog.Show("Build Family", sb.ToString());
                    StatusTextBlock.Text = "Build failed: validation errors (" + validation.Errors.Count + ").";
                    return;
                }

                var coordinator = new FamilyGenerationCoordinator();
                string message = coordinator.Run(definition, _uiApp);

                TaskDialog.Show("Build Family", message);
                StatusTextBlock.Text = "Family document created: " + definition.FamilyName + ".";
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Build Family", "Error: " + ex.Message);
                StatusTextBlock.Text = "Build failed: unexpected error.";
            }
        }
    }
}