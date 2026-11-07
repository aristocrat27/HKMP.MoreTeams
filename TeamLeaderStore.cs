using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace HKMP.MoreTeams;

internal sealed class TeamLeaderStore {
    private readonly object _sync = new object();
    private readonly string _filePath;
    private Dictionary<byte, TeamLeaderRecord> _leaders = new Dictionary<byte, TeamLeaderRecord>();

    public TeamLeaderStore(string directoryPath) {
        _filePath = Path.Combine(directoryPath, "team-leaders.json");
        Load();
    }

    public IReadOnlyList<TeamLeaderRecord> GetAll() {
        lock (_sync) {
            return _leaders.Values.Select(record => record.Copy()).OrderBy(record => record.TeamId).ToList();
        }
    }

    public TeamLeaderRecord Get(byte teamId) {
        lock (_sync) {
            return _leaders.TryGetValue(teamId, out var record) ? record.Copy() : null;
        }
    }

    public bool Set(byte teamId, string fingerprint, string username) {
        if (teamId < TeamConfigStore.FirstCustomTeamId || teamId > TeamConfigStore.LastCustomTeamId || string.IsNullOrWhiteSpace(fingerprint)) {
            return false;
        }

        lock (_sync) {
            var previous = Clone();
            foreach (var record in _leaders.Values) {
                if (string.Equals(record.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) {
                    record.Fingerprint = string.Empty;
                    record.Username = string.Empty;
                }
            }

            _leaders[teamId] = new TeamLeaderRecord {
                TeamId = teamId,
                Fingerprint = fingerprint.Trim(),
                Username = NormalizeUsername(username)
            };

            if (Save()) {
                return true;
            }

            _leaders = previous;
            return false;
        }
    }

    public bool Remove(byte teamId) {
        lock (_sync) {
            if (!_leaders.Remove(teamId)) {
                return true;
            }

            return Save();
        }
    }

    private void Load() {
        if (!File.Exists(_filePath)) {
            return;
        }

        try {
            var file = JsonConvert.DeserializeObject<TeamLeaderFile>(File.ReadAllText(_filePath));
            if (file?.Leaders == null) {
                return;
            }

            var loaded = new Dictionary<byte, TeamLeaderRecord>();
            foreach (var record in file.Leaders) {
                if (record == null || record.TeamId < TeamConfigStore.FirstCustomTeamId || record.TeamId > TeamConfigStore.LastCustomTeamId || string.IsNullOrWhiteSpace(record.Fingerprint)) {
                    continue;
                }

                loaded[record.TeamId] = record.Copy();
            }

            lock (_sync) {
                _leaders = loaded;
            }
        } catch (Exception exception) {
            System.Diagnostics.Trace.TraceError("HKMP.MoreTeams could not load team-leaders.json: " + exception);
        }
    }

    private bool Save() {
        var temporaryPath = _filePath + ".tmp";
        try {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) {
                Directory.CreateDirectory(directory);
            }

            var file = new TeamLeaderFile {
                Leaders = _leaders.Values.Select(record => record.Copy()).OrderBy(record => record.TeamId).ToList()
            };
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(file, Formatting.Indented), new System.Text.UTF8Encoding(false));
            if (File.Exists(_filePath)) {
                try {
                    File.Replace(temporaryPath, _filePath, null);
                } catch {
                    File.Copy(temporaryPath, _filePath, true);
                    File.Delete(temporaryPath);
                }
            } else {
                File.Move(temporaryPath, _filePath);
            }

            return true;
        } catch (Exception exception) {
            System.Diagnostics.Trace.TraceError("HKMP.MoreTeams could not save team-leaders.json: " + exception);
            try {
                if (File.Exists(temporaryPath)) {
                    File.Delete(temporaryPath);
                }
            } catch {
            }

            return false;
        }
    }

    private Dictionary<byte, TeamLeaderRecord> Clone() {
        return _leaders.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
    }

    private static string NormalizeUsername(string value) {
        var username = value ?? string.Empty;
        return username.Length <= 20 ? username : username.Substring(0, 20);
    }

    private sealed class TeamLeaderFile {
        public List<TeamLeaderRecord> Leaders { get; set; } = new List<TeamLeaderRecord>();
    }
}

internal sealed class TeamLeaderRecord {
    public byte TeamId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    public TeamLeaderRecord Copy() {
        return new TeamLeaderRecord {
            TeamId = TeamId,
            Fingerprint = Fingerprint ?? string.Empty,
            Username = Username ?? string.Empty
        };
    }
}
