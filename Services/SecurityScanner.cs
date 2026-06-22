using System.Text;
using WebApplication1.Models;

namespace WebApplication1.Services
{
    /// <summary>
    /// Audits an endpoint's HTTP security posture by probing it and evaluating the real response
    /// against a rule set (security headers, TLS, cookie flags, CORS, information disclosure).
    /// This is deterministic and requires no external services, so it always works; an optional AI
    /// overview is layered on top by the controller.
    /// </summary>
    public class SecurityScanner : ISecurityScanner
    {
        private static readonly HashSet<string> AllowedMethods =
            new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

        // Headers that belong on the content (body) rather than the request, applied via HttpContent.
        private static readonly HashSet<string> ContentHeaderNames =
            new(StringComparer.OrdinalIgnoreCase) { "Content-Type", "Content-Length", "Content-Encoding", "Content-Language", "Content-Disposition" };

        private readonly HttpClient _http;
        private readonly ILogger<SecurityScanner> _logger;

        public SecurityScanner(HttpClient http, ILogger<SecurityScanner> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<SecurityScanResult> ScanAsync(SecurityScanRequest request, CancellationToken cancellationToken = default)
        {
            var input = request.TargetUrl?.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return Error("Enter the API or web URL you want to audit (for example https://api.example.com/v1/health).");
            }

            // Default to https:// when the user omits a scheme.
            if (!input.Contains("://", StringComparison.Ordinal))
            {
                input = "https://" + input;
            }

            if (!Uri.TryCreate(input, UriKind.Absolute, out var target) ||
                (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            {
                return Error("That doesn't look like a valid absolute http/https URL.");
            }

            // Resolve and validate the HTTP method. Default to GET when unspecified.
            var methodName = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.Trim().ToUpperInvariant();
            if (!AllowedMethods.Contains(methodName))
            {
                return Error($"Unsupported HTTP method '{methodName}'. Use one of: {string.Join(", ", AllowedMethods)}.");
            }
            var method = new HttpMethod(methodName);
            var sendsBody = methodName is "POST" or "PUT" or "PATCH";

            HttpResponseMessage response;
            try
            {
                using var probe = new HttpRequestMessage(method, target);
                probe.Headers.TryAddWithoutValidation("User-Agent", "ApiIntelligenceHub-SecurityScanner/1.0");
                probe.Headers.TryAddWithoutValidation("Accept", "*/*");

                // Attach caller-supplied headers (e.g. Authorization) so authenticated endpoints
                // can be audited. Content headers are applied to the body below.
                ApplyCustomHeaders(probe, request.Headers, out var hasAuthHeader);

                if (sendsBody && !string.IsNullOrEmpty(request.Body))
                {
                    var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/json" : request.ContentType.Trim();
                    probe.Content = new StringContent(request.Body, Encoding.UTF8, contentType);
                }

                response = await _http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                using (response)
                {
                    var result = new SecurityScanResult
                    {
                        HasResult = true,
                        TargetUrl = target.ToString(),
                        Method = methodName,
                        StatusCode = (int)response.StatusCode,
                        RequestSnippet = BuildRequestSnippet(target, methodName, request, sendsBody),
                        ResponseSnippet = BuildResponseSnippet(response),
                    };

                    EvaluateRules(target, methodName, sendsBody, hasAuthHeader, response, result);
                    ScoreAndGrade(result);
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // genuine client cancellation - let the caller turn it into a quiet 499
            }
            catch (OperationCanceledException)
            {
                return Error($"The request to {target} timed out before a response was received.");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Security probe failed for {Target}.", target);
                return Error($"Could not reach {target}. {ex.Message}");
            }
        }

        // ---- Rule set ------------------------------------------------------------

        private static void EvaluateRules(Uri target, string method, bool sendsBody, bool hasAuthHeader, HttpResponseMessage response, SecurityScanResult result)
        {
            var findings = result.Findings;
            var isHttps = target.Scheme == Uri.UriSchemeHttps;

            string? Header(string name) => GetHeader(response, name);
            bool Has(string name) => !string.IsNullOrWhiteSpace(Header(name));

            // 1. Cleartext HTTP
            if (!isHttps)
            {
                findings.Add(new SecurityFinding
                {
                    Title = "API served over cleartext HTTP",
                    Severity = SecuritySeverity.High,
                    Status = SecurityStatus.NotFixed,
                    Synopsis = "The endpoint was reached over plain HTTP rather than HTTPS, so traffic is not encrypted in transit.",
                    Impact = "An attacker positioned on the network can read or modify requests and responses (man-in-the-middle), capturing credentials, tokens and personal data.",
                    Recommendation = "Serve the API exclusively over HTTPS with a valid TLS certificate and redirect all HTTP traffic to HTTPS.",
                    Reference = "https://cwe.mitre.org/data/definitions/319.html",
                    ProofOfConcept = "In the given PoC observe that the endpoint responds over http:// without enforcing TLS.",
                    Evidence = $"Scheme: {target.Scheme}",
                });
            }

            // 2. HSTS (only meaningful over HTTPS)
            if (isHttps && !Has("Strict-Transport-Security"))
            {
                findings.Add(new SecurityFinding
                {
                    Title = "HTTP Strict Transport Security not implemented",
                    Severity = SecuritySeverity.Low,
                    Status = SecurityStatus.NotFixed,
                    Synopsis = "HTTP Strict Transport Security (HSTS) is not implemented, meaning the server does not instruct browsers to enforce secure (HTTPS) connections. As a result, clients may initially connect over insecure HTTP before any redirection occurs.",
                    Impact = "Without HSTS, attackers can perform man-in-the-middle (MITM) or SSL stripping attacks to downgrade connections from HTTPS to HTTP, potentially intercepting sensitive data such as credentials, session tokens, or personal information.",
                    Recommendation = "Enable HSTS by adding the Strict-Transport-Security header with an appropriate max-age (e.g. max-age=31536000), and include directives like includeSubDomains and preload if applicable. Ensure HTTPS is enforced before enabling HSTS to avoid accessibility issues.",
                    Reference = "https://cwe.mitre.org/data/definitions/319.html",
                    ProofOfConcept = "In the given PoC observe that the Strict-Transport-Security header is not implemented.",
                });
            }

            // 3. Content-Security-Policy
            if (!Has("Content-Security-Policy"))
            {
                findings.Add(new SecurityFinding
                {
                    Title = "Missing Content Security Policy (CSP) Header",
                    Severity = SecuritySeverity.Low,
                    Status = SecurityStatus.NotFixed,
                    Synopsis = "The API response does not include the Content-Security-Policy (CSP) header. This header is a crucial security control that restricts how content such as JavaScript, images, and styles can be loaded in a browser or WebView.",
                    Impact = "Without a CSP, the app becomes more susceptible to client-side attacks like Cross-Site Scripting (XSS), especially if any part of the response renders web content. An attacker may exploit this to execute malicious scripts in the user's context.",
                    Recommendation = "Include a secure CSP header in API or web responses that could be rendered in browsers or mobile WebViews.",
                    Reference = "https://cwe.mitre.org/data/definitions/693.html",
                    ProofOfConcept = "In the given PoC observe that the Content-Security-Policy header is not implemented.",
                });
            }

            // 4. X-Content-Type-Options
            if (!string.Equals(Header("X-Content-Type-Options"), "nosniff", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SecurityFinding
                {
                    Title = "Missing X-Content-Type-Options Header",
                    Severity = SecuritySeverity.Low,
                    Status = SecurityStatus.NotFixed,
                    Synopsis = "The response does not set X-Content-Type-Options: nosniff, so browsers may MIME-sniff the response and interpret it as a different content type than declared.",
                    Impact = "MIME sniffing can let an attacker coax the browser into executing a response as script or another active type, enabling content-type confusion and XSS-style attacks.",
                    Recommendation = "Add the response header X-Content-Type-Options: nosniff to every response.",
                    Reference = "https://cwe.mitre.org/data/definitions/693.html",
                    ProofOfConcept = "In the given PoC observe that the X-Content-Type-Options: nosniff header is not present.",
                });
            }

            // 5. Clickjacking protection (X-Frame-Options OR CSP frame-ancestors)
            var csp = Header("Content-Security-Policy");
            var hasFrameAncestors = csp != null && csp.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase);
            if (!Has("X-Frame-Options") && !hasFrameAncestors)
            {
                findings.Add(new SecurityFinding
                {
                    Title = "Missing Clickjacking Protection (X-Frame-Options)",
                    Severity = SecuritySeverity.Medium,
                    Status = SecurityStatus.NotFixed,
                    Synopsis = "The response does not set X-Frame-Options nor a CSP frame-ancestors directive, so the content can be embedded in a frame or iframe on an attacker-controlled site.",
                    Impact = "An attacker can overlay or frame the page to trick users into clicking hidden elements (clickjacking), performing unintended actions with their authenticated session.",
                    Recommendation = "Set X-Frame-Options: DENY (or SAMEORIGIN) and/or a Content-Security-Policy with frame-ancestors 'none'.",
                    Reference = "https://cwe.mitre.org/data/definitions/1021.html",
                    ProofOfConcept = "In the given PoC observe that neither X-Frame-Options nor CSP frame-ancestors is implemented.",
                });
            }

            // 6. Referrer-Policy
            if (!Has("Referrer-Policy"))
            {
                findings.Add(new SecurityFinding
                {
                    Title = "Missing Referrer-Policy Header",
                    Severity = SecuritySeverity.Low,
                    Status = SecurityStatus.NotFixed,
                    Synopsis = "The response does not specify a Referrer-Policy, so the full URL (which may contain sensitive tokens or identifiers) can be sent in the Referer header to third-party destinations.",
                    Impact = "Sensitive data embedded in URLs may leak to external sites via the Referer header, aiding reconnaissance or token theft.",
                    Recommendation = "Set a restrictive Referrer-Policy such as no-referrer or strict-origin-when-cross-origin.",
                    Reference = "https://cwe.mitre.org/data/definitions/200.html",
                    ProofOfConcept = "In the given PoC observe that the Referrer-Policy header is not implemented.",
                });
            }

            // 7. Permissions-Policy
            if (!Has("Permissions-Policy") && !Has("Feature-Policy"))
            {
                findings.Add(new SecurityFinding
                {
                    Title = "Missing Permissions-Policy Header",
                    Severity = SecuritySeverity.Low,
                    Status = SecurityStatus.NotFixed,
                    Synopsis = "The response does not set a Permissions-Policy header, leaving powerful browser features (camera, geolocation, microphone, etc.) unrestricted for any rendered content.",
                    Impact = "If the response is rendered in a browser or WebView, embedded or injected content could access sensitive device features that should be disabled.",
                    Recommendation = "Add a Permissions-Policy header that disables unused features, e.g. geolocation=(), camera=(), microphone=().",
                    Reference = "https://cwe.mitre.org/data/definitions/693.html",
                    ProofOfConcept = "In the given PoC observe that the Permissions-Policy header is not implemented.",
                });
            }

            // 8. CORS misconfiguration (wildcard, worse with credentials)
            var acao = Header("Access-Control-Allow-Origin");
            if (acao == "*")
            {
                var allowsCreds = string.Equals(Header("Access-Control-Allow-Credentials"), "true", StringComparison.OrdinalIgnoreCase);
                findings.Add(new SecurityFinding
                {
                    Title = "Overly Permissive CORS Policy",
                    Severity = allowsCreds ? SecuritySeverity.High : SecuritySeverity.Medium,
                    Status = SecurityStatus.NotFixed,
                    Synopsis = "The API returns Access-Control-Allow-Origin: *, allowing any website to read its responses via the browser." +
                               (allowsCreds ? " It also sets Access-Control-Allow-Credentials: true." : string.Empty),
                    Impact = allowsCreds
                        ? "Combining a wildcard origin with credentials lets any malicious site make authenticated cross-origin requests and read the responses, enabling account takeover and data theft."
                        : "Any origin can read responses from this API, which may expose data intended only for trusted front-ends.",
                    Recommendation = "Restrict Access-Control-Allow-Origin to an explicit allow-list of trusted origins. Never combine a wildcard origin with Access-Control-Allow-Credentials: true.",
                    Reference = "https://cwe.mitre.org/data/definitions/942.html",
                    ProofOfConcept = "In the given PoC observe that Access-Control-Allow-Origin is set to '*'.",
                    Evidence = "Access-Control-Allow-Origin: *" + (allowsCreds ? "; Access-Control-Allow-Credentials: true" : string.Empty),
                });
            }

            // 9. Information disclosure via server/technology headers
            foreach (var name in new[] { "Server", "X-Powered-By", "X-AspNet-Version", "X-AspNetMvc-Version" })
            {
                var value = Header(name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    findings.Add(new SecurityFinding
                    {
                        Title = $"Information Disclosure via '{name}' Header",
                        Severity = SecuritySeverity.Low,
                        Status = SecurityStatus.NotFixed,
                        Synopsis = $"The response advertises server/technology details through the '{name}' header ('{value}').",
                        Impact = "Disclosing the server software or framework version helps attackers fingerprint the stack and target known vulnerabilities for that version.",
                        Recommendation = $"Remove or suppress the '{name}' header so the response does not reveal server or framework details.",
                        Reference = "https://cwe.mitre.org/data/definitions/200.html",
                        ProofOfConcept = $"In the given PoC observe that the response includes '{name}: {value}'.",
                        Evidence = $"{name}: {value}",
                    });
                }
            }

            // 10. Cookie flags (Secure / HttpOnly / SameSite)
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var cookie in cookies)
                {
                    var cookieName = cookie.Split('=', 2)[0].Trim();

                    if (isHttps && !cookie.Contains("Secure", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new SecurityFinding
                        {
                            Title = $"Cookie '{cookieName}' missing Secure flag",
                            Severity = SecuritySeverity.Medium,
                            Status = SecurityStatus.NotFixed,
                            Synopsis = $"The cookie '{cookieName}' is set without the Secure attribute, so it can be transmitted over unencrypted HTTP.",
                            Impact = "A cookie without Secure may be sent over plain HTTP, where a network attacker can capture it and hijack the session.",
                            Recommendation = "Add the Secure attribute to all cookies so they are only sent over HTTPS.",
                            Reference = "https://cwe.mitre.org/data/definitions/614.html",
                            ProofOfConcept = $"In the given PoC observe that the Set-Cookie for '{cookieName}' omits the Secure attribute.",
                            Evidence = Truncate(cookie, 200),
                        });
                    }

                    if (!cookie.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new SecurityFinding
                        {
                            Title = $"Cookie '{cookieName}' missing HttpOnly flag",
                            Severity = SecuritySeverity.Medium,
                            Status = SecurityStatus.NotFixed,
                            Synopsis = $"The cookie '{cookieName}' is set without the HttpOnly attribute, so it is accessible to client-side JavaScript.",
                            Impact = "Without HttpOnly, a successful XSS attack can read the cookie (e.g. a session token) and exfiltrate it.",
                            Recommendation = "Add the HttpOnly attribute to session and authentication cookies.",
                            Reference = "https://cwe.mitre.org/data/definitions/1004.html",
                            ProofOfConcept = $"In the given PoC observe that the Set-Cookie for '{cookieName}' omits the HttpOnly attribute.",
                            Evidence = Truncate(cookie, 200),
                        });
                    }

                    if (!cookie.Contains("SameSite", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new SecurityFinding
                        {
                            Title = $"Cookie '{cookieName}' missing SameSite attribute",
                            Severity = SecuritySeverity.Low,
                            Status = SecurityStatus.NotFixed,
                            Synopsis = $"The cookie '{cookieName}' does not specify a SameSite attribute, so the browser applies weaker cross-site defaults.",
                            Impact = "Cookies without an explicit SameSite policy can be sent on cross-site requests, increasing exposure to Cross-Site Request Forgery (CSRF).",
                            Recommendation = "Set SameSite=Lax or SameSite=Strict on cookies (use None only with Secure when cross-site use is required).",
                            Reference = "https://cwe.mitre.org/data/definitions/1275.html",
                            ProofOfConcept = $"In the given PoC observe that the Set-Cookie for '{cookieName}' omits the SameSite attribute.",
                            Evidence = Truncate(cookie, 200),
                        });
                    }
                }
            }

