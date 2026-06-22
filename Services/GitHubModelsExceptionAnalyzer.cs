using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebApplication1.Models;

namespace WebApplication1.Services
{
    /// <summary>
    /// Calls an OpenAI-compatible chat-completions endpoint (defaults to the free GitHub Models
    /// service) to produce a root-cause analysis for an exception. Uses a plain <see cref="HttpClient"/>
    /// so there are no extra SDK dependencies. Falls back gracefully when not configured.
    /// </summary>
    public class GitHubModelsExceptionAnalyzer : IExceptionAnalyzer
    {
        private readonly HttpClient _http;
        private readonly AiOptions _options;
        private readonly ILogger<GitHubModelsExceptionAnalyzer> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public GitHubModelsExceptionAnalyzer(
            HttpClient http,
            IOptions<AiOptions> options,
            ILogger<GitHubModelsExceptionAnalyzer> logger)
        {
            _http = http;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<ExceptionAnalysis> AnalyzeAsync(ExceptionAnalysisRequest request, CancellationToken cancellationToken = default)
        {
            var apiKey = ResolveApiKey();

            if (!_options.Enabled || string.IsNullOrWhiteSpace(apiKey))
            {
                return new ExceptionAnalysis
                {
                    Success = false,
                    FromAi = false,
                    ErrorMessage = "AI analysis is not configured. Set the GITHUB_TOKEN environment variable " +
                                   "(or Ai:ApiKey) to a GitHub token to enable real AI exception analysis.",
                };
            }

            try
            {
                var payload = new
                {
                    model = _options.Model,
                    temperature = 0.2,
                    messages = new object[]
                    {
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = BuildUserPrompt(request) },
                    },
                    response_format = new { type = "json_object" },
                };

                using var response = await SendWithRetryAsync(payload, apiKey, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("AI endpoint returned {Status}: {Body}", (int)response.StatusCode, Truncate(body, 400));

                    var apiMessage = ExtractApiErrorMessage(body);
                    var hint = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized =>
                            "Your token was rejected — check GITHUB_TOKEN is set and valid.",
                        System.Net.HttpStatusCode.Forbidden =>
                            $"Access denied for model '{_options.Model}'. Your GitHub token needs the 'models:read' " +
                            "permission, and the model id must be one your account can use. " +
                            "Fix: create a fine-grained token with Models access, or change \"Ai:Model\" in appsettings " +
                            "to a model you have access to (see github.com/marketplace/models).",
                        System.Net.HttpStatusCode.NotFound =>
                            $"Model '{_options.Model}' was not found. Update \"Ai:Model\" to a valid id from the GitHub Models catalog.",
                        (System.Net.HttpStatusCode)429 =>
                            "Rate limit reached on the free tier — wait a minute and retry.",
                        _ => "Try again shortly.",
                    };

                    return new ExceptionAnalysis
                    {
                        Success = false,
                        FromAi = false,
                        ErrorMessage = $"AI request failed ({(int)response.StatusCode}). {hint}" +
                                       (string.IsNullOrWhiteSpace(apiMessage) ? "" : $" Details: {apiMessage}"),
                    };
                }

                var content = ExtractMessageContent(body);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return new ExceptionAnalysis { Success = false, FromAi = false, ErrorMessage = "The model returned an empty response." };
                }

