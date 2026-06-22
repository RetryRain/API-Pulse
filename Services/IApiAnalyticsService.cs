using WebApplication1.Models;

namespace WebApplication1.Services
{
    /// <summary>
    /// Provides aggregated API statistics sourced from Application Insights.
    /// </summary>
    public interface IApiAnalyticsService
    {
        /// <summary>
        /// Returns aggregated API analytics for the requested workspace, API filter and look-back window.
        /// </summary>
        /// <param name="request">The workspace, API filter and time window to analyze.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        Task<ApiAnalyticsResult> GetAnalyticsAsync(ApiAnalyticsRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns an in-depth drill-down for a single endpoint: latency percentiles, status codes,
        /// exceptions with stack traces, dependencies, slowest samples and a deeper AI analysis.
        /// </summary>
        /// <param name="request">The workspace, operation name and time window to analyze.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        Task<EndpointDetail> GetEndpointDetailAsync(EndpointDetailRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The workspace ID configured in appsettings, surfaced so the UI can pre-fill it.
        /// </summary>
        string? DefaultWorkspaceId { get; }
    }
}
