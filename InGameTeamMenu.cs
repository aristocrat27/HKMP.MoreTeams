using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Hkmp.Api.Client;
using Hkmp.Game;
using UnityEngine;
using UnityEngine.UI;

namespace HKMP.MoreTeams;

internal sealed class InGameTeamMenu {
    private const float PositionX = 1710f;
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static MoreTeamsClientAddon _owner;
    private static InGameTeamMenu _current;
    private static object _clientApiRoot;
    private static object _teamChangedSource;
    private static EventInfo _teamChangedEvent;
    private static Delegate _teamChangedHandler;
    private static Sprite _whiteSprite;

    private readonly object _settingsGroup;
    private readonly List<object> _originalContent;
    private readonly object _mainGroup;
    private readonly object _pageGroup;
    private readonly object _pageTitle;
    private readonly object _previousButton;
    private readonly object _nextButton;
    private readonly object _createButton;
    private readonly object _backButton;
    private readonly object _statusButton;
    private object _contentGroup;
    private int _page;
    private bool _pageVisible;
    private int _lastDisplayedTeamId = -1;

    private InGameTeamMenu(object settingsGroup) {
        _settingsGroup = settingsGroup ?? throw new ArgumentNullException(nameof(settingsGroup));
        var componentList = HkmpRuntimeResolver.GetMemberValue(settingsGroup, "_components") as IList;
        if (componentList == null || componentList.Count < 2) {
            throw new InvalidOperationException("HKMP settings ComponentGroup did not contain its header and close button");
        }

        var allOriginalComponents = componentList.Cast<object>().ToList();
        WrapCloseButton(allOriginalComponents[1]);
        _originalContent = allOriginalComponents.Skip(2).ToList();

        _mainGroup = CreateComponentGroup(settingsGroup, true);
        _pageGroup = CreateComponentGroup(settingsGroup, false);

        var moreButton = CreateButton(_mainGroup, new Vector2(1790f, 826f), "More  ›");
        SetButtonGeometry(moreButton, new Vector2(1790f, 826f), new Vector2(76f, 30f), 17);
        SetButtonAction(moreButton, OpenTeamPage);

        _pageTitle = CreateButton(_pageGroup, new Vector2(PositionX, 918f), "Additional teams  1/2");
        SetButtonGeometry(_pageTitle, new Vector2(PositionX, 918f), new Vector2(230f, 30f), 17);
        SetButtonInteractable(_pageTitle, false);

        _previousButton = CreateButton(_pageGroup, new Vector2(1600f, 918f), "‹");
        SetButtonGeometry(_previousButton, new Vector2(1600f, 918f), new Vector2(36f, 30f), 18);
        SetButtonAction(_previousButton, () => ChangePage(_page - 1));

        _nextButton = CreateButton(_pageGroup, new Vector2(1840f, 918f), "›");
        SetButtonGeometry(_nextButton, new Vector2(1840f, 918f), new Vector2(36f, 30f), 18);
        SetButtonAction(_nextButton, () => ChangePage(_page + 1));

        _createButton = CreateButton(_pageGroup, new Vector2(PositionX, 442f), "+  Create Team");
        SetButtonGeometry(_createButton, new Vector2(PositionX, 442f), new Vector2(240f, 34f), 18);
        SetButtonAction(_createButton, OpenCreateDialog);

        _lastDisplayedTeamId = _owner.CurrentTeamId;
        _statusButton = CreateButton(_pageGroup, new Vector2(PositionX, 486f), string.Empty);
        SetButtonGeometry(_statusButton, new Vector2(PositionX, 486f), new Vector2(280f, 30f), 15);
        SetButtonInteractable(_statusButton, false);

        _backButton = CreateButton(_pageGroup, new Vector2(PositionX, 398f), "‹  Back to settings");
        SetButtonGeometry(_backButton, new Vector2(PositionX, 398f), new Vector2(240f, 34f), 17);
        SetButtonAction(_backButton, ShowMainSettings);

        RefreshPage();
    }

    public static void Install(MoreTeamsClientAddon owner, object clientApiRoot, object clientManager) {
        if (owner == null) {
            throw new ArgumentNullException(nameof(owner));
        }
        if (clientApiRoot == null) {
            throw new ArgumentNullException(nameof(clientApiRoot));
        }
        if (clientManager == null) {
            throw new ArgumentNullException(nameof(clientManager));
        }

        _owner = owner;
        _clientApiRoot = clientApiRoot;
        SubscribeTeamChangedEvent(clientManager);
        if (TryAttachExisting(_clientApiRoot)) {
            _owner.LogInGameMenuInfo("HKMP.MoreTeams attached to the existing in-game Settings interface without Harmony");
        } else {
            _owner.LogInGameMenuInfo("HKMP.MoreTeams is waiting for the stock HKMP team Settings control; attachment is retried while the client is active");
        }
    }

