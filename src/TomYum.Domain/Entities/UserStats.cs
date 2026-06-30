namespace TomYum.Domain.Entities;

public sealed class UserStats
{
    public int UserId { get; init; }
    public int Mode { get; init; }
    public long RankedScore { get; set; }
    public long TotalScore { get; set; }
    public int PlayCount { get; set; }
    public float Accuracy { get; set; }
    public double Pp { get; set; }
    public int GlobalRank { get; set; }
}
