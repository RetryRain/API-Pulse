namespace WebApplication1.Models
{
    /// <summary>
    /// Request to run a security audit against a live API / web endpoint. The scanner probes the URL
    /// and inspects the real HTTP response (headers, TLS, cookies) to detect missing security controls.
    /// </summary>
    public class SecurityScanRequest
    {
        /// <summary>Absolute http/https URL of the API or page to audit.</summary>
        public string TargetUrl { get; set; } = string.Empty;

        /// <summary>HTTP method to probe with (GET, POST, PUT, PATCH, DELETE). Defaults to GET.</summary>
        public string Method { get; set; } = "GET";

        /// <summary>Optional request body sent for POST/PUT/PATCH probes (e.g. a JSON payload).</summary>
        public string? Body { get; set; }

        /// <summary>Content type of <see cref="Body"/>. Defaults to application/json.</summary>
        public string ContentType { get; set; } = "application/json";

        /// <summary>
        /// Optional extra request headers (e.g. "Authorization": "Bearer ..."), supplied so
        /// authenticated endpoints can be audited. Never logged or persisted.
        /// </summary>
        public Dictionary<string, string>? Headers { get; set; }
    }

    /// <summary>Severity rating for a finding, ordered most → least severe.</summary>
    public enum SecuritySeverity
    {
        Critical,
        High,
        Medium,
        Low,
        Info,
    }

    /// <summary>Remediation status of a finding.</summary>
    public enum SecurityStatus
    {
        /// <summary>The control is missing/weak — action required.</summary>
        NotFixed,

        /// <summary>The control is present and correctly configured.</summary>
        Fixed,
    }

    /// <summary>
    /// The result of a security scan: the probed target, an overall posture grade, the AI overview
    /// (optional) and the ordered list of findings rendered as a vulnerability report.
    /// </summary>
    public class SecurityScanResult
    {
        public bool HasResult { get; set; }

        /// <summary>Populated with a friendly message when the scan could not be performed.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>The URL that was actually probed (after normalization).</summary>
        public string? TargetUrl { get; set; }

        /// <summary>The HTTP method used for the probe (GET, POST, …).</summary>
        public string Method { get; set; } = "GET";

        /// <summary>HTTP status code returned by the probe (e.g. 200).</summary>
        public int StatusCode { get; set; }

        /// <summary>The reconstructed request line + headers, shown in the Proof of Concept blocks.</summary>
        public string? RequestSnippet { get; set; }

        /// <summary>The response status line + headers observed, shown as evidence.</summary>
        public string? ResponseSnippet { get; set; }

        /// <summary>Letter grade (A–F) summarizing the posture from the findings.</summary>
        public string Grade { get; set; } = "A";

        /// <summary>0–100 score derived from the severity-weighted findings.</summary>
        public int Score { get; set; } = 100;

        public int CriticalCount { get; set; }
        public int HighCount { get; set; }
        public int MediumCount { get; set; }
        public int LowCount { get; set; }

        /// <summary>Optional AI-generated posture summary; null when AI is unconfigured.</summary>
        public SecurityAiOverview? AiOverview { get; set; }

        public List<SecurityFinding> Findings { get; set; } = new();

        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// One audit finding, modelled on a vulnerability-report issue (severity, status, synopsis,
    /// impact, recommendation, CWE reference and a proof-of-concept note).
    /// </summary>
    public class SecurityFinding
    {
        /// <summary>Sequential issue number for display ("Issue #1").</summary>
        public int Number { get; set; }

        public string Title { get; set; } = string.Empty;

        public SecuritySeverity Severity { get; set; }

        public SecurityStatus Status { get; set; }

        /// <summary>What the issue is.</summary>
        public string Synopsis { get; set; } = string.Empty;

        /// <summary>Why it matters / what an attacker can do.</summary>
        public string Impact { get; set; } = string.Empty;

        /// <summary>How to remediate it.</summary>
        public string Recommendation { get; set; } = string.Empty;

        /// <summary>CWE / external reference URL.</summary>
        public string? Reference { get; set; }

        /// <summary>Plain-language proof-of-concept note describing the observation.</summary>
        public string? ProofOfConcept { get; set; }

        /// <summary>The specific header/value observed (evidence), if any.</summary>
        public string? Evidence { get; set; }
    }

    /// <summary>Optional AI-generated security posture overview.</summary>
    public class SecurityAiOverview
    {
        public bool FromAi { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>One-paragraph plain-language summary of the overall posture.</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>The highest-priority actions to take, in order.</summary>
        public List<string> Priorities { get; set; } = new();

        public int Confidence { get; set; }
    }
}
