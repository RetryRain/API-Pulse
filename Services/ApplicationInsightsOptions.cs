namespace WebApplication1.Services
{
    /// <summary>
    /// Strongly typed configuration for connecting to Application Insights / Log Analytics
    /// via Azure Monitor Query using a managed identity.
    /// </summary>
    public class ApplicationInsightsOptions
    {
        public const string SectionName = "ApplicationInsights";

        /// <summary>
        /// The Application Insights / Log Analytics identifier to query by default. Accepts a
        /// Workspace ID (GUID) or a full Azure resource ID. Used to pre-fill the input box.
        /// </summary>
        public string? WorkspaceId { get; set; }

        /// <summary>
        /// Optional user-assigned managed identity client ID. When empty, a
        /// system-assigned identity (or the local developer credential) is used.
        /// </summary>
        public string? ManagedIdentityClientId { get; set; }

        /// <summary>
        /// Estimated cost in USD applied per 10,000 API calls. Used to provide a
        /// rough cost insight in the prototype.
        /// </summary>
        public double CostPerTenThousandCalls { get; set; } = 1.50d;
    }
}
