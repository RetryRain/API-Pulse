namespace WebApplication1.Models
{
    /// <summary>
    /// Identifies a single exception (from the detail page) to send to the LLM for analysis,
    /// along with light context about the API it occurred on.
    /// </summary>
    public class ExceptionAnalysisRequest
    {
        public string? WorkspaceId { get; set; }

        public string OperationName { get; set; } = string.Empty;

        public string ExceptionType { get; set; } = string.Empty;

        public string? Message { get; set; }

        public string? Method { get; set; }

        public string? StackTrace { get; set; }

        public long Count { get; set; }

        /// <summary>
        /// When true, this is a whole-endpoint analysis (not a single exception). The
        /// <see cref="Context"/> string carries the metrics/status/dependency summary.
        /// </summary>
        public bool IsEndpointAnalysis { get; set; }

        /// <summary>
        /// When true, this analyzes a single failing HTTP status code for the operation. The
        /// <see cref="StatusCode"/> identifies which code and <see cref="Context"/> carries the
        /// correlated telemetry evidence (failing URLs, exceptions, dependencies).
        /// </summary>
        public bool IsStatusCodeAnalysis { get; set; }

        /// <summary>The HTTP status code being analyzed (e.g. "500", "504") for a status-code analysis.</summary>
        public string? StatusCode { get; set; }

        /// <summary>Free-form telemetry context for endpoint-level analysis.</summary>
        public string? Context { get; set; }
    }

    /// <summary>
    /// Structured root-cause analysis returned by the LLM (or a fallback) for one exception.
    /// </summary>
    public class ExceptionAnalysis
    {
        public bool Success { get; set; }

        /// <summary>Populated when analysis could not be produced (e.g. AI not configured).</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>True when the result came from the real LLM (vs a local fallback).</summary>
        public bool FromAi { get; set; }

        /// <summary>One-paragraph explanation of the most likely root cause.</summary>
        public string RootCause { get; set; } = string.Empty;

        /// <summary>Ranked list of likely causes.</summary>
        public List<string> LikelyCauses { get; set; } = new();

        /// <summary>Concrete, ordered steps to fix or mitigate the exception.</summary>
        public List<string> HowToFix { get; set; } = new();

        /// <summary>Code areas / layers to inspect (e.g. "data access layer", "input validation").</summary>
        public List<string> CodeAreas { get; set; } = new();

        /// <summary>How confident the model is, 0-100.</summary>
        public int Confidence { get; set; }
    }
}