    internal static void TryAttachPending() {
        if (_current != null || _owner == null || _clientApiRoot == null) return;
        try {
            if (TryAttachExisting(_clientApiRoot)) {
                _owner.LogInGameMenuInfo("HKMP.MoreTeams attached to the stock in-game Settings interface");
            }
        } catch (Exception exception) {
            _owner.LogInGameMenuWarning("Could not attach the Harmony-free More Teams page to HKMP Settings: " + exception.GetBaseException());
        }
    }

    internal static void RefreshSelectionIfChanged() {
        if (_teamChangedSource != null) {
            NormalizeStockTeamChangedHandler(_teamChangedSource);
        }

        if (_current == null || _owner == null) {
            return;
        }

        var currentTeamId = _owner.CurrentTeamId;
        if (_current._lastDisplayedTeamId == currentTeamId) {
            return;
        }

        _current._lastDisplayedTeamId = currentTeamId;
        _current._page = _current.GetPageForTeam(currentTeamId);
        _owner.OnClientTeamChanged((Team)currentTeamId);
        _current.RefreshPage();
    }

    private static void SubscribeTeamChangedEvent(object clientManager) {
        if (clientManager == null) {
            _owner?.LogInGameMenuWarning("HKMP ClientManager is not available for team-change notifications");
            return;
        }

        if (ReferenceEquals(clientManager, _teamChangedSource) && _teamChangedEvent != null && _teamChangedHandler != null) {
            return;
        }

        if (_teamChangedEvent != null && _teamChangedSource != null && _teamChangedHandler != null) {
            _teamChangedEvent.RemoveEventHandler(_teamChangedSource, _teamChangedHandler);
        }

        _teamChangedSource = clientManager;
        _teamChangedEvent = clientManager.GetType().GetEvent("TeamChangedEvent", InstanceFlags);
        _teamChangedHandler = null;
        var normalizedStockHandlers = NormalizeStockTeamChangedHandler(clientManager);
        if (_teamChangedEvent == null) {
            _owner?.LogInGameMenuInfo(normalizedStockHandlers
                ? "HKMP stock team menu callback was normalized; selection is also tracked by the client refresh runner"
                : "HKMP ClientManager.TeamChangedEvent is unavailable; the More Teams selector uses authoritative catalog state and the client refresh runner");
            return;
        }

        try {
            _teamChangedHandler = new Action<Team>(team => {
                try {
                    _owner?.OnClientTeamChanged(team);
                } catch (Exception colorException) {
                    _owner?.LogInGameMenuWarning("Could not apply the confirmed custom team color: " + colorException.GetBaseException());
                }
                try {
                    RefreshCurrent();
                } catch (Exception menuException) {
                    _owner?.LogInGameMenuWarning("Could not refresh the selected team in the More Teams menu: " + menuException.GetBaseException());
                }
            });
            _teamChangedEvent.AddEventHandler(_teamChangedSource, _teamChangedHandler);
            _owner?.LogInGameMenuInfo("HKMP ClientManager.TeamChangedEvent subscription installed");
        } catch (Exception exception) {
            _teamChangedHandler = null;
            _owner?.LogInGameMenuWarning("Could not subscribe to HKMP ClientManager.TeamChangedEvent: " + exception.GetBaseException());
        }
    }

