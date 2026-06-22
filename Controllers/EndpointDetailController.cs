using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebApplication1.Models;
using WebApplication1.Services;

namespace WebApplication1.Controllers
{
    public class EndpointDetailController : Controller
    {
        private static readonly int[] AllowedWindows = { 1, 6, 12, 24, 72, 168 };

        // Serialize enums (e.g. EndpointHealth) as their string names so the client gets
        // "Healthy" rather than 0, and camelCase for JS consumption. AllowNamedFloatingPointLiterals
        // prevents a NaN/Infinity from a sparse aggregate from throwing during serialization.
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private readonly IApiAnalyticsService _analyticsService;
        private readonly IExceptionAnalyzer _exceptionAnalyzer;

        public EndpointDetailController(IApiAnalyticsService analyticsService, IExceptionAnalyzer exceptionAnalyzer)
        {
            _analyticsService = analyticsService;
            _exceptionAnalyzer = exceptionAnalyzer;
        }

        [HttpGet]
        public IActionResult Index(string? workspaceId, string? operation, string? apiFilter, int windowHours,
            DateTimeOffset? customStart, DateTimeOffset? customEnd)
        {
            if (!AllowedWindows.Contains(windowHours))
            {
                windowHours = 24;
            }

            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                workspaceId = _analyticsService.DefaultWorkspaceId;
            }

            return View(new EndpointDetailViewModel
            {
                WorkspaceId = workspaceId,
                Operation = operation,
                ApiFilter = apiFilter,
                WindowHours = windowHours,
                CustomStart = customStart,
                CustomEnd = customEnd,
            });
        }

        /// <summary>AJAX: returns the in-depth detail for the operation as JSON.</summary>
        [HttpGet]
        public async Task<IActionResult> Data(string operation, string? workspaceId, int windowHours,
            DateTimeOffset? customStart, DateTimeOffset? customEnd, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(operation))
            {
                return BadRequest("operation is required.");
            }

            var hasCustomRange = customStart is { } s && customEnd is { } e && s < e;
            // Only enforce the preset allow-list when a custom range isn't driving the query.
            if (!hasCustomRange)
            {
                windowHours = AllowedWindows.Contains(windowHours) ? windowHours : 24;
            }
            workspaceId = string.IsNullOrWhiteSpace(workspaceId)
                ? _analyticsService.DefaultWorkspaceId
                : workspaceId;

