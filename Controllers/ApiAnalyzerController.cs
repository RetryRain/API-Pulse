using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text.Json;
using WebApplication1.Models;
using WebApplication1.Services;

namespace WebApplication1.Controllers
{
    public class ApiAnalyzerController : Controller
    {
        private static readonly int[] AllowedWindows = { 1, 6, 12, 24, 72, 168 };

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IApiAnalyticsService _analyticsService;

        public ApiAnalyzerController(IApiAnalyticsService analyticsService)
        {
            _analyticsService = analyticsService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int windowHours, string? workspaceId, string? apiFilter, string? source,
            DateTimeOffset? customStart, DateTimeOffset? customEnd, CancellationToken cancellationToken)
        {
            var hasCustomRange = customStart is { } cs && customEnd is { } ce && cs < ce;
            // Only enforce the preset allow-list when a custom range isn't driving the query.
            if (!hasCustomRange && !AllowedWindows.Contains(windowHours))
            {
                windowHours = 24;
            }

            // Pre-fill the input with the configured default the first time the page loads.
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                workspaceId = _analyticsService.DefaultWorkspaceId;
            }

            var request = new ApiAnalyticsRequest
            {
                WorkspaceId = workspaceId,
                ApiFilter = apiFilter,
                Source = source,
                WindowHours = windowHours,
                CustomStart = customStart,
                CustomEnd = customEnd,
            };

            ApiAnalyticsResult analytics;
            try
            {
                analytics = await _analyticsService.GetAnalyticsAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The browser aborted this request (e.g. a second Analyze click or navigating away)
                // before the Application Insights query finished. The connection is gone, so there is
                // nothing to render - return quietly instead of surfacing a TaskCanceledException.
                return new StatusCodeResult(499);
            }

            var model = new ApiAnalyzerViewModel
            {
                WindowHours = windowHours,
                WorkspaceId = workspaceId,
                ApiFilter = apiFilter,
                Source = source,
                CustomStart = customStart,
                CustomEnd = customEnd,
                Analytics = analytics,
                MaxTimelineCalls = analytics.Timeline.Count == 0
                    ? 0
                    : analytics.Timeline.Max(p => p.Calls),
                TimelineJson = JsonSerializer.Serialize(analytics.Timeline.Select(p => new
                {
                    Time = p.Timestamp.ToLocalTime().ToString("MMM d, h:mm tt"),
                    Calls = p.Calls,
                    Avg = p.AverageDurationMs,
                }), JsonOptions),
            };

            return View(model);
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel
            {
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
            });
        }
    }
}
