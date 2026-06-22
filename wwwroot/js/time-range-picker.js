// Shared, framework-free time-range picker used by both the dashboard and the endpoint
// detail page. Renders a fully-themed popover (preset quick-ranges + a custom month calendar
// with explicit hour/minute inputs) so we never fall back to the browser's unstyleable native
// datetime-local popup. Exposes window.TimeRangePicker.attach(options).
(function () {
    "use strict";

    var MONTHS = ["January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"];
    var DOW = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];

    var PRESETS = [
        { hours: 1, label: "Last hour" },
        { hours: 6, label: "Last 6 hours" },
        { hours: 12, label: "Last 12 hours" },
        { hours: 24, label: "Last 24 hours" },
        { hours: 72, label: "Last 3 days" },
        { hours: 168, label: "Last 7 days" }
    ];

    function pad(n) { return (n < 10 ? "0" : "") + n; }

    function fmtDateTime(d) {
        if (!d || isNaN(d)) { return "—"; }
        return d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate()) +
            " " + pad(d.getHours()) + ":" + pad(d.getMinutes());
    }

    function sameDay(a, b) {
        return a && b && a.getFullYear() === b.getFullYear() &&
            a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
    }

    // options: { button, popover, activeHours, customStart (ISO|null), customEnd (ISO|null),
    //            onPreset(hours), onApply(startDate, endDate) }
    function attach(options) {
        var button = options.button;
        var popover = options.popover;
        if (!button || !popover) { return null; }

        var hasCustom = !!(options.customStart && options.customEnd);

        // Working state: From/To Date objects (local time). Default to the active relative window.
        var now = new Date();
        var to = hasCustom ? new Date(options.customEnd) : now;
        var from = hasCustom ? new Date(options.customStart)
            : new Date(now.getTime() - (options.activeHours || 24) * 3600 * 1000);

        // Which field the calendar is currently editing, and which month it's showing.
        var editing = "from";
        var viewMonth = new Date(from.getFullYear(), from.getMonth(), 1);

        popover.innerHTML = template();
        var els = {
            presets: popover.querySelector(".trp__presets"),
            tabFrom: popover.querySelector('[data-tab="from"]'),
            tabTo: popover.querySelector('[data-tab="to"]'),
            calTitle: popover.querySelector(".trp__cal-title"),
            grid: popover.querySelector(".trp__grid"),
            prev: popover.querySelector(".trp__nav--prev"),
            next: popover.querySelector(".trp__nav--next"),
            hour: popover.querySelector(".trp__hour"),
            minute: popover.querySelector(".trp__minute"),
            summary: popover.querySelector(".trp__summary"),
            error: popover.querySelector(".trp__error"),
            apply: popover.querySelector(".trp__apply"),
            cancel: popover.querySelector(".trp__cancel")
        };

        // ---- preset buttons ----
        PRESETS.forEach(function (p) {
            var b = document.createElement("button");
            b.type = "button";
            b.className = "trp__preset" + (!hasCustom && options.activeHours === p.hours ? " trp__preset--active" : "");
            b.textContent = p.label;
            b.addEventListener("click", function () { options.onPreset(p.hours); });
            els.presets.appendChild(b);
        });

        function currentField() { return editing === "from" ? from : to; }
        function setCurrentField(d) { if (editing === "from") { from = d; } else { to = d; } }

        function renderTabs() {
            els.tabFrom.classList.toggle("trp__tab--active", editing === "from");
            els.tabTo.classList.toggle("trp__tab--active", editing === "to");
            els.tabFrom.querySelector(".trp__tab-val").textContent = fmtDateTime(from);
            els.tabTo.querySelector(".trp__tab-val").textContent = fmtDateTime(to);
        }

        function renderTime() {
            var d = currentField();
            els.hour.value = pad(d.getHours());
            els.minute.value = pad(d.getMinutes());
        }

        function renderSummary() {
            var ms = to - from;
            var valid = !isNaN(ms) && ms > 0;
            els.summary.textContent = valid
                ? "Range: " + fmtDateTime(from) + "  →  " + fmtDateTime(to)
                : "Pick a valid range (start before end).";
            els.summary.classList.toggle("trp__summary--bad", !valid);
        }

        function renderCalendar() {
            els.calTitle.textContent = MONTHS[viewMonth.getMonth()] + " " + viewMonth.getFullYear();
            els.grid.innerHTML = "";

            DOW.forEach(function (d) {
                var h = document.createElement("div");
                h.className = "trp__dow";
                h.textContent = d;
                els.grid.appendChild(h);
            });

            var firstDay = new Date(viewMonth.getFullYear(), viewMonth.getMonth(), 1).getDay();
            var daysInMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() + 1, 0).getDate();
            var sel = currentField();
            var today = new Date();

            for (var i = 0; i < firstDay; i++) {
                els.grid.appendChild(document.createElement("div"));
            }
            for (var day = 1; day <= daysInMonth; day++) {
                var cellDate = new Date(viewMonth.getFullYear(), viewMonth.getMonth(), day);
                var cell = document.createElement("button");
                cell.type = "button";
                cell.className = "trp__day";
                if (sameDay(cellDate, sel)) { cell.classList.add("trp__day--selected"); }
                if (sameDay(cellDate, today)) { cell.classList.add("trp__day--today"); }
                // Highlight the days within the selected range.
                if (cellDate >= stripTime(from) && cellDate <= stripTime(to)) {
                    cell.classList.add("trp__day--inrange");
                }
                cell.textContent = String(day);
                (function (cd) {
                    cell.addEventListener("click", function () { pickDay(cd); });
                })(cellDate);
                els.grid.appendChild(cell);
            }
        }

        function stripTime(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()); }

        function pickDay(cellDate) {
            var d = currentField();
            // Keep the existing hour/minute, change only the date.
            var updated = new Date(cellDate.getFullYear(), cellDate.getMonth(), cellDate.getDate(),
                d.getHours(), d.getMinutes());
            setCurrentField(updated);
            renderAll();
        }

        function renderAll() {
            renderTabs();
            renderTime();
            renderCalendar();
            renderSummary();
            els.error.hidden = true;
        }

        // ---- wiring ----
        els.tabFrom.addEventListener("click", function () {
            editing = "from"; viewMonth = new Date(from.getFullYear(), from.getMonth(), 1); renderAll();
        });
        els.tabTo.addEventListener("click", function () {
            editing = "to"; viewMonth = new Date(to.getFullYear(), to.getMonth(), 1); renderAll();
        });
        els.prev.addEventListener("click", function () {
            viewMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() - 1, 1); renderCalendar();
        });
        els.next.addEventListener("click", function () {
            viewMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() + 1, 1); renderCalendar();
        });

        function clampNum(input, min, max) {
            var v = parseInt(input.value, 10);
            if (isNaN(v)) { v = min; }
            return Math.max(min, Math.min(max, v));
        }
        els.hour.addEventListener("change", function () {
            var d = currentField();
            d = new Date(d.getFullYear(), d.getMonth(), d.getDate(), clampNum(els.hour, 0, 23), d.getMinutes());
            setCurrentField(d); renderAll();
        });
        els.minute.addEventListener("change", function () {
            var d = currentField();
            d = new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours(), clampNum(els.minute, 0, 59));
            setCurrentField(d); renderAll();
        });

        els.cancel.addEventListener("click", close);
        els.apply.addEventListener("click", function () {
            if (isNaN(from) || isNaN(to)) { return showError("Those dates don't look valid."); }
            if (from >= to) { return showError("The start must be before the end."); }
            if (to > new Date(Date.now() + 60000)) { return showError("The end can't be in the future."); }
            options.onApply(from, to);
        });

        function showError(msg) { els.error.textContent = msg; els.error.hidden = false; }

        function open() { popover.hidden = false; button.setAttribute("aria-expanded", "true"); renderAll(); }
        function close() { popover.hidden = true; button.setAttribute("aria-expanded", "false"); }

        button.addEventListener("click", function (ev) {
            ev.stopPropagation();
            if (popover.hidden) { open(); } else { close(); }
        });
        // Keep clicks inside the popover from reaching the document handler below. Clicking a day
        // rebuilds the grid (detaching the clicked node), so a contains() check on the document
        // handler would wrongly treat it as an outside click and close the picker mid-selection.
        popover.addEventListener("click", function (ev) { ev.stopPropagation(); });
        document.addEventListener("click", function (ev) {
            if (!popover.hidden && !popover.contains(ev.target) && ev.target !== button && !button.contains(ev.target)) {
                close();
            }
        });

        renderAll();
        return { open: open, close: close };
    }

    function template() {
        return '' +
            '<div class="trp">' +
            '  <div class="trp__col trp__col--presets">' +
            '    <p class="trp__heading">Quick ranges</p>' +
            '    <div class="trp__presets"></div>' +
            '  </div>' +
            '  <div class="trp__col trp__col--cal">' +
            '    <div class="trp__tabs">' +
            '      <button type="button" class="trp__tab trp__tab--active" data-tab="from">' +
            '        <span class="trp__tab-label">From</span><span class="trp__tab-val">—</span></button>' +
            '      <button type="button" class="trp__tab" data-tab="to">' +
            '        <span class="trp__tab-label">To</span><span class="trp__tab-val">—</span></button>' +
            '    </div>' +
            '    <div class="trp__cal-head">' +
            '      <button type="button" class="trp__nav trp__nav--prev" aria-label="Previous month">‹</button>' +
            '      <span class="trp__cal-title"></span>' +
            '      <button type="button" class="trp__nav trp__nav--next" aria-label="Next month">›</button>' +
            '    </div>' +
            '    <div class="trp__grid"></div>' +
            '    <div class="trp__time">' +
            '      <span class="trp__time-label">Time</span>' +
            '      <input type="number" class="trp__hour" min="0" max="23" inputmode="numeric" aria-label="Hour" />' +
            '      <span class="trp__time-colon">:</span>' +
            '      <input type="number" class="trp__minute" min="0" max="59" inputmode="numeric" aria-label="Minute" />' +
            '      <span class="trp__time-hint">24-hour (HH:MM)</span>' +
            '    </div>' +
            '    <p class="trp__summary"></p>' +
            '    <p class="trp__error" hidden></p>' +
            '    <div class="trp__actions">' +
            '      <button type="button" class="detail__action-btn trp__cancel">Cancel</button>' +
            '      <button type="button" class="api-analyzer__btn trp__apply">Apply</button>' +
            '    </div>' +
            '  </div>' +
            '</div>';
    }

    window.TimeRangePicker = { attach: attach };
})();
