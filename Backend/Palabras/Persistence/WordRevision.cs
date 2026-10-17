namespace Palabras.Persistence;

internal sealed class WordRevision
{
    public int Id { get; set; }
    public int WordId { get; set; }
    public int Version { get; set; }
    public int? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public long CreatedAt { get; set; }
}