            // 11. State-changing method accepted without authentication (broken access control).
            //     Only meaningful for POST/PUT/PATCH/DELETE: a 2xx/3xx with no auth supplied suggests
            //     the operation ran unauthenticated.
            var isStateChanging = method is "POST" or "PUT" or "PATCH" or "DELETE";
            if (isStateChanging && !hasAuthHeader &&
                (int)response.StatusCode is >= 200 and < 400)
            {
                findings.Add(new SecurityFinding
                {
                    Title = $"State-changing {method} accepted without authentication",
                    Severity = SecuritySeverity.High,
                    Status = SecurityStatus.NotFixed,
                    Synopsis = $"A {method} request was accepted (HTTP {(int)response.StatusCode}) even though no Authorization header or credentials were supplied.",
                    Impact = "If a state-changing endpoint can be invoked without authentication, anyone on the internet may create, modify or delete data, leading to broken access control and data tampering.",
                    Recommendation = "Require authentication and authorization on all state-changing endpoints. Return 401/403 when credentials are missing or insufficient, and verify the caller is allowed to perform the action.",
                    Reference = "https://cwe.mitre.org/data/definitions/306.html",
                    ProofOfConcept = $"In the given PoC observe that the {method} request returned HTTP {(int)response.StatusCode} without any Authorization header.",
                    Evidence = $"{method} {target.PathAndQuery} -> {(int)response.StatusCode} (no Authorization sent)",
                });
            }

