namespace Palabras.Persistence;

// Every future FK (review history, SRS, feedback, sessions) MUST reference users.id,
// NEVER username. Username is mutable; a rename must never delete/recreate this row.
internal sealed class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
