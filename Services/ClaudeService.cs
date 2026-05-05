using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitFamilyBuilder.Config;

namespace RevitFamilyBuilder.Services
{
    /// <summary>
    /// Lightweight context object derived from the Call 1 review JSON and used
    /// to parameterise the Call 2 family schema. The minimal set today is the
    /// number of geometries and the build strategy; both come straight from
    /// review fields. Future PRs will extend this with requires_voids /
    /// requires_formulas / requires_connectors flags so the schema can also
    /// drop those sections when the user's prompt does not need them.
    /// </summary>
    public class FamilyContext
    {
        /// <summary>Number of distinct extrusions Claude announced in the review (review.geometry_count).</summary>
        public int GeometryCount { get; set; }

        /// <summary>Build strategy from the review (single_type / explicit_types / lookup_table).</summary>
        public string BuildStrategy { get; set; }
    }

    /// <summary>
    /// Two-call Claude workflow:
    ///   Call 1 (Generate JSON button) — review-only schema, lightweight.
    ///   Call 2 (Confirm and Generate) — family-JSON-only schema, no review wrapper.
    ///
    /// Grammar-size discipline: each schema is self-contained and well under
    /// Anthropic's compiled-grammar limit. No combined wrapper is ever built.
    /// The Call 2 schema is also CONTEXT-AWARE: it only includes the geometry
    /// override fields when the review announces 2+ extrusions, keeping the
    /// compiled grammar small for the common single-geometry case.
    ///
    /// Content-block construction uses JArray/JObject exclusively (not C# anonymous
    /// types) so that serialization is deterministic regardless of which Newtonsoft.Json
    /// assembly Revit loads at runtime.
    /// </summary>
    public static class ClaudeService
    {
        private static readonly HttpClient Http = new HttpClient();

        private const string ApiUrl     = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";