    private static bool NormalizeStockTeamChangedHandler(object clientManager) {
        var eventField = GetAllInstanceFields(clientManager.GetType()).FirstOrDefault(field =>
            field.FieldType == typeof(Action<Team>)
            && field.Name.IndexOf("TeamChangedEvent", StringComparison.OrdinalIgnoreCase) >= 0
        );
        if (eventField == null) {
            return false;
        }

        var currentHandlers = eventField.GetValue(clientManager) as Action<Team>;
        if (currentHandlers == null) {
            return false;
        }

        var normalizedHandlers = new List<Action<Team>>();
        var normalizedCount = 0;
        foreach (var invocation in currentHandlers.GetInvocationList()) {
            var handler = (Action<Team>)invocation;
            var targetTypeName = handler.Target?.GetType().FullName ?? string.Empty;
            var methodTypeName = handler.Method.DeclaringType?.FullName ?? string.Empty;
            var isStockMenuHandler = targetTypeName.StartsWith("Hkmp.Menu.ModMenu", StringComparison.Ordinal)
                || methodTypeName.StartsWith("Hkmp.Menu.ModMenu", StringComparison.Ordinal);
            if (isStockMenuHandler) {
                var originalHandler = handler;
                normalizedHandlers.Add(team => {
                    if (IsNativeTeam(team)) {
                        originalHandler(team);
                        return;
                    }

                    if (!InvokeStockTeamHandlerWithoutApplying(originalHandler, Team.None)) {
                        _owner?.LogInGameMenuWarning("Skipped the stock HKMP team-selector refresh for custom team ID " + Convert.ToInt32(team) + " because its apply callback could not be safely suppressed");
                    }
                });
                normalizedCount++;
            } else {
                normalizedHandlers.Add(handler);
            }
        }

        if (normalizedCount == 0) {
            return false;
        }

        try {
            eventField.SetValue(clientManager, Delegate.Combine(normalizedHandlers.Cast<Delegate>().ToArray()));
            _owner?.LogInGameMenuInfo("HKMP built-in team selector is guarded for custom IDs; its display fallback cannot submit a Team.None update");
            return true;
        } catch (Exception exception) {
            _owner?.LogInGameMenuWarning("Could not normalize the HKMP built-in team menu callback: " + exception.GetBaseException());
            return false;
        }
    }

    private static bool InvokeStockTeamHandlerWithoutApplying(Action<Team> handler, Team displayTeam) {
        if (handler == null || handler.Target == null) {
            return false;
        }

        var teamOption = FindStockTeamOption(handler.Target, new HashSet<object>(ReferenceComparer.Instance), 0);
        if (teamOption == null) {
            return false;
        }

        var menuSetting = HkmpRuntimeResolver.GetMemberValue(teamOption, "menuSetting", "_menuSetting");
        if (menuSetting == null && HkmpRuntimeResolver.GetMemberValue(teamOption, "customApplySetting", "_customApplySetting") is Delegate) {
            menuSetting = teamOption;
        }
        if (menuSetting == null) {
            return false;
        }

        var applyMemberName = HkmpRuntimeResolver.GetMemberValue(menuSetting, "customApplySetting") is Delegate
            ? "customApplySetting"
            : "_customApplySetting";
        var originalApply = HkmpRuntimeResolver.GetMemberValue(menuSetting, applyMemberName);
        if (!(originalApply is Delegate originalDelegate)) {
            return false;
        }

        Delegate noOp;
        try {
            var invokeMethod = originalDelegate.GetType().GetMethod("Invoke");
            if (invokeMethod == null) {
                return false;
            }

            var parameters = invokeMethod.GetParameters()
                .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
                .ToArray();
            noOp = Expression.Lambda(originalDelegate.GetType(), Expression.Empty(), parameters).Compile();
        } catch (Exception exception) {
            _owner?.LogInGameMenuWarning("Could not create a safe no-op for the stock HKMP team selector: " + exception.GetBaseException());
            return false;
        }

        if (!HkmpRuntimeResolver.TrySetMemberValue(menuSetting, applyMemberName, noOp)) {
            return false;
        }

        try {
            handler(displayTeam);
            return true;
        } catch (Exception exception) {
            _owner?.LogInGameMenuWarning("Could not refresh the stock HKMP team selector: " + exception.GetBaseException());
            return false;
        } finally {
            HkmpRuntimeResolver.TrySetMemberValue(menuSetting, applyMemberName, originalDelegate);
        }
    }

    private static object FindStockTeamOption(object instance, HashSet<object> visited, int depth) {
        if (instance == null || depth > 4 || !visited.Add(instance)) {
            return null;
        }

        foreach (var field in GetAllInstanceFields(instance.GetType())) {
            if (field.IsStatic) {
                continue;
            }

            object value;
            try {
                value = field.GetValue(instance);
            } catch {
                continue;
            }

            if (value == null) {
                continue;
            }

            if (field.Name.IndexOf("teamHorizontalOption", StringComparison.OrdinalIgnoreCase) >= 0) {
                return value;
            }

            var valueType = value.GetType();
            if (value is string || value is Delegate || valueType.IsPrimitive || valueType.IsEnum
                || value is UnityEngine.Object || value is IEnumerable) {
                continue;
            }

            var nested = FindStockTeamOption(value, visited, depth + 1);
            if (nested != null) {
                return nested;
            }
        }

