using WebApplication1.Models;

namespace WebApplication1.Services
{
    /// <summary>
    /// Produces a root-cause analysis and remediation steps for a single exception, using an LLM.
    /// </summary>
    public interface IExceptionAnalyzer
    {
        /// <summary>
        /// Analyzes the supplied exception (type, message, stack trace and API context) and returns
        /// a structured explanation of the likely root cause and how to fix it.
        /// </summary>
        Task<ExceptionAnalysis> AnalyzeAsync(ExceptionAnalysisRequest request, CancellationToken cancellationToken = default);
    }
}
