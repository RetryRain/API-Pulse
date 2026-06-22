// Persists Bruno-style environment variables for the Security Audit page so they can be
// reused across scans. Variables are stored as an array of { name, value, secret } objects
// in localStorage. Exposes window.SecurityVars with a small synchronous API.
(function () {
    "use strict";

    var KEY = "apiHub.securityVars";

    function load() {
        try {
            var raw = window.localStorage.getItem(KEY);
            if (!raw) { return []; }
            var parsed = JSON.parse(raw);
            return Array.isArray(parsed) ? parsed.filter(isValid) : [];
        } catch (e) {
            return [];
        }
    }

    function save(list) {
        try {
            window.localStorage.setItem(KEY, JSON.stringify((list || []).filter(isValid)));
        } catch (e) {
            // Storage full / disabled - variables simply won't persist this session.
        }
    }

    function isValid(v) {
        return v && typeof v.name === "string";
    }

    // Builds a quick name -> value lookup (last definition wins, blank names ignored).
    function toMap(list) {
        var map = {};
        (list || load()).forEach(function (v) {
            var name = (v.name || "").trim();
            if (name) { map[name] = v.value == null ? "" : String(v.value); }
        });
        return map;
    }

    // Replaces {{name}} tokens in text using the current variables. Unknown tokens are
    // left untouched so the user can see what didn't resolve.
    function substitute(text, map) {
        if (text == null) { return text; }
        map = map || toMap();
        return String(text).replace(/\{\{\s*([\w.-]+)\s*\}\}/g, function (whole, name) {
            return Object.prototype.hasOwnProperty.call(map, name) ? map[name] : whole;
        });
    }

    // Returns the distinct {{names}} referenced in any of the supplied strings that are
    // NOT defined in the current variable set (so the UI can warn before scanning).
    function findUnresolved(strings, map) {
        map = map || toMap();
        var missing = {};
        (strings || []).forEach(function (text) {
            if (text == null) { return; }
            var re = /\{\{\s*([\w.-]+)\s*\}\}/g;
            var m;
            while ((m = re.exec(String(text))) !== null) {
                if (!Object.prototype.hasOwnProperty.call(map, m[1])) {
                    missing[m[1]] = true;
                }
            }
        });
        return Object.keys(missing);
    }

    window.SecurityVars = {
        load: load,
        save: save,
        toMap: toMap,
        substitute: substitute,
        findUnresolved: findUnresolved
    };
})();
