using WebApplication1.Models;

namespace WebApplication1.Services
{
    /// <summary>
    /// Probes a live API / web endpoint and audits its HTTP security posture, returning a
    /// vulnerability-style report of missing or weak security controls.
    /// </summary>
    public interface ISecurityScanner
    {
        /// <summary>
        /// Issues a request to the target URL and evaluates the response against a set of security
        /// rules (security headers, TLS, cookie flags, information disclosure, CORS).
        /// </summary>
        /// <param name="request">The target URL to audit.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        Task<SecurityScanResult> ScanAsync(SecurityScanRequest request, CancellationToken cancellationToken = default);
    }
}
