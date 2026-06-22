using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebApplication1.Models;
using WebApplication1.Services;

namespace WebApplication1.Controllers
{
    public class SecurityController : Controller
    {
        // Serialize enums (SecuritySeverity/SecurityStatus) as their string names and use camelCase
        // so the client JS gets "Low"/"NotFixed" rather than integers.
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private readonly ISecurityScanner _scanner;
        private readonly ISecurityAdvisor _advisor;

        public SecurityController(ISecurityScanner scanner, ISecurityAdvisor advisor)
        {
            _scanner = scanner;
            _advisor = advisor;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        /// <summary>AJAX: probes the target URL with the chosen method, audits its security posture and returns JSON.</summary>
        [HttpPost]
        public async Task<IActionResult> Scan([FromBody] SecurityScanRequest request, CancellationToken cancellationToken)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.TargetUrl))
            {
                return BadRequest("targetUrl is required.");
            }

            try
            {
                var result = await _scanner.ScanAsync(request, cancellationToken);

                // Enrich with an optional AI overview only when the probe produced findings.
                if (result.HasResult)
                {
                    result.AiOverview = await _advisor.SummarizeAsync(result, cancellationToken);
                }

                return new JsonResult(result, JsonOptions);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Client navigated away / cancelled. 499 = client closed request; no error noise.
                return new StatusCodeResult(499);
            }
        }
    }
}
