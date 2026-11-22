using System;
using System.Linq;

namespace CompAI;

// Detached profile only; Plugin binds lifetime to character/login and access changes.
internal sealed class RecentPvpCache
{
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);
    private ProgressionSnapshot? candidate;
    private ProgressionSnapshot? saved;
    internal void Reset() { this.candidate = this.saved = null; }
    internal void ResetCandidate() => this.candidate = null;
    internal bool HasRecent(DateTimeOffset now) => this.saved is { } value &&
        value.CapturedAtUtc <= now && now - value.CapturedAtUtc < Retention;

    internal bool Observe(ProgressionSnapshot value)
    {
        if (value.Category != "pvp") throw new ArgumentException("Expected a PvP profile observation.");
        if (!value.Available) { this.candidate = null; return false; }
        if (value.Readiness != "visible_menu_observation") throw new ArgumentException("Expected a current profile capture.");
        var before = this.candidate;
        this.candidate = value;
        if (before is not null && value.CapturedAtUtc - before.CapturedAtUtc >= TimeSpan.FromMilliseconds(250) &&
            before.Entries.SequenceEqual(value.Entries))
        {
            this.saved = new("pvp", value.CapturedAtUtc, true, "last_observed_profile", value.Entries);
            return true;
        }
        return false;
    }

    internal ProgressionSnapshot Freeze(DateTimeOffset now) => this.HasRecent(now) ? this.saved! :
        ProgressionSnapshot.Unavailable("pvp", "No recent PvP profile observation. Open PvP Profile and let it load to refresh; observations last 15 minutes in this login.");
}
