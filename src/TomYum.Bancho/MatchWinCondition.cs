namespace TomYum.Bancho;

/// <summary>Win condition for a multiplayer match (osu!stable wire values).</summary>
public enum MatchWinCondition : byte
{
    Score    = 0,
    Accuracy = 1,
    Combo    = 2,
    ScoreV2  = 3,
}