        return null;
    }

    private static bool IsNativeTeam(Team team) {
        var teamId = Convert.ToInt32(team);
        return teamId >= (int)Team.None && teamId <= (int)Team.Lifeblood;
    }

    internal static bool IsTeamCreationContextActive() {
        var current = _current;
        if (current == null || !current._pageVisible || current._settingsGroup == null) {
            return false;
        }

        var isActiveMethod = current._settingsGroup.GetType().GetMethod(
            "IsActive",
            InstanceFlags,
            null,
            Type.EmptyTypes,
            null
        );
        if (isActiveMethod == null) {
            return false;
        }

        try {
            return isActiveMethod.Invoke(current._settingsGroup, null) is bool isActive && isActive;
        } catch (Exception exception) {
            _owner?.LogInGameMenuWarning("Could not verify whether the HKMP team creation page is active: " + exception.GetBaseException());
            return false;
        }
    }

    public static void RefreshCurrent() {
        if (_current == null && _clientApiRoot != null) {
            try {
                if (!TryAttachExisting(_clientApiRoot)) {
                    _owner?.LogInGameMenuInfo("HKMP.MoreTeams has not found an existing Team selection control yet");
                }
            } catch (Exception exception) {
                _owner?.LogInGameMenuWarning("Could not attach to the existing HKMP Settings interface: " + exception.GetBaseException());
            }
        }

        if (_current != null) {
            var currentTeamId = _owner.CurrentTeamId;
            if (_current._lastDisplayedTeamId != currentTeamId) {
                _current._lastDisplayedTeamId = currentTeamId;
                _current._page = _current.GetPageForTeam(currentTeamId);
            }
            _current.RefreshPage();
        }
    }

    private static bool TryAttachExisting(object clientApiRoot) {
        if (clientApiRoot == null || _owner == null) {
            return false;
        }

        var settingsGroup = FindExistingSettingsGroup(
            clientApiRoot,
            new HashSet<object>(ReferenceComparer.Instance),
            0
        );
        if (settingsGroup == null) {
            return false;
        }

        Attach(settingsGroup);
        return _current != null && ReferenceEquals(_current._settingsGroup, settingsGroup);
    }

    private static bool IsTeamRadioButtonConstructor(ConstructorInfo constructor) {
        var parameters = constructor.GetParameters();
        return parameters.Length == 5
            && string.Equals(parameters[0].ParameterType.Name, "ComponentGroup", StringComparison.Ordinal)
            && parameters[1].ParameterType == typeof(Vector2)
            && parameters[2].ParameterType == typeof(string)
            && parameters[3].ParameterType == typeof(string[])
            && parameters[4].ParameterType == typeof(int);
    }


    private static void Attach(object settingsGroup) {
        if (_current != null && ReferenceEquals(_current._settingsGroup, settingsGroup)) {
            return;
        }

        _current = new InGameTeamMenu(settingsGroup);
        _owner.LogInGameMenuInfo("HKMP.MoreTeams added the More Teams arrow to the in-game Settings interface");
    }

    private static object FindExistingSettingsGroup(object instance, HashSet<object> visited, int depth) {
        if (instance == null || depth > 12 || !visited.Add(instance)) {
            return null;
        }

        var type = instance.GetType();
        if (string.Equals(type.Name, "ComponentGroup", StringComparison.Ordinal) && HasTeamRadioButton(instance)) {
            return instance;
        }

        if (instance is IDictionary dictionary) {
            var inspected = 0;
            foreach (DictionaryEntry entry in dictionary) {
                if (inspected++ >= 256) {
                    break;
                }

                var found = FindExistingSettingsGroup(entry.Value, visited, depth + 1);
                if (found != null) {
                    return found;
                }
            }
            return null;
        }

        if (instance is IEnumerable enumerable && !(instance is string)) {
            var inspected = 0;
            foreach (var item in enumerable) {
                if (inspected++ >= 256) {
                    break;
                }

                var found = FindExistingSettingsGroup(item, visited, depth + 1);
                if (found != null) {
                    return found;
                }
            }
            return null;
        }

        if (type.Assembly != typeof(ClientAddon).Assembly) {
            return null;
        }

        foreach (var field in GetAllInstanceFields(type)) {
            if (field.IsStatic || field.FieldType.IsPrimitive || field.FieldType.IsEnum
                || field.FieldType == typeof(string)
                || typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)
                || typeof(Delegate).IsAssignableFrom(field.FieldType)) {
                continue;
            }

