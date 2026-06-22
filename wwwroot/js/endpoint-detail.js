(function () {
    "use strict";

    document.addEventListener("DOMContentLoaded", function () {
        var root = document.querySelector(".api-analyzer[data-operation]");
        if (!root) {
            return;
        }

        var operation = root.getAttribute("data-operation") || "";
        var workspaceId = root.getAttribute("data-workspace") || "";
        var windowHours = root.getAttribute("data-window") || "24";
        var customStart = root.getAttribute("data-custom-start") || "";
        var customEnd = root.getAttribute("data-custom-end") || "";
        var hasCustomRange = customStart !== "" && customEnd !== "";

        var loading = document.getElementById("detailLoading");
        var content = document.getElementById("detailContent");
        var errorEl = document.getElementById("detailError");

        if (!operation) {
            showError("No operation was specified.");
            return;
        }

        var params = new URLSearchParams({
            operation: operation,
            WorkspaceId: workspaceId,
            WindowHours: windowHours
        });
        if (hasCustomRange) {
            params.set("CustomStart", customStart);
            params.set("CustomEnd", customEnd);
        }

        // Wire the time-range picker immediately so it works even while data is still loading.
        wireTimeRangePicker();

        fetch("/EndpointDetail/Data?" + params.toString(), { headers: { "Accept": "application/json" } })
            .then(function (r) {
                if (r.status === 499) { return null; } // cancelled by navigation - ignore quietly
                if (!r.ok) { throw new Error("the server returned status " + r.status + "."); }
                return r.json();
            })
            .then(function (detail) {
                if (detail === null) { return; }
                if (!detail || !detail.hasResult) {
                    showError(detail && detail.errorMessage ? detail.errorMessage : "No telemetry found for this API in the selected window.");
                    return;
                }
                render(detail);
                loading.hidden = true;
                content.hidden = false;
                wireHeaderActions();
            })
            .catch(function (err) { showError("Could not load telemetry — " + err.message + " Try a shorter time window or check the Workspace ID."); });

        // Reveal and wire the header "Copy link" and "Export CSV" actions once data has loaded.
        function wireHeaderActions() {
            var exportBtn = document.getElementById("exportCsvBtn");
            if (exportBtn) {
                exportBtn.hidden = false;
                exportBtn.addEventListener("click", function () {
                    var query = new URLSearchParams({
                        operation: operation,
                        WorkspaceId: workspaceId,
                        WindowHours: windowHours
                    });
                    if (hasCustomRange) {
                        query.set("CustomStart", customStart);
                        query.set("CustomEnd", customEnd);
                    }
                    window.location.href = "/EndpointDetail/ExportCsv?" + query.toString();
                });
            }

            var copyBtn = document.getElementById("copyLinkBtn");
            if (copyBtn) {
                copyBtn.hidden = false;
                copyBtn.addEventListener("click", function () {
                    var url = window.location.href;
                    var done = function () {
                        var original = copyBtn.innerHTML;
                        copyBtn.innerHTML = "✓ Copied";
                        setTimeout(function () { copyBtn.innerHTML = original; }, 1500);
                    };
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(url).then(done).catch(function () { fallbackCopy(url, done); });
                    } else {
                        fallbackCopy(url, done);
                    }
                });
            }
        }

        // Clipboard fallback for non-secure contexts or older browsers.
        function fallbackCopy(text, done) {
            var ta = document.createElement("textarea");
            ta.value = text;
            ta.style.position = "fixed";
            ta.style.opacity = "0";
            document.body.appendChild(ta);
            ta.select();
            try { document.execCommand("copy"); done(); } catch (e) { /* ignore */ }
            document.body.removeChild(ta);
        }

        // Wires the shared time-range picker. Applying navigates (full reload) so the one-shot
        // loader repopulates cleanly — the detail renderer only reveals sections, never re-hides them.
        function wireTimeRangePicker() {
            var btn = document.getElementById("timeRangeBtn");
            var popover = document.getElementById("timeRangePopover");
            if (!btn || !popover || !window.TimeRangePicker) { return; }

            window.TimeRangePicker.attach({
                button: btn,
                popover: popover,
                activeHours: parseInt(windowHours, 10),
                customStart: hasCustomRange ? customStart : null,
                customEnd: hasCustomRange ? customEnd : null,
                onPreset: function (hours) { navigate({ WindowHours: String(hours) }); },
                onApply: function (startDate, endDate) {
                    navigate({ CustomStart: startDate.toISOString(), CustomEnd: endDate.toISOString() });
                }
            });
        }

        // Navigate to the detail page with a new range, preserving operation + workspace.
        function navigate(extra) {
            var query = new URLSearchParams({
                Operation: operation,
                WorkspaceId: workspaceId,
                WindowHours: windowHours
            });
            Object.keys(extra).forEach(function (k) { query.set(k, extra[k]); });
            // A preset overrides any prior custom range, so drop the old custom params.
            if (extra.WindowHours) { query.delete("CustomStart"); query.delete("CustomEnd"); }
            window.location.href = "/EndpointDetail/Index?" + query.toString();
        }

        function showError(message) {
            loading.hidden = true;
            content.hidden = true;
            errorEl.textContent = message;
            errorEl.hidden = false;
        }

        function render(detail) {
            var o = detail.overview || {};
            var insight = detail.insight || {};

            document.getElementById("detailTitle").textContent = detail.operationName || operation;

            var health = String(o.health || "Healthy").toLowerCase();
            var healthEl = document.getElementById("detailHealth");
            healthEl.textContent = health.charAt(0).toUpperCase() + health.slice(1);
            healthEl.className = "api-analyzer__health api-modal__health--" + health;

            // Metric cards
            document.getElementById("detailMetrics").innerHTML =
                card("Total Calls", formatNumber(o.calls)) +
                card("Success Rate", oneDecimal(o.successRate) + "%", "success") +
                card("Failed", formatNumber(o.failedCalls)) +
                card("Avg Response", formatNumber(o.averageDurationMs) + " ms", "accent") +
                card("Slow Requests", formatNumber(o.p95DurationMs) + " ms") +
                costCard(o.estimatedCostUsd);

            // The Est. Cost card opens a detailed cost-analysis popup.
            wireCostCard(detail);

            // Overview
            document.getElementById("detailScore").innerHTML =
                "Health score " + (insight.score != null ? insight.score : "-") + "<span> / 100</span>";
            renderOverviewTable(detail, insight);
            fillList("detailSuggestions", insight.suggestions);

            // Response speed — friendly summary instead of raw percentiles
            var lat = detail.latency || {};
            document.getElementById("detailLatency").innerHTML =
                friendlyLatency("Typical", lat.p50, "What most requests feel") +
                friendlyLatency("Slow requests", lat.p95, "1 in 20 are slower than this") +
                friendlyLatency("Worst case", lat.max, "The single slowest request");
            document.getElementById("detailBuckets").innerHTML = barList(detail.latencyBuckets);
            show("detailLatencySection");

            // Status codes
            var statuses = detail.statusCodes || [];
            if (statuses.length) {
                document.getElementById("detailStatus").innerHTML = statuses.map(function (s) {
                    return '<span class="statuscode ' + (s.isError ? "statuscode--error" : "statuscode--ok") + '">' +
                        escapeHtml(s.resultCode) + ' <b>' + formatNumber(s.count) + '</b> <i>' + oneDecimal(s.sharePercent) + '%</i></span>';
                }).join("");
                show("detailStatusSection");
            }

            // Per-status-code failure analysis (one analyzable card per failing code)
            var failing = statuses.filter(function (s) { return s.isError; });
            if (failing.length) {
                document.getElementById("detailFailures").innerHTML = failing.map(renderFailureCard).join("");
                show("detailFailuresSection");
                wireFailureAnalysis();
            }

            // Exceptions (friendly + technical)
            var exceptions = detail.exceptions || [];
            if (exceptions.length) {
                document.getElementById("detailExceptions").innerHTML = exceptions.map(renderException).join("");
                show("detailExceptionsSection");
                wireExceptionToggles();
            }

            // Dependencies
            var deps = detail.dependencies || [];
            if (deps.length) {
                document.getElementById("detailDeps").innerHTML =
                    '<thead><tr><th>Dependency</th><th>Type</th><th class="num">Calls</th><th class="num">Avg</th><th class="num">Success</th></tr></thead><tbody>' +
                    deps.map(function (d) {
                        return '<tr><td>' + escapeHtml(d.name) + '</td><td>' + escapeHtml(d.type || "-") + '</td>' +
                            '<td class="num">' + formatNumber(d.calls) + '</td>' +
                            '<td class="num">' + formatNumber(d.averageDurationMs) + ' ms</td>' +
                            '<td class="num">' + oneDecimal(d.successRate) + '%</td></tr>';
                    }).join("") + '</tbody>';
                show("detailDepsSection");
            }

            // Slowest samples
            var samples = detail.slowestSamples || [];
            if (samples.length) {
                document.getElementById("detailSamples").innerHTML =
                    '<thead><tr><th>Time</th><th class="num">Duration</th><th>Result</th></tr></thead><tbody>' +
                    samples.map(function (s) {
                        var when = s.timestamp ? new Date(s.timestamp).toLocaleString() : "-";
                        var cls = s.success ? "" : ' class="statuscode--error"';
                        return '<tr><td>' + escapeHtml(when) + '</td><td class="num">' + formatNumber(s.durationMs) + ' ms</td>' +
                            '<td' + cls + '>' + escapeHtml(s.resultCode || "-") + '</td></tr>';
                    }).join("") + '</tbody>';
                show("detailSamplesSection");
            }

            // Breakdowns
            if ((detail.topUrls || []).length) { document.getElementById("detailUrls").innerHTML = barList(detail.topUrls); show("detailUrlsSection"); }
            if ((detail.roles || []).length) { document.getElementById("detailRoles").innerHTML = barList(detail.roles); show("detailRolesSection"); }
            if ((detail.clientGeo || []).length) { document.getElementById("detailGeo").innerHTML = barList(detail.clientGeo); show("detailGeoSection"); }

            // Properties
            var props = detail.properties || [];
            if (props.length) {
                document.getElementById("detailProps").innerHTML = '<tbody>' + props.map(function (p) {
                    return '<tr><td class="kv__key">' + escapeHtml(p.key) + '</td><td class="kv__val">' + escapeHtml(p.value) + '</td></tr>';
                }).join("") + '</tbody>';
                show("detailPropsSection");
            }

            // Whole-endpoint AI analysis button (works even when there are no exceptions).
            wireEndpointAnalysis(detail);

            // Collapse/expand-all control for the (now visible) sections.
            wireToggleAll();
        }

        // Wires the header "Collapse all / Expand all" button. Operates only on
        // sections that are currently visible (their `hidden` flag was cleared by show()).
        function wireToggleAll() {
            var btn = document.getElementById("toggleAllBtn");
            if (!btn) { return; }
            btn.hidden = false;

            function visiblePanels() {
                return Array.prototype.slice
                    .call(document.querySelectorAll("#detailContent details.api-analyzer__panel"))
                    .filter(function (d) { return !d.hidden; });
            }

            function sync() {
                var panels = visiblePanels();
                var anyOpen = panels.some(function (d) { return d.open; });
                btn.textContent = anyOpen ? "Collapse all" : "Expand all";
                btn.setAttribute("aria-expanded", anyOpen ? "true" : "false");
            }

            btn.addEventListener("click", function () {
                var panels = visiblePanels();
                var anyOpen = panels.some(function (d) { return d.open; });
                panels.forEach(function (d) { d.open = !anyOpen; });
                sync();
            });

            document.querySelectorAll("#detailContent details.api-analyzer__panel").forEach(function (d) {
                d.addEventListener("toggle", sync);
            });

            sync();
        }

        // Render the overview as a short verdict plus signal chips that surface facts NOT
        // already shown on the metric cards (traffic share, worst case, exceptions, dependencies).
        function renderOverviewTable(detail, insight) {
            var o = detail.overview || {};
            var lat = detail.latency || {};
            var host = document.getElementById("detailOverview");
            if (!host) { return; }

            var tailRatio = (lat.p50 > 0) ? (lat.p99 / lat.p50) : 1;

            // Signal chips surface facts that are NOT on the metric cards.
            var signals = [];
            if (o.trafficSharePercent != null) {
                signals.push(signalChip("\ud83d\udcca", oneDecimal(o.trafficSharePercent) + "% of all traffic"));
            }
            signals.push(signalChip("\u23f1", "Worst case " + prettyDuration(lat.max)));

            var exceptions = detail.exceptions || [];
            if (exceptions.length) {
                var totalEx = exceptions.reduce(function (sum, e) { return sum + (e.count || 0); }, 0);
                signals.push(signalChip("\u26a0", formatNumber(totalEx) + " exceptions \u00b7 " + exceptions.length + " type" + (exceptions.length === 1 ? "" : "s"), "warn"));
            } else {
                signals.push(signalChip("\u2713", "No exceptions", "good"));
            }

            var deps = detail.dependencies || [];
            if (deps.length) {
                signals.push(signalChip("\ud83d\udd17", deps.length + " dependenc" + (deps.length === 1 ? "y" : "ies")));
            }
            if (tailRatio >= 3) {
                signals.push(signalChip("\ud83d\udcc8", "Uneven speed: slowest requests are " + oneDecimal(tailRatio) + "\u00d7 the typical one", "warn"));
            }

            host.innerHTML =
                (insight.summary ? '<p class="ov-verdict">' + escapeHtml(insight.summary) + '</p>' : '') +
                '<div class="ov-signals">' + signals.join("") + '</div>';
        }

        function signalChip(icon, text, mod) {
            var cls = mod ? " ov-signal--" + mod : "";
            return '<span class="ov-signal' + cls + '">' +
                '<span class="ov-signal__icon" aria-hidden="true">' + icon + '</span>' +
                escapeHtml(text) + '</span>';
        }

        // ---- Detailed cost analysis popup ----------------------------------------
        var COST_PER_10K = 1.50; // mirrors the server's EstimateCost rate

        function openCostModal(detail) {
            var modal = document.getElementById("costModal");
            var body = document.getElementById("costModalBody");
            if (!modal || !body) { return; }

            body.innerHTML =
                costSummarySection(detail) +
                costBreakdownSection(detail) +
                costAssessmentSection(detail) +
                costAiSection();

            wireCostAi(detail);

            // Open + wire close (backdrop, ✕, Escape).
            modal.hidden = false;
            modal.classList.add("is-open");

            function close() {
                modal.classList.remove("is-open");
                modal.hidden = true;
                document.removeEventListener("keydown", onKey);
            }
            function onKey(ev) { if (ev.key === "Escape") { close(); } }

            modal.querySelectorAll("[data-cost-close]").forEach(function (el) {
                el.addEventListener("click", close, { once: false });
            });
            document.addEventListener("keydown", onKey);
        }

        // Profile a SINGLE successful request: how many resources it touches, how much time it
        // spends downstream, and its tiny per-request cost. Per-request numbers are averages across
        // the window's successful requests, which is what reveals "this calls the DB 3× per request".
        function costSummarySection(detail) {
            var o = detail.overview || {};
            var deps = detail.dependencies || [];

            // Successful requests are the honest denominator for "per request" math.
            var successful = Math.max(0, (o.calls || 0) - (o.failedCalls || 0)) || (o.calls || 0);

            var totalDepCalls = deps.reduce(function (s, d) { return s + (d.calls || 0); }, 0);
            var callsPerReq = successful > 0 ? totalDepCalls / successful : 0;
            var depTimePerReq = successful > 0
                ? deps.reduce(function (s, d) { return s + (d.calls || 0) * (d.averageDurationMs || 0); }, 0) / successful
                : 0;
            var avg = o.averageDurationMs || 0;
            var downstreamShare = avg > 0 ? Math.min(100, (depTimePerReq / avg) * 100) : 0;

            var costPerReq = successful > 0 ? (o.estimatedCostUsd || 0) / successful : 0;
            var costPerReqLabel = costPerReq >= 0.0001 ? "$" + costPerReq.toFixed(4) : "<$0.0001";

            return '<section class="cost-sec">' +
                '<p class="cost-lead">Profile of <strong>one successful request</strong> to this API ' +
                    '(averaged over ' + formatNumber(successful) + ' successful calls).</p>' +
                '<div class="cost-figures">' +
                    costFigure("Resource calls / request", oneDecimal(callsPerReq),
                        deps.length + " distinct resource" + (deps.length === 1 ? "" : "s") + " touched") +
                    costFigure("Downstream time / request", Math.round(depTimePerReq) + " ms",
                        deps.length ? oneDecimal(downstreamShare) + "% of the " + Math.round(avg) + " ms response" : "no dependencies") +
                    costFigure("Cost / request", costPerReqLabel,
                        "est. at $" + twoDecimals(COST_PER_10K) + " / 10k calls") +
                '</div>' +
                '<p class="cost-note">Per-request figures are averages. If a resource shows more than ~1 call per request, the API is likely fetching the same data repeatedly — see the breakdown below.</p>' +
                '</section>';
        }

        function costFigure(label, value, hint) {
            return '<div class="cost-figure">' +
                '<span class="cost-figure__label">' + escapeHtml(label) + '</span>' +
                '<span class="cost-figure__value">' + escapeHtml(value) + '</span>' +
                '<span class="cost-figure__hint">' + escapeHtml(hint) + '</span></div>';
        }

        // Per-request resource breakdown: how many times ONE request touches each resource and how
        // much time that adds. Resources hit more than ~1.5×/request are flagged as likely redundant.
        function costBreakdownSection(detail) {
            var deps = (detail.dependencies || []).slice();
            var o = detail.overview || {};
            var successful = Math.max(0, (o.calls || 0) - (o.failedCalls || 0)) || (o.calls || 0);

            if (!deps.length) {
                var urls = detail.topUrls || [];
                if (!urls.length) {
                    return '<section class="cost-sec"><h3 class="cost-sec__title">Resources per request</h3>' +
                        '<p class="api-modal__muted">No downstream dependencies were recorded for this API, so a single request doesn\u2019t appear to call a database or external service. Nothing to optimize on the resource side.</p></section>';
                }
                var urlRows = urls.map(function (u) {
                    return '<tr><td>' + escapeHtml(u.name || "(not set)") + '</td>' +
                        '<td class="num">' + formatNumber(u.count) + '</td>' +
                        '<td class="num">' + oneDecimal(u.sharePercent) + '%</td></tr>';
                }).join("");
                return '<section class="cost-sec"><h3 class="cost-sec__title">Busiest request URLs</h3>' +
                    '<table class="cost-table"><thead><tr><th>URL</th><th class="num">Calls</th><th class="num">Share</th></tr></thead>' +
                    '<tbody>' + urlRows + '</tbody></table></section>';
            }

            // Per-request figures + sort by time spent per request (the optimization lever).
            deps.forEach(function (d) {
                d.__perReq = successful > 0 ? (d.calls || 0) / successful : 0;
                d.__timePerReq = d.__perReq * (d.averageDurationMs || 0);
            });
            deps.sort(function (a, b) { return b.__timePerReq - a.__timePerReq; });

            var rows = deps.map(function (d) {
                var repeated = d.__perReq >= 1.5; // called clearly more than once per request
                return '<tr' + (repeated ? ' class="cost-row--warn"' : '') + '>' +
                    '<td><span class="cost-res__name">' + escapeHtml(d.name || "(unknown)") + '</span>' +
                        '<span class="cost-res__type">' + escapeHtml(d.type || "dependency") + '</span></td>' +
                    '<td class="num">' + oneDecimal(d.__perReq) +
                        (repeated ? ' <span class="cost-flag">repeated</span>' : '') + '</td>' +
                    '<td class="num">' + Math.round(d.__timePerReq) + ' ms</td>' +
                    '<td class="num">' + formatNumber(d.averageDurationMs) + ' ms</td>' +
                    '<td class="num">' + oneDecimal(d.successRate) + '%</td></tr>';
            }).join("");

            return '<section class="cost-sec"><h3 class="cost-sec__title">Resources touched per request</h3>' +
                '<table class="cost-table"><thead><tr>' +
                '<th>Resource</th><th class="num">Calls / req</th><th class="num">Time / req</th><th class="num">Avg</th><th class="num">Success</th>' +
                '</tr></thead><tbody>' + rows + '</tbody></table>' +
                '<p class="cost-note"><strong>Calls / req</strong> above 1 means the API hits that resource multiple times for a single request — usually a chance to fetch once and reuse.</p></section>';
        }

        // Per-request resource-optimization engine: inspects how one request uses each resource and
        // suggests concrete code changes (fetch once, point-read/index, batch, parallelize, cache).
        function costAssessmentSection(detail) {
            var o = detail.overview || {};
            var deps = (detail.dependencies || []).slice();
            var successful = Math.max(0, (o.calls || 0) - (o.failedCalls || 0)) || (o.calls || 0);
            var findings = [];

            deps.forEach(function (d) {
                d.__perReq = successful > 0 ? (d.calls || 0) / successful : 0;
                d.__timePerReq = d.__perReq * (d.averageDurationMs || 0);
            });

            var name = (detail.operationName || "").toLowerCase();
            function looksLikeQuery(d) {
                var n = ((d.name || "") + " " + (d.type || "")).toLowerCase();
                return n.indexOf("query") !== -1 || n.indexOf("sql") !== -1 || n.indexOf("select") !== -1;
            }
            function isDb(d) {
                var t = ((d.type || "") + " " + (d.name || "")).toLowerCase();
                return t.indexOf("sql") !== -1 || t.indexOf("documentdb") !== -1 || t.indexOf("cosmos") !== -1 ||
                    t.indexOf("mongo") !== -1 || t.indexOf("db") !== -1 || t.indexOf("table") !== -1 || t.indexOf("storage") !== -1;
            }

            // 1) Repeated calls to the same resource within one request → fetch once and reuse.
            deps.filter(function (d) { return d.__perReq >= 2; })
                .sort(function (a, b) { return b.__perReq - a.__perReq; })
                .slice(0, 2)
                .forEach(function (d) {
                    var times = Math.round(d.__perReq);
                    findings.push(costFinding("bad",
                        "Redundant calls to '" + truncate(d.name, 28) + "'",
                        "Each request hits this resource about " + oneDecimal(d.__perReq) + "\u00d7 (~" + Math.round(d.__timePerReq) + " ms/request).",
                        "Fetch it once per request and reuse the result (memoize in the request scope, or load all needed ids in a single batched call) instead of calling it " + times + " times."));
                });

            // 2) Query that looks like a point lookup → swap for an indexed point read.
            deps.filter(function (d) { return looksLikeQuery(d) && d.averageDurationMs <= 60 && d.__perReq >= 1; })
                .slice(0, 1)
                .forEach(function (d) {
                    findings.push(costFinding("warn",
                        "Query where a point-read may do",
                        "'" + truncate(d.name, 28) + "' runs a query averaging " + Math.round(d.averageDurationMs) + " ms.",
                        "If you're fetching by id/key, replace the query with an indexed point read (e.g. ReadItem/ReadItemAsync with the partition key, or a covering index) — cheaper RUs and lower latency than a SQL query."));
                });

            // 3) Slow query → add/adjust an index.
            deps.filter(function (d) { return looksLikeQuery(d) && d.averageDurationMs >= 150; })
                .sort(function (a, b) { return b.averageDurationMs - a.averageDurationMs; })
                .slice(0, 1)
                .forEach(function (d) {
                    findings.push(costFinding("warn",
                        "Slow query — likely a missing index",
                        "'" + truncate(d.name, 28) + "' averages " + Math.round(d.averageDurationMs) + " ms.",
                        "Add a covering index on the filtered/sorted columns (or the partition key + filter path) so this stops scanning; verify with the query's execution plan."));
                });

            // 4) High fan-out across many resources → batch / parallelize.
            var totalPerReq = deps.reduce(function (s, d) { return s + d.__perReq; }, 0);
            if (totalPerReq >= 4) {
                findings.push(costFinding("warn",
                    "Chatty request (" + oneDecimal(totalPerReq) + " calls/request)",
                    "One request fans out to " + oneDecimal(totalPerReq) + " downstream calls across " + deps.length + " resource(s).",
                    "Batch related reads into a single call, and run independent ones in parallel (Task.WhenAll) so latency is the slowest call, not the sum."));
            }

            // 5) A single resource dominating per-request time → cache it.
            var dominant = deps.slice().sort(function (a, b) { return b.__timePerReq - a.__timePerReq; })[0];
            if (dominant && o.averageDurationMs > 0 && dominant.__timePerReq >= o.averageDurationMs * 0.5 && dominant.__timePerReq >= 50) {
                findings.push(costFinding("warn",
                    "'" + truncate(dominant.name, 28) + "' dominates the request",
                    "It accounts for ~" + Math.round(dominant.__timePerReq) + " ms of the " + Math.round(o.averageDurationMs) + " ms average response.",
                    (isDb(dominant)
                        ? "Cache its result (in-memory/distributed) if the data is read-heavy and changes slowly, or precompute it."
                        : "Cache or memoize this dependency's response where the data tolerates a short TTL.")));
            }

            // 6) Failing resource that forces retries/rework.
            var failingDep = deps.filter(function (d) { return d.successRate < 99 && d.calls > 0; })
                .sort(function (a, b) { return a.successRate - b.successRate; })[0];
            if (failingDep) {
                findings.push(costFinding("bad",
                    "Unreliable resource '" + truncate(failingDep.name, 24) + "'",
                    "It fails " + oneDecimal(100 - failingDep.successRate) + "% of the time, which can trigger retries and wasted work per request.",
                    "Add a timeout + bounded retry policy and a fallback; investigate the failures so requests don't pay for repeated attempts."));
            }

            // Verdict.
            var bad = findings.filter(function (f) { return f.rating === "bad"; }).length;
            var warn = findings.filter(function (f) { return f.rating === "warn"; }).length;
            var verdict, vClass;
            if (bad > 0) { verdict = "Inefficient resource use — one request does redundant work"; vClass = "bad"; }
            else if (warn > 0) { verdict = "Works, but the per-request resource pattern can be tightened"; vClass = "warn"; }
            else { verdict = "Efficient: a request makes lean, well-behaved resource calls"; vClass = "good"; }

            if (findings.length === 0) {
                findings.push(costFinding("good", "Lean resource usage",
                    deps.length
                        ? "Each request makes about " + oneDecimal(totalPerReq) + " downstream call(s), all fast and reliable — no obvious redundancy."
                        : "This request doesn't call a database or external service, so there's nothing to optimize on the resource side.",
                    "Keep results cached where the data allows, and watch for new dependencies creeping in."));
            }

            return '<section class="cost-sec">' +
                '<h3 class="cost-sec__title">How to optimize this API</h3>' +
                '<div class="cost-verdict cost-verdict--' + vClass + '">' + escapeHtml(verdict) + '</div>' +
                '<ul class="cost-findings">' + findings.map(renderCostFinding).join("") + '</ul>' +
                '</section>';
        }

        function costFinding(rating, title, detailText, action) {
            return { rating: rating, title: title, detail: detailText, action: action };
        }
        function renderCostFinding(f) {
            var icon = f.rating === "bad" ? "\u26a0" : (f.rating === "warn" ? "\u25c6" : "\u2713");
            return '<li class="cost-finding cost-finding--' + f.rating + '">' +
                '<span class="cost-finding__icon" aria-hidden="true">' + icon + '</span>' +
                '<div><span class="cost-finding__title">' + escapeHtml(f.title) + '</span>' +
                '<span class="cost-finding__detail">' + escapeHtml(f.detail) + '</span>' +
                '<span class="cost-finding__action">' + escapeHtml(f.action) + '</span></div></li>';
        }

        // The cost popup's AI deep-dive section: a button + a result container.
        function costAiSection() {
            return '<section class="cost-sec cost-sec--ai">' +
                '<button type="button" class="exc-card__ai" id="costAiBtn">\u2728 Get AI optimization plan for this API</button>' +
                '<div class="exc-card__aiResult" id="costAiResult" hidden></div>' +
                '</section>';
        }

        // Reuses the existing endpoint AnalyzeException endpoint with a cost-focused context.
        function wireCostAi(detail) {
            var btn = document.getElementById("costAiBtn");
            var result = document.getElementById("costAiResult");
            if (!btn || !result) { return; }

            btn.addEventListener("click", function () {
                var payload = {
                    isEndpointAnalysis: true,
                    operationName: detail.operationName || operation,
                    context: buildCostContext(detail)
                };

                btn.disabled = true;
                var original = btn.innerHTML;
                btn.innerHTML = '<span class="spinner"></span> Analyzing\u2026';
                result.hidden = false;
                result.innerHTML = '<p class="api-modal__muted">Asking the model how to optimize this API\u2019s resource usage\u2026</p>';

                fetch("/EndpointDetail/AnalyzeException", {
                    method: "POST",
                    headers: { "Content-Type": "application/json", "Accept": "application/json" },
                    body: JSON.stringify(payload)
                })
                    .then(function (r) {
                        if (r.status === 499) { return null; }
                        if (!r.ok) { throw new Error("the server returned status " + r.status + "."); }
                        return r.json();
                    })
                    .then(function (a) {
                        btn.disabled = false;
                        btn.innerHTML = original;
                        if (a === null) { result.hidden = true; return; }
                        if (!a.success) {
                            result.innerHTML = '<div class="ai-result ai-result--info">' + escapeHtml(a.errorMessage || "AI analysis is unavailable.") + '</div>';
                            return;
                        }
                        result.innerHTML = renderAiAnalysis(a);
                    })
                    .catch(function (err) {
                        btn.disabled = false;
                        btn.innerHTML = original;
                        result.innerHTML = '<div class="ai-result ai-result--info">Could not analyze — ' + escapeHtml(err.message) + '</div>';
                    });
            });
        }

        // A per-request, optimization-focused telemetry summary for the model.
        function buildCostContext(detail) {
            var o = detail.overview || {};
            var lat = detail.latency || {};
            var successful = Math.max(0, (o.calls || 0) - (o.failedCalls || 0)) || (o.calls || 0);
            var lines = [];
            lines.push("Task: analyze a SINGLE successful request to this API and explain whether it uses its resources efficiently. Identify redundant work (e.g. the same database/resource called multiple times per request that could be fetched once), and give concrete code/configuration optimizations: fetch-once/memoize, swap a query for an indexed point read (e.g. ReadItem with partition key), add a covering index, batch multiple reads into one, parallelize independent calls (Task.WhenAll), or cache. Be specific to the resources shown.");
            lines.push("Per-request basis: " + formatNumber(successful) + " successful requests in the window.");
            lines.push("Response time ms — avg " + Math.round(o.averageDurationMs) + ", P50 " + Math.round(lat.p50) + ", P95 " + Math.round(lat.p95) + ".");

            var deps = (detail.dependencies || []).map(function (d) {
                var perReq = successful > 0 ? (d.calls || 0) / successful : 0;
                return d.name + " (" + (d.type || "?") + "): " + perReq.toFixed(1) + " calls/request, avg " +
                    Math.round(d.averageDurationMs) + "ms, " + oneDecimal(d.successRate) + "% success (total " + d.calls + " calls)";
            });
            if (deps.length) {
                lines.push("Resources touched per request:");
                lines.push(deps.join("\n"));
            } else {
                lines.push("This request calls no downstream database or service dependencies.");
            }
            return lines.join("\n");
        }

        function wireEndpointAnalysis(detail) {
            var btn = document.getElementById("analyzeEndpointBtn");
            var result = document.getElementById("endpointAiResult");
            if (!btn || !result) { return; }

            btn.addEventListener("click", function () {
                var payload = {
                    isEndpointAnalysis: true,
                    operationName: detail.operationName || operation,
                    context: buildEndpointContext(detail)
                };

                btn.disabled = true;
                var original = btn.innerHTML;
                btn.innerHTML = '<span class="spinner"></span> Analyzing…';
                result.hidden = false;
                result.innerHTML = '<p class="api-modal__muted">Asking the model to analyze this endpoint…</p>';

                fetch("/EndpointDetail/AnalyzeException", {
                    method: "POST",
                    headers: { "Content-Type": "application/json", "Accept": "application/json" },
                    body: JSON.stringify(payload)
                })
                    .then(function (r) {
                        if (r.status === 499) { return null; }
                        if (!r.ok) { throw new Error("the server returned status " + r.status + "."); }
                        return r.json();
                    })
                    .then(function (a) {
                        btn.disabled = false;
                        btn.innerHTML = original;
                        if (a === null) { result.hidden = true; return; }
                        if (!a.success) {
                            result.innerHTML = '<div class="ai-result ai-result--info">' + escapeHtml(a.errorMessage || "AI analysis is unavailable.") + '</div>';
                            return;
                        }
                        result.innerHTML = renderAiAnalysis(a);
                    })
                    .catch(function (err) {
                        btn.disabled = false;
                        btn.innerHTML = original;
                        result.innerHTML = '<div class="ai-result ai-result--info">Could not analyze — ' + escapeHtml(err.message) + '</div>';
                    });
            }, { once: false });
        }

        // Build a compact, model-friendly summary of the endpoint's telemetry.
        function buildEndpointContext(detail) {
            var o = detail.overview || {};
            var lat = detail.latency || {};
            var lines = [];
            lines.push("Calls: " + o.calls + ", failed: " + o.failedCalls + ", success rate: " + oneDecimal(o.successRate) + "%.");
            lines.push("Latency ms — avg: " + Math.round(o.averageDurationMs) + ", P50: " + Math.round(lat.p50) +
                ", P95: " + Math.round(lat.p95) + ", P99: " + Math.round(lat.p99) + ", max: " + Math.round(lat.max) + ".");

            var codes = (detail.statusCodes || []).map(function (s) { return s.resultCode + "×" + s.count; });
            if (codes.length) { lines.push("Status codes: " + codes.join(", ") + "."); }

            var deps = (detail.dependencies || []).map(function (d) {
                return d.name + " (" + (d.type || "?") + ") avg " + Math.round(d.averageDurationMs) + "ms, " + oneDecimal(d.successRate) + "% success";
            });
            if (deps.length) { lines.push("Dependencies: " + deps.join("; ") + "."); }

            var exes = (detail.exceptions || []).map(function (e) { return e.type + " ×" + e.count; });
            if (exes.length) { lines.push("Exceptions: " + exes.join(", ") + "."); }
            else { lines.push("No exceptions recorded (failures are HTTP-level, e.g. 4xx/5xx)."); }

            return lines.join("\n");
        }

        function renderException(ex) {
            var stack = ex.stackTrace
                ? '<pre class="stacktrace">' + escapeHtml(ex.stackTrace) + '</pre>'
                : '<p class="api-modal__muted">No stack trace captured for this exception.</p>';

            var techRows =
                kvRow("Type", ex.type) +
                (ex.method ? kvRow("Throwing method", ex.method) : "") +
                (ex.message ? kvRow("Message", ex.message) : "") +
                (ex.lastSeen ? kvRow("Last seen", new Date(ex.lastSeen).toLocaleString()) : "");

            // Encode the exception payload onto the button so we can analyze it on click.
            var payload = encodeURIComponent(JSON.stringify({
                operationName: detail_operationName(),
                exceptionType: ex.type || "",
                message: ex.message || "",
                method: ex.method || "",
                stackTrace: ex.stackTrace || "",
                count: ex.count || 0
            }));

            return '<div class="exc-card">' +
                '<div class="exc-card__head">' +
                    '<div>' +
                        '<span class="exc-card__cat">' + escapeHtml(ex.friendlyCategory || "Exception") + '</span>' +
                        '<span class="exc-card__type">' + escapeHtml(ex.type || "") + '</span>' +
                    '</div>' +
                    '<span class="exc-card__count">' + formatNumber(ex.count) + '×</span>' +
                '</div>' +
                '<p class="exc-card__explain">' + escapeHtml(ex.friendlyExplanation || "") + '</p>' +
                (ex.suggestedAction
                    ? '<p class="exc-card__action"><strong>Suggested:</strong> ' + escapeHtml(ex.suggestedAction) + '</p>'
                    : '') +
                '<div class="exc-card__actions">' +
                    '<button type="button" class="exc-card__ai" data-payload="' + payload + '">✨ Analyze with AI</button>' +
                    '<button type="button" class="exc-card__toggle" aria-expanded="false">▸ Technical details</button>' +
                '</div>' +
                '<div class="exc-card__aiResult" hidden></div>' +
                '<div class="exc-card__tech" hidden>' +
                    '<table class="mini-table kv-table"><tbody>' + techRows + '</tbody></table>' +
                    stack +
                '</div>' +
                '</div>';
        }

        function detail_operationName() {
            var el = document.getElementById("detailTitle");
            return el ? el.textContent : "";
        }

        // ---- Per-status-code failure analysis ----
        function renderFailureCard(s) {
            var ev = s.evidence || {};
            var exTypes = ev.exceptionTypes || [];
            var exMsgs = ev.exceptionMessages || [];
            var urls = ev.sampleUrls || [];
            var deps = ev.failingDependencies || [];

            var evidenceRows = "";
            if (exTypes.length) { evidenceRows += kvRow("Exceptions", exTypes.join(", ")); }
            if (exMsgs.length) { evidenceRows += kvRow("Messages", exMsgs.slice(0, 3).join(" | ")); }
            if (deps.length) { evidenceRows += kvRow("Failing dependencies", deps.join(", ")); }
            if (urls.length) { evidenceRows += kvRow("Sample URLs", urls.slice(0, 3).join("  ")); }
            if (ev.lastSeen) { evidenceRows += kvRow("Last seen", new Date(ev.lastSeen).toLocaleString()); }

            var evidenceBlock = evidenceRows
                ? '<table class="mini-table kv-table"><tbody>' + evidenceRows + '</tbody></table>'
                : '<p class="api-modal__muted">No correlated exceptions or dependency failures were recorded for this code — likely rejected/handled before app code ran (gateway/proxy timeout, auth, routing, or upstream limits).</p>';

            // Build a compact, model-friendly evidence string for this specific code.
            var contextLines = [];
            contextLines.push("Failed requests with this code: " + (ev.failedRequests || s.count) + ".");
            if (exTypes.length) { contextLines.push("Correlated exception types: " + exTypes.join(", ") + "."); }
            if (exMsgs.length) { contextLines.push("Exception messages: " + exMsgs.slice(0, 5).join(" | ") + "."); }
            if (deps.length) { contextLines.push("Failing downstream dependencies: " + deps.join(", ") + "."); }
            if (urls.length) { contextLines.push("Sample request URLs: " + urls.slice(0, 5).join(", ") + "."); }

            var payload = encodeURIComponent(JSON.stringify({
                isStatusCodeAnalysis: true,
                statusCode: s.resultCode || "",
                operationName: detail_operationName(),
                context: contextLines.join("\n")
            }));

            return '<div class="exc-card exc-card--status">' +
                '<div class="exc-card__head">' +
                    '<div>' +
                        '<span class="exc-card__cat">HTTP ' + escapeHtml(s.resultCode) + '</span>' +
                        '<span class="exc-card__type">' + statusCodeMeaning(s.resultCode) + '</span>' +
                    '</div>' +
                    '<span class="exc-card__count">' + formatNumber(ev.failedRequests || s.count) + '×</span>' +
                '</div>' +
                evidenceBlock +
                '<div class="exc-card__actions">' +
                    '<button type="button" class="exc-card__ai" data-payload="' + payload + '">✨ Pinpoint reason with AI</button>' +
                '</div>' +
                '<div class="exc-card__aiResult" hidden></div>' +
                '</div>';
        }

        function wireFailureAnalysis() {
            document.querySelectorAll("#detailFailures .exc-card__ai").forEach(function (btn) {
                btn.addEventListener("click", function () { analyzeStatusCode(btn); });
            });

            var allBtn = document.getElementById("analyzeAllFailuresBtn");
            if (allBtn) {
                allBtn.addEventListener("click", function () {
                    var buttons = Array.prototype.slice.call(document.querySelectorAll("#detailFailures .exc-card__ai"));
                    allBtn.disabled = true;
                    var original = allBtn.innerHTML;
                    allBtn.innerHTML = '<span class="spinner"></span> Analyzing all…';
                    // Run sequentially to respect free-tier rate limits.
                    var i = 0;
                    (function next() {
                        if (i >= buttons.length) { allBtn.disabled = false; allBtn.innerHTML = original; return; }
                        analyzeStatusCode(buttons[i], next);
                        i++;
                    })();
                });
            }
        }

        function analyzeStatusCode(btn, done) {
            var card = btn.closest(".exc-card");
            if (!card) { if (done) { done(); } return; }
            var result = card.querySelector(".exc-card__aiResult");
            var payload;
            try {
                payload = JSON.parse(decodeURIComponent(btn.getAttribute("data-payload")));
            } catch (e) {
                if (done) { done(); }
                return;
            }

            btn.disabled = true;
            var originalLabel = btn.innerHTML;
            btn.innerHTML = '<span class="spinner"></span> Analyzing…';
            result.hidden = false;
            result.innerHTML = '<p class="api-modal__muted">Pinpointing the reason for HTTP ' + escapeHtml(payload.statusCode) + ' from telemetry…</p>';

            fetch("/EndpointDetail/AnalyzeException", {
                method: "POST",
                headers: { "Content-Type": "application/json", "Accept": "application/json" },
                body: JSON.stringify(payload)
            })
                .then(function (r) {
                    if (r.status === 499) { return null; }
                    if (!r.ok) { throw new Error("the server returned status " + r.status + "."); }
                    return r.json();
                })
                .then(function (a) {
                    btn.disabled = false;
                    btn.innerHTML = originalLabel;
                    if (a === null) { result.hidden = true; if (done) { done(); } return; }
                    if (!a.success) {
                        result.innerHTML = '<div class="ai-result ai-result--info">' + escapeHtml(a.errorMessage || "AI analysis is unavailable.") + '</div>';
                        if (done) { done(); }
                        return;
                    }
                    result.innerHTML = renderAiAnalysis(a);
                    if (done) { done(); }
                })
                .catch(function (err) {
                    btn.disabled = false;
                    btn.innerHTML = originalLabel;
                    result.innerHTML = '<div class="ai-result ai-result--info">Could not analyze — ' + escapeHtml(err.message) + '</div>';
                    if (done) { done(); }
                });
        }

        // A short, human-friendly label for common HTTP status codes.
        function statusCodeMeaning(code) {
            var map = {
                "400": "Bad Request", "401": "Unauthorized", "403": "Forbidden", "404": "Not Found",
                "405": "Method Not Allowed", "408": "Request Timeout", "409": "Conflict",
                "429": "Too Many Requests", "500": "Internal Server Error", "501": "Not Implemented",
                "502": "Bad Gateway", "503": "Service Unavailable", "504": "Gateway Timeout"
            };
            return map[String(code)] || "Failed requests";
        }

        function wireExceptionToggles() {
            document.querySelectorAll(".exc-card__toggle").forEach(function (btn) {
                btn.addEventListener("click", function () {
                    var tech = btn.closest(".exc-card").querySelector(".exc-card__tech");
                    var open = !tech.hidden;
                    tech.hidden = open;
                    btn.setAttribute("aria-expanded", open ? "false" : "true");
                    btn.innerHTML = (open ? "▸" : "▾") + " Technical details";
                });
            });

            // Only wire AI buttons that live inside an exception card. The endpoint-level
            // button (#analyzeEndpointBtn) shares the .exc-card__ai class but has its own handler.
            document.querySelectorAll(".exc-card .exc-card__ai").forEach(function (btn) {
                btn.addEventListener("click", function () { analyzeException(btn); });
            });
        }

        function analyzeException(btn) {
            var card = btn.closest(".exc-card");
            if (!card) { return; }
            var result = card.querySelector(".exc-card__aiResult");
            var payload;
            try {
                payload = JSON.parse(decodeURIComponent(btn.getAttribute("data-payload")));
            } catch (e) {
                return;
            }

            btn.disabled = true;
            var originalLabel = btn.innerHTML;
            btn.innerHTML = '<span class="spinner"></span> Analyzing…';
            result.hidden = false;
            result.innerHTML = '<p class="api-modal__muted">Asking the model to analyze this exception…</p>';

            fetch("/EndpointDetail/AnalyzeException", {
                method: "POST",
                headers: { "Content-Type": "application/json", "Accept": "application/json" },
                body: JSON.stringify(payload)
            })
                .then(function (r) {
                    if (r.status === 499) { return null; }
                    if (!r.ok) { throw new Error("the server returned status " + r.status + "."); }
                    return r.json();
                })
                .then(function (a) {
                    btn.disabled = false;
                    btn.innerHTML = originalLabel;
                    if (a === null) { result.hidden = true; return; }
                    if (!a.success) {
                        result.innerHTML = '<div class="ai-result ai-result--info">' + escapeHtml(a.errorMessage || "AI analysis is unavailable.") + '</div>';
                        return;
                    }
                    result.innerHTML = renderAiAnalysis(a);
                })
                .catch(function (err) {
                    btn.disabled = false;
                    btn.innerHTML = originalLabel;
                    result.innerHTML = '<div class="ai-result ai-result--info">Could not analyze — ' + escapeHtml(err.message) + '</div>';
                });
        }

        function renderAiAnalysis(a) {
            var areas = (a.codeAreas || []).map(function (c) { return '<span class="ai-chip">' + escapeHtml(c) + '</span>'; }).join("");

            return '<div class="ai-result">' +
                '<div class="ai-result__head"><span class="ai-result__badge">✨ AI analysis</span>' +
                    (a.confidence ? '<span class="ai-result__conf">' + a.confidence + '% confidence</span>' : '') +
                '</div>' +
                (a.rootCause ? '<p class="ai-result__root">' + escapeHtml(a.rootCause) + '</p>' : '') +
                aiTable("Likely causes", "#", "Cause", a.likelyCauses) +
                aiTable("How to fix", "Step", "Action", a.howToFix) +
                (areas ? '<h5 class="ai-result__h">Code areas to inspect</h5><div class="ai-chips">' + areas + '</div>' : '') +
                '<p class="ai-result__foot">Generated by an LLM — verify before acting.</p>' +
                '</div>';
        }

        // Render a list of analysis points as a compact, numbered two-column table so long
        // sentences are easy to scan instead of running together as bullet points.
        function aiTable(heading, indexHeader, valueHeader, items) {
            items = items || [];
            if (!items.length) { return ""; }
            var rows = items.map(function (text, i) {
                return '<tr><td class="ai-table__num">' + (i + 1) + '</td>' +
                    '<td class="ai-table__text">' + escapeHtml(text) + '</td></tr>';
            }).join("");
            return '<h5 class="ai-result__h">' + escapeHtml(heading) + '</h5>' +
                '<table class="ai-table"><thead><tr>' +
                '<th class="ai-table__num">' + escapeHtml(indexHeader) + '</th>' +
                '<th>' + escapeHtml(valueHeader) + '</th></tr></thead>' +
                '<tbody>' + rows + '</tbody></table>';
        }

        // ---- small render helpers ----
        function card(label, value, mod) {
            var cls = mod ? " metric-card__value--" + mod : "";
            return '<div class="metric-card"><p class="metric-card__label">' + escapeHtml(label) + '</p>' +
                '<p class="metric-card__value' + cls + '">' + escapeHtml(value) + '</p></div>';
        }
        // The Est. Cost card is clickable and opens the detailed cost-analysis popup.
        function costCard(estimatedCostUsd) {
            return '<button type="button" class="metric-card metric-card--action" id="costCard" ' +
                'aria-haspopup="dialog" title="Click for a detailed cost breakdown">' +
                '<p class="metric-card__label">Est. Cost <span class="metric-card__more">details ›</span></p>' +
                '<p class="metric-card__value">$' + twoDecimals(estimatedCostUsd) + '</p></button>';
        }
        function wireCostCard(detail) {
            var cardEl = document.getElementById("costCard");
            if (!cardEl) { return; }
            cardEl.addEventListener("click", function () { openCostModal(detail); });
        }
        // A friendly response-speed card: plain-language label, a readable duration and a hint.
        function friendlyLatency(label, value, hint) {
            var rating = latencyRating(value);
            return '<div class="latency latency--' + rating + '">' +
                '<span class="latency__label">' + escapeHtml(label) + '</span>' +
                '<span class="latency__value">' + prettyDuration(value) + '</span>' +
                '<span class="latency__hint">' + escapeHtml(hint) + '</span></div>';
        }
        // Coarse good/ok/slow rating used only to colour the card.
        function latencyRating(ms) {
            if (ms == null || isNaN(ms)) { return "good"; }
            if (ms < 300) { return "good"; }
            if (ms < 1000) { return "ok"; }
            return "slow";
        }
        // Render milliseconds in the friendliest unit (ms under 1s, seconds otherwise).
        function prettyDuration(ms) {
            if (ms == null || isNaN(ms)) { return "—"; }
            if (ms < 1000) { return Math.round(ms) + " ms"; }
            var seconds = ms / 1000;
            return (seconds < 10 ? (Math.round(seconds * 10) / 10) : Math.round(seconds)) + " s";
        }
        function barList(items) {
            items = items || [];
            if (!items.length) { return ""; }
            var max = Math.max.apply(null, items.map(function (i) { return i.count; }));
            max = max <= 0 ? 1 : max;
            return items.map(function (i) {
                var pct = Math.max(2, (i.count / max) * 100);
                var name = (i.name && i.name.trim()) ? i.name : "(not set)";
                return '<div class="barrow">' +
                    '<div class="barrow__label" title="' + escapeHtml(name) + '">' + escapeHtml(truncate(name, 48)) + '</div>' +
                    '<div class="barrow__track"><div class="barrow__fill" style="width:' + pct + '%"></div></div>' +
                    '<div class="barrow__count">' + formatNumber(i.count) + '</div>' +
                    '<div class="barrow__pct">' + oneDecimal(i.sharePercent) + '%</div>' +
                    '</div>';
            }).join("");
        }
        function kvRow(k, v) { return '<tr><td class="kv__key">' + escapeHtml(k) + '</td><td class="kv__val">' + escapeHtml(v) + '</td></tr>'; }
        function fillList(id, items) {
            var el = document.getElementById(id);
            el.innerHTML = "";
            (items || []).forEach(function (s) { var li = document.createElement("li"); li.textContent = s; el.appendChild(li); });
        }
        function show(id) { var el = document.getElementById(id); if (el) { el.hidden = false; } }

        // ---- utils ----
        function truncate(s, n) { if (!s) { return ""; } return s.length > n ? s.slice(0, n) + "…" : s; }
        function formatNumber(n) { if (n == null || isNaN(n)) { return "0"; } return Math.round(n).toLocaleString(); }
        function oneDecimal(n) { if (n == null || isNaN(n)) { return "0"; } return (Math.round(n * 10) / 10).toLocaleString(); }
        function twoDecimals(n) { if (n == null || isNaN(n)) { return "0.00"; } return n.toFixed(2); }
        function escapeHtml(value) {
            if (value == null) { return ""; }
            return String(value).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
        }
    });
})();
