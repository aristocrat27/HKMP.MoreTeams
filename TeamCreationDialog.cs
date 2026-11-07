using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace HKMP.MoreTeams;

internal sealed class TeamCreationDialog : MonoBehaviour {
    private const int WindowId = 1856341;
    private const int PickerWidth = 190;
    private const int PickerHeight = 180;
    private static TeamCreationDialog _current;

    private MoreTeamsClientAddon _owner;
    private Func<bool> _isCreationContextActive;
    private bool _isClosing;
    private string _teamName = string.Empty;
    private string _error = string.Empty;
    private string _textHex = "#9B59D0";
    private string _outlineHex = "#4A235A";
    private float _textHue = 0.78f;
    private float _textSaturation = 0.55f;
    private float _textValue = 0.85f;
    private float _outlineHue = 0.78f;
    private float _outlineSaturation = 0.55f;
    private float _outlineValue = 0.35f;
    private Texture2D _textPickerTexture;
    private Texture2D _outlinePickerTexture;
    private Texture2D _windowTexture;
    private Texture2D _buttonTexture;
    private Texture2D _buttonHoverTexture;
    private Texture2D _inputTexture;
    private Texture2D _accentTexture;
    private Texture2D _pickerMarkerTexture;
    private readonly Dictionary<EventSystem, bool> _blockedEventSystems = new Dictionary<EventSystem, bool>();
    private float _lastTextHue = -1f;
    private float _lastOutlineHue = -1f;
    private Rect _windowRect;
    private bool _windowInitialized;
    private bool _stylesInitialized;
    private bool _outlineEnabled = true;
    private GUIStyle _windowStyle;
    private GUIStyle _brandStyle;
    private GUIStyle _headingStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _fieldStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _cancelButtonStyle;
    private GUIStyle _errorStyle;

    public static void Show(MoreTeamsClientAddon owner, Func<bool> isCreationContextActive) {
        if (owner == null || isCreationContextActive == null) {
            return;
        }

        if (_current != null) {
            _current._owner = owner;
            _current._isCreationContextActive = isCreationContextActive;
            _current._error = string.Empty;
            _current.BlockGameMenuInput();
            return;
        }

        var gameObject = new GameObject("HKMP.MoreTeams Team Creation Dialog");
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        _current = gameObject.AddComponent<TeamCreationDialog>();
        _current._owner = owner;
        _current._isCreationContextActive = isCreationContextActive;
        _current.BlockGameMenuInput();
    }

    public static void CloseCurrent() {
        if (_current != null) {
            _current.Close();
        }
    }

    private void Update() {
        if (_isClosing) {
            return;
        }

        if (_owner == null || !IsCreationContextActive()) {
            Close();
            return;
        }

        BlockGameMenuInput();
    }

    private void OnGUI() {
        if (_isClosing) {
            return;
        }

        if (_owner == null || !IsCreationContextActive()) {
            Close();
            return;
        }

        BlockGameMenuInput();
        EnsureStyles();
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape) {
            Event.current.Use();
            Close();
            return;
        }

        var width = Mathf.Min(660f, Screen.width - 24f);
        var height = Mathf.Min(610f, Screen.height - 24f);
        if (!_windowInitialized || Mathf.Abs(_windowRect.width - width) > 1f || Mathf.Abs(_windowRect.height - height) > 1f) {
            _windowRect = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
            _windowInitialized = true;
        }

