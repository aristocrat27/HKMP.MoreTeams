using System.Collections.Generic;
using Newtonsoft.Json;

namespace HKMP.MoreTeams;

public sealed class TeamConfigFile {
    [JsonProperty("teams")]
    public List<TeamDefinition> Teams { get; set; } = new List<TeamDefinition>();
}
