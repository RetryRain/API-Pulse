using WebApplication1.Models;

namespace WebApplication1.Services
{
    /// <summary>
    /// Produces an optional, AI-generated plain-language overview of an endpoint's security posture
    /// based on the deterministic findings from <see cref="ISecurityScanner"/>.
    /// </summary>
    public interface ISecurityAdvisor
    {
        /// <summary>
        /// Summarizes the supplied scan result and prioritizes remediation. Returns an overview whose
        /// <see cref="SecurityAiOverview.FromAi"/> is false (with a message) when AI is unavailable.
        /// </summary>
        Task<SecurityAiOverview> SummarizeAsync(SecurityScanResult scan, CancellationToken cancellationToken = default);
    }
}