        // Base family JSON schema (Call 2).
        private const string FamilySchemaJson = @"
{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""required"": [""schema_version"",""family_name"",""family_template"",""category"",
                 ""parameters"",""reference_planes"",""dimensions"",
                 ""symbolic_lines"",""geometry"",""warnings""],
  ""properties"": {
    ""schema_version"":  { ""type"": ""string"" },
    ""family_name"":     { ""type"": ""string"" },
    ""family_template"": { ""type"": ""string"", ""enum"": [""GenericModelMetric""] },
    ""category"":        { ""type"": ""string"", ""enum"": [""Generic Models""]    },
    ""parameters"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""additionalProperties"": false,
        ""required"": [""name"",""type"",""group"",""is_instance""],
        ""properties"": {
          ""name"":          { ""type"": ""string"" },
          ""type"":          { ""type"": ""string"",
                              ""enum"": [""Length"",""Angle"",""Number"",""YesNo"",""Text"",""Material""] },
          ""group"":         { ""type"": ""string"" },
          ""is_instance"":   { ""type"": ""boolean"" },
          ""default_value"": { ""type"": ""string"" }
        }
      }
    },
    ""reference_planes"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""additionalProperties"": false,
        ""required"": [""name"",""orientation"",""offset""],
        ""properties"": {
          ""name"":        { ""type"": ""string"" },
          ""orientation"": { ""type"": ""string"", ""enum"": [""vertical"",""horizontal"",""elevation""] },
          ""offset"":      { ""type"": ""number"" }
        }
      }
    },
    ""dimensions"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""additionalProperties"": false,
        ""required"": [""reference_plane1"",""reference_plane2""],
        ""properties"": {
          ""reference_plane1"": { ""type"": ""string"" },
          ""reference_plane2"": { ""type"": ""string"" },
          ""parameter_name"":   { ""type"": ""string"" }
        }
      }
    },
    ""symbolic_lines"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""additionalProperties"": false,
        ""required"": [""start_x"",""start_y"",""end_x"",""end_y"",""view""],
        ""properties"": {
          ""start_x"": { ""type"": ""number"" },
          ""start_y"": { ""type"": ""number"" },
          ""end_x"":   { ""type"": ""number"" },
          ""end_y"":   { ""type"": ""number"" },
          ""view"":    { ""type"": ""string"", ""enum"": [""plan""] }
        }
      }
    },
    ""geometry"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""additionalProperties"": false,
        ""required"": [""id"",""type"",""profile""],
        ""properties"": {
          ""id"":               { ""type"": ""string"" },
          ""type"":             { ""type"": ""string"", ""enum"": [""Extrusion""]    },
          ""profile"":          { ""type"": ""string"", ""enum"": [""rectangular""] },
          ""width_parameter"":  { ""type"": ""string"" },
          ""depth_parameter"":  { ""type"": ""string"" },
          ""height_parameter"": { ""type"": ""string"" }
        }
      }
    },
    ""warnings"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" }
    }
  }
}";

        // ── Shared content-block builder ─────────────────────────────────────
        // Builds the user-message "content" value for the Anthropic API.
        //
        // Uses JArray/JObject instead of C# anonymous types so that
        // Newtonsoft.Json serialization is identical regardless of which DLL
        // version Revit loads at runtime.
        //
        // Returns:
        //   string  — when text-only (no attachment).
        //   JArray  — when an image or PDF is attached (document/image + text blocks).

        private static object BuildUserContent(
            string textMessage,
            string imageBase64    = null,
            string imageMediaType = null,
            string pdfBase64      = null)
        {
            bool hasPdf   = !string.IsNullOrEmpty(pdfBase64);
            bool hasImage = !hasPdf && !string.IsNullOrEmpty(imageBase64);

            if (!hasPdf && !hasImage)
                return textMessage;

            var blocks = new JArray();

            if (hasPdf)
            {
                blocks.Add(new JObject
                {
                    ["type"]   = "document",
                    ["source"] = new JObject
                    {
                        ["type"]       = "base64",
                        ["media_type"] = "application/pdf",
                        ["data"]       = pdfBase64
                    }
                });
            }
            else
            {
                blocks.Add(new JObject
                {
                    ["type"]   = "image",
                    ["source"] = new JObject
                    {
                        ["type"]       = "base64",
                        ["media_type"] = imageMediaType ?? "image/png",
                        ["data"]       = imageBase64
                    }
                });
            }

            blocks.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = textMessage
            });

            return blocks;
        }

        // Short label describing the content shape (for error diagnostics).
        private static string DescribeContent(object userContent)
        {
            if (userContent is string) return "text";
            if (userContent is JArray arr)
            {
                var labels = new List<string>();
                foreach (JToken t in arr)
                {
                    string bt = (t as JObject)?["type"]?.Value<string>();
                    if (bt != null) labels.Add(bt);
                }
                return string.Join("+", labels);
            }
            return "unknown";
        }

        // ── Call 1: Review only ──────────────────────────────────────────────

        public static Task<string> GetReviewAsync(string userPrompt)
            => CallApiAsync(
                BuildUserContent(PromptComposer.BuildReviewMessage(userPrompt)),
                BuildReviewSchema(),
                PromptComposer.ReviewSystemPrompt);

        public static Task<string> GetReviewWithImageAsync(
            string userPrompt, string imageBase64, string mediaType)
            => CallApiAsync(
                BuildUserContent(
                    PromptComposer.BuildReviewMessageWithImage(userPrompt),
                    imageBase64: imageBase64,
                    imageMediaType: mediaType),
                BuildReviewSchema(),
                PromptComposer.ReviewSystemPrompt);

        public static Task<string> GetReviewWithPdfAsync(
            string userPrompt, string pdfBase64)
            => CallApiAsync(
                BuildUserContent(
                    PromptComposer.BuildReviewMessageWithPdf(userPrompt),
                    pdfBase64: pdfBase64),
                BuildReviewSchema(),
                PromptComposer.ReviewSystemPrompt);

        // ── Call 2: Proposed JSON only ───────────────────────────────────────
        // Optional attachment parameters allow Call 2 to re-send the image/PDF
        // so Claude can reference the original source while generating family JSON.
        // When not provided (null), Call 2 is text-only and relies on the review
        // context passed in the user message.

        public static Task<string> GetProposedJsonAsync(
            string userPrompt,
            string reviewJson,
            IList<string> selectedTypes,
            FamilyContext context,
            string imageBase64    = null,
            string imageMediaType = null,
            string pdfBase64      = null)
            => CallApiAsync(
                BuildUserContent(
                    PromptComposer.BuildFamilyMessage(userPrompt, reviewJson, selectedTypes),
                    imageBase64, imageMediaType, pdfBase64),
                BuildFamilySchema(context),
                PromptComposer.FamilySystemPrompt);

        // The repair call MUST use the same context as the original failed
        // request so the schema shape is identical. The caller is expected to
        // hold on to the context across the Get → Repair retry.
        public static Task<string> RepairProposedJsonAsync(
            string invalidJson, IList<string> validationErrors, FamilyContext context)
            => CallApiAsync(
                BuildUserContent(PromptComposer.BuildRepairMessage(invalidJson, validationErrors)),
                BuildFamilySchema(context),
                PromptComposer.FamilySystemPrompt);

        // ── Shared HTTP plumbing ─────────────────────────────────────────────

        private static async Task<string> CallApiAsync(
            object userContent, JObject schema, string systemPrompt)
        {
            string apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "Environment variable ANTHROPIC_API_KEY is not set. "
                    + "Set it before launching Revit and try again.");

            // Build the request body as a JObject so every node is a JToken.
            // This avoids anonymous-type serialization variance across
            // different Newtonsoft.Json assemblies loaded by Revit.
            var message = new JObject
            {
                ["role"]    = "user",
                ["content"] = userContent is JArray arr
                    ? (JToken)arr
                    : (JToken)new JValue(userContent)
            };

            var requestObj = new JObject
            {
                ["model"]      = AppConfig.ClaudeModel,
                ["max_tokens"] = 4096,
                ["system"]     = systemPrompt,
                ["messages"]   = new JArray(message),
                ["output_config"] = new JObject
                {
                    ["format"] = new JObject
                    {
                        ["type"]   = "json_schema",
                        ["schema"] = schema
                    }
                }
            };

            string requestJson = JsonConvert.SerializeObject(requestObj);

            string contentDesc = DescribeContent(userContent);

            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl))
            {
                httpRequest.Headers.Add("x-api-key",         apiKey);
                httpRequest.Headers.Add("anthropic-version", ApiVersion);
                httpRequest.Content = new StringContent(
                    requestJson, Encoding.UTF8, "application/json");

                using (HttpResponseMessage httpResponse =
                    await Http.SendAsync(httpRequest).ConfigureAwait(false))
                {
                    string responseBody = await httpResponse.Content
                        .ReadAsStringAsync().ConfigureAwait(false);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        try
                        {
                            string debugPath = System.IO.Path.Combine(
                                System.IO.Path.GetTempPath(),
                                "RevitFamilyBuilder_last_error.json");
                            JObject debugReq = JObject.Parse(requestJson);
                            TruncateBase64(debugReq);
                            System.IO.File.WriteAllText(debugPath,
                                "=== REQUEST [" + contentDesc + "] ===\n"
                                + JsonConvert.SerializeObject(debugReq, Formatting.Indented)
                                + "\n\n=== RESPONSE ("
                                + (int)httpResponse.StatusCode + ") ===\n"
                                + responseBody);
                        }
                        catch { /* best-effort diagnostic */ }

                        throw new InvalidOperationException(
                            "Anthropic API error " + (int)httpResponse.StatusCode
                            + " [" + contentDesc + "]: " + responseBody);
                    }

                    JObject responseObj = JObject.Parse(responseBody);
                    string json = responseObj["content"]?[0]?["text"]?.Value<string>();

                    if (string.IsNullOrWhiteSpace(json))
                        throw new InvalidOperationException(
                            "Anthropic API returned an empty or unexpected response body: "
                            + responseBody);

                    return json;
                }
            }
        }

        // ── Schema builders ──────────────────────────────────────────────────

        private static JObject BuildReviewSchema()
        {
            return new JObject
            {
                ["type"]                 = "object",
                ["additionalProperties"] = false,
                ["required"]             = new JArray(
                    "match_status", "generation_scope", "build_strategy",
                    "selected_model", "detected_dimensions", "source",
                    "confidence", "warnings", "build_summary",
                    "detected_type_count", "detected_types",
                    "selected_family_logic", "detected_formulas", "formula_summary",
                    "geometry_count", "geometry_breakdown"),
                ["properties"]           = new JObject
                {
                    ["match_status"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray(
                            "match_found", "ambiguous_match",
                            "no_match", "insufficient_information")
                    },
                    ["generation_scope"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray(
                            "single_type",
                            "multi_type_single_family",
                            "multiple_families_recommended")
                    },
                    ["build_strategy"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("single_type", "explicit_types", "lookup_table")
                    },
                    ["selected_model"]      = new JObject { ["type"] = "string" },
                    ["detected_dimensions"] = new JObject { ["type"] = "string" },
                    ["source"]              = new JObject { ["type"] = "string" },
                    ["confidence"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("high", "medium", "low")
                    },
                    ["warnings"] = new JObject
                    {
                        ["type"]  = "array",
                        ["items"] = new JObject { ["type"] = "string" }
                    },
                    ["build_summary"]       = new JObject { ["type"] = "string" },
                    ["detected_type_count"] = new JObject { ["type"] = "integer" },
                    ["detected_types"] = new JObject
                    {
                        ["type"]  = "array",
                        ["items"] = new JObject { ["type"] = "string" }
                    },
                    ["selected_family_logic"] = new JObject { ["type"] = "string" },
                    ["detected_formulas"] = new JObject
                    {
                        ["type"]  = "array",
                        ["items"] = new JObject { ["type"] = "string" }
                    },
                    ["formula_summary"] = new JObject { ["type"] = "string" },
                    ["geometry_count"]  = new JObject { ["type"] = "integer" },
                    ["geometry_breakdown"] = new JObject
                    {
                        ["type"]  = "array",
                        ["items"] = new JObject { ["type"] = "string" }
                    }
                }
            };
        }

        // Builds the Call 2 family-JSON schema, parameterised by the review
        // context so the compiled grammar stays small.
        //
        // ALWAYS included (essentials): schema_version, family_name,
        // family_template, category, parameters, reference_planes, dimensions,
        // symbolic_lines, geometry, warnings, build_strategy.
        //
        // CONDITIONALLY included on geometry items (when GeometryCount >= 2):
        // a SINGLE optional sub-object "plane_overrides" that, when present,
        // requires all six plane names. This shape was chosen specifically to
        // keep Anthropic's compiled grammar small enough to compile within
        // the API's time budget. The earlier design (8 independent optional
        // string fields) produced 2^8 = 256 combinations per geometry item,
        // which timed the grammar compiler out (400 grammar_compilation
        // timed_out / 503 grammar_compilation overloaded_error) on
        // multi-geometry prompts. The monolithic sub-object collapses that
        // to 2 states (present / absent) — a 128× reduction.
        //
        // The "subcategory" and "convention" geometry fields are NOT exposed
        // to the AI in this PR; JsonSchemaService.Parse defaults Convention
        // to "Body" so the engine still applies a consistent subcategory.
        // Per-geometry conventions will return in a future PR if the use
        // case justifies the schema cost.
        //
        // INTENTIONALLY EXCLUDED: formulas, types, voids. The follow-up
        // "gros cohérent" PR will gate them on review-level flags
        // (requires_formulas / requires_voids / requires_connectors).
        // The server-side sample (SampleJsonService) is unaffected because
        // it bypasses Anthropic entirely.
        private static JObject BuildFamilySchema(FamilyContext context)
        {
            JObject schema   = JObject.Parse(FamilySchemaJson);
            var     props    = (JObject)schema["properties"];
            var     required = (JArray)schema["required"];

            // build_strategy is always required so the engine knows which
            // type-management path to take.
            props["build_strategy"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("single_type", "explicit_types", "lookup_table")
            };
            required.Add("build_strategy");

            // Multi-geometry override fields — added only when needed, and
            // grouped into a single all-or-nothing sub-object to keep the
            // compiled grammar tractable (see method header).
            if (context != null && context.GeometryCount >= 2)
            {
                var geometryItemProps =
                    (JObject)props["geometry"]["items"]["properties"];

                geometryItemProps["plane_overrides"] = new JObject
                {
                    ["type"]                 = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JArray(
                        "left_plane", "right_plane",
                        "front_plane", "back_plane",
                        "base_plane",  "top_plane"),
                    ["properties"] = new JObject
                    {
                        ["left_plane"]  = new JObject { ["type"] = "string" },
                        ["right_plane"] = new JObject { ["type"] = "string" },
                        ["front_plane"] = new JObject { ["type"] = "string" },
                        ["back_plane"]  = new JObject { ["type"] = "string" },
                        ["base_plane"]  = new JObject { ["type"] = "string" },
                        ["top_plane"]   = new JObject { ["type"] = "string" }
                    }
                };
            }

            return schema;
        }

        // Strips long base64 strings from a debug JObject to keep logs small.
        private static void TruncateBase64(JToken token)
        {
            if (token == null) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (JProperty p in ((JObject)token).Properties())
                {
                    if (p.Value.Type == JTokenType.String && p.Name == "data")
                    {
                        string v = p.Value.Value<string>() ?? string.Empty;
                        if (v.Length > 200)
                            p.Value = v.Substring(0, 100) + "...[" + v.Length + " chars]";
                    }
                    else
                    {
                        TruncateBase64(p.Value);
                    }
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (JToken item in (JArray)token)
                    TruncateBase64(item);
            }
        }
    }
}