            object value;
            try {
                value = field.GetValue(instance);
            } catch {
                continue;
            }

            var found = FindExistingSettingsGroup(value, visited, depth + 1);
            if (found != null) {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<FieldInfo> GetAllInstanceFields(Type type) {
        for (var current = type; current != null; current = current.BaseType) {
            foreach (var field in current.GetFields(InstanceFlags | BindingFlags.DeclaredOnly)) {
                yield return field;
            }
        }
    }

    private static bool HasTeamRadioButton(object componentGroup) {
        var components = HkmpRuntimeResolver.GetMemberValue(componentGroup, "_components") as IList;
        if (components == null) {
            return false;
        }

        return components.Cast<object>().Any(component => {
            if (component == null || !string.Equals(component.GetType().Name, "RadioButtonBoxComponent", StringComparison.Ordinal)) {
                return false;
            }

            var header = HkmpRuntimeResolver.GetMemberValue(component, "_headerTextComponent");
            var text = HkmpRuntimeResolver.GetMemberValue(header, "_textObject") as Text;
            return text != null && string.Equals(text.text, "Team selection", StringComparison.OrdinalIgnoreCase);
        });
    }

    private void OpenTeamPage() {
        SetOriginalContentActive(false);
        SetGroupActive(_mainGroup, false);
        SetGroupActive(_pageGroup, true);
        _pageVisible = true;
        _page = GetPageForTeam(_owner.CurrentTeamId);
        RefreshPage();
    }

    private void ShowMainSettings() {
        TeamCreationDialog.CloseCurrent();
        SetOriginalContentActive(true);
        SetGroupActive(_pageGroup, false);
        SetGroupActive(_mainGroup, true);
        _pageVisible = false;
    }

    private void ChangePage(int page) {
        var pageCount = GetPageCount(_owner.GetCustomTeams().Count);
        _page = Math.Max(0, Math.Min(pageCount - 1, page));
        RefreshPage();
    }

    private static int GetPageCount(int customCount) {
        var customPages = Math.Max(1, (Math.Max(0, customCount) + 7) / 8);
        return Math.Min(8, 1 + customPages);
    }

    private int GetPageForTeam(int teamId) {
        if (teamId < TeamConfigStore.FirstCustomTeamId) {
            return 0;
        }

        var customTeams = _owner.GetCustomTeams().OrderBy(team => team.Id).ToList();
        var teamIndex = customTeams.FindIndex(team => team.Id == teamId);
        if (teamIndex < 0) {
            return 0;
        }

        return Math.Min(GetPageCount(customTeams.Count) - 1, 1 + teamIndex / 8);
    }

    private void RefreshPage() {
        if (_owner == null) {
            return;
        }

        var customTeams = _owner.GetCustomTeams().OrderBy(team => team.Id).ToList();
        var pageCount = GetPageCount(customTeams.Count);
        _page = Math.Max(0, Math.Min(pageCount - 1, _page));
        SetButtonText(_pageTitle, "Additional teams  " + (_page + 1) + "/" + pageCount);
        SetComponentActive(_previousButton, _page > 0);
        SetComponentActive(_nextButton, _page < pageCount - 1);
        SetComponentActive(_createButton, _page > 0 && _page == pageCount - 1
            && _owner.CanCreateTeams && customTeams.Count < TeamConfigStore.MaximumCustomTeams);

        if (_contentGroup != null) {
            DestroyComponentGroup(_contentGroup);
            _contentGroup = null;
        }

        _contentGroup = CreateComponentGroup(_pageGroup, true);
        var pageTeams = _page == 0
            ? _owner.GetBuiltinTeams().ToList()
            : customTeams.Skip((_page - 1) * 8).Take(8).ToList();

        if (pageTeams.Count > 0) {
            CreateRadioTeamList(pageTeams);
        } else {
            var emptyButton = CreateButton(_contentGroup, new Vector2(PositionX, 770f), "No custom teams on this page");
            SetButtonGeometry(emptyButton, new Vector2(PositionX, 770f), new Vector2(280f, 30f), 16);
            SetButtonInteractable(emptyButton, false);
        }

        var selectionEnabled = _owner.TeamSelectionEnabled;
        SetButtonText(
            _statusButton,
            selectionEnabled
                ? "Current team: " + _owner.CurrentTeamName
                : "Enable Teams in HKMP Server Settings"
        );
        SetComponentActive(_statusButton, true);
        SetComponentActive(_backButton, true);

        if (!_pageVisible) {
            SetGroupActive(_pageGroup, false);
        }
    }

    private void CreateRadioTeamList(IReadOnlyList<TeamDefinition> teams) {
        var radioType = HkmpRuntimeResolver.FindType(
            "Hkmp.Ui.Component.RadioButtonBoxComponent",
            "RadioButtonBoxComponent"
        );
        if (radioType == null) {
            throw new InvalidOperationException("HKMP RadioButtonBoxComponent was not found");
        }

        var constructor = radioType.GetConstructors(InstanceFlags).FirstOrDefault(IsTeamRadioButtonConstructor);
        if (constructor == null) {
            throw new InvalidOperationException("HKMP RadioButtonBoxComponent constructor was not found");
        }

        var labels = teams.Select(team => team.Name).ToArray();
        var currentTeam = _owner.CurrentTeamId;
        _lastDisplayedTeamId = currentTeam;
        var selectedIndex = teams.ToList().FindIndex(team => team.Id == currentTeam);

        var radio = constructor.Invoke(new object[] {
            _contentGroup,
            new Vector2(PositionX, 826f),
            "Team selection",
            labels,
            selectedIndex
        });

        var setOnChange = radioType.GetMethod("SetOnChange", InstanceFlags);
        if (setOnChange == null) {
            throw new InvalidOperationException("HKMP RadioButtonBoxComponent.SetOnChange was not found");
        }

        Action<int> callback = index => {
            if (index < 0 || index >= teams.Count) {
                return;
            }

            _owner.SelectTeamFromInGameMenu(teams[index].Id);
        };
        var callbackType = setOnChange.GetParameters()[0].ParameterType;
        var handler = Delegate.CreateDelegate(callbackType, callback.Target, callback.Method);
        setOnChange.Invoke(radio, new object[] { handler });

        var setInteractable = radioType.GetMethod("SetInteractable", InstanceFlags);
        if (setInteractable == null) {
            throw new InvalidOperationException("HKMP RadioButtonBoxComponent.SetInteractable was not found");
        }
        setInteractable.Invoke(radio, new object[] { _owner.TeamSelectionEnabled });

        NormalizeRadioButtonRendering(radio);
        ApplyRadioTeamColors(radio, teams);
        CreateTrashButtons(teams);
    }

    private void CreateTrashButtons(IReadOnlyList<TeamDefinition> teams) {
        if (!_owner.CanManageTeams || teams == null || teams.Count == 0
            || teams[0].Id < TeamConfigStore.FirstCustomTeamId) {
            return;
        }

        for (var index = 0; index < teams.Count; index++) {
            var teamId = teams[index].Id;
            var deleteButton = CreateButton(
                _contentGroup,
                new Vector2(1870f, 787f - index * 38f),
                string.Empty
            );
            SetButtonGeometry(
                deleteButton,
                new Vector2(1870f, 787f - index * 38f),
                new Vector2(30f, 28f),
                12
            );
            SetButtonAction(deleteButton, () => _owner.DeleteTeam(teamId));
            DrawTrashIcon(deleteButton);
        }
    }

    private static void DrawTrashIcon(object button) {
        var buttonObject = HkmpRuntimeResolver.GetMemberValue(button, "GameObject") as GameObject;
        if (buttonObject == null) {
            throw new InvalidOperationException("HKMP delete button GameObject was not found");
        }

        var text = HkmpRuntimeResolver.GetMemberValue(button, "_text") as Text;
        if (text != null) {
            text.text = string.Empty;
        }

        var bodyColor = new Color(0.96f, 0.96f, 0.96f, 1f);
        var detailColor = new Color(0.18f, 0.18f, 0.18f, 1f);
        AddTrashIconPart(buttonObject, "TrashBody", new Vector2(0f, -1f), new Vector2(11f, 13f), bodyColor);
        AddTrashIconPart(buttonObject, "TrashLid", new Vector2(0f, 7f), new Vector2(16f, 2f), bodyColor);
        AddTrashIconPart(buttonObject, "TrashHandle", new Vector2(0f, 10f), new Vector2(6f, 2f), bodyColor);
        AddTrashIconPart(buttonObject, "TrashSlotLeft", new Vector2(-3f, -1f), new Vector2(1.3f, 8f), detailColor);
        AddTrashIconPart(buttonObject, "TrashSlotCenter", new Vector2(0f, -1f), new Vector2(1.3f, 8f), detailColor);
        AddTrashIconPart(buttonObject, "TrashSlotRight", new Vector2(3f, -1f), new Vector2(1.3f, 8f), detailColor);
    }

    private static void AddTrashIconPart(
        GameObject parent,
        string name,
        Vector2 position,
        Vector2 size,
        Color color
    ) {
        var part = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var transform = part.GetComponent<RectTransform>();
        transform.SetParent(parent.transform, false);
        transform.anchorMin = new Vector2(0.5f, 0.5f);
        transform.anchorMax = new Vector2(0.5f, 0.5f);
        transform.anchoredPosition = position;
        transform.sizeDelta = size;

        var image = part.GetComponent<Image>();
        if (_whiteSprite == null) {
            var texture = Texture2D.whiteTexture;
            _whiteSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f)
            );
        }
        image.sprite = _whiteSprite;
        image.color = color;
        image.raycastTarget = false;
    }

