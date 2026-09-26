namespace TomYum.Bancho;

/// <summary>Team arrangement for a multiplayer match (osu!stable wire values).</summary>
public enum MatchTeamType : byte
{
    HeadToHead = 0,
    TagCoop    = 1,
    TeamVs     = 2,
    TagTeamVs  = 3,
}
