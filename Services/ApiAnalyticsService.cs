using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WebApplication1.Models;

namespace WebApplication1.Services
{
    /// <summary>
    /// Queries Application Insights (backed by a Log Analytics workspace) through
    /// Azure Monitor Query using a managed identity. Falls back to generated sample
    /// data when the workspace is not configured so the prototype always renders.
    /// </summary>
    public class ApiAnalyticsService : IApiAnalyticsService
    {
        /// <summary>How long live query results are cached, to absorb repeat navigations and double-clicks.</summary>
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

        private readonly ApplicationInsightsOptions _options;
        private readonly ILogger<ApiAnalyticsService> _logger;
        private readonly LogsQueryClient _client;
        private readonly IMemoryCache _cache;

        public ApiAnalyticsService(IOptions<ApplicationInsightsOptions> options, ILogger<ApiAnalyticsService> logger, IMemoryCache cache)
        {
            _options = options.Value;
            _logger = logger;
            _cache = cache;

            // DefaultAzureCredential resolves a managed identity in Azure and the
            // developer's local credentials during development. A user-assigned
            // identity client ID can be supplied when one is configured. The client is
            // workspace-agnostic: the workspace to query is supplied per request, so the
            // user can point the analyzer at any Application Insights resource at runtime.
            var credentialOptions = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrWhiteSpace(_options.ManagedIdentityClientId))
            {
                credentialOptions.ManagedIdentityClientId = _options.ManagedIdentityClientId;
            }

            _client = new LogsQueryClient(new DefaultAzureCredential(credentialOptions));
        }

        public string? DefaultWorkspaceId => _options.WorkspaceId;