    private static void NormalizeRadioButtonRendering(object radio) {
        var checkboxes = HkmpRuntimeResolver.GetMemberValue(radio, "_checkboxes") as IEnumerable;
        if (checkboxes == null) {
            return;
        }

        foreach (var checkbox in checkboxes) {
            if (checkbox == null) {
                continue;
            }

            ConfigureRoundButtonImage(HkmpRuntimeResolver.GetMemberValue(checkbox, "_bgImage") as Image);
            ConfigureRoundButtonImage(HkmpRuntimeResolver.GetMemberValue(checkbox, "_checkmarkImage") as Image);
        }
    }

    private static void ConfigureRoundButtonImage(Image image) {
        if (image == null) {
            return;
        }

        image.type = Image.Type.Simple;
        image.useSpriteMesh = true;
        image.preserveAspect = true;
        if (image.sprite != null && image.sprite.texture != null) {
            image.sprite.texture.wrapMode = TextureWrapMode.Clamp;
            image.sprite.texture.filterMode = FilterMode.Bilinear;
        }

        image.SetVerticesDirty();
        image.SetMaterialDirty();
    }

    private static void ApplyRadioTeamColors(object radio, IReadOnlyList<TeamDefinition> teams) {
        var textComponents = HkmpRuntimeResolver.GetMemberValue(radio, "_textComponents") as IEnumerable;
        if (textComponents == null) {
            return;
        }

        var index = 0;
        foreach (var textComponent in textComponents) {
            if (textComponent == null || index >= teams.Count) {
                index++;
                continue;
            }

            var text = HkmpRuntimeResolver.GetMemberValue(textComponent, "_textObject") as Text;
            if (text != null) {
                if (ColorUtility.TryParseHtmlString(teams[index].TextColor, out var textColor)) {
                    text.color = textColor;
                }

                var outline = text.GetComponent<Outline>();
                if (teams[index].OutlineEnabled && ColorUtility.TryParseHtmlString(teams[index].OutlineColor, out var outlineColor)) {
                    if (outline == null) {
                        outline = text.gameObject.AddComponent<Outline>();
                    }
                    outline.effectColor = outlineColor;
                    outline.effectDistance = new Vector2(1.2f, 1.2f);
                    outline.enabled = true;
                } else if (outline != null) {
                    outline.enabled = false;
                }
            }

            index++;
        }
    }

