namespace WebApplication1.Models
{
    /// <summary>
    /// View model for the API analyzer dashboard.
    /// </summary>
    public class ApiAnalyzerViewModel
    {
        public int WindowHours { get; set; } = 24;

        public string? WorkspaceId { get; set; }

        public string? ApiFilter { get; set; }

        /// <summary>The selected telemetry source (Application Insights role) filter, echoed back to the UI.</summary>
        public string? Source { get; set; }

        /// <summary>Optional explicit start of a custom analysis range (UTC). Pairs with CustomEnd.</summary>
        public DateTimeOffset? CustomStart { get; set; }

        /// <summary>Optional explicit end of a custom analysis range (UTC).</summary>
        public DateTimeOffset? CustomEnd { get; set; }

        /// <summary>True when a valid explicit start/end range is supplied (start strictly before end).</summary>
        public bool HasCustomRange => CustomStart is { } s && CustomEnd is { } e && s < e;

        public ApiAnalyticsResult Analytics { get; set; } = new();

        public long MaxTimelineCalls { get; set; }

        /// <summary>Timeline points serialized as JSON so the client can draw the line chart.</summary>
        public string TimelineJson { get; set; } = "[]";

        public static string SuccessRatePillClass(double successRate) => successRate switch
        {
            >= 99 => "pill pill--good",
            >= 95 => "pill pill--warn",
            _ => "pill pill--bad",
        };
    }
}
