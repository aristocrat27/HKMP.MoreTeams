using System;
using System.Linq;
using System.Reflection;
using Hkmp.Api.Client;
using UnityEngine;

namespace HKMP.MoreTeams;

public static class TeamColorService {
    private static GameObject _refreshRunnerObject;


    internal static void StartRefreshRunner(MoreTeamsClientAddon owner) {
        if (owner == null || _refreshRunnerObject != null) {
            return;
        }

        _refreshRunnerObject = new GameObject("HKMP.MoreTeams.ColorRefresh");
        UnityEngine.Object.DontDestroyOnLoad(_refreshRunnerObject);
        _refreshRunnerObject.AddComponent<TeamColorRefreshRunner>().Initialize(owner);
    }


    internal static bool ApplyCustomColor(object textMesh, int teamId) {
        if (textMesh == null || !MoreTeamsClientAddon.TryGetTeam(teamId, out var team)) {
            return false;
        }

        try {
            if (!ColorUtility.TryParseHtmlString(team.TextColor, out var textColor)) {
                return false;
            }

            var outlineColor = Color.clear;
            if (team.OutlineEnabled && !ColorUtility.TryParseHtmlString(team.OutlineColor, out outlineColor)) {
                return false;
            }

            var changed = SetColorProperty(textMesh, "color", textColor);
            changed |= SetColorProperty(textMesh, "outlineColor", outlineColor);
            SetFloatProperty(textMesh, "outlineWidth", team.OutlineEnabled ? 0.2f : 0f);
            return changed;
        } catch (Exception exception) {
            Modding.Logger.LogWarn("HKMP.MoreTeams could not apply the team color to a username component: " + exception.GetBaseException());
            return false;
        }
    }

    internal static int ApplyCustomColorToContainer(GameObject container, int teamId) {
        if (container == null || !MoreTeamsClientAddon.TryGetTeam(teamId, out _)) {
            return 0;
        }

        var changed = 0;
        foreach (var component in container.GetComponentsInChildren<Component>(true)) {
            if (component == null
                || !string.Equals(component.GetType().Name, "TextMeshPro", StringComparison.Ordinal)
                || !string.Equals(component.gameObject.name, "Username", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (ApplyCustomColor(component, teamId)) {
                changed++;
            }
        }

        return changed;
    }

    internal static bool SetColorProperty(object instance, string name, Color color) {
        if (instance == null) {
            return false;
        }

        var property = FindProperty(instance.GetType(), name);
        if (property != null && property.CanWrite) {
            if (property.PropertyType == typeof(Color)) {
                property.SetValue(instance, color, null);
                return true;
            }

            if (property.PropertyType == typeof(Color32)) {
                property.SetValue(instance, (Color32)color, null);
                return true;
            }
        }

        var field = FindField(instance.GetType(), name);
        if (field == null || field.IsInitOnly) {
            return false;
        }

        if (field.FieldType == typeof(Color)) {
            field.SetValue(instance, color);
            return true;
        }

        if (field.FieldType == typeof(Color32)) {
            field.SetValue(instance, (Color32)color);
            return true;
        }

        return false;
    }

    internal static bool SetFloatProperty(object instance, string name, float value) {
        if (instance == null) {
            return false;
        }

        var property = FindProperty(instance.GetType(), name);
        if (property != null && property.CanWrite && property.PropertyType == typeof(float)) {
            property.SetValue(instance, value, null);
            return true;
        }

        var field = FindField(instance.GetType(), name);
        if (field == null || field.IsInitOnly || field.FieldType != typeof(float)) {
            return false;
        }

        field.SetValue(instance, value);
        return true;
    }

    private static FieldInfo FindField(Type type, string name) {
        for (var current = type; current != null; current = current.BaseType) {
            var field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
            );
            if (field != null) {
                return field;
            }
        }

        return null;
    }

    private static PropertyInfo FindProperty(Type type, string name) {
        for (var current = type; current != null; current = current.BaseType) {
            var property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
            );
            if (property != null && property.GetIndexParameters().Length == 0) {
                return property;
            }
        }

        return null;
    }

}

internal sealed class TeamColorRefreshRunner : MonoBehaviour {
    private MoreTeamsClientAddon _owner;
    private float _elapsed;

    public void Initialize(MoreTeamsClientAddon owner) {
        _owner = owner;
    }

    private void Update() {
        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed < 0.12f) {
            return;
        }

        _elapsed = 0f;
        InGameTeamMenu.TryAttachPending();
        InGameTeamMenu.RefreshSelectionIfChanged();
        _owner?.RefreshPlayerColorsFromRunner();
    }
}