    private void OpenCreateDialog() {
        if (_owner == null || !_owner.CanCreateTeams) {
            return;
        }

        TeamCreationDialog.Show(_owner, IsTeamCreationContextActive);
    }

    private void SetOriginalContentActive(bool active) {
        foreach (var component in _originalContent) {
            SetComponentActive(component, active);
        }
    }

    private void WrapCloseButton(object closeButton) {
        var actionField = FindField(closeButton.GetType(), "_onPress");
        if (actionField == null || actionField.FieldType != typeof(Action)) {
            throw new InvalidOperationException("HKMP settings close button action was not found");
        }

        var originalAction = actionField.GetValue(closeButton) as Action;
        actionField.SetValue(closeButton, new Action(() => {
            ShowMainSettings();
            originalAction?.Invoke();
        }));
    }

    private static object CreateComponentGroup(object parent, bool active) {
        var groupType = parent.GetType();
        var constructor = groupType.GetConstructor(
            InstanceFlags,
            null,
            new[] { typeof(bool), groupType },
            null
        );
        if (constructor == null) {
            throw new InvalidOperationException("HKMP ComponentGroup(bool, ComponentGroup) constructor was not found");
        }

        return constructor.Invoke(new[] { (object) active, parent });
    }

    private static object CreateButton(object group, Vector2 position, string text) {
        var buttonType = typeof(ClientAddon).Assembly.GetType("Hkmp.Ui.Component.ButtonComponent", false);
        if (buttonType == null) {
            throw new InvalidOperationException("HKMP ButtonComponent was not found");
        }