        var previousColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.78f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = previousColor;
        var currentEvent = Event.current;
        _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, new GUIContent(string.Empty), _windowStyle);
        if (currentEvent.type != EventType.Layout
            && currentEvent.type != EventType.Repaint
            && currentEvent.type != EventType.Used) {
            currentEvent.Use();
        }
    }

    private void DrawWindow(int windowId) {
        GUILayout.BeginVertical();
        GUILayout.Space(7f);
        GUILayout.Label("HKMP.MoreTeams", _brandStyle, GUILayout.Height(22f));
        GUILayout.Label("CREATE A NEW TEAM", _headingStyle, GUILayout.Height(32f));
        GUILayout.Space(5f);
        GUI.DrawTexture(GUILayoutUtility.GetRect(600f, 2f, GUILayout.ExpandWidth(true)), _accentTexture);
        GUILayout.Space(10f);

        GUILayout.Label("TEAM NAME", _labelStyle, GUILayout.Height(20f));
        _teamName = GUI.TextField(
            GUILayoutUtility.GetRect(600f, 32f, GUILayout.ExpandWidth(true)),
            _teamName ?? string.Empty,
            24,
            _fieldStyle);

        GUILayout.Space(13f);
        GUILayout.BeginHorizontal();
        DrawColorPicker(
            "TEAM NAME COLOR",
            ref _textHue,
            ref _textSaturation,
            ref _textValue,
            ref _textHex,
            ref _textPickerTexture,
            ref _lastTextHue);
        GUILayout.Space(22f);
        GUILayout.BeginVertical(GUILayout.Width(PickerWidth + 8f));
        _outlineEnabled = GUILayout.Toggle(
            _outlineEnabled,
            "OUTLINE ENABLED",
            _buttonStyle,
            GUILayout.Width(PickerWidth),
            GUILayout.Height(30f));
        if (_outlineEnabled) {
            DrawColorPicker(
                "OUTLINE COLOR",
                ref _outlineHue,
                ref _outlineSaturation,
                ref _outlineValue,
                ref _outlineHex,
                ref _outlinePickerTexture,
                ref _lastOutlineHue);
        } else {
            GUILayout.Space(26f);
            GUILayout.Label("Outline disabled", _labelStyle, GUILayout.Height(22f));
        }
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        GUILayout.Space(9f);
        if (!string.IsNullOrEmpty(_error)) {
            GUILayout.Label(_error, _errorStyle, GUILayout.Height(34f));
        } else {
            GUILayout.Space(34f);
        }

        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("FORGE TEAM", _buttonStyle, GUILayout.Width(150f), GUILayout.Height(36f))) {
            Submit();
        }
        if (GUILayout.Button("CANCEL", _cancelButtonStyle, GUILayout.Width(120f), GUILayout.Height(36f))) {
            Close();
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(5f);
        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 26f));
    }

    private void DrawColorPicker(
        string label,
        ref float hue,
        ref float saturation,
        ref float value,
        ref string hexValue,
        ref Texture2D texture,
        ref float lastHue
    ) {
        GUILayout.BeginVertical(GUILayout.Width(PickerWidth + 8f));
        GUILayout.Label(label, _labelStyle, GUILayout.Height(22f));
        var selected = Color.HSVToRGB(hue, saturation, value);
        var swatchRect = GUILayoutUtility.GetRect(PickerWidth, 22f, GUILayout.Width(PickerWidth), GUILayout.Height(22f));
        GUI.DrawTexture(swatchRect, Texture2D.whiteTexture);
        var previousColor = GUI.color;
        GUI.color = selected;
        GUI.DrawTexture(swatchRect, Texture2D.whiteTexture);
        GUI.color = previousColor;

        GUILayout.BeginHorizontal();
        GUILayout.Label("HEX", _labelStyle, GUILayout.Width(38f), GUILayout.Height(27f));
        var editedHex = GUILayout.TextField(
            hexValue ?? string.Empty,
            7,
            _fieldStyle,
            GUILayout.Width(PickerWidth - 44f),
            GUILayout.Height(27f));
        if (!string.Equals(editedHex, hexValue, System.StringComparison.Ordinal)) {
            hexValue = editedHex;
            if (TryParseHex(editedHex, out var parsed, out var normalized)) {
                Color.RGBToHSV(parsed, out hue, out saturation, out value);
                hexValue = normalized;
                lastHue = -1f;
            }
        }
        GUILayout.EndHorizontal();

        if (texture == null || Mathf.Abs(lastHue - hue) > 0.002f) {
            RebuildPickerTexture(ref texture, hue);
            lastHue = hue;
        }

        var pickerRect = GUILayoutUtility.GetRect(PickerWidth, PickerHeight, GUILayout.Width(PickerWidth), GUILayout.Height(PickerHeight));
        GUI.DrawTexture(pickerRect, texture, ScaleMode.StretchToFill, false);
        var previousSaturation = saturation;
        var previousValue = value;
        HandlePickerInput(pickerRect, ref saturation, ref value);
        DrawPickerMarker(pickerRect, saturation, value);
        GUILayout.Label("HUE", _labelStyle, GUILayout.Height(18f));
        var newHue = GUI.HorizontalSlider(
            GUILayoutUtility.GetRect(PickerWidth, 18f, GUILayout.Width(PickerWidth), GUILayout.Height(18f)),
            hue,
            0f,
            1f);
        var hueChanged = Mathf.Abs(newHue - hue) > 0.002f;
        if (hueChanged) {
            hue = newHue;
            lastHue = -1f;
        }

        if (Mathf.Abs(previousSaturation - saturation) > 0.002f ||
            Mathf.Abs(previousValue - value) > 0.002f ||
            hueChanged) {
            hexValue = "#" + ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(hue, saturation, value));
        }

        GUILayout.EndVertical();
    }

    private bool IsCreationContextActive() {
        if (_isCreationContextActive == null) {
            return false;
        }

        try {
            return _isCreationContextActive();
        } catch {
            return false;
        }
    }

    private void BlockGameMenuInput() {
        var eventSystems = Resources.FindObjectsOfTypeAll<EventSystem>();
        foreach (var eventSystem in eventSystems) {
            if (eventSystem == null || !eventSystem.gameObject.activeInHierarchy || !eventSystem.gameObject.scene.IsValid()) {
                continue;
            }

            if (!_blockedEventSystems.ContainsKey(eventSystem)) {
                _blockedEventSystems.Add(eventSystem, eventSystem.enabled);
            }

            if (eventSystem.enabled) {
                eventSystem.enabled = false;
            }
        }
    }

    private void RestoreGameMenuInput() {
        foreach (var entry in _blockedEventSystems) {
            if (entry.Key != null) {
                entry.Key.enabled = entry.Value;
            }
        }

        _blockedEventSystems.Clear();
    }

    private void DrawPickerMarker(Rect pickerRect, float saturation, float value) {
        const float markerSize = 18f;
        var markerCenter = new Vector2(
            pickerRect.x + Mathf.Clamp01(saturation) * pickerRect.width,
            pickerRect.y + (1f - Mathf.Clamp01(value)) * pickerRect.height
        );
        markerCenter.x = Mathf.Clamp(markerCenter.x, pickerRect.x + markerSize / 2f, pickerRect.xMax - markerSize / 2f);
        markerCenter.y = Mathf.Clamp(markerCenter.y, pickerRect.y + markerSize / 2f, pickerRect.yMax - markerSize / 2f);
        var markerRect = new Rect(
            markerCenter.x - markerSize / 2f,
            markerCenter.y - markerSize / 2f,
            markerSize,
            markerSize
        );
        GUI.DrawTexture(markerRect, _pickerMarkerTexture, ScaleMode.StretchToFill, true);
    }

    private static Texture2D CreatePickerMarkerTexture() {
        const int size = 32;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        var pixels = new Color[size * size];
        var center = (size - 1) / 2f;
        var outerRadius = size * 0.47f;
        var innerRadius = size * 0.34f;
        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                var dx = x - center;
                var dy = y - center;
                var distance = Mathf.Sqrt(dx * dx + dy * dy);
                Color color;
                if (distance <= innerRadius) {
                    color = Color.white;
                } else if (distance <= outerRadius) {
                    color = new Color(0.02f, 0.025f, 0.035f, 1f);
                } else {
                    color = Color.clear;
                }

                pixels[y * size + x] = color;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, true);
        return texture;
    }

    private static bool TryParseHex(string value, out Color color, out string normalized) {
        color = Color.white;
        normalized = string.Empty;
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.StartsWith("#", System.StringComparison.Ordinal)) {
            candidate = candidate.Substring(1);
        }
        if (candidate.Length != 6) {
            return false;
        }

        candidate = "#" + candidate;
        if (!ColorUtility.TryParseHtmlString(candidate, out color)) {
            return false;
        }

        normalized = candidate.ToUpperInvariant();
        return true;
    }

    private static void HandlePickerInput(Rect pickerRect, ref float saturation, ref float value) {
        var current = Event.current;
        if ((current.type != EventType.MouseDown && current.type != EventType.MouseDrag)
            || current.button != 0
            || !pickerRect.Contains(current.mousePosition)) {
            return;
        }

        saturation = Mathf.Clamp01((current.mousePosition.x - pickerRect.x) / pickerRect.width);
        value = 1f - Mathf.Clamp01((current.mousePosition.y - pickerRect.y) / pickerRect.height);
        current.Use();
    }

    private static void RebuildPickerTexture(ref Texture2D texture, float hue) {
        if (texture == null) {
            texture = new Texture2D(PickerWidth, PickerHeight, TextureFormat.RGB24, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
        }

        var pixels = new Color[PickerWidth * PickerHeight];
        for (var y = 0; y < PickerHeight; y++) {
            var value = (float)y / (PickerHeight - 1);
            for (var x = 0; x < PickerWidth; x++) {
                var saturation = (float)x / (PickerWidth - 1);
                pixels[y * PickerWidth + x] = Color.HSVToRGB(hue, saturation, value);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
    }

    private void EnsureStyles() {
        if (_stylesInitialized || GUI.skin == null) {
            return;
        }

        _windowTexture = CreateTexture(new Color(0.025f, 0.035f, 0.055f, 0.98f));
        _buttonTexture = CreateTexture(new Color(0.12f, 0.16f, 0.22f, 1f));
        _buttonHoverTexture = CreateTexture(new Color(0.22f, 0.27f, 0.34f, 1f));
        _inputTexture = CreateTexture(new Color(0.045f, 0.060f, 0.080f, 1f));
        _accentTexture = CreateTexture(new Color(0.72f, 0.60f, 0.34f, 1f));
        _pickerMarkerTexture = CreatePickerMarkerTexture();

        _windowStyle = new GUIStyle(GUI.skin.window);
        _windowStyle.normal.background = _windowTexture;
        _windowStyle.normal.textColor = new Color(0.91f, 0.84f, 0.65f, 1f);
        _windowStyle.border = new RectOffset(10, 10, 10, 10);
        _windowStyle.padding = new RectOffset(18, 18, 14, 14);

        _brandStyle = new GUIStyle(GUI.skin.label);
        _brandStyle.fontSize = 11;
        _brandStyle.fontStyle = FontStyle.Bold;
        _brandStyle.alignment = TextAnchor.MiddleCenter;
        _brandStyle.normal.textColor = new Color(0.64f, 0.68f, 0.76f, 1f);

        _headingStyle = new GUIStyle(GUI.skin.label);
        _headingStyle.fontSize = 21;
        _headingStyle.fontStyle = FontStyle.Bold;
        _headingStyle.alignment = TextAnchor.MiddleCenter;
        _headingStyle.normal.textColor = new Color(0.91f, 0.84f, 0.65f, 1f);

        _labelStyle = new GUIStyle(GUI.skin.label);
        _labelStyle.fontSize = 12;
        _labelStyle.fontStyle = FontStyle.Bold;
        _labelStyle.normal.textColor = new Color(0.76f, 0.80f, 0.86f, 1f);

        _fieldStyle = new GUIStyle(GUI.skin.textField);
        _fieldStyle.fontSize = 14;
        _fieldStyle.normal.background = _inputTexture;
        _fieldStyle.focused.background = _inputTexture;
        _fieldStyle.normal.textColor = new Color(0.94f, 0.94f, 0.96f, 1f);
        _fieldStyle.focused.textColor = Color.white;
        _fieldStyle.padding = new RectOffset(9, 9, 5, 5);

        _buttonStyle = new GUIStyle(GUI.skin.button);
        _buttonStyle.fontSize = 12;
        _buttonStyle.fontStyle = FontStyle.Bold;
        _buttonStyle.normal.background = _buttonTexture;
        _buttonStyle.hover.background = _buttonHoverTexture;
        _buttonStyle.active.background = _accentTexture;
        _buttonStyle.normal.textColor = new Color(0.91f, 0.84f, 0.65f, 1f);
        _buttonStyle.hover.textColor = Color.white;
        _buttonStyle.padding = new RectOffset(8, 8, 5, 5);

        _cancelButtonStyle = new GUIStyle(_buttonStyle);
        _cancelButtonStyle.normal.textColor = new Color(0.76f, 0.80f, 0.86f, 1f);

        _errorStyle = new GUIStyle(GUI.skin.label);
        _errorStyle.fontSize = 12;
        _errorStyle.alignment = TextAnchor.MiddleCenter;
        _errorStyle.wordWrap = true;
        _errorStyle.normal.textColor = new Color(1f, 0.52f, 0.48f, 1f);

        _stylesInitialized = true;
    }

    private static Texture2D CreateTexture(Color color) {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, color);
        texture.Apply(false, true);
        return texture;
    }

    private void Submit() {
        var normalizedName = (_teamName ?? string.Empty).Trim();
        if (normalizedName.Length == 0) {
            _error = "Enter a team name";
            return;
        }

        if (!TryParseHex(_textHex, out _, out var textColor) ||
            !TryParseHex(_outlineHex, out _, out var outlineColor)) {
            _error = "Enter valid HEX colors, for example #A1B2C3";
            return;
        }

        var created = _owner.CreateTeamFromInGameMenu(
            normalizedName,
            textColor,
            outlineColor,
            _outlineEnabled
        );
        if (created) {
            Close();
        } else {
            _error = "The team request could not be sent. Check authorization and the team limit.";
        }
    }

    private void Close() {
        if (_isClosing) {
            return;
        }

        _isClosing = true;
        RestoreGameMenuInput();
        if (_current == this) {
            _current = null;
        }

        if (gameObject != null) {
            UnityEngine.Object.Destroy(gameObject);
        }
    }

    private void OnDisable() {
        if (_isClosing) {
            return;
        }

        _isClosing = true;
        RestoreGameMenuInput();
        if (_current == this) {
            _current = null;
        }

        if (gameObject != null) {
            UnityEngine.Object.Destroy(gameObject);
        }
    }

    private void OnDestroy() {
        _isClosing = true;
        RestoreGameMenuInput();
        DestroyTexture(ref _textPickerTexture);
        DestroyTexture(ref _outlinePickerTexture);
        DestroyTexture(ref _windowTexture);
        DestroyTexture(ref _buttonTexture);
        DestroyTexture(ref _buttonHoverTexture);
        DestroyTexture(ref _inputTexture);
        DestroyTexture(ref _accentTexture);
        DestroyTexture(ref _pickerMarkerTexture);
        if (_current == this) {
            _current = null;
        }
    }

    private static void DestroyTexture(ref Texture2D texture) {
        if (texture == null) {
            return;
        }

        UnityEngine.Object.Destroy(texture);
        texture = null;
    }
}
