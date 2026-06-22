(function () {
    "use strict";

    document.addEventListener("DOMContentLoaded", function () {
        initLineChart();
        initSortableTable();
        initRowNavigation();
        initNavSpinner();
        initTimeRange();
    });

    // ---- Custom time-range picker (dashboard) --------------------------------
    function initTimeRange() {
        var button = document.getElementById("timeRangeBtn");
        var popover = document.getElementById("timeRangePopover");
        var form = document.querySelector(".api-analyzer__toolbar");
        if (!button || !popover || !form || !window.TimeRangePicker) { return; }

        var windowEl = document.getElementById("WindowHours");
        var startEl = document.getElementById("CustomStart");
        var endEl = document.getElementById("CustomEnd");
        var activeHours = parseInt((windowEl && windowEl.value) || "24", 10);
        var hasCustom = !!(startEl && startEl.value && endEl && endEl.value);

        window.TimeRangePicker.attach({
            button: button,
            popover: popover,
            activeHours: activeHours,
            customStart: hasCustom ? startEl.value : null,
            customEnd: hasCustom ? endEl.value : null,
            onPreset: function (hours) {
                // A preset uses the relative window and clears any custom range.
                if (windowEl) { windowEl.value = String(hours); }
                if (startEl) { startEl.value = ""; }
                if (endEl) { endEl.value = ""; }
                form.submit();
            },
            onApply: function (startDate, endDate) {
                // Submit an explicit UTC range.
                if (startEl) { startEl.value = startDate.toISOString(); }
                if (endEl) { endEl.value = endDate.toISOString(); }
                form.submit();
            }
        });
    }

    // ---- Interactive SVG line chart ------------------------------------------
    function initLineChart() {
        var host = document.getElementById("lineChart");
        var tooltip = document.getElementById("chartTooltip");
        var dataEl = document.getElementById("timelineData");
        if (!host || !tooltip || !dataEl) {
            return;
        }

        var points = [];
        try {
            points = JSON.parse(dataEl.textContent || "[]");
        } catch (e) {
            points = [];
        }
        if (points.length === 0) {
            return;
        }

        var SVG_NS = "http://www.w3.org/2000/svg";
        var width = 1000;
        var height = 200;
        var padding = { top: 16, right: 12, bottom: 16, left: 12 };
        var innerW = width - padding.left - padding.right;
        var innerH = height - padding.top - padding.bottom;

        var maxCalls = Math.max.apply(null, points.map(function (p) { return p.calls; }));
        maxCalls = maxCalls <= 0 ? 1 : maxCalls;

        function x(i) {
            return points.length === 1
                ? padding.left + innerW / 2
                : padding.left + (i / (points.length - 1)) * innerW;
        }
        function y(v) {
            return padding.top + innerH - (v / maxCalls) * innerH;
        }

        var linePath = "";
        var areaPath = "";
        points.forEach(function (p, i) {
            var px = x(i).toFixed(2);
            var py = y(p.calls).toFixed(2);
            linePath += (i === 0 ? "M" : "L") + px + " " + py + " ";
            areaPath += (i === 0 ? "M" : "L") + px + " " + py + " ";
        });
        areaPath += "L" + x(points.length - 1).toFixed(2) + " " + (padding.top + innerH) + " " +
            "L" + x(0).toFixed(2) + " " + (padding.top + innerH) + " Z";

        var svg = document.createElementNS(SVG_NS, "svg");
        svg.setAttribute("viewBox", "0 0 " + width + " " + height);
        svg.setAttribute("preserveAspectRatio", "none");
        svg.setAttribute("class", "linechart__svg");

        svg.innerHTML =
            '<defs>' +
            '<linearGradient id="lcFill" x1="0" y1="0" x2="0" y2="1">' +
            '<stop offset="0%" stop-color="#0078d4" stop-opacity="0.30" />' +
            '<stop offset="100%" stop-color="#0078d4" stop-opacity="0" />' +
            '</linearGradient>' +
            '</defs>' +
            '<path d="' + areaPath + '" fill="url(#lcFill)" />' +
            '<path d="' + linePath.trim() + '" fill="none" stroke="#0078d4" stroke-width="2" ' +
            'vector-effect="non-scaling-stroke" stroke-linejoin="round" stroke-linecap="round" />' +
            '<line class="linechart__crosshair" id="lcCrosshair" y1="' + padding.top + '" y2="' + (padding.top + innerH) + '" x1="0" x2="0" />' +
            '<circle class="linechart__marker" id="lcMarker" r="4" cx="0" cy="0" />';

        host.insertBefore(svg, tooltip);

        var crosshair = svg.querySelector("#lcCrosshair");
        var marker = svg.querySelector("#lcMarker");

        function show(i, clientX) {
            var p = points[i];
            crosshair.setAttribute("x1", x(i));
            crosshair.setAttribute("x2", x(i));
            crosshair.classList.add("is-visible");
            marker.setAttribute("cx", x(i));
            marker.setAttribute("cy", y(p.calls));
            marker.classList.add("is-visible");

            tooltip.innerHTML =
                '<div class="chart-tooltip__time">' + escapeHtml(p.time) + '</div>' +
                '<div class="chart-tooltip__row"><span>Calls</span><span class="chart-tooltip__val chart-tooltip__val--accent">' + formatNumber(p.calls) + '</span></div>' +
                '<div class="chart-tooltip__row"><span>Avg</span><span class="chart-tooltip__val">' + formatNumber(p.avg) + ' ms</span></div>';

            var hostRect = host.getBoundingClientRect();
            var left = clientX != null ? clientX : (hostRect.left + (x(i) / width) * hostRect.width);
            left = Math.max(hostRect.left + 70, Math.min(left, hostRect.right - 70));
            tooltip.style.left = left + "px";
            tooltip.style.top = (hostRect.top - 6) + "px";
            tooltip.classList.add("is-visible");
            tooltip.setAttribute("aria-hidden", "false");
        }

        function hide() {
            crosshair.classList.remove("is-visible");
            marker.classList.remove("is-visible");
            tooltip.classList.remove("is-visible");
            tooltip.setAttribute("aria-hidden", "true");
        }

        function nearestIndex(clientX) {
            var rect = host.getBoundingClientRect();
            var ratio = (clientX - rect.left) / rect.width;
            var vx = ratio * width;
            var rel = (vx - padding.left) / innerW;
            var idx = Math.round(rel * (points.length - 1));
            return Math.max(0, Math.min(points.length - 1, idx));
        }

        host.addEventListener("mousemove", function (ev) { show(nearestIndex(ev.clientX), ev.clientX); });
        host.addEventListener("mouseleave", hide);

        var current = 0;
        host.setAttribute("tabindex", "0");
        host.addEventListener("keydown", function (ev) {
            if (ev.key === "ArrowRight" || ev.key === "ArrowLeft") {
                ev.preventDefault();
                current += ev.key === "ArrowRight" ? 1 : -1;
                current = Math.max(0, Math.min(points.length - 1, current));
                show(current, null);
            } else if (ev.key === "Escape") {
                hide();
            }
        });
        host.addEventListener("blur", hide);
    }

    // ---- Sortable endpoints table --------------------------------------------
    function initSortableTable() {
        var table = document.getElementById("endpointsTable");
        if (!table) {
            return;
        }

        var headers = table.querySelectorAll("th.sortable");
        var tbody = table.querySelector("tbody");

        headers.forEach(function (th) {
            th.addEventListener("click", function () {
                var key = th.getAttribute("data-sort");
                var current = th.classList.contains("sort-asc")
                    ? "asc"
                    : (th.classList.contains("sort-desc") ? "desc" : "");
                var next = current === "desc" ? "asc" : "desc";

                headers.forEach(function (h) { h.classList.remove("sort-asc", "sort-desc"); });
                th.classList.add(next === "asc" ? "sort-asc" : "sort-desc");

                var rows = Array.prototype.slice.call(tbody.querySelectorAll("tr"));
                rows.sort(function (a, b) {
                    var av = parseFloat(a.getAttribute("data-" + key)) || 0;
                    var bv = parseFloat(b.getAttribute("data-" + key)) || 0;
                    return next === "asc" ? av - bv : bv - av;
                });
                rows.forEach(function (r) { tbody.appendChild(r); });
            });
        });
    }

    // ---- Navigate to the dedicated detail page on row click ------------------
    function initRowNavigation() {
        var table = document.getElementById("endpointsTable");
        if (!table) {
            return;
        }

        var root = document.querySelector(".api-analyzer[data-workspace]");
        var workspaceId = root ? (root.getAttribute("data-workspace") || "") : "";
        var windowHours = root ? (root.getAttribute("data-window") || "24") : "24";
        var apiFilter = root ? (root.getAttribute("data-filter") || "") : "";

        // Carry the dashboard's active custom date range through to the detail page (if one is set).
        var startEl = document.getElementById("CustomStart");
        var endEl = document.getElementById("CustomEnd");
        var customStart = startEl && startEl.value ? startEl.value : "";
        var customEnd = endEl && endEl.value ? endEl.value : "";

        function go(operation) {
            if (!operation) { return; }
            showOverlay("Loading " + operation + "…");
            var params = new URLSearchParams({
                Operation: operation,
                WorkspaceId: workspaceId,
                WindowHours: windowHours,
                ApiFilter: apiFilter
            });
            if (customStart && customEnd) {
                params.set("CustomStart", customStart);
                params.set("CustomEnd", customEnd);
            }
            window.location.href = "/EndpointDetail/Index?" + params.toString();
        }

        table.querySelectorAll(".api-table__row").forEach(function (row) {
            row.addEventListener("click", function () { go(row.getAttribute("data-endpoint")); });
            row.addEventListener("keydown", function (ev) {
                if (ev.key === "Enter" || ev.key === " ") {
                    ev.preventDefault();
                    go(row.getAttribute("data-endpoint"));
                }
            });
        });
    }

    // ---- Show a loading overlay when the Analyze form submits ----------------
    function initNavSpinner() {
        var form = document.querySelector(".api-analyzer__toolbar");
        if (form && form.tagName === "FORM") {
            form.addEventListener("submit", function () { showOverlay("Querying Application Insights…"); });
        }
    }

    function showOverlay(message) {
        var existing = document.getElementById("navOverlay");
        if (existing) { return; }
        var overlay = document.createElement("div");
        overlay.id = "navOverlay";
        overlay.className = "nav-overlay";
        overlay.innerHTML = '<div class="nav-overlay__box"><span class="spinner spinner--lg"></span>' +
            '<p>' + escapeHtml(message || "Loading…") + '</p></div>';
        document.body.appendChild(overlay);
    }

    // ---- Helpers --------------------------------------------------------------
    function formatNumber(n) {
        if (n == null || isNaN(n)) { return "0"; }
        return Math.round(n).toLocaleString();
    }

    function escapeHtml(value) {
        if (value == null) { return ""; }
        return String(value)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }
})();
