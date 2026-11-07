namespace HKMP.MoreTeams;

public sealed class TeamDefinition {
    public byte Id { get; set; }
    public string Name { get; set; }
    public string TextColor { get; set; }
    public string OutlineColor { get; set; }
    public bool OutlineEnabled { get; set; } = true;

    public TeamDefinition Copy() {
        return new TeamDefinition {
            Id = Id,
            Name = Name,
            TextColor = TextColor,
            OutlineColor = OutlineColor,
            OutlineEnabled = OutlineEnabled
        };
    }
}
