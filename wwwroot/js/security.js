(function () {
    "use strict";

    document.addEventListener("DOMContentLoaded", function () {
        var form = document.getElementById("securityForm");
        var input = document.getElementById("targetUrl");
        if (!form || !input) { return; }

        var methodEl = document.getElementById("method");
        var extras = document.getElementById("requestExtras");
        var bodyEl = document.getElementById("requestBody");
        var headersEl = document.getElementById("requestHeaders");
        var contentTypeEl = document.getElementById("contentType");

        var loading = document.getElementById("securityLoading");
        var errorEl = document.getElementById("securityError");
        var content = document.getElementById("securityContent");
        var empty = document.getElementById("securityEmpty");

        var tokenEl = document.getElementById("bearerToken");
        var tokenToggle = document.getElementById("bearerToggle");

        var vars = window.SecurityVars;

        // Show/hide the bearer token (eye icon), like Bruno/Postman.
        if (tokenToggle && tokenEl) {
            tokenToggle.addEventListener("click", function () {
                var show = tokenEl.type === "password";
                tokenEl.type = show ? "text" : "password";
                tokenToggle.classList.toggle("is-on", show);
            });
        }

        // ---- Environment variables (Bruno-style {{name}}) ---------------------
        var varsList = document.getElementById("secVarsList");
        var varsAdd = document.getElementById("secVarsAdd");
        var varsCount = document.getElementById("secVarsCount");

        function renderVars() {
            if (!varsList || !vars) { return; }
            var list = vars.load();
            if (list.length === 0) {
                list = [{ name: "", value: "", secret: false }];
            }
            varsList.innerHTML = "";
            list.forEach(function (v, i) { varsList.appendChild(varRow(v, i)); });
            updateVarsCount();
        }

        function varRow(v, index) {
            var row = document.createElement("div");
            row.className = "sec-vars__row";

            var name = document.createElement("input");
            name.type = "text";
            name.className = "api-analyzer__input sec-vars__name";
            name.placeholder = "name";
            name.value = v.name || "";

            var value = document.createElement("input");
            value.type = v.secret ? "password" : "text";
            value.className = "api-analyzer__input sec-vars__value";
            value.placeholder = "value";
            value.value = v.value || "";

            var secret = document.createElement("button");
            secret.type = "button";
            secret.className = "sec-vars__secret" + (v.secret ? " is-on" : "");
            secret.title = "Mask this value (treat as secret)";
            secret.textContent = "👁";
            secret.addEventListener("click", function () {
                var nowSecret = value.type === "text";
                value.type = nowSecret ? "password" : "text";
                secret.classList.toggle("is-on", nowSecret);
                persistVars();
            });

            var remove = document.createElement("button");
            remove.type = "button";
            remove.className = "sec-vars__remove";
            remove.title = "Remove";
            remove.textContent = "✕";
            remove.addEventListener("click", function () {
                row.remove();
                persistVars();
                if (!varsList.children.length) { renderVars(); }
            });

            name.addEventListener("input", function () { persistVars(); refreshBodyStatus(); });
            value.addEventListener("input", persistVars);

            row.appendChild(name);
            row.appendChild(value);
            row.appendChild(secret);
            row.appendChild(remove);
            return row;
        }

        function collectVars() {
            if (!varsList) { return []; }
            return Array.prototype.map.call(varsList.querySelectorAll(".sec-vars__row"), function (row) {
                return {
                    name: row.querySelector(".sec-vars__name").value.trim(),
                    value: row.querySelector(".sec-vars__value").value,
                    secret: row.querySelector(".sec-vars__value").type === "password"
                };
            }).filter(function (v) { return v.name || v.value; });
        }

        function persistVars() {
            if (vars) { vars.save(collectVars()); }
            updateVarsCount();
        }

        function updateVarsCount() {
            if (!varsCount || !vars) { return; }
            var n = collectVars().filter(function (v) { return v.name; }).length;
            varsCount.textContent = n ? "(" + n + ")" : "";
        }

        if (varsAdd) {
            varsAdd.addEventListener("click", function () {
                varsList.appendChild(varRow({ name: "", value: "", secret: false }, varsList.children.length));
            });
        }
        renderVars();

        // Show the body/headers fields only for methods that carry a payload.
        function syncExtras() {
            if (!extras || !methodEl) { return; }
            var m = methodEl.value;
            extras.hidden = !(m === "POST" || m === "PUT" || m === "PATCH");
        }
        if (methodEl) { methodEl.addEventListener("change", syncExtras); }
        syncExtras();

        // ---- JSON body editor niceties ----------------------------------------
        var bodyStatus = document.getElementById("bodyStatus");
        var bodyFormat = document.getElementById("bodyFormat");

        // Replaces {{vars}} with a JSON-safe placeholder so validation isn't broken by them.
        function bodyForValidation(text) {
            return String(text).replace(/\{\{\s*[\w.-]+\s*\}\}/g, '"__var__"');
        }

        function refreshBodyStatus() {
            if (!bodyStatus || !bodyEl) { return; }
            var text = bodyEl.value.trim();
            if (!text) {
                bodyStatus.textContent = "";
                bodyStatus.className = "sec-body__status";
                return;
            }
            try {
                JSON.parse(bodyForValidation(text));
                bodyStatus.textContent = "✓ valid JSON";
                bodyStatus.className = "sec-body__status sec-body__status--ok";
            } catch (e) {
                bodyStatus.textContent = "⚠ invalid JSON";
                bodyStatus.className = "sec-body__status sec-body__status--bad";
            }
        }

        function formatBody() {
            if (!bodyEl) { return; }
            var text = bodyEl.value.trim();
            if (!text) { return; }
            // Protect variables through the round-trip, then restore them after pretty-printing.
            var stash = [];
            var guarded = text.replace(/\{\{\s*[\w.-]+\s*\}\}/g, function (m) {
                stash.push(m);
                return '"__var' + (stash.length - 1) + '__"';
            });
            try {
                var pretty = JSON.stringify(JSON.parse(guarded), null, 2);
                pretty = pretty.replace(/"__var(\d+)__"/g, function (whole, i) { return stash[Number(i)]; });
                bodyEl.value = pretty;
                refreshBodyStatus();
            } catch (e) {
                refreshBodyStatus();
            }
        }

        if (bodyFormat) { bodyFormat.addEventListener("click", formatBody); }
        if (bodyEl) {
            bodyEl.addEventListener("input", refreshBodyStatus);
            // Tab inserts two spaces instead of leaving the field.
            bodyEl.addEventListener("keydown", function (ev) {
                if (ev.key === "Tab") {
                    ev.preventDefault();
                    var s = bodyEl.selectionStart, e = bodyEl.selectionEnd;
                    bodyEl.value = bodyEl.value.slice(0, s) + "  " + bodyEl.value.slice(e);
                    bodyEl.selectionStart = bodyEl.selectionEnd = s + 2;
                }
            });
        }

        form.addEventListener("submit", function (ev) {
            ev.preventDefault();
            var url = input.value.trim();
            if (!url) { return; }
            runScan(url);
        });

        function parseHeaders(text) {
            var headers = {};
            if (!text) { return headers; }
            text.split(/\r?\n/).forEach(function (line) {
                var trimmed = line.trim();
                if (!trimmed) { return; }
                var idx = trimmed.indexOf(":");
                if (idx <= 0) { return; }
                var name = trimmed.slice(0, idx).trim();
                var value = trimmed.slice(idx + 1).trim();
                if (name) { headers[name] = value; }
            });
            return headers;
        }

        // Returns true when the header collection already has a key (case-insensitive).
        function hasHeader(headers, name) {
            return Object.keys(headers).some(function (k) {
                return k.toLowerCase() === name.toLowerCase();
            });
        }

        function runScan(url) {
            empty.hidden = true;
            content.hidden = true;
            errorEl.hidden = true;
            loading.hidden = false;

            var method = methodEl ? methodEl.value : "GET";
            var sendsBody = (method === "POST" || method === "PUT" || method === "PATCH");

            var rawHeaderText = headersEl ? headersEl.value : "";
            var rawToken = tokenEl ? tokenEl.value.trim() : "";
            var rawBody = sendsBody && bodyEl ? bodyEl.value : null;

            // Resolve {{variables}} across every input, Bruno-style.
            var map = vars ? vars.toMap() : {};
            if (vars) {
                var unresolved = vars.findUnresolved([url, rawToken, rawHeaderText, rawBody], map);
                if (unresolved.length) {
                    loading.hidden = true;
                    showError("Unknown variable(s): " + unresolved.map(function (n) { return "{{" + n + "}}"; }).join(", ") +
                        ". Define them in Environment variables or remove the reference.");
                    return;
                }
            }

            var resolvedUrl = vars ? vars.substitute(url, map) : url;
            var headers = parseHeaders(vars ? vars.substitute(rawHeaderText, map) : rawHeaderText);

            // Build Authorization from the dedicated token field unless an explicit header was given.
            var token = vars ? vars.substitute(rawToken, map) : rawToken;
            if (token && !hasHeader(headers, "Authorization")) {
                // Accept a raw token or a full "Bearer xyz" / "Basic xyz" value.
                headers["Authorization"] = /^(bearer|basic|negotiate|digest)\s/i.test(token)
                    ? token
                    : "Bearer " + token;
            }

            var payload = {
                targetUrl: resolvedUrl,
                method: method,
                body: rawBody != null ? (vars ? vars.substitute(rawBody, map) : rawBody) : null,
                contentType: contentTypeEl && contentTypeEl.value.trim() ? contentTypeEl.value.trim() : "application/json",
                headers: headers
            };

            fetch("/Security/Scan", {
                method: "POST",
                headers: { "Content-Type": "application/json", "Accept": "application/json" },
                body: JSON.stringify(payload)
            })
                .then(function (r) {
                    if (r.status === 499) { return null; }
                    if (!r.ok) { throw new Error("the server returned status " + r.status + "."); }
                    return r.json();
                })
                .then(function (result) {
                    loading.hidden = true;
                    if (result === null) { return; }
                    if (!result || !result.hasResult) {
                        showError(result && result.errorMessage ? result.errorMessage : "The scan could not be completed.");
                        return;
                    }
                    render(result);
                    content.hidden = false;
                })
                .catch(function (e) {
                    loading.hidden = true;
                    showError("Sorry, the scan failed: " + e.message);
                });
        }

        function showError(message) {
            errorEl.textContent = message;
            errorEl.hidden = false;
        }

        function render(result) {
            renderSummary(result);
            renderAi(result.aiOverview);
            renderFindings(result.findings || []);

            var foot = document.getElementById("securityFootnote");
            foot.textContent = "Scanned " + (result.method || "GET") + " " + (result.targetUrl || "") +
                " — HTTP " + result.statusCode + " — generated " + formatDate(result.generatedAt) + ".";
        }

        function renderSummary(result) {
            var host = document.getElementById("securitySummary");
            var gradeClass = gradeValueClass(result.grade);
            host.innerHTML =
                card("Security Grade", escapeHtml(result.grade), gradeClass, "score " + result.score + "/100") +
                card("Critical / High", result.criticalCount + " / " + result.highCount,
                    (result.criticalCount + result.highCount) > 0 ? "metric-card__value--danger" : "metric-card__value--success",
                    "most severe issues") +
                card("Medium", String(result.mediumCount), result.mediumCount > 0 ? "metric-card__value--accent" : "", "moderate issues") +
                card("Low", String(result.lowCount), "", "hardening opportunities");
        }

        function card(label, value, valueClass, hint) {
            return '<div class="metric-card">' +
                '<p class="metric-card__label">' + escapeHtml(label) + '</p>' +
                '<p class="metric-card__value ' + (valueClass || "") + '">' + value + '</p>' +
                '<p class="metric-card__hint">' + escapeHtml(hint || "") + '</p>' +
                '</div>';
        }

        function gradeValueClass(grade) {
            if (grade === "A" || grade === "B") { return "metric-card__value--success"; }
            if (grade === "C") { return "metric-card__value--accent"; }
            return "metric-card__value--danger";
        }

        function renderAi(ai) {
            var section = document.getElementById("securityAiSection");
            var summary = document.getElementById("securityAiSummary");
            var priorities = document.getElementById("securityAiPriorities");
            var conf = document.getElementById("securityAiConfidence");

            if (!ai) { section.hidden = true; return; }
            section.hidden = false;

            if (ai.fromAi) {
                summary.textContent = ai.summary || "";
                conf.innerHTML = ai.confidence ? ('Confidence <span>' + ai.confidence + '%</span>') : "";
                priorities.innerHTML = (ai.priorities || []).map(function (p) {
                    return "<li>" + escapeHtml(p) + "</li>";
                }).join("");
            } else {
                summary.textContent = ai.errorMessage || "AI overview is unavailable.";
                conf.innerHTML = "";
                priorities.innerHTML = "";
            }
        }

        function renderFindings(findings) {
            var host = document.getElementById("securityFindings");
            if (!findings.length) {
                host.innerHTML = '<div class="sec-clean">' +
                    '<h2 class="sec-clean__title">✓ No issues detected</h2>' +
                    '<p class="sec-clean__text">All checked security controls are present on the response. ' +
                    'Re-run periodically and consider deeper authenticated and payload-level testing.</p>' +
                    '</div>';
                return;
            }

            host.innerHTML = findings.map(renderFinding).join("");
        }

        function renderFinding(f) {
            var sev = (f.severity || "Info");
            var status = f.status === "Fixed" ? "FIXED" : "NOT FIXED";
            var statusClass = f.status === "Fixed" ? "sec-status--fixed" : "sec-status--notfixed";

            var html = '<section class="sec-issue">' +
                '<div class="sec-issue__bar">Issue #' + f.number + ': ' + escapeHtml(f.title) + '</div>' +
                '<div class="sec-issue__body">' +
                block("Severity", '<span class="sec-sev sec-sev--' + sev.toLowerCase() + '">' + escapeHtml(sev.toUpperCase()) + '</span>') +
                block("Status", '<span class="sec-status ' + statusClass + '">' + status + '</span>') +
                block("Synopsis", '<p class="sec-issue__text">' + escapeHtml(f.synopsis) + '</p>') +
                block("Impact", '<p class="sec-issue__text">' + escapeHtml(f.impact) + '</p>') +
                block("Recommendation", '<p class="sec-issue__text">' + escapeHtml(f.recommendation) + '</p>');

            if (f.reference) {
                html += block("Reference", '<a class="sec-issue__ref" href="' + escapeAttr(f.reference) +
                    '" target="_blank" rel="noopener noreferrer">' + escapeHtml(f.reference) + '</a>');
            }

            if (f.proofOfConcept || f.evidence) {
                var poc = "";
                if (f.proofOfConcept) {
                    poc += '<p class="sec-issue__text"><strong>PoC Note:</strong> ' + escapeHtml(f.proofOfConcept) + '</p>';
                }
                if (f.evidence) {
                    poc += '<pre class="sec-issue__evidence">' + escapeHtml(f.evidence) + '</pre>';
                }
                html += block("Proof Of Concept", poc);
            }

            html += '</div></section>';
            return html;
        }

        function block(heading, inner) {
            return '<div class="sec-issue__section">' +
                '<h3 class="sec-issue__heading">' + escapeHtml(heading) + '</h3>' + inner + '</div>';
        }

        // ---- Helpers ----------------------------------------------------------
        function formatDate(iso) {
            if (!iso) { return ""; }
            var d = new Date(iso);
            return isNaN(d.getTime()) ? "" : d.toLocaleString();
        }

        function escapeHtml(value) {
            if (value == null) { return ""; }
            return String(value)
                .replace(/&/g, "&amp;")
                .replace(/</g, "&lt;")
                .replace(/>/g, "&gt;")
                .replace(/"/g, "&quot;");
        }

        function escapeAttr(value) {
            return escapeHtml(value).replace(/'/g, "&#39;");
        }
    });
})();
