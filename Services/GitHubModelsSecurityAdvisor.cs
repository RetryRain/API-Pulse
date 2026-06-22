using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebApplication1.Models;

namespace WebApplication1.Services
{
    /// <summary>
    /// Calls an OpenAI-compatible chat-completions endpoint (defaults to the free GitHub Models
    /// service) to produce a concise security-posture overview from the scanner's findings. Mirrors
    /// <see cref="GitHubModelsExceptionAnalyzer"/> and degrades gracefully when not configured.
    /// </summary>
    public class GitHubModelsSecurityAdvisor : ISecurityAdvisor
    {
        private readonly HttpClient _http;
        private readonly AiOptions _options;
        private readonly ILogger<GitHubModelsSecurityAdvisor> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public GitHubModelsSecurityAdvisor(
            HttpClient http,
            IOptions<AiOptions> options,
            ILogger<GitHubModelsSecurityAdvisor> logger)
        {
            _http = http;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<SecurityAiOverview> SummarizeAsync(SecurityScanResult scan, CancellationToken cancellationToken = default)
        {
            var apiKey = ResolveApiKey();

            if (!_options.Enabled || string.IsNullOrWhiteSpace(apiKey))
            {
                return new SecurityAiOverview
                {
                    FromAi = false,
                    ErrorMessage = "AI overview is not configured. Set the GITHUB_TOKEN environment variable " +
                                   "(or Ai:ApiKey) to enable an AI security summary. The findings below are still " +
                                   "produced from the live response.",
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
                        new { role = "user", content = BuildUserPrompt(scan) },
                    },
                    response_format = new { type = "json_object" },
                };

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                };
                httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

                using var response = await _http.SendAsync(httpRequest, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("AI security overview returned {Status}: {Body}", (int)response.StatusCode, Truncate(body, 400));
                    return new SecurityAiOverview
                    {
                        FromAi = false,
                        ErrorMessage = $"AI overview request failed ({(int)response.StatusCode}). The findings below are still valid.",
                    };
                }

                var content = ExtractMessageContent(body);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return new SecurityAiOverview { FromAi = false, ErrorMessage = "The model returned an empty response." };
                }

                var overview = ParseOverview(content);
                overview.FromAi = true;
                return overview;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI security overview failed.");
                return new SecurityAiOverview
                {
                    FromAi = false,
                    ErrorMessage = "Could not reach the AI service. The findings below are still valid.",
                };
            }
        }

        private string? ResolveApiKey() =>
            !string.IsNullOrWhiteSpace(_options.ApiKey)
                ? _options.ApiKey
                : Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        private const string SystemPrompt =
            "You are a senior application security engineer. Given a list of HTTP security findings " +
            "detected from a live API response (missing security headers, weak cookie flags, CORS or " +
            "transport issues), write a concise, practical overview of the endpoint's security posture " +
            "and the order in which the issues should be fixed. Base your answer ONLY on the findings " +
            "provided. Respond ONLY with a JSON object using exactly these keys: " +
            "\"summary\" (string, one short paragraph), " +
            "\"priorities\" (array of short ordered strings naming the most important fixes first), " +
            "\"confidence\" (integer 0-100). Do not include markdown or any text outside the JSON.";

        private static string BuildUserPrompt(SecurityScanResult scan)
        {
            var sb = new StringBuilder();
            sb.Append("Target: ").AppendLine(scan.TargetUrl);
            sb.Append("HTTP status: ").AppendLine(scan.StatusCode.ToString());
            sb.Append("Posture score: ").Append(scan.Score).Append("/100 (grade ").Append(scan.Grade).AppendLine(")");
            sb.AppendLine($"Counts: {scan.CriticalCount} critical, {scan.HighCount} high, {scan.MediumCount} medium, {scan.LowCount} low.");
            sb.AppendLine("Findings:");
            foreach (var f in scan.Findings)
            {
                sb.Append("- [").Append(f.Severity).Append("] ").Append(f.Title);
                if (!string.IsNullOrWhiteSpace(f.Evidence))
                {
                    sb.Append(" (").Append(Truncate(f.Evidence!, 160)).Append(')');
                }
                sb.AppendLine();
            }
            if (scan.Findings.Count == 0)
            {
                sb.AppendLine("- No issues detected; all checked security controls are present.");
            }
            return sb.ToString();
        }

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

        private static SecurityAiOverview ParseOverview(string content)
        {
            var json = ExtractJsonObject(content);
            try
            {
                var parsed = JsonSerializer.Deserialize<SecurityAiOverview>(json, JsonOptions);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Fall through to a best-effort wrapper below.
            }

            return new SecurityAiOverview { Summary = content.Trim() };
        }

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
