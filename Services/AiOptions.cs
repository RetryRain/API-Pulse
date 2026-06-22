namespace WebApplication1.Services
{
    /// <summary>
    /// Configuration for the LLM-backed exception analyzer. Defaults target the free
    /// GitHub Models endpoint (OpenAI-compatible). The API key is read from configuration
    /// or the GITHUB_TOKEN environment variable so it is never committed to source.
    /// </summary>
    public class AiOptions
    {
        public const string SectionName = "Ai";

        /// <summary>Master switch. When false, the analyzer returns a clear "not configured" message.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>OpenAI-compatible chat-completions endpoint. Defaults to GitHub Models.</summary>
        public string Endpoint { get; set; } = "https://models.github.ai/inference/chat/completions";

        /// <summary>Model id to use (must exist in the GitHub Models catalog).</summary>
        public string Model { get; set; } = "openai/gpt-4.1";

        /// <summary>
        /// API key / token. If empty, the GITHUB_TOKEN environment variable is used. Keep this out
        /// of appsettings.json in source control — prefer the env var or user-secrets.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>Request timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; } = 30;
    }
}