            // 12. CORS allowing state-changing cross-origin requests with credentials.
            if (isStateChanging)
            {
                var acaoState = Header("Access-Control-Allow-Origin");
                var allowsCredsState = string.Equals(Header("Access-Control-Allow-Credentials"), "true", StringComparison.OrdinalIgnoreCase);
                if (acaoState == "*" && allowsCredsState)
                {
                    findings.Add(new SecurityFinding
                    {
                        Title = "Cross-origin state change allowed with credentials",
                        Severity = SecuritySeverity.High,
                        Status = SecurityStatus.NotFixed,
                        Synopsis = $"The {method} response allows any origin (Access-Control-Allow-Origin: *) together with credentials, so a malicious site could drive authenticated state-changing requests from a victim's browser.",
                        Impact = "This enables cross-site request forgery (CSRF) style attacks against authenticated users, allowing an attacker's page to perform actions on their behalf.",
                        Recommendation = "Restrict CORS to an explicit allow-list of trusted origins, never combine a wildcard origin with credentials, and add anti-CSRF tokens / SameSite cookies for state-changing endpoints.",
                        Reference = "https://cwe.mitre.org/data/definitions/352.html",
                        ProofOfConcept = "In the given PoC observe that Access-Control-Allow-Origin is '*' while Access-Control-Allow-Credentials is 'true' on a state-changing method.",
                        Evidence = "Access-Control-Allow-Origin: *; Access-Control-Allow-Credentials: true",
                    });
                }
            }