            try
            {
                var detail = await _analyticsService.GetEndpointDetailAsync(new EndpointDetailRequest
                {
                    WorkspaceId = workspaceId,
                    OperationName = operation,
                    WindowHours = windowHours,
                    CustomStart = customStart,
                    CustomEnd = customEnd,
                }, cancellationToken);

                return new JsonResult(detail, JsonOptions);
            }
            catch (OperationCanceledException)
            {
                // Client navigated away / cancelled. 499 = client closed request; no error noise.
                return new StatusCodeResult(499);
            }
        }

        /// <summary>Exports the endpoint's telemetry summary as a downloadable CSV file.</summary>
        [HttpGet]
        public async Task<IActionResult> ExportCsv(string operation, string? workspaceId, int windowHours,
            DateTimeOffset? customStart, DateTimeOffset? customEnd, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(operation))
            {
                return BadRequest("operation is required.");
            }

            var hasCustomRange = customStart is { } s && customEnd is { } e && s < e;
            if (!hasCustomRange)
            {
                windowHours = AllowedWindows.Contains(windowHours) ? windowHours : 24;
            }
            workspaceId = string.IsNullOrWhiteSpace(workspaceId)
                ? _analyticsService.DefaultWorkspaceId
                : workspaceId;

            try
            {
                // Reuses the cached detail query, so exporting right after viewing is effectively free.
                var detail = await _analyticsService.GetEndpointDetailAsync(new EndpointDetailRequest
                {
                    WorkspaceId = workspaceId,
                    OperationName = operation,
                    WindowHours = windowHours,
                    CustomStart = customStart,
                    CustomEnd = customEnd,
                }, cancellationToken);

                var csv = BuildCsv(detail);
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv);
                var rangeLabel = hasCustomRange
                    ? $"{customStart!.Value.UtcDateTime:yyyyMMddHHmm}-{customEnd!.Value.UtcDateTime:yyyyMMddHHmm}"
                    : $"{windowHours}h";
                var fileName = $"endpoint-{Sanitize(detail.OperationName)}-{rangeLabel}.csv";
                return File(bytes, "text/csv", fileName);
            }
            catch (OperationCanceledException)
            {
                return new StatusCodeResult(499);
            }
        }

        /// <summary>Builds a multi-section CSV summary (metrics, status codes, exceptions, dependencies).</summary>
        private static string BuildCsv(EndpointDetail detail)
        {
            var o = detail.Overview;
            var sb = new StringBuilder();

            sb.AppendLine("Section,Metric,Value");
            sb.AppendLine($"Overview,Operation,{Csv(detail.OperationName)}");
            sb.AppendLine($"Overview,Window (hours),{detail.WindowHours}");
            sb.AppendLine($"Overview,Total calls,{o.Calls}");
            sb.AppendLine($"Overview,Failed calls,{o.FailedCalls}");
            sb.AppendLine($"Overview,Success rate (%),{o.SuccessRate:0.##}");
            sb.AppendLine($"Overview,Avg response (ms),{o.AverageDurationMs:0.##}");
            sb.AppendLine($"Overview,Slow requests (ms),{o.P95DurationMs:0.##}");
            sb.AppendLine($"Overview,Est. cost (USD),{o.EstimatedCostUsd:0.####}");
            sb.AppendLine($"Latency,Typical (ms),{detail.Latency.P50:0.##}");
            sb.AppendLine($"Latency,Worst case (ms),{detail.Latency.Max:0.##}");

            if (detail.StatusCodes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Status code,Count,Share (%)");
                foreach (var s in detail.StatusCodes)
                {
                    sb.AppendLine($"{Csv(s.ResultCode)},{s.Count},{s.SharePercent:0.##}");
                }
            }

            if (detail.Exceptions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Exception type,Count");
                foreach (var e in detail.Exceptions)
                {
                    sb.AppendLine($"{Csv(e.Type)},{e.Count}");
                }
            }

            if (detail.Dependencies.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Dependency,Type,Calls,Avg (ms),Success (%)");
                foreach (var d in detail.Dependencies)
                {
                    sb.AppendLine($"{Csv(d.Name)},{Csv(d.Type)},{d.Calls},{d.AverageDurationMs:0.##},{d.SuccessRate:0.##}");
                }
            }

            return sb.ToString();
        }

        /// <summary>Escapes a CSV field (quotes when it contains a comma, quote or newline).</summary>
        private static string Csv(string? value)
        {
            value ??= string.Empty;
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        /// <summary>Makes a string safe for use in a download file name.</summary>
        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return "api"; }
            var cleaned = new string(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            return cleaned.Trim('-').ToLowerInvariant() is { Length: > 0 } s ? s : "api";
        }

        /// <summary>AJAX (POST): runs real LLM root-cause analysis for a single exception, for a single
        /// failing HTTP status code when <c>IsStatusCodeAnalysis</c> is set, or for the whole endpoint
        /// when <c>IsEndpointAnalysis</c> is set.</summary>
        [HttpPost]
        public async Task<IActionResult> AnalyzeException([FromBody] ExceptionAnalysisRequest request, CancellationToken cancellationToken)
        {
            if (request is null ||
                (!request.IsEndpointAnalysis && !request.IsStatusCodeAnalysis && string.IsNullOrWhiteSpace(request.ExceptionType)))
            {
                return BadRequest("exceptionType is required.");
            }

            if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            {
                request.WorkspaceId = _analyticsService.DefaultWorkspaceId;
            }

            try
            {
                var analysis = await _exceptionAnalyzer.AnalyzeAsync(request, cancellationToken);
                return new JsonResult(analysis, JsonOptions);
            }
            catch (OperationCanceledException)
            {
                return new StatusCodeResult(499);
            }
        }
    }
}
