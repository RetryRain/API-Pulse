namespace WebApplication1.Models
{
    /// <summary>
    /// View model for the endpoint detail page. The heavy data is loaded client-side via the
    /// Data action; this model only carries the route context the view needs to bootstrap.
    /// </summary>
    public class EndpointDetailViewModel
    {
        public string? WorkspaceId { get; set; }

        public string? Operation { get; set; }

        /// <summary>The dashboard search filter, carried through so "Back" restores the search.</summary>
        public string? ApiFilter { get; set; }

        public int WindowHours { get; set; } = 24;

        /// <summary>Optional explicit start of a custom analysis range (UTC). Takes precedence over WindowHours when paired with CustomEnd.</summary>
        public DateTimeOffset? CustomStart { get; set; }

        /// <summary>Optional explicit end of a custom analysis range (UTC).</summary>
        public DateTimeOffset? CustomEnd { get; set; }

        /// <summary>True when a valid explicit start/end range is supplied (start strictly before end).</summary>
        public bool HasCustomRange => CustomStart is { } s && CustomEnd is { } e && s < e;
    }
}