        public async Task<ApiAnalyticsResult> GetAnalyticsAsync(ApiAnalyticsRequest request, CancellationToken cancellationToken = default)
        {
            var windowHours = Math.Clamp(request.WindowHours, 1, 720);
            var apiFilter = NormalizeApiFilter(request.ApiFilter);
            var source = string.IsNullOrWhiteSpace(request.Source) ? null : request.Source.Trim();

            // A valid explicit start/end range takes precedence over the relative window. The span
            // is capped at 720h to match the relative-window clamp above.
            DateTimeOffset? customStart = null;
            DateTimeOffset? customEnd = null;
            if (request.HasCustomRange)
            {
                customEnd = request.CustomEnd!.Value.ToUniversalTime();
                customStart = request.CustomStart!.Value.ToUniversalTime();
                if (customEnd.Value - customStart.Value > TimeSpan.FromHours(720))
                {
                    customStart = customEnd.Value.AddHours(-720);
                }
                windowHours = Math.Max(1, (int)Math.Round((customEnd.Value - customStart.Value).TotalHours));
            }

            var input = string.IsNullOrWhiteSpace(request.WorkspaceId)
                ? _options.WorkspaceId
                : request.WorkspaceId.Trim();

            // Nothing supplied yet: show the empty state with guidance, not sample data.
            if (string.IsNullOrWhiteSpace(input))
            {
                return EmptyResult(windowHours, apiFilter, input,
                    "Enter your Application Insights Workspace ID (a GUID) or resource ID, then select Analyze.");
            }

            if (!TryResolveWorkspaceTarget(input, out var target, out var validationError))
            {
                _logger.LogWarning("Invalid Application Insights identifier supplied: {WorkspaceInput}", input);
                return EmptyResult(windowHours, apiFilter, input, validationError!);
            }

            try
            {
                // Cache only successful live results for a short window, so repeat navigations
                // and accidental double-clicks reuse the same data instead of re-querying Azure.
                // WorkspaceTarget sets exactly one of WorkspaceId/ResourceId, so combine both. The
                // range (custom or relative) is part of the key so a custom range never collides.
                var targetKey = target.WorkspaceId ?? target.ResourceId?.ToString() ?? input;
                var rangeKey = customStart is { } cs && customEnd is { } ce
                    ? $"{cs:O}_{ce:O}"
                    : $"{windowHours}h";
                var cacheKey = $"analytics:{targetKey}:{rangeKey}:{apiFilter}:{source}";
                if (_cache.TryGetValue(cacheKey, out ApiAnalyticsResult? cached) && cached is not null)
                {
                    return cached;
                }

                var range = customStart is { } start && customEnd is { } end
                    ? new QueryTimeRange(start, end)
                    : new QueryTimeRange(TimeSpan.FromHours(windowHours));
                var result = await QueryLiveAsync(target, windowHours, apiFilter, source, range, cancellationToken);
                _cache.Set(cacheKey, result, CacheDuration);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw; // client cancelled - let the page handler turn it into a quiet 499
            }
            catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException)
            {
                _logger.LogError(ex, "Failed to query Application Insights target {Target}.", input);
                return EmptyResult(windowHours, apiFilter, input, DescribeLiveError(ex));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error querying Application Insights target {Target}.", input);
                return EmptyResult(windowHours, apiFilter, input, "Something went wrong querying telemetry. " + ex.Message);
            }
        }

        /// <summary>
        /// Returns an empty (no figures) result carrying a guidance / error message for the UI.
        /// </summary>
        private static ApiAnalyticsResult EmptyResult(int windowHours, string? apiFilter, string? workspaceDisplay, string message) =>
            new()
            {
                HasResult = false,
                WindowHours = windowHours,
                WorkspaceId = workspaceDisplay,
                ApiFilter = apiFilter,
                ErrorMessage = message,
                GeneratedAt = DateTimeOffset.UtcNow,
            };

        public async Task<EndpointDetail> GetEndpointDetailAsync(EndpointDetailRequest request, CancellationToken cancellationToken = default)
        {
            var windowHours = Math.Clamp(request.WindowHours, 1, 720);

            // A valid explicit start/end range takes precedence over the relative window. The span
            // is capped at 720h to match the relative-window clamp above.
            DateTimeOffset? customStart = null;
            DateTimeOffset? customEnd = null;
            if (request.HasCustomRange)
            {
                customEnd = request.CustomEnd!.Value.ToUniversalTime();
                customStart = request.CustomStart!.Value.ToUniversalTime();
                if (customEnd.Value - customStart.Value > TimeSpan.FromHours(720))
                {
                    customStart = customEnd.Value.AddHours(-720);
                }
                // Effective hours used by the (best-effort) period comparison.
                windowHours = Math.Max(1, (int)Math.Round((customEnd.Value - customStart.Value).TotalHours));
            }

            var operationName = NormalizeApiFilter(request.OperationName) ?? request.OperationName?.Trim() ?? string.Empty;

            var detail = new EndpointDetail
            {
                OperationName = operationName,
                WindowHours = windowHours,
            };

            if (string.IsNullOrWhiteSpace(operationName))
            {
                detail.ErrorMessage = "No operation name was supplied.";
                return detail;
            }

            var input = string.IsNullOrWhiteSpace(request.WorkspaceId)
                ? _options.WorkspaceId
                : request.WorkspaceId.Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                detail.ErrorMessage = "A valid Workspace ID is required to load endpoint detail.";
                return detail;
            }

            if (!TryResolveWorkspaceTarget(input, out var target, out var validationError))
            {
                detail.ErrorMessage = validationError ?? "A valid Workspace ID is required to load endpoint detail.";
                return detail;
            }

            // Serve a recent cached drill-down when available; this also makes the CSV export
            // (which re-requests the same detail) effectively free. The range (custom or relative)
            // is part of the key so a custom range never collides with a preset result.
            var targetKey = target.WorkspaceId ?? target.ResourceId?.ToString() ?? input;
            var rangeKey = customStart is { } cs && customEnd is { } ce
                ? $"{cs:O}_{ce:O}"
                : $"{windowHours}h";
            var cacheKey = $"detail:{targetKey}:{rangeKey}:{operationName}";
            if (_cache.TryGetValue(cacheKey, out EndpointDetail? cachedDetail) && cachedDetail is not null)
            {
                return cachedDetail;
            }

            try
            {
                var timeRange = customStart is { } start && customEnd is { } end
                    ? new QueryTimeRange(start, end)
                    : new QueryTimeRange(TimeSpan.FromHours(windowHours));
                var nameLiteral = EscapeKqlString(operationName);
                // Contains-match on name OR url so a pasted request URL resolves to the operation.
                var scope = $"\n| where name contains '{nameLiteral}' or url contains '{nameLiteral}' or operation_Name contains '{nameLiteral}'";

                // Overview first because the share-of-traffic calculations need its call count.
                detail.Overview = await SafeAsync(() => GetDetailOverviewAsync(target, timeRange, scope, operationName, cancellationToken), new ApiEndpointStat { Name = operationName }, cancellationToken);
                var totalCalls = detail.Overview.Calls;

                // Fan out the remaining sections in parallel - each is a separate Azure round-trip,
                // so running them concurrently turns ~12 sequential waits into one.
                var latencyTask = SafeAsync(() => GetLatencyAsync(target, timeRange, scope, cancellationToken), new LatencyDistribution(), cancellationToken);
                var bucketsTask = SafeAsync(() => GetLatencyBucketsAsync(target, timeRange, scope, totalCalls, cancellationToken), (IReadOnlyList<NamedCount>)new List<NamedCount>(), cancellationToken);
                var statusTask = SafeAsync(() => GetStatusCodesAsync(target, timeRange, scope, totalCalls, cancellationToken), (IReadOnlyList<StatusCodeStat>)new List<StatusCodeStat>(), cancellationToken);
                var statusEvidenceTask = SafeAsync(() => GetStatusCodeFailureEvidenceAsync(target, timeRange, scope, cancellationToken), (IReadOnlyDictionary<string, StatusCodeFailureEvidence>)new Dictionary<string, StatusCodeFailureEvidence>(), cancellationToken);
                var exceptionsTask = SafeAsync(() => GetExceptionsAsync(target, timeRange, nameLiteral, cancellationToken), (IReadOnlyList<ExceptionStat>)new List<ExceptionStat>(), cancellationToken);
                var dependenciesTask = SafeAsync(() => GetDependenciesAsync(target, timeRange, nameLiteral, cancellationToken), (IReadOnlyList<DependencyStat>)new List<DependencyStat>(), cancellationToken);
                var samplesTask = SafeAsync(() => GetSlowestSamplesAsync(target, timeRange, scope, cancellationToken), (IReadOnlyList<RequestSample>)new List<RequestSample>(), cancellationToken);
                var timelineTask = SafeAsync(() => GetEndpointTimelineAsync(target, timeRange, scope, cancellationToken), (IReadOnlyList<RequestVolumePoint>)new List<RequestVolumePoint>(), cancellationToken);
                var urlsTask = SafeAsync(() => GetBreakdownAsync(target, timeRange, scope, "tostring(url)", totalCalls, cancellationToken), (IReadOnlyList<NamedCount>)new List<NamedCount>(), cancellationToken);
                var rolesTask = SafeAsync(() => GetBreakdownAsync(target, timeRange, scope, "cloud_RoleName", totalCalls, cancellationToken), (IReadOnlyList<NamedCount>)new List<NamedCount>(), cancellationToken);
                var geoTask = SafeAsync(() => GetBreakdownAsync(target, timeRange, scope, "iff(isempty(client_City), tostring(client_CountryOrRegion), iff(isempty(client_CountryOrRegion), tostring(client_City), strcat(client_City, ', ', client_CountryOrRegion)))", totalCalls, cancellationToken), (IReadOnlyList<NamedCount>)new List<NamedCount>(), cancellationToken);
                var propsTask = SafeAsync(() => GetEndpointPropertiesAsync(target, timeRange, scope, cancellationToken), (IReadOnlyList<KeyValueItem>)new List<KeyValueItem>(), cancellationToken);
                var comparisonTask = SafeAsync(() => GetComparisonAsync(target, windowHours, scope, detail.Overview, cancellationToken), (PeriodComparison?)null, cancellationToken);

                await Task.WhenAll(
                    latencyTask, bucketsTask, statusTask, statusEvidenceTask, exceptionsTask, dependenciesTask, samplesTask,
                    timelineTask, urlsTask, rolesTask, geoTask, propsTask, comparisonTask);

                detail.Latency = latencyTask.Result;
                detail.LatencyBuckets = bucketsTask.Result;
                detail.StatusCodes = MergeStatusCodeEvidence(statusTask.Result, statusEvidenceTask.Result);
                detail.Exceptions = exceptionsTask.Result;
                detail.Dependencies = dependenciesTask.Result;
                detail.SlowestSamples = samplesTask.Result;
                detail.Timeline = timelineTask.Result;
                detail.TopUrls = urlsTask.Result;
                detail.Roles = rolesTask.Result;
                detail.ClientGeo = geoTask.Result;
                detail.Properties = propsTask.Result;
                detail.Comparison = comparisonTask.Result;

                detail.Overview.TrafficSharePercent = 0; // not meaningful in single-endpoint scope
                detail.Overview.EstimatedCostUsd = EstimateCost(detail.Overview.Calls);
                detail.Overview.Health = ClassifyHealth(detail.Overview);
                detail.Insight = BuildDeepInsight(detail);
                detail.HasResult = true;

                // Cache only the fully-populated success path for a short window.
                _cache.Set(cacheKey, detail, CacheDuration);
            }
            catch (OperationCanceledException)
            {
                // The client navigated away / cancelled the request - not an error, swallow quietly.
                throw;
            }
            catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException)
            {
                _logger.LogError(ex, "Failed to load endpoint detail for {Operation}.", operationName);
                detail.ErrorMessage = DescribeLiveError(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error loading endpoint detail for {Operation}.", operationName);
                detail.ErrorMessage = "Something went wrong loading this API's telemetry. " + ex.Message;
            }

            return detail;
        }

        /// <summary>
        /// Runs a per-section query, returning a fallback value if it fails so one bad query can't
        /// break the whole detail page. Cancellation is rethrown so the caller can handle it.
        /// </summary>
        private async Task<T> SafeAsync<T>(Func<Task<T>> query, T fallback, CancellationToken cancellationToken)
        {
            try
            {
                return await query();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A detail sub-query failed and was skipped.");
                return fallback;
            }
        }

        private async Task<ApiEndpointStat> GetDetailOverviewAsync(WorkspaceTarget target, QueryTimeRange timeRange, string scope, string operationName, CancellationToken cancellationToken)
        {
            var query = $@"requests{scope}
| summarize
    Calls = count(),
    FailedCalls = countif(success == false),
    AverageDurationMs = avg(duration),
    P95DurationMs = percentile(duration, 95)";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var row = result.Table.Rows.FirstOrDefault();
            var stat = new ApiEndpointStat { Name = operationName };
            if (row is not null)
            {
                stat.Calls = GetInt64(row, "Calls");
                stat.FailedCalls = GetInt64(row, "FailedCalls");
                stat.AverageDurationMs = Math.Round(GetDouble(row, "AverageDurationMs"), 2);
                stat.P95DurationMs = Math.Round(GetDouble(row, "P95DurationMs"), 2);
            }

            return stat;
        }

        private async Task<LatencyDistribution> GetLatencyAsync(WorkspaceTarget target, QueryTimeRange timeRange, string scope, CancellationToken cancellationToken)
        {
            var query = $@"requests{scope}
| summarize
    P50 = percentile(duration, 50),
    P90 = percentile(duration, 90),
    P95 = percentile(duration, 95),
    P99 = percentile(duration, 99),
    Max = max(duration)";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var row = result.Table.Rows.FirstOrDefault();
            var dist = new LatencyDistribution();
            if (row is not null)
            {
                dist.P50 = Math.Round(GetDouble(row, "P50"), 1);
                dist.P90 = Math.Round(GetDouble(row, "P90"), 1);
                dist.P95 = Math.Round(GetDouble(row, "P95"), 1);
                dist.P99 = Math.Round(GetDouble(row, "P99"), 1);
                dist.Max = Math.Round(GetDouble(row, "Max"), 1);
            }

            return dist;
        }

        private async Task<IReadOnlyList<StatusCodeStat>> GetStatusCodesAsync(WorkspaceTarget target, QueryTimeRange timeRange, string scope, long totalCalls, CancellationToken cancellationToken)
        {
            var query = $@"requests{scope}
| summarize Count = count() by ResultCode = tostring(resultCode)
| top 12 by Count desc";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var list = new List<StatusCodeStat>();
            foreach (var row in result.Table.Rows)
            {
                var code = GetString(row, "ResultCode");
                var count = GetInt64(row, "Count");
                list.Add(new StatusCodeStat
                {
                    ResultCode = string.IsNullOrWhiteSpace(code) ? "(none)" : code,
                    Count = count,
                    SharePercent = totalCalls == 0 ? 0 : Math.Round((double)count / totalCalls * 100, 1),
                    IsError = int.TryParse(code, out var c) && c >= 400,
                });
            }

            return list;
        }

        /// <summary>
        /// Gathers real telemetry evidence for each failing status code: the failing request URLs,
        /// the exceptions correlated to those requests (via operation_Id), and any downstream
        /// dependencies that failed on them. This lets the AI pinpoint the actual reason for each
        /// code rather than explain it generically.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, StatusCodeFailureEvidence>> GetStatusCodeFailureEvidenceAsync(WorkspaceTarget target, QueryTimeRange timeRange, string scope, CancellationToken cancellationToken)
        {
            var query = $@"let failed = requests{scope}
| where success == false
| project operation_Id, ResultCode = tostring(resultCode), Url = tostring(url), timestamp;
let exForOp = exceptions
| project operation_Id, ExType = tostring(type), ExMsg = tostring(outerMessage);
let depForOp = dependencies
| where success == false
| project operation_Id, DepName = tostring(name), DepType = tostring(type);
failed
| summarize FailedRequests = count(), LastSeen = max(timestamp), SampleUrls = make_set(Url, 5) by ResultCode
| join kind=leftouter (
    failed
    | join kind=inner exForOp on operation_Id
    | summarize ExceptionTypes = make_set(ExType, 5), ExceptionMessages = make_set(ExMsg, 5) by ResultCode
) on ResultCode
| join kind=leftouter (
    failed
    | join kind=inner depForOp on operation_Id
    | summarize FailingDependencies = make_set(strcat(DepName, ' (', DepType, ')'), 5) by ResultCode
) on ResultCode
| project ResultCode, FailedRequests, LastSeen, SampleUrls, ExceptionTypes, ExceptionMessages, FailingDependencies
| top 12 by FailedRequests desc";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var map = new Dictionary<string, StatusCodeFailureEvidence>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in result.Table.Rows)
            {
                var code = GetString(row, "ResultCode");
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                map[code] = new StatusCodeFailureEvidence
                {
                    FailedRequests = GetInt64(row, "FailedRequests"),
                    LastSeen = GetDateTimeOffset(row, "LastSeen"),
                    SampleUrls = GetStringList(row, "SampleUrls"),
                    ExceptionTypes = GetStringList(row, "ExceptionTypes"),
                    ExceptionMessages = GetStringList(row, "ExceptionMessages"),
                    FailingDependencies = GetStringList(row, "FailingDependencies"),
                };
            }

            return map;
        }

        /// <summary>
        /// Attaches the correlated failure evidence to each error status code so the UI/AI can
        /// pinpoint the actual reason. Successful codes are left untouched.
        /// </summary>
        private static IReadOnlyList<StatusCodeStat> MergeStatusCodeEvidence(
            IReadOnlyList<StatusCodeStat> statusCodes,
            IReadOnlyDictionary<string, StatusCodeFailureEvidence> evidence)
        {
            if (evidence.Count == 0)
            {
                return statusCodes;
            }

            foreach (var status in statusCodes)
            {
                if (status.IsError && evidence.TryGetValue(status.ResultCode, out var ev))
                {
                    status.Evidence = ev;
                }
            }

            return statusCodes;
        }

        private async Task<IReadOnlyList<ExceptionStat>> GetExceptionsAsync(WorkspaceTarget target, QueryTimeRange timeRange, string nameLiteral, CancellationToken cancellationToken)
        {
            // Join exceptions to their parent request operation via operation_Name.
            var query = $@"exceptions
| where operation_Name == '{nameLiteral}'
| summarize Count = count(), LastSeen = max(timestamp), AnyDetails = any(details)
    by Type = type, Method = method, OuterMessage = outerMessage
| top 5 by Count desc";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var list = new List<ExceptionStat>();
            foreach (var row in result.Table.Rows)
            {
                var stat = new ExceptionStat
                {
                    Type = GetString(row, "Type"),
                    Message = GetString(row, "OuterMessage"),
                    Method = GetString(row, "Method"),
                    Count = GetInt64(row, "Count"),
                    LastSeen = GetDateTimeOffset(row, "LastSeen"),
                    StackTrace = ExtractStackTrace(row, "AnyDetails"),
                };
                DescribeException(stat);
                list.Add(stat);
            }

            return list;
        }

        /// <summary>
        /// Maps a raw exception type/message to a friendly category, plain-language explanation and a
        /// suggested next step, so non-experts can understand failures without reading a stack trace.
        /// </summary>
        private static void DescribeException(ExceptionStat e)
        {
            var type = e.Type ?? string.Empty;
            var shortType = type.Contains('.') ? type[(type.LastIndexOf('.') + 1)..] : type;
            var msg = e.Message ?? string.Empty;

            (string category, string explanation, string action) info = shortType switch
            {
                "SqlException" or "DbException" or "DbUpdateException" => (
                    "Database error",
                    "The call to the database failed — this could be a timeout, a constraint violation, a connection drop, or bad SQL.",
                    "Check database availability and connection limits, review the failing query, and confirm the connection string / firewall rules."),
                "TimeoutException" or "TaskCanceledException" or "OperationCanceledException" => (
                    "Timeout / cancelled",
                    "The operation took too long and was cancelled, or a downstream call exceeded its timeout.",
                    "Profile the slow path, increase the timeout if appropriate, add retries with backoff, or cache the slow dependency."),
                "HttpRequestException" or "WebException" or "SocketException" => (
                    "Network / downstream call",
                    "A call to another service or API failed at the network level (DNS, connection refused, TLS, or the remote returned an error).",
                    "Verify the downstream endpoint is healthy and reachable, check TLS/certs and DNS, and add resilient retry/circuit-breaker policies."),
                "NullReferenceException" => (
                    "Null reference",
                    "The code tried to use an object that was null — usually a missing value that wasn't checked before use.",
                    "Add null checks / guard clauses on the highlighted method, and validate inputs and upstream responses."),
                "ArgumentException" or "ArgumentNullException" or "ArgumentOutOfRangeException" => (
                    "Bad argument / input",
                    "A value passed into a method was missing, out of range, or invalid.",
                    "Validate request inputs and parameters before use, and return a clear 400 for invalid client input."),
                "UnauthorizedAccessException" => (
                    "Authorization / permissions",
                    "The code was denied access to a resource — a permission, token, or identity problem.",
                    "Check the identity's roles/permissions, token scopes/expiry, and any file/resource ACLs."),
                "InvalidOperationException" => (
                    "Invalid operation / state",
                    "The code attempted something not valid for the current state (e.g. using a disposed object or a sequence with no elements).",
                    "Review the object lifecycle and state assumptions around the failing method."),
                "JsonException" or "JsonReaderException" or "SerializationException" => (
                    "Serialization error",
                    "Data could not be parsed or serialized — usually a malformed or unexpected payload shape.",
                    "Validate the incoming/outgoing payload schema and handle malformed data gracefully."),
                "KeyNotFoundException" => (
                    "Missing key / lookup failed",
                    "A lookup (dictionary, cache or config) didn't contain the expected key.",
                    "Confirm the key exists before access (TryGetValue) and check configuration/cache population."),
                _ => (
                    "Application exception",
                    "An unhandled exception was thrown while serving this operation.",
                    "Open the stack trace below to find the throwing method, then add handling or fix the root cause."),
            };

            e.FriendlyCategory = info.category;
            e.FriendlyExplanation = string.IsNullOrWhiteSpace(msg)
                ? info.explanation
                : $"{info.explanation} Reported message: \u201c{Truncate(msg, 200)}\u201d.";
            e.SuggestedAction = info.action;

            static string Truncate(string s, int n) => s.Length > n ? s[..n] + "…" : s;
        }

        private async Task<IReadOnlyList<DependencyStat>> GetDependenciesAsync(WorkspaceTarget target, QueryTimeRange timeRange, string nameLiteral, CancellationToken cancellationToken)
        {
            var query = $@"dependencies
| where operation_Name == '{nameLiteral}'
| summarize
    Calls = count(),
    FailedCalls = countif(success == false),
    AverageDurationMs = avg(duration)
    by Name = name, Type = type, Target = target
| top 8 by Calls desc";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var list = new List<DependencyStat>();
            foreach (var row in result.Table.Rows)
            {
                list.Add(new DependencyStat
                {
                    Name = GetString(row, "Name"),
                    Type = GetString(row, "Type"),
                    Target = GetString(row, "Target"),
                    Calls = GetInt64(row, "Calls"),
                    FailedCalls = GetInt64(row, "FailedCalls"),
                    AverageDurationMs = Math.Round(GetDouble(row, "AverageDurationMs"), 2),
                });
            }

            return list;
        }

        private async Task<IReadOnlyList<RequestSample>> GetSlowestSamplesAsync(WorkspaceTarget target, QueryTimeRange timeRange, string scope, CancellationToken cancellationToken)
        {
            var query = $@"requests{scope}
| top 8 by duration desc
| project timestamp, duration, resultCode = tostring(resultCode), success, operation_Id";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var list = new List<RequestSample>();
            foreach (var row in result.Table.Rows)
            {
                list.Add(new RequestSample
                {
                    Timestamp = GetDateTimeOffset(row, "timestamp"),
                    DurationMs = Math.Round(GetDouble(row, "duration"), 1),
                    ResultCode = GetString(row, "resultCode"),
                    Success = GetBool(row, "success"),
                    OperationId = GetString(row, "operation_Id"),
                });
            }

            return list;
        }

        private async Task<IReadOnlyList<NamedCount>> GetLatencyBucketsAsync(WorkspaceTarget target, QueryTimeRange timeRange, string scope, long totalCalls, CancellationToken cancellationToken)
        {
            var query = $@"requests{scope}
| extend Bucket = case(
    duration < 100, '0-100 ms',
    duration < 250, '100-250 ms',
    duration < 500, '250-500 ms',
    duration < 1000, '500ms-1s',
    duration < 3000, '1-3s',
    '3s+')
| summarize Count = count() by Bucket
| order by Count desc";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            return ToNamedCounts(result, "Bucket", "Count", totalCalls);
        }

        private async Task<IReadOnlyList<NamedCount>> GetBreakdownAsync(WorkspaceTarget target, QueryTimeRange timeRange, string scope, string byExpression, long totalCalls, CancellationToken cancellationToken)
        {
            var query = $@"requests{scope}
| summarize Count = count() by Key = {byExpression}
| where isnotempty(Key)
| top 6 by Count desc";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            return ToNamedCounts(result, "Key", "Count", totalCalls);
        }

        private static IReadOnlyList<NamedCount> ToNamedCounts(LogsQueryResult result, string nameColumn, string countColumn, long totalCalls)
        {
            var list = new List<NamedCount>();
            foreach (var row in result.Table.Rows)
            {
                var name = GetString(row, nameColumn);
                var count = GetInt64(row, countColumn);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                list.Add(new NamedCount
                {
                    Name = name,
                    Count = count,
                    SharePercent = totalCalls == 0 ? 0 : Math.Round((double)count / totalCalls * 100, 1),
                });
            }

            return list;
        }

        private async Task<IReadOnlyList<KeyValueItem>> GetEndpointPropertiesAsync(WorkspaceTarget target, QueryTimeRange timeRange, string scope, CancellationToken cancellationToken)
        {
            var query = $@"requests{scope}
| top 1 by timestamp desc
| project cloud_RoleName, cloud_RoleInstance, client_Type, client_OS, client_Browser, application_Version, url, customDimensions = tostring(customDimensions)";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var row = result.Table.Rows.FirstOrDefault();
            var list = new List<KeyValueItem>();
            if (row is null)
            {
                return list;
            }

            void Add(string key, string column)
            {
                var value = GetString(row, column);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    list.Add(new KeyValueItem { Key = key, Value = value });
                }
            }

            Add("Cloud role", "cloud_RoleName");
            Add("Role instance", "cloud_RoleInstance");
            Add("Client type", "client_Type");
            Add("Client OS", "client_OS");
            Add("Browser", "client_Browser");
            Add("App version", "application_Version");
            Add("Sample URL", "url");

            var custom = GetString(row, "customDimensions");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(custom);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject().Take(15))
                        {
                            list.Add(new KeyValueItem { Key = prop.Name, Value = prop.Value.ToString() });
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Ignore malformed custom dimensions.
                }
            }

            return list;
        }

        private async Task<PeriodComparison?> GetComparisonAsync(WorkspaceTarget target, int windowHours, string scope, ApiEndpointStat current, CancellationToken cancellationToken)
        {
            // Query the immediately preceding window of the same length for a trend comparison.
            var previousRange = new QueryTimeRange(
                DateTimeOffset.UtcNow.AddHours(-2 * windowHours),
                DateTimeOffset.UtcNow.AddHours(-windowHours));

            var query = $@"requests{scope}
| summarize Calls = count(), FailedCalls = countif(success == false), AverageDurationMs = avg(duration)";

            try
            {
                var result = await RunQueryAsync(target, query, previousRange, cancellationToken);
                var row = result.Table.Rows.FirstOrDefault();
                if (row is null)
                {
                    return null;
                }

                var prevCalls = GetInt64(row, "Calls");
                var prevFailed = GetInt64(row, "FailedCalls");
                var prevAvg = Math.Round(GetDouble(row, "AverageDurationMs"), 1);

                return new PeriodComparison
                {
                    PreviousCalls = prevCalls,
                    PreviousSuccessRate = prevCalls == 0 ? 0 : Math.Round((double)(prevCalls - prevFailed) / prevCalls * 100, 2),
                    PreviousAvgDurationMs = prevAvg,
                    CurrentCalls = current.Calls,
                    CurrentSuccessRate = current.SuccessRate,
                    CurrentAvgDurationMs = current.AverageDurationMs,
                };
            }
            catch (RequestFailedException)
            {
                return null; // comparison is best-effort
            }
        }

        private async Task<IReadOnlyList<RequestVolumePoint>> GetEndpointTimelineAsync(WorkspaceTarget target, QueryTimeRange timeRange, string scope, CancellationToken cancellationToken)
        {
            var query = $@"requests{scope}
| summarize Calls = count(), AverageDurationMs = avg(duration) by Timestamp = bin(timestamp, 1h)
| order by Timestamp asc";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var list = new List<RequestVolumePoint>();
            foreach (var row in result.Table.Rows)
            {
                list.Add(new RequestVolumePoint
                {
                    Timestamp = GetDateTimeOffset(row, "Timestamp"),
                    Calls = GetInt64(row, "Calls"),
                    AverageDurationMs = Math.Round(GetDouble(row, "AverageDurationMs"), 1),
                });
            }

            return list;
        }

        /// <summary>
        /// Produces a richer, multi-paragraph AI analysis using the deep telemetry: exceptions,
        /// dependencies, status codes and latency tail. Self-contained heuristic engine.
        /// </summary>
        private static AiInsight BuildDeepInsight(EndpointDetail detail)
        {
            var o = detail.Overview;
            var baseInsight = BuildInsight(o);
            var suggestions = new List<string>(baseInsight.Suggestions);
            var risks = new List<string>();

            // Latency tail analysis.
            if (detail.Latency.P99 >= detail.Latency.P50 * 4 && detail.Latency.P50 > 0)
            {
                risks.Add($"Severe long tail: P99 ({detail.Latency.P99:N0} ms) is {detail.Latency.P99 / Math.Max(detail.Latency.P50, 1):N0}x the median ({detail.Latency.P50:N0} ms).");
            }

            // Dominant exception.
            var topException = detail.Exceptions.FirstOrDefault();
            if (topException is not null)
            {
                risks.Add($"Top exception: {topException.Type} ({topException.Count:N0}x){(string.IsNullOrWhiteSpace(topException.Method) ? "" : $" in {topException.Method}")}.");
                suggestions.Add($"Investigate {topException.Type}: add a try/catch or guard around {(string.IsNullOrWhiteSpace(topException.Method) ? "the failing call" : topException.Method)}, and validate inputs/dependencies.");
            }

            // Slow / failing dependency.
            var slowDep = detail.Dependencies.OrderByDescending(d => d.AverageDurationMs).FirstOrDefault();
            if (slowDep is not null && slowDep.AverageDurationMs >= 300)
            {
                risks.Add($"Dependency '{slowDep.Name}' averages {slowDep.AverageDurationMs:N0} ms ({slowDep.SuccessRate:0.#}% success).");
                suggestions.Add($"The '{slowDep.Name}' {slowDep.Type} dependency is slow — add caching, a timeout/retry policy, or batch its calls.");
            }

            var failingDep = detail.Dependencies.FirstOrDefault(d => d.SuccessRate < 99 && d.Calls > 0);
            if (failingDep is not null)
            {
                risks.Add($"Dependency '{failingDep.Name}' is failing ({(100 - failingDep.SuccessRate):0.#}% errors).");
            }

            // Server errors.
            var serverErrors = detail.StatusCodes.Where(s => int.TryParse(s.ResultCode, out var c) && c >= 500).Sum(s => s.Count);
            if (serverErrors > 0)
            {
                risks.Add($"{serverErrors:N0} server-side (5xx) responses in this window.");
            }

            // Traffic concentration on a single URL.
            var topUrl = detail.TopUrls.FirstOrDefault();
            if (topUrl is not null && topUrl.SharePercent >= 80 && detail.TopUrls.Count > 1)
            {
                suggestions.Add($"{topUrl.SharePercent:0.#}% of calls hit a single URL variant — caching or CDN at that path could yield outsized wins.");
            }

            var analysis =
                $"Over the last {detail.WindowHours}h this operation served {o.Calls:N0} requests " +
                $"with a {o.SuccessRate:0.#}% success rate. Latency runs {detail.Latency.P50:N0} ms median, " +
                $"{detail.Latency.P95:N0} ms P95 and {detail.Latency.P99:N0} ms P99 (max {detail.Latency.Max:N0} ms). " +
                (detail.Exceptions.Count > 0
                    ? $"{detail.Exceptions.Sum(e => e.Count):N0} exceptions were recorded across {detail.Exceptions.Count} type(s). "
                    : "No exceptions were recorded. ") +
                (detail.Dependencies.Count > 0
                    ? $"It calls {detail.Dependencies.Count} downstream dependency(ies)."
                    : "No downstream dependencies were observed.");

            if (risks.Count == 0)
            {
                risks.Add("No significant risks detected in this window.");
            }

            return new AiInsight
            {
                Summary = baseInsight.Summary,
                Analysis = analysis,
                Suggestions = suggestions.Distinct().ToList(),
                Risks = risks,
                Score = baseInsight.Score,
            };
        }

        private static string EscapeKqlString(string value) =>
            value.Replace("\\", "\\\\").Replace("'", "\\'");

        private static string? ExtractStackTrace(LogsTableRow row, string column)
        {
            // exceptions.details is a dynamic array; surface the first parsedStack / rawStack we find.
            string? raw;
            try
            {
                raw = row[column]?.ToString();
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var first = root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() > 0
                    ? root[0]
                    : root;

                if (first.TryGetProperty("rawStack", out var rawStack) && rawStack.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return Trim(rawStack.GetString());
                }

                if (first.TryGetProperty("parsedStack", out var parsed) && parsed.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var frames = parsed.EnumerateArray()
                        .Take(20)
                        .Select(f =>
                        {
                            var method = f.TryGetProperty("method", out var m) ? m.GetString() : null;
                            var asm = f.TryGetProperty("assembly", out var a) ? a.GetString() : null;
                            var line = f.TryGetProperty("line", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Number ? l.GetInt32() : 0;
                            var loc = line > 0 ? $":{line}" : string.Empty;
                            return $"   at {method}{loc}" + (string.IsNullOrEmpty(asm) ? string.Empty : $"  [{asm}]");
                        });
                    var joined = string.Join("\n", frames);
                    return string.IsNullOrWhiteSpace(joined) ? Trim(raw) : joined;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON - return the raw text trimmed.
            }

            return Trim(raw);

            static string? Trim(string? s) =>
                string.IsNullOrEmpty(s) ? s : (s.Length > 4000 ? s[..4000] + "\n…(truncated)" : s);
        }

        /// <summary>
        /// Validates and classifies the identifier the user supplied. Accepts a Workspace ID (GUID)
        /// or a full Azure resource ID (/subscriptions/...). Anything else is rejected with guidance.
        /// </summary>
        private static bool TryResolveWorkspaceTarget(string input, out WorkspaceTarget target, out string? error)
        {
            target = default;
            error = null;

            if (Guid.TryParse(input, out _))
            {
                target = WorkspaceTarget.FromWorkspaceId(input);
                return true;
            }

            if (input.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    target = WorkspaceTarget.FromResourceId(new ResourceIdentifier(input));
                    return true;
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    error = "That looks like an Azure resource ID but it could not be parsed. " +
                            "Copy the 'id' value from the resource's JSON View in the Azure portal.";
                    return false;
                }
            }

            if (input.StartsWith("DefaultWorkspace-", StringComparison.OrdinalIgnoreCase))
            {
                error = "That value is the workspace resource name, not its Workspace ID. " +
                        "Open the Log Analytics workspace -> Overview -> copy the 'Workspace ID' " +
                        "(a GUID like 00000000-0000-0000-0000-000000000000).";
                return false;
            }

            if (input.Contains('=') || input.Contains("InstrumentationKey", StringComparison.OrdinalIgnoreCase))
            {
                error = "That looks like an instrumentation key / connection string, which can only SEND " +
                        "telemetry - it cannot be used to query it. Paste the Workspace ID (GUID) from the " +
                        "Application Insights / Log Analytics Overview blade instead.";
                return false;
            }

            error = "Enter a valid Workspace ID (a GUID) or a full Azure resource ID starting with " +
                    "'/subscriptions/'. You can find the Workspace ID on the resource's Overview blade.";
            return false;
        }

        private static string DescribeLiveError(Exception ex)
        {
            var firstLine = ex.Message.Split('\n')[0].Trim();

            if (ex is RequestFailedException rfe)
            {
                var hint = rfe.Status switch
                {
                    401 or 403 => " Your identity may lack the 'Log Analytics Reader' / 'Monitoring Reader' role on this resource.",
                    404 => " The resource could not be found - double-check the Workspace ID (GUID) is correct.",
                    _ => string.Empty,
                };
                return $"Live query failed ({firstLine}).{hint}";
            }

            if (ex is AuthenticationFailedException)
            {
                return $"Authentication failed ({firstLine}). Sign in with an account that can read this resource " +
                       "(e.g. via Visual Studio / Azure CLI) or configure a managed identity.";
            }

            return $"Live query failed ({firstLine}).";
        }

        private async Task<ApiAnalyticsResult> QueryLiveAsync(WorkspaceTarget target, int windowHours, string? apiFilter, string? source, QueryTimeRange timeRange, CancellationToken cancellationToken)
        {
            var filterClause = BuildFilterClause(apiFilter, source);

            // Run the independent queries concurrently instead of sequentially.
            var summaryTask = GetSummaryAsync(target, timeRange, filterClause, cancellationToken);
            var endpointsTask = GetEndpointsAsync(target, timeRange, filterClause, cancellationToken);
            var timelineTask = GetTimelineAsync(target, timeRange, filterClause, cancellationToken);
            // The Source dropdown lists roles available under the OTHER active filters (not the source
            // itself), so the user can freely switch between sources. Best-effort: empty on failure.
            var sourcesTask = SafeAsync(() => GetAvailableSourcesAsync(target, timeRange, BuildFilterClause(apiFilter, null), cancellationToken),
                (IReadOnlyList<string>)new List<string>(), cancellationToken);

            await Task.WhenAll(summaryTask, endpointsTask, timelineTask, sourcesTask);

            var summary = summaryTask.Result;
            var endpoints = endpointsTask.Result;
            var timeline = timelineTask.Result;

            summary.EstimatedCostUsd = EstimateCost(summary.TotalCalls);

            return new ApiAnalyticsResult
            {
                Summary = summary,
                Endpoints = endpoints,
                Timeline = timeline,
                AvailableSources = sourcesTask.Result,
                HasResult = true,
                WindowHours = windowHours,
                WorkspaceId = target.Display,
                ApiFilter = apiFilter,
                Source = source,
                GeneratedAt = DateTimeOffset.UtcNow,
            };
        }

        /// <summary>
        /// Returns the distinct telemetry sources (Application Insights roles, i.e. cloud_RoleName)
        /// present in the window, so the UI can offer them as a Source filter dropdown.
        /// </summary>
        private async Task<IReadOnlyList<string>> GetAvailableSourcesAsync(WorkspaceTarget target, QueryTimeRange timeRange, string filterClause, CancellationToken cancellationToken)
        {
            var query = $@"requests{filterClause}
| where isnotempty(cloud_RoleName)
| summarize Count = count() by Source = cloud_RoleName
| top 50 by Count desc
| project Source";

            var result = await RunQueryAsync(target, query, timeRange, cancellationToken);
            var sources = new List<string>();
            foreach (var row in result.Table.Rows)
            {
                var name = GetString(row, "Source");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    sources.Add(name);
                }
            }

            return sources;
        }

        /// <summary>
        /// Runs a KQL query against either a workspace (by GUID) or an Azure resource (by resource ID),
        /// depending on what the user supplied.
        /// </summary>
        private async Task<LogsQueryResult> RunQueryAsync(WorkspaceTarget target, string query, QueryTimeRange timeRange, CancellationToken cancellationToken)
        {
            Response<LogsQueryResult> response = target.ResourceId is null
                ? await _client.QueryWorkspaceAsync(target.WorkspaceId!, query, timeRange, cancellationToken: cancellationToken)
                : await _client.QueryResourceAsync(target.ResourceId, query, timeRange, cancellationToken: cancellationToken);

            return response.Value;
        }

        /// <summary>
        /// Builds a KQL filter pipeline segment that limits results to a specific API/operation.
        /// Single quotes and backslashes are escaped to avoid breaking the query.
        /// </summary>
        private static string BuildFilterClause(string? apiFilter, string? source)
        {
            var predicates = new List<string>();

            var normalized = NormalizeApiFilter(apiFilter);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                var escaped = normalized.Replace("\\", "\\\\").Replace("'", "\\'");
                // Match against the operation name, the dependency-style name, AND the raw URL so a
                // pasted request URL still resolves to the right operation.
                predicates.Add($"(name contains '{escaped}' or url contains '{escaped}' or operation_Name contains '{escaped}')");
            }

            // Source = Application Insights role (cloud_RoleName). Supports one or many roles
            // (comma-separated). Exact match per role, like App Insights search.
            if (!string.IsNullOrWhiteSpace(source))
            {
                var roles = source
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(r => "'" + r.Replace("\\", "\\\\").Replace("'", "\\'") + "'")
                    .ToList();

                if (roles.Count == 1)
                {
                    predicates.Add($"cloud_RoleName == {roles[0]}");
                }
                else if (roles.Count > 1)
                {
                    predicates.Add($"cloud_RoleName in ({string.Join(", ", roles)})");
                }
            }

            return predicates.Count == 0
                ? string.Empty
                : "\n| where " + string.Join(" and ", predicates);
        }

        /// <summary>
        /// Cleans a user-supplied API filter so a pasted request URL still matches telemetry.
        /// Strips <c>{{tokens}}</c>, protocol/host, query string and fragments, leaving the most
        /// distinctive path segment (e.g. "/sessions/list" from
        /// "{{base_url}}/sessions/list?masterEventId={{prod_event}}").
        /// </summary>
        internal static string? NormalizeApiFilter(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var value = raw.Trim();

            // Remove Postman/templating tokens like {{base_url}} or :id placeholders.
            value = System.Text.RegularExpressions.Regex.Replace(value, @"\{\{[^}]*\}\}", string.Empty);

            // Drop query string and fragment.
            var queryIndex = value.IndexOfAny(new[] { '?', '#' });
            if (queryIndex >= 0)
            {
                value = value[..queryIndex];
            }

            // Strip protocol + host if a full/partial URL was pasted.
            value = System.Text.RegularExpressions.Regex.Replace(value, @"^[a-zA-Z][a-zA-Z0-9+.-]*://", string.Empty);
            var firstSlash = value.IndexOf('/');
            if (firstSlash > 0 && (value[..firstSlash].Contains('.') || value[..firstSlash].Contains(':')))
            {
                // Looks like "host[:port]/path" - keep from the first slash (the path).
                value = value[firstSlash..];
            }

            // Collapse duplicate slashes left by removed tokens and trim trailing slash.
            value = System.Text.RegularExpressions.Regex.Replace(value, "/{2,}", "/").Trim();
            if (value.Length > 1)
            {
                value = value.TrimEnd('/');
            }

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private async Task<ApiAnalyticsSummary> GetSummaryAsync(WorkspaceTarget target, QueryTimeRange timeRange, string filterClause, CancellationToken cancellationToken)
        {
            var query = $@"requests{filterClause}
| summarize
    TotalCalls = count(),
    FailedCalls = countif(success == false),
    AverageDurationMs = avg(duration),
    P95DurationMs = percentile(duration, 95)";

            LogsQueryResult result = await RunQueryAsync(target, query, timeRange, cancellationToken);

            var summary = new ApiAnalyticsSummary();
            var row = result.Table.Rows.FirstOrDefault();
            if (row is not null)
            {
                summary.TotalCalls = GetInt64(row, "TotalCalls");
                summary.FailedCalls = GetInt64(row, "FailedCalls");
                summary.AverageDurationMs = Math.Round(GetDouble(row, "AverageDurationMs"), 2);
                summary.P95DurationMs = Math.Round(GetDouble(row, "P95DurationMs"), 2);
            }

            return summary;
        }

        private async Task<IReadOnlyList<ApiEndpointStat>> GetEndpointsAsync(WorkspaceTarget target, QueryTimeRange timeRange, string filterClause, CancellationToken cancellationToken)
        {
            var query = $@"requests{filterClause}
| summarize
    Calls = count(),
    FailedCalls = countif(success == false),
    AverageDurationMs = avg(duration),
    P95DurationMs = percentile(duration, 95)
    by Name = name
| top 15 by Calls desc";

            LogsQueryResult result = await RunQueryAsync(target, query, timeRange, cancellationToken);

            var endpoints = new List<ApiEndpointStat>();
            foreach (var row in result.Table.Rows)
            {
                endpoints.Add(new ApiEndpointStat
                {
                    Name = GetString(row, "Name"),
                    Calls = GetInt64(row, "Calls"),
                    FailedCalls = GetInt64(row, "FailedCalls"),
                    AverageDurationMs = Math.Round(GetDouble(row, "AverageDurationMs"), 2),
                    P95DurationMs = Math.Round(GetDouble(row, "P95DurationMs"), 2),
                });
            }

            EnrichEndpoints(endpoints);
            return endpoints;
        }

        /// <summary>
        /// Fills in derived per-endpoint fields (traffic share, estimated cost, health) and a
        /// heuristic AI overview so the detail view has something rich to show.
        /// </summary>
        private void EnrichEndpoints(List<ApiEndpointStat> endpoints)
        {
            var totalCalls = endpoints.Sum(e => e.Calls);

            foreach (var endpoint in endpoints)
            {
                endpoint.TrafficSharePercent = totalCalls == 0
                    ? 0
                    : Math.Round((double)endpoint.Calls / totalCalls * 100, 1);
                endpoint.EstimatedCostUsd = EstimateCost(endpoint.Calls);
                endpoint.Health = ClassifyHealth(endpoint);
                endpoint.Insight = BuildInsight(endpoint);
            }
        }

        private static EndpointHealth ClassifyHealth(ApiEndpointStat e)
        {
            if (e.SuccessRate < 95 || e.P95DurationMs >= 1500)
            {
                return EndpointHealth.Degraded;
            }

            if (e.SuccessRate < 99 || e.AverageDurationMs >= 500 || e.P95DurationMs >= 800)
            {
                return EndpointHealth.Watch;
            }

            return EndpointHealth.Healthy;
        }

        /// <summary>
        /// Generates a lightweight, rule-based "AI" overview for an endpoint. Self-contained so the
        /// prototype works offline; swap this for a real LLM call later.
        /// </summary>
        private static AiInsight BuildInsight(ApiEndpointStat e)
        {
            var suggestions = new List<string>();

            if (e.SuccessRate < 99)
            {
                suggestions.Add($"Error rate is {(100 - e.SuccessRate):0.#}% — inspect failed requests for a common exception, status code or downstream dependency.");
            }

            if (e.P95DurationMs >= 1000)
            {
                suggestions.Add($"P95 latency is high ({e.P95DurationMs:N0} ms). Profile the slow path, add caching, or parallelize downstream calls.");
            }
            else if (e.P95DurationMs >= e.AverageDurationMs * 3 && e.AverageDurationMs > 0)
            {
                suggestions.Add("P95 is far above the average — a long tail suggests intermittent slowdowns (cold starts, lock contention or GC pauses).");
            }

            if (e.AverageDurationMs >= 400)
            {
                suggestions.Add($"Average response is {e.AverageDurationMs:N0} ms; consider async I/O, response compression or an output cache.");
            }

            if (e.TrafficSharePercent >= 40)
            {
                suggestions.Add($"This endpoint drives {e.TrafficSharePercent:0.#}% of traffic — a hotspot worth load-testing and rate-limiting.");
            }

            if (suggestions.Count == 0)
            {
                suggestions.Add("Healthy: latency and success rate are within good ranges. Keep an eye on traffic growth.");
            }

            // Score: start at 100, subtract for failures and latency.
            var score = 100.0;
            score -= (100 - e.SuccessRate) * 4;                       // reliability weighs heavily
            score -= Math.Min(30, e.AverageDurationMs / 40.0);       // up to -30 for avg latency
            score -= Math.Min(25, e.P95DurationMs / 80.0);           // up to -25 for tail latency
            var finalScore = (int)Math.Clamp(Math.Round(score), 0, 100);

            var health = ClassifyHealth(e);
            var headline = health switch
            {
                EndpointHealth.Degraded => "Needs attention",
                EndpointHealth.Watch => "Watch closely",
                _ => "Looking healthy",
            };

            var summary = $"{headline}: {e.Calls:N0} calls at {e.AverageDurationMs:N0} ms avg " +
                          $"({e.P95DurationMs:N0} ms P95), {e.SuccessRate:0.#}% success.";

            return new AiInsight
            {
                Summary = summary,
                Suggestions = suggestions,
                Score = finalScore,
            };
        }

        private async Task<IReadOnlyList<RequestVolumePoint>> GetTimelineAsync(WorkspaceTarget target, QueryTimeRange timeRange, string filterClause, CancellationToken cancellationToken)
        {
            var query = $@"requests{filterClause}
| summarize
    Calls = count(),
    AverageDurationMs = avg(duration)
    by Timestamp = bin(timestamp, 1h)
| order by Timestamp asc";

            LogsQueryResult result = await RunQueryAsync(target, query, timeRange, cancellationToken);

            var timeline = new List<RequestVolumePoint>();
            foreach (var row in result.Table.Rows)
            {
                timeline.Add(new RequestVolumePoint
                {
                    Timestamp = GetDateTimeOffset(row, "Timestamp"),
                    Calls = GetInt64(row, "Calls"),
                    AverageDurationMs = Math.Round(GetDouble(row, "AverageDurationMs"), 2),
                });
            }

            return timeline;
        }

        private double EstimateCost(long totalCalls) =>
            Math.Round(totalCalls / 10_000d * _options.CostPerTenThousandCalls, 2);

        private static string GetString(LogsTableRow row, string column)
        {
            try
            {
                return row[column]?.ToString() ?? string.Empty;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Reads a KQL dynamic array column (e.g. from <c>make_set</c>) into a list of non-empty,
        /// trimmed strings. Tolerates a non-array scalar or missing column by returning an empty list.
        /// </summary>
        private static List<string> GetStringList(LogsTableRow row, string column)
        {
            var list = new List<string>();
            string? raw;
            try
            {
                raw = row[column]?.ToString();
            }
            catch (ArgumentException)
            {
                return list;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return list;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var value = item.ValueKind == System.Text.Json.JsonValueKind.String
                            ? item.GetString()
                            : item.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            list.Add(value.Trim());
                        }
                    }
                }
                else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var value = doc.RootElement.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        list.Add(value.Trim());
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON - treat the raw value as a single entry.
                list.Add(raw.Trim());
            }

            return list;
        }

        private static long GetInt64(LogsTableRow row, string column)
        {
            try
            {
                var value = row[column];
                return value is null ? 0 : Convert.ToInt64(value);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException)
            {
                return 0;
            }
        }

        private static double GetDouble(LogsTableRow row, string column)
        {
            try
            {
                var value = row[column];
                if (value is null)
                {
                    return 0;
                }

                var d = Convert.ToDouble(value);
                // KQL aggregates (avg/percentile) over sparse/empty data can yield NaN or Infinity,
                // which System.Text.Json refuses to serialize. Normalize them to 0.
                return double.IsNaN(d) || double.IsInfinity(d) ? 0 : d;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException)
            {
                return 0;
            }
        }

        private static bool GetBool(LogsTableRow row, string column)
        {
            try
            {
                var value = row[column];
                return value switch
                {
                    null => false,
                    bool b => b,
                    string s => bool.TryParse(s, out var r) && r,
                    _ => Convert.ToBoolean(value),
                };
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException)
            {
                return false;
            }
        }

        private static DateTimeOffset GetDateTimeOffset(LogsTableRow row, string column)
        {
            try
            {
                var value = row[column];
                return value switch
                {
                    DateTimeOffset dto => dto,
                    DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
                    _ => DateTimeOffset.UtcNow,
                };
            }
            catch (ArgumentException)
            {
                return DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// Identifies what to query: either a Log Analytics Workspace ID (GUID) or a full
        /// Azure resource ID. Exactly one of <see cref="WorkspaceId"/> / <see cref="ResourceId"/> is set.
        /// </summary>
        private readonly struct WorkspaceTarget
        {
            private WorkspaceTarget(string? workspaceId, ResourceIdentifier? resourceId, string display)
            {
                WorkspaceId = workspaceId;
                ResourceId = resourceId;
                Display = display;
            }

            public string? WorkspaceId { get; }

            public ResourceIdentifier? ResourceId { get; }

            public string Display { get; }

            public static WorkspaceTarget FromWorkspaceId(string workspaceId) =>
                new(workspaceId, null, workspaceId);

            public static WorkspaceTarget FromResourceId(ResourceIdentifier resourceId) =>
                new(null, resourceId, resourceId.Name);
        }
    }
}