        var constructor = buttonType.GetConstructor(
            InstanceFlags,
            null,
            new[] { group.GetType(), typeof(Vector2), typeof(string) },
            null
        );
        if (constructor == null) {
            throw new InvalidOperationException("HKMP ButtonComponent(ComponentGroup, Vector2, string) constructor was not found");
        }

        return constructor.Invoke(new object[] { group, position, text });
    }

    private static void SetButtonGeometry(object button, Vector2 position, Vector2 size, int fontSize) {
        var setPosition = button.GetType().GetMethod("SetPosition", InstanceFlags);
        if (setPosition == null) {
            throw new InvalidOperationException("HKMP UI component SetPosition was not found");
        }

        setPosition.Invoke(button, new object[] { position });

        var transformField = FindField(button.GetType(), "_transform");
        var rectTransform = transformField?.GetValue(button) as RectTransform;
        if (rectTransform == null) {
            throw new InvalidOperationException("HKMP ButtonComponent RectTransform was not found");
        }

        rectTransform.sizeDelta = size;
        var textField = FindField(button.GetType(), "_text");
        var text = textField?.GetValue(button) as Text;
        if (text != null) {
            text.fontSize = fontSize;
            text.rectTransform.sizeDelta = size;
        }
    }

    private static void SetButtonAction(object button, Action action) {
        var method = button.GetType().GetMethod("SetOnPress", InstanceFlags);
        if (method == null) {
            throw new InvalidOperationException("HKMP ButtonComponent.SetOnPress was not found");
        }

        method.Invoke(button, new object[] { action });
    }

    private static void SetButtonText(object button, string text) {
        var method = button.GetType().GetMethod("SetText", InstanceFlags);
        if (method == null) {
            throw new InvalidOperationException("HKMP ButtonComponent.SetText was not found");
        }

        method.Invoke(button, new object[] { text });
    }

    private static void SetButtonInteractable(object button, bool interactable) {
        var method = button.GetType().GetMethod("SetInteractable", InstanceFlags);
        if (method == null) {
            throw new InvalidOperationException("HKMP ButtonComponent.SetInteractable was not found");
        }

        method.Invoke(button, new object[] { interactable });
    }

    private static void SetComponentActive(object component, bool active) {
        var method = component.GetType().GetMethod("SetActive", InstanceFlags);
        if (method == null) {
            throw new InvalidOperationException("HKMP UI component SetActive was not found for " + component.GetType().FullName);
        }

        method.Invoke(component, new object[] { active });
    }

    private static void SetGroupActive(object group, bool active) {
        var method = group.GetType().GetMethod("SetActive", InstanceFlags);
        if (method == null) {
            throw new InvalidOperationException("HKMP ComponentGroup.SetActive was not found");
        }

        method.Invoke(group, new object[] { active });
    }

    private static void DestroyComponentGroup(object group) {
        if (group == null) {
            return;
        }

        SetGroupActive(group, false);
        var children = HkmpRuntimeResolver.GetMemberValue(group, "_children") as IList;
        if (children != null) {
            foreach (var child in children.Cast<object>().ToList()) {
                DestroyComponentGroup(child);
            }
            children.Clear();
        }

        var components = HkmpRuntimeResolver.GetMemberValue(group, "_components") as IList;
        if (components != null) {
            foreach (var component in components.Cast<object>().ToList()) {
                var destroy = component?.GetType().GetMethod("Destroy", InstanceFlags);
                destroy?.Invoke(component, null);
            }
            components.Clear();
        }

        var parent = HkmpRuntimeResolver.GetMemberValue(group, "_parent");
        var parentChildren = HkmpRuntimeResolver.GetMemberValue(parent, "_children") as IList;
        parentChildren?.Remove(group);
    }

    private static FieldInfo FindField(Type type, string name) {
        for (var current = type; current != null; current = current.BaseType) {
            var field = current.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (field != null) {
                return field;
            }
        }

        return null;
    }


    private sealed class ReferenceComparer : IEqualityComparer<object> {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();

        public new bool Equals(object left, object right) {
            return ReferenceEquals(left, right);
        }

        public int GetHashCode(object value) {
            return RuntimeHelpers.GetHashCode(value);
        }
    }
}