                var analysis = ParseAnalysis(content);
                analysis.Success = true;
                analysis.FromAi = true;
                return analysis;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI exception analysis failed.");
                return new ExceptionAnalysis
                {
                    Success = false,
                    FromAi = false,
                    ErrorMessage = "Could not reach the AI service. " + ex.Message,
                };
            }
        }

        private string? ResolveApiKey() =>
            !string.IsNullOrWhiteSpace(_options.ApiKey)
                ? _options.ApiKey
                : Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        // The free GitHub Models tier throttles (429) and occasionally returns transient 5xx.
        // Retry a few times with capped backoff (honoring Retry-After) without pulling in a
        // resilience package, to stay consistent with this class's "no extra SDK" approach.
        private async Task<HttpResponseMessage> SendWithRetryAsync(object payload, string apiKey, CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            var json = JsonSerializer.Serialize(payload);

            for (var attempt = 1; ; attempt++)
            {
                // A fresh request each attempt: HttpRequestMessage can't be re-sent.
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

                var response = await _http.SendAsync(httpRequest, cancellationToken);

                if (attempt >= maxAttempts || !IsTransient(response.StatusCode))
                {
                    return response;
                }

                var delay = RetryDelay(response, attempt);
                _logger.LogWarning("AI endpoint returned {Status}; retry {Attempt}/{Max} in {Delay}s.",
                    (int)response.StatusCode, attempt, maxAttempts, delay.TotalSeconds);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
        }

        private static bool IsTransient(System.Net.HttpStatusCode status) =>
            (int)status == 429 ||
            status == System.Net.HttpStatusCode.BadGateway ||
            status == System.Net.HttpStatusCode.ServiceUnavailable ||
            status == System.Net.HttpStatusCode.GatewayTimeout;

        private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
        {
            // Honor an explicit Retry-After (seconds) when present, else exponential backoff capped at 8s.
            if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return delta > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delta;
            }

            var seconds = Math.Min(8, Math.Pow(2, attempt - 1)); // 1s, 2s, 4s...
            return TimeSpan.FromSeconds(seconds);
        }

        private static readonly string SystemPrompt =
            "You are a senior .NET site-reliability engineer embedded in a telemetry analysis tool. " +
            "The evidence block you are given IS the relevant Application Insights data (traces, " +
            "exceptions, failing URLs, dependencies, latency, status codes) — already extracted for " +
            "you. The user is looking at this tool, NOT the Azure portal. " +
            "CRITICAL: Never tell the user to 'review Application Insights', 'check the traces', " +
            "'open the Azure portal', 'gather more telemetry', or otherwise go look the data up — " +
            "they already have it and that is what you are analyzing. Instead, reason directly from " +
            "the evidence and give CODE-LEVEL and CONFIGURATION-LEVEL fixes they can act on in their " +
            "own codebase (specific methods, null/validation guards, retry/timeout policies, query " +
            "or index changes, caching, payload trimming, etc.). " +
            "Base your answer ONLY on the evidence provided — quote the specific URLs, exception " +
            "types/messages, or dependencies you were given rather than speaking generically. If the " +
            "evidence is genuinely insufficient to be certain, state the most probable cause given " +
            "what IS shown and lower your confidence — do NOT defer to 'collect more data'. Be " +
            "specific and practical. Respond ONLY with a JSON object using exactly these keys: " +
            "\"rootCause\" (string, one short paragraph that pinpoints the actual reason from the evidence), " +
            "\"likelyCauses\" (array of short strings), " +
            "\"howToFix\" (array of concrete ordered steps the developer can do IN THEIR CODE/CONFIG — " +
            "never 'look at telemetry' steps), " +
            "\"codeAreas\" (array of short strings naming layers/components to inspect), " +
            "\"confidence\" (integer 0-100). Do not include markdown or any text outside the JSON.";

        private static string BuildUserPrompt(ExceptionAnalysisRequest r)
        {
            var sb = new StringBuilder();

            if (r.IsStatusCodeAnalysis)
            {
                sb.AppendLine($"Pinpoint why this API endpoint is returning HTTP {r.StatusCode} and how to fix it.");
                sb.AppendLine($"API operation: {r.OperationName}");
                sb.AppendLine($"HTTP status code: {r.StatusCode}");
                sb.AppendLine("Use the correlated Application Insights evidence below (failing URLs, " +
                              "exceptions thrown on those requests, and failing downstream dependencies) " +
                              "to identify the specific cause for THIS status code:");
                sb.AppendLine(string.IsNullOrWhiteSpace(r.Context)
                    ? "(No correlated exceptions or dependency failures were recorded for this code — " +
                      "this usually points to the request being rejected/handled before app code ran, " +
                      "e.g. gateway/proxy timeouts, auth, routing, or upstream limits.)"
                    : Truncate(r.Context, 5000));
                return sb.ToString();
            }

            if (r.IsEndpointAnalysis)
            {
                sb.AppendLine($"Analyze the overall health of this API endpoint and explain why it is " +
                              $"degraded and how to fix it.");
                sb.AppendLine($"API operation: {r.OperationName}");
                if (!string.IsNullOrWhiteSpace(r.Context))
                {
                    sb.AppendLine(Truncate(r.Context, 5000));
                }
                return sb.ToString();
            }

            sb.AppendLine($"API operation: {r.OperationName}");
            sb.AppendLine($"Exception type: {r.ExceptionType}");
            if (!string.IsNullOrWhiteSpace(r.Method)) sb.AppendLine($"Throwing method: {r.Method}");
            if (!string.IsNullOrWhiteSpace(r.Message)) sb.AppendLine($"Message: {r.Message}");
            sb.AppendLine($"Occurrences in window: {r.Count}");
            if (!string.IsNullOrWhiteSpace(r.StackTrace))
            {
                sb.AppendLine("Stack trace:");
                sb.AppendLine(Truncate(r.StackTrace, 4000));
            }
            return sb.ToString();
        }

        /// <summary>Pulls choices[0].message.content out of the OpenAI-compatible response.</summary>
        private static string? ExtractMessageContent(string body)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var msg = choices[0].GetProperty("message");
                if (msg.TryGetProperty("content", out var content))
                {
                    return content.GetString();
                }
            }
            return null;
        }

        /// <summary>Pulls error.message out of an error response body, if present.</summary>
        private static string? ExtractApiErrorMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message))
                {
                    return message.GetString();
                }
            }
            catch (JsonException)
            {
                // Not JSON - ignore.
            }

            return null;
        }

        /// <summary>Parses the model's JSON answer into our DTO, tolerating extra prose around it.</summary>
        private static ExceptionAnalysis ParseAnalysis(string content)
        {
            var json = ExtractJsonObject(content);
            try
            {
                var parsed = JsonSerializer.Deserialize<ExceptionAnalysis>(json, JsonOptions);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Fall through to a best-effort wrapper below.
            }

            return new ExceptionAnalysis { RootCause = content.Trim() };
        }

        /// <summary>Extracts the first {...} block in case the model wrapped it in prose/markdown.</summary>
        private static string ExtractJsonObject(string content)
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            return (start >= 0 && end > start) ? content[start..(end + 1)] : content;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
    }
}
