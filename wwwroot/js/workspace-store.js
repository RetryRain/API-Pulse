// Tiny IndexedDB helper to persist the Application Insights Workspace ID so the user
// doesn't have to paste it on every visit. Falls back to localStorage if IndexedDB is
// unavailable. Exposes window.WorkspaceStore.{get,set,clear} (all return Promises).
(function () {
    "use strict";

    var DB_NAME = "ApiAnalyzerDB";
    var STORE = "settings";
    var KEY = "workspaceId";

    function openDb() {
        return new Promise(function (resolve, reject) {
            if (!("indexedDB" in window)) {
                reject(new Error("IndexedDB unavailable"));
                return;
            }
            var req = window.indexedDB.open(DB_NAME, 1);
            req.onupgradeneeded = function () {
                var db = req.result;
                if (!db.objectStoreNames.contains(STORE)) {
                    db.createObjectStore(STORE);
                }
            };
            req.onsuccess = function () { resolve(req.result); };
            req.onerror = function () { reject(req.error || new Error("IndexedDB open failed")); };
        });
    }

    function idbGet() {
        return openDb().then(function (db) {
            return new Promise(function (resolve, reject) {
                var tx = db.transaction(STORE, "readonly");
                var req = tx.objectStore(STORE).get(KEY);
                req.onsuccess = function () { resolve(req.result || null); };
                req.onerror = function () { reject(req.error); };
            });
        });
    }

    function idbSet(value) {
        return openDb().then(function (db) {
            return new Promise(function (resolve, reject) {
                var tx = db.transaction(STORE, "readwrite");
                tx.objectStore(STORE).put(value, KEY);
                tx.oncomplete = function () { resolve(); };
                tx.onerror = function () { reject(tx.error); };
            });
        });
    }

    function idbClear() {
        return openDb().then(function (db) {
            return new Promise(function (resolve, reject) {
                var tx = db.transaction(STORE, "readwrite");
                tx.objectStore(STORE).delete(KEY);
                tx.oncomplete = function () { resolve(); };
                tx.onerror = function () { reject(tx.error); };
            });
        });
    }

    window.WorkspaceStore = {
        get: function () {
            return idbGet().catch(function () {
                try { return localStorage.getItem(KEY); } catch (e) { return null; }
            });
        },
        set: function (value) {
            return idbSet(value).catch(function () {
                try { localStorage.setItem(KEY, value); } catch (e) { /* ignore */ }
            });
        },
        clear: function () {
            return idbClear().catch(function () {
                try { localStorage.removeItem(KEY); } catch (e) { /* ignore */ }
            });
        }
    };
})();
