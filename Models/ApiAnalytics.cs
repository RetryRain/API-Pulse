namespace WebApplication1.Models
{
    /// <summary>
    /// Describes which Application Insights resource and API to analyze.
    /// </summary>
    public class ApiAnalyticsRequest
    {
        /// <summary>
        /// Identifies the Application Insights / Log Analytics resource to query. Accepts a
        /// Workspace ID (GUID) or a full Azure resource ID (/subscriptions/...). When empty, the
        /// value configured in appsettings is used.
        /// </summary>
        public string? WorkspaceId { get; set; }

        /// <summary>
        /// Optional API / operation name to drill into. When set, results are limited to
        /// requests whose name contains this value (case-insensitive).
        /// </summary>
        public string? ApiFilter { get; set; }

        /// <summary>
        /// Optional telemetry source (Application Insights role, i.e. <c>cloud_RoleName</c>) to filter
        /// by. When set, results are limited to requests emitted by that role. Empty means all sources.
        /// </summary>
        public string? Source { get; set; }

        /// <summary>
        /// How many hours of telemetry to summarize.
        /// </summary>
        public int WindowHours { get; set; } = 24;

        /// <summary>
        /// Optional explicit start of a custom analysis range. When both <see cref="CustomStart"/>
        /// and <see cref="CustomEnd"/> are set, they take precedence over <see cref="WindowHours"/>.
        /// </summary>
        public DateTimeOffset? CustomStart { get; set; }

        /// <summary>Optional explicit end of a custom analysis range (see <see cref="CustomStart"/>).</summary>
        public DateTimeOffset? CustomEnd { get; set; }

        /// <summary>True when a valid explicit start/end range is supplied (start strictly before end).</summary>
        public bool HasCustomRange => CustomStart is { } s && CustomEnd is { } e && s < e;
    }

    /// <summary>
    /// Aggregated view of API telemetry pulled from Application Insights.
    /// </summary>
    public class ApiAnalyticsResult
    {
        public ApiAnalyticsSummary Summary { get; set; } = new();

        public IReadOnlyList<ApiEndpointStat> Endpoints { get; set; } = new List<ApiEndpointStat>();

        public IReadOnlyList<RequestVolumePoint> Timeline { get; set; } = new List<RequestVolumePoint>();

        /// <summary>
        /// True when a live query against Application Insights produced these results.
        /// When false the dashboard shows an empty state / guidance instead of figures.
        /// </summary>
        public bool HasResult { get; set; }

        /// <summary>
        /// The time window, in hours, the analytics cover.
        /// </summary>
        public int WindowHours { get; set; }

        /// <summary>
        /// The workspace / resource identifier that produced these results (echoed back for the UI).
        /// </summary>
        public string? WorkspaceId { get; set; }

        /// <summary>
        /// The API / operation filter applied to these results, if any.
        /// </summary>
        public string? ApiFilter { get; set; }

        /// <summary>
        /// The telemetry source (Application Insights role) filter applied to these results, if any.
        /// </summary>
        public string? Source { get; set; }

        /// <summary>
        /// Distinct telemetry sources (Application Insights roles) seen in the window, used to
        /// populate the Source filter dropdown. Empty when none could be discovered.
        /// </summary>
        public IReadOnlyList<string> AvailableSources { get; set; } = new List<string>();

        /// <summary>
        /// Populated with a friendly message when no live data could be shown (not configured,
        /// invalid identifier, or a failed query).
        /// </summary>
        public string? ErrorMessage { get; set; }

        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Headline metrics for the selected time window.
    /// </summary>
    public class ApiAnalyticsSummary
    {
        public long TotalCalls { get; set; }

        public long FailedCalls { get; set; }

        public double SuccessRate => TotalCalls == 0
            ? 0
            : Math.Round((double)(TotalCalls - FailedCalls) / TotalCalls * 100, 2);

        public double AverageDurationMs { get; set; }

        public double P95DurationMs { get; set; }

        public double EstimatedCostUsd { get; set; }
    }

    /// <summary>
    /// Per-endpoint roll-up of call volume and latency.
    /// </summary>
    public class ApiEndpointStat
    {
        public string Name { get; set; } = string.Empty;

        public long Calls { get; set; }

        public long FailedCalls { get; set; }

        public double AverageDurationMs { get; set; }

        public double P95DurationMs { get; set; }

        public double SuccessRate => Calls == 0
            ? 0
            : Math.Round((double)(Calls - FailedCalls) / Calls * 100, 2);

        /// <summary>Share of total calls across all endpoints, as a percentage (0-100).</summary>
        public double TrafficSharePercent { get; set; }

        /// <summary>Rough estimated cost in USD attributed to this endpoint over the window.</summary>
        public double EstimatedCostUsd { get; set; }

        /// <summary>A coarse health rating used to colour the row and detail view.</summary>
        public EndpointHealth Health { get; set; } = EndpointHealth.Healthy;

        /// <summary>Heuristic "AI" overview and suggestions generated for this endpoint.</summary>
        public AiInsight Insight { get; set; } = new();
    }

    /// <summary>
    /// Coarse health classification for an endpoint.
    /// </summary>
    public enum EndpointHealth
    {
        Healthy,
        Watch,
        Degraded,
    }

    /// <summary>
    /// A lightweight, heuristic "AI" overview generated from an endpoint's metrics. This keeps the
    /// prototype self-contained (no external LLM call) while demonstrating the experience.
    /// </summary>
    public class AiInsight
    {
        /// <summary>A one-line natural-language summary of the endpoint's behaviour.</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>A longer multi-sentence analysis used in the detail view.</summary>
        public string? Analysis { get; set; }

        /// <summary>Actionable optimization / reliability suggestions.</summary>
        public List<string> Suggestions { get; set; } = new();

        /// <summary>Notable risks or anomalies detected from the telemetry.</summary>
        public List<string> Risks { get; set; } = new();

        /// <summary>An overall score from 0-100 reflecting health, latency and reliability.</summary>
        public int Score { get; set; }
    }

    /// <summary>
    /// A single point in the request-volume timeline.
    /// </summary>
    public class RequestVolumePoint
    {
        public DateTimeOffset Timestamp { get; set; }

        public long Calls { get; set; }

        public double AverageDurationMs { get; set; }
    }

    /// <summary>
    /// Request for an in-depth, on-demand drill-down into a single endpoint/operation.
    /// </summary>
    public class EndpointDetailRequest
    {
        public string? WorkspaceId { get; set; }

        public string OperationName { get; set; } = string.Empty;

        public int WindowHours { get; set; } = 24;

        /// <summary>
        /// Optional explicit start of a custom analysis range. When both <see cref="CustomStart"/>
        /// and <see cref="CustomEnd"/> are set, they take precedence over <see cref="WindowHours"/>.
        /// </summary>
        public DateTimeOffset? CustomStart { get; set; }

        /// <summary>Optional explicit end of a custom analysis range (see <see cref="CustomStart"/>).</summary>
        public DateTimeOffset? CustomEnd { get; set; }

        /// <summary>True when a valid explicit start/end range is supplied (start strictly before end).</summary>
        public bool HasCustomRange => CustomStart is { } s && CustomEnd is { } e && s < e;
    }

    /// <summary>
    /// Deep-dive telemetry for a single endpoint: latency distribution, status codes,
    /// exceptions (with stack traces), dependencies, slowest samples and AI analysis.
    /// </summary>
    public class EndpointDetail
    {
        public string OperationName { get; set; } = string.Empty;

        public int WindowHours { get; set; }

        public bool HasResult { get; set; }

        public string? ErrorMessage { get; set; }

        public ApiEndpointStat Overview { get; set; } = new();

        public LatencyDistribution Latency { get; set; } = new();

        public IReadOnlyList<StatusCodeStat> StatusCodes { get; set; } = new List<StatusCodeStat>();

        public IReadOnlyList<ExceptionStat> Exceptions { get; set; } = new List<ExceptionStat>();

        public IReadOnlyList<DependencyStat> Dependencies { get; set; } = new List<DependencyStat>();

        public IReadOnlyList<RequestSample> SlowestSamples { get; set; } = new List<RequestSample>();

        public IReadOnlyList<RequestVolumePoint> Timeline { get; set; } = new List<RequestVolumePoint>();

        /// <summary>Latency histogram buckets (e.g. 0-100ms, 100-250ms…) with request counts.</summary>
        public IReadOnlyList<NamedCount> LatencyBuckets { get; set; } = new List<NamedCount>();

        /// <summary>Top distinct request URLs hitting this operation.</summary>
        public IReadOnlyList<NamedCount> TopUrls { get; set; } = new List<NamedCount>();

        /// <summary>Cloud roles / instances serving this operation.</summary>
        public IReadOnlyList<NamedCount> Roles { get; set; } = new List<NamedCount>();

        /// <summary>Client geography (city / country) calling this operation.</summary>
        public IReadOnlyList<NamedCount> ClientGeo { get; set; } = new List<NamedCount>();

        /// <summary>Representative custom dimensions / properties from a recent request.</summary>
        public IReadOnlyList<KeyValueItem> Properties { get; set; } = new List<KeyValueItem>();

        /// <summary>This-window vs previous-window comparison for key metrics.</summary>
        public PeriodComparison? Comparison { get; set; }

        public AiInsight Insight { get; set; } = new();
    }

    /// <summary>A label + count pair used for breakdowns (URLs, roles, buckets, geo).</summary>
    public class NamedCount
    {
        public string Name { get; set; } = string.Empty;
        public long Count { get; set; }
        public double SharePercent { get; set; }
    }

    /// <summary>A single key/value custom dimension.</summary>
    public class KeyValueItem
    {
        public string Key { get; set; } = string.Empty;
        public string? Value { get; set; }
    }

    /// <summary>Compares the current window against the immediately preceding one.</summary>
    public class PeriodComparison
    {
        public long PreviousCalls { get; set; }
        public double PreviousSuccessRate { get; set; }
        public double PreviousAvgDurationMs { get; set; }

        public long CurrentCalls { get; set; }
        public double CurrentSuccessRate { get; set; }
        public double CurrentAvgDurationMs { get; set; }

        public double CallsChangePercent => PreviousCalls == 0
            ? 0
            : Math.Round((double)(CurrentCalls - PreviousCalls) / PreviousCalls * 100, 1);

        public double SuccessRateDelta => Math.Round(CurrentSuccessRate - PreviousSuccessRate, 2);

        public double AvgDurationChangePercent => PreviousAvgDurationMs == 0
            ? 0
            : Math.Round((CurrentAvgDurationMs - PreviousAvgDurationMs) / PreviousAvgDurationMs * 100, 1);
    }

    /// <summary>Latency percentiles for an endpoint.</summary>
    public class LatencyDistribution
    {
        public double P50 { get; set; }
        public double P90 { get; set; }
        public double P95 { get; set; }
        public double P99 { get; set; }
        public double Max { get; set; }
    }

    /// <summary>Count of requests by HTTP result/status code.</summary>
    public class StatusCodeStat
    {
        public string ResultCode { get; set; } = string.Empty;
        public long Count { get; set; }
        public double SharePercent { get; set; }
        public bool IsError { get; set; }

        /// <summary>
        /// Real telemetry evidence correlated to this status code (failing URLs, the exceptions
        /// thrown on those requests, failing dependencies). Only populated for error codes so the
        /// AI can pinpoint the actual reason rather than guess. Null for successful codes.
        /// </summary>
        public StatusCodeFailureEvidence? Evidence { get; set; }
    }

    /// <summary>
    /// Evidence gathered from Application Insights for a single failing status code, used to drive a
    /// pinpointed, data-grounded root-cause analysis (instead of a generic explanation).
    /// </summary>
    public class StatusCodeFailureEvidence
    {
        /// <summary>Number of failed requests that returned this status code in the window.</summary>
        public long FailedRequests { get; set; }

        /// <summary>Most recent time a request returned this code.</summary>
        public DateTimeOffset LastSeen { get; set; }

        /// <summary>Distinct request URLs (sampled) that returned this code.</summary>
        public List<string> SampleUrls { get; set; } = new();

        /// <summary>Exception types correlated (via operation_Id) to requests with this code.</summary>
        public List<string> ExceptionTypes { get; set; } = new();

        /// <summary>Representative exception messages correlated to this code.</summary>
        public List<string> ExceptionMessages { get; set; } = new();

        /// <summary>Downstream dependencies that failed on requests returning this code.</summary>
        public List<string> FailingDependencies { get; set; } = new();
    }

    /// <summary>An exception type seen on the endpoint, with a representative stack trace.</summary>
    public class ExceptionStat
    {
        public string Type { get; set; } = string.Empty;
        public string? Message { get; set; }
        public long Count { get; set; }
        public string? Method { get; set; }
        public string? StackTrace { get; set; }
        public DateTimeOffset LastSeen { get; set; }

        /// <summary>A short, human-friendly category for the exception (e.g. "Timeout", "Null reference").</summary>
        public string? FriendlyCategory { get; set; }

        /// <summary>A plain-language explanation of what the exception usually means.</summary>
        public string? FriendlyExplanation { get; set; }

        /// <summary>A suggested next step to investigate or fix the exception.</summary>
        public string? SuggestedAction { get; set; }
    }

    /// <summary>A downstream dependency called while serving the endpoint.</summary>
    public class DependencyStat
    {
        public string Name { get; set; } = string.Empty;
        public string? Type { get; set; }
        public string? Target { get; set; }
        public long Calls { get; set; }
        public long FailedCalls { get; set; }
        public double AverageDurationMs { get; set; }

        public double SuccessRate => Calls == 0
            ? 0
            : Math.Round((double)(Calls - FailedCalls) / Calls * 100, 1);
    }

    /// <summary>A single (slow) request instance for drill-through.</summary>
    public class RequestSample
    {
        public DateTimeOffset Timestamp { get; set; }
        public double DurationMs { get; set; }
        public string? ResultCode { get; set; }
        public bool Success { get; set; }
        public string? OperationId { get; set; }
    }
}