            // Number the findings in display order (severity first, then discovery order).
            var ordered = findings
                .OrderBy(f => (int)f.Severity)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Number = i + 1;
            }
            result.Findings = ordered;
        }

        // ---- Scoring -------------------------------------------------------------

        private static void ScoreAndGrade(SecurityScanResult result)
        {
            result.CriticalCount = result.Findings.Count(f => f.Severity == SecuritySeverity.Critical);
            result.HighCount = result.Findings.Count(f => f.Severity == SecuritySeverity.High);
            result.MediumCount = result.Findings.Count(f => f.Severity == SecuritySeverity.Medium);
            result.LowCount = result.Findings.Count(f => f.Severity == SecuritySeverity.Low);

            var score = 100;
            foreach (var finding in result.Findings)
            {
                score -= finding.Severity switch
                {
                    SecuritySeverity.Critical => 40,
                    SecuritySeverity.High => 20,
                    SecuritySeverity.Medium => 10,
                    SecuritySeverity.Low => 4,
                    _ => 0,
                };
            }

            result.Score = Math.Clamp(score, 0, 100);
            result.Grade = result.Score switch
            {
                >= 90 => "A",
                >= 75 => "B",
                >= 60 => "C",
                >= 40 => "D",
                _ => "F",
            };
        }

        // ---- Helpers -------------------------------------------------------------

        /// <summary>Looks up a header from either the response or entity (content) header collection.</summary>
        private static string? GetHeader(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                return string.Join(", ", values);
            }

            if (response.Content.Headers.TryGetValues(name, out var contentValues))
            {
                return string.Join(", ", contentValues);
            }

            return null;
        }

        /// <summary>
        /// Applies caller-supplied headers to the probe, routing content headers (e.g. Content-Type)
        /// appropriately is handled by StringContent, so those are skipped here. Reports whether an
        /// Authorization header was supplied so the auth rule can reason about it.
        /// </summary>
        private static void ApplyCustomHeaders(HttpRequestMessage probe, Dictionary<string, string>? headers, out bool hasAuthHeader)
        {
            hasAuthHeader = false;
            if (headers is null)
            {
                return;
            }

            foreach (var (rawKey, rawValue) in headers)
            {
                var key = rawKey?.Trim();
                if (string.IsNullOrEmpty(key) || rawValue is null)
                {
                    continue;
                }

                if (string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(rawValue))
                {
                    hasAuthHeader = true;
                }

                // Content headers are set via StringContent (ContentType); skip them here so they
                // don't get rejected on the request header collection.
                if (ContentHeaderNames.Contains(key))
                {
                    continue;
                }

                probe.Headers.TryAddWithoutValidation(key, rawValue);
            }
        }

        private static string BuildRequestSnippet(Uri target, string method, SecurityScanRequest request, bool sendsBody)
        {
            var pathAndQuery = string.IsNullOrEmpty(target.PathAndQuery) ? "/" : target.PathAndQuery;
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(pathAndQuery).AppendLine(" HTTP/1.1");
            sb.Append("Host: ").AppendLine(target.Authority);
            sb.AppendLine("User-Agent: ApiIntelligenceHub-SecurityScanner/1.0");
            sb.AppendLine("Accept: */*");

            if (request.Headers is not null)
            {
                foreach (var (key, value) in request.Headers)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    // Mask credential values in the displayed snippet so tokens aren't echoed back.
                    var shown = string.Equals(key.Trim(), "Authorization", StringComparison.OrdinalIgnoreCase)
                        ? MaskSecret(value)
                        : value;
                    sb.Append(key.Trim()).Append(": ").AppendLine(shown);
                }
            }

            if (sendsBody && !string.IsNullOrEmpty(request.Body))
            {
                var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/json" : request.ContentType.Trim();
                sb.Append("Content-Type: ").AppendLine(contentType);
                sb.AppendLine();
                sb.AppendLine(Truncate(request.Body, 2000));
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>Masks a credential so only its scheme/prefix is shown in the request snippet.</summary>
        private static string MaskSecret(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var space = value.IndexOf(' ');
            var scheme = space > 0 ? value[..space] : string.Empty;
            return string.IsNullOrEmpty(scheme) ? "********" : $"{scheme} ********";
        }

        private static string BuildResponseSnippet(HttpResponseMessage response)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/").Append(response.Version).Append(' ')
              .Append((int)response.StatusCode).Append(' ').AppendLine(response.ReasonPhrase);

            foreach (var header in response.Headers)
            {
                sb.Append(header.Key).Append(": ").AppendLine(Truncate(string.Join(", ", header.Value), 300));
            }
            foreach (var header in response.Content.Headers)
            {
                sb.Append(header.Key).Append(": ").AppendLine(Truncate(string.Join(", ", header.Value), 300));
            }
            return sb.ToString().TrimEnd();
        }

        private static SecurityScanResult Error(string message) => new()
        {
            HasResult = false,
            ErrorMessage = message,
        };

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
    }
}
