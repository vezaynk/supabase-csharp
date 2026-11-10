using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Supabase.CookieSession;

/// <summary>Postgrest model for the sample notes table. Queried with the user JWT, never a service key.</summary>
[Table("notes")]
public sealed class Note : BaseModel
{
    [PrimaryKey("id")]
    public long Id { get; set; }

    [Column("body")]
    public string? Body { get; set; }
}
