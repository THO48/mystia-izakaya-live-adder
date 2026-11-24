using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace NightbirdLiveAdder;

public class LiveAdderUI : MonoBehaviour
{
    public LiveAdderUI(IntPtr ptr) : base(ptr) { }

    private static readonly int[] Presets = { 1, 10, 50, 99, 999, 9999 };

    // Panel visibility & geometry
    private bool _isOpen;
    private bool _prevCursorVisible;
    private CursorLockMode _prevCursorLock;
    private Rect _windowRect;
    private bool _windowInitialized;
    private bool _draggingWindow;
    private Vector2 _dragOffset;

    // Scroll state (fully driven by InputSystem mouse wheel + scrollbar drag)
    private float _listScrollY;
    private bool _draggingListScroll;
    private float _listScrollDragOffsetY;
    private float _tagScrollX;

    // InputSystem polled state (sampled in Update, consumed in Repaint)
    private Vector2 _mouseGuiPos;
    private bool _leftHeld;
    private bool _pendingClick;
    private bool _clickConsumed;
    private float _pendingScrollY;

    // Text input state (0=none, 1=search, 2=amount)
    private int _focusedField;
    private Keyboard _hookedKeyboard;
    private Il2CppSystem.Action<char> _textInputHandler;
    private float _backspaceRepeatTimer;
    private int _lastBackspaceFrame = -1;

    // Filters & inputs
    private ItemCategory _category = ItemCategory.Beverages;
    private string _searchText = "";
    private string _selectedModule = "ALL";
    private int _ownFilter; // 0=All, 1=Owned, 2=Missing
    private string _selectedBevTag = "";
    private string _amountInput = "99";
    private bool _unlockAlbum;

    // Feedback & startup toast
    private string _statusMessage = "选择任意饮品或食材，点击「+数量」即可实时加入仓库。";
    private bool _statusIsError;
    private float _startupToastTimer;
    private CatalogItem _focusedItem;
    private float _nextDbSyncTime;
    private int _lastToggleFrame = -1;

    // Input config
    private string _toggleKeyName = "F7";
    private KeyCode _toggleKeyCode = KeyCode.F7;

    // Cached filtered list
    private readonly List<CatalogItem> _filteredItems = new();

    // Transparent uGUI raycast blocker so clicks on our panel don't hit game UI behind it
    private GameObject _blockerCanvasGo;
    private RectTransform _blockerPanelRt;

    // Cached font, textures, styles
    private bool _stylesReady;
    private Font _cjkFont;
    private Texture2D _texShadow;
    private Texture2D _texBgMain;
    private Texture2D _texBgHeader;
    private Texture2D _texBgCard;
    private Texture2D _texBgCardHover;
    private Texture2D _texBgInput;
    private Texture2D _texBgInputFocus;
    private Texture2D _texBgBtn;
    private Texture2D _texBgBtnHover;
    private Texture2D _texBgBtnDisabled;
    private Texture2D _texBgGold;
    private Texture2D _texBgGoldHover;
    private Texture2D _texBgAccent;
    private Texture2D _texBgAccentHover;
    private Texture2D _texBgPill;
    private Texture2D _texBgPillActive;
    private Texture2D _texBgToast;
    private Texture2D _texBorder;
    private Texture2D _texBorderFocus;
    private Texture2D _texScrollTrack;
    private Texture2D _texScrollThumb;

    private GUIStyle _stTitle;
    private GUIStyle _stSubtitle;
    private GUIStyle _stLabel;
    private GUIStyle _stLabelMuted;
    private GUIStyle _stLabelBold;
    private GUIStyle _stLabelGold;
    private GUIStyle _stBadgeReady;
    private GUIStyle _stBadgeWait;
    private GUIStyle _stBtnLabel;
    private GUIStyle _stBtnLabelSmall;
    private GUIStyle _stBtnLabelDisabled;
    private GUIStyle _stInputLabel;
    private GUIStyle _stToast;

    private void Awake()
    {
        _toggleKeyName = Plugin.ToggleKeyConfig?.Value?.Trim() ?? "F7";
        if (!Enum.TryParse(_toggleKeyName, true, out _toggleKeyCode))
        {
            _toggleKeyName = "F7";
            _toggleKeyCode = KeyCode.F7;
        }

        int defAmt = Math.Max(1, Plugin.DefaultAddAmountConfig?.Value ?? 99);
        _amountInput = defAmt.ToString();
        _unlockAlbum = Plugin.UnlockAlbumOnAddConfig?.Value ?? false;
        _startupToastTimer = (Plugin.ShowStartupToastConfig?.Value ?? true) ? 7.0f : 0f;

        _textInputHandler = (Action<char>)OnKeyboardTextInput;
        RebuildFilteredList();
    }

    private void OnDestroy()
    {
        UnhookKeyboardTextInput();
        if (_blockerCanvasGo != null)
        {
            UnityEngine.Object.Destroy(_blockerCanvasGo);
            _blockerCanvasGo = null;
        }
    }

    private void Update()
    {
        if (_startupToastTimer > 0f)
            _startupToastTimer -= Time.unscaledDeltaTime;

        if (Time.unscaledTime >= _nextDbSyncTime)
        {
            _nextDbSyncTime = Time.unscaledTime + 2.0f;
            CatalogService.TrySyncWithRuntimeDatabase();
        }

        if (IsToggleKeyPressedThisFrame())
        {
            ToggleOpen();
            return;
        }

        if (!_isOpen)
            return;

        EnsureKeyboardTextHook();
        PollMouseAndKeyboard();
        SyncBlockerRect();
    }

    private void OnGUI()
    {
        if (!_isOpen && _startupToastTimer <= 0f)
            return;

        EnsureStyles();

        var ev = Event.current;
        if (ev == null)
            return;

        if (_startupToastTimer > 0f && !_isOpen)
        {
            if (ev.type == EventType.Repaint)
                DrawStartupToast();
            return;
        }

        if (!_isOpen)
            return;

        // Only draw and process our InputSystem-driven clicks on Repaint (once per visual frame)
        if (ev.type != EventType.Repaint)
            return;

        EnsureWindowRect();

        GUI.depth = -1000;
        var prevFont = GUI.skin.font;
        if (_cjkFont != null)
            GUI.skin.font = _cjkFont;

        _clickConsumed = false;
        DrawPanel();

        // Clicking empty area inside panel clears text focus
        if (_pendingClick && !_clickConsumed)
        {
            _focusedField = 0;
        }

        // Consume one-frame latched input
        _pendingClick = false;
        _pendingScrollY = 0f;

        GUI.skin.font = prevFont;
    }

    [HideFromIl2Cpp]
    private void ToggleOpen() => SetOpen(!_isOpen);

    [HideFromIl2Cpp]
    private void SetOpen(bool open)
    {
        if (_isOpen == open) return;
        _isOpen = open;
        _startupToastTimer = 0f;
        _draggingWindow = false;
        _draggingListScroll = false;
        _focusedField = 0;
        _pendingClick = false;
        _pendingScrollY = 0f;

        if (_isOpen)
        {
            _prevCursorVisible = Cursor.visible;
            _prevCursorLock = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            CatalogService.TrySyncWithRuntimeDatabase();
            RebuildFilteredList();
            EnsureKeyboardTextHook();
            SetBlockerActive(true);
            SyncBlockerRect();
        }
        else
        {
            Cursor.visible = _prevCursorVisible;
            Cursor.lockState = _prevCursorLock;
            UnhookKeyboardTextInput();
            SetBlockerActive(false);
        }
    }

    [HideFromIl2Cpp]
    private void PollMouseAndKeyboard()
    {
        // Keep cursor unlocked while panel is open
        if (!Cursor.visible) Cursor.visible = true;
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;

        // 1. Escape closes text focus first, then panel
        if (IsEscapePressedThisFrame())
        {
            if (_focusedField != 0)
                _focusedField = 0;
            else
                SetOpen(false);
            return;
        }

        // 2. Poll mouse via New Input System (with Legacy fallback)
        bool gotNewMouse = false;
        try
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var pos = mouse.position.ReadValue();
                _mouseGuiPos = new Vector2(pos.x, Screen.height - pos.y);
                _leftHeld = mouse.leftButton.isPressed;
                if (mouse.leftButton.wasPressedThisFrame)
                    _pendingClick = true;

                var scroll = mouse.scroll.ReadValue();
                if (Mathf.Abs(scroll.y) > 0.01f)
                    _pendingScrollY += scroll.y;

                gotNewMouse = true;
            }
        }
        catch { }

        if (!gotNewMouse)
        {
            try
            {
                var pos = Input.mousePosition;
                _mouseGuiPos = new Vector2(pos.x, Screen.height - pos.y);
                _leftHeld = Input.GetMouseButton(0);
                if (Input.GetMouseButtonDown(0))
                    _pendingClick = true;
                float sy = Input.mouseScrollDelta.y;
                if (Mathf.Abs(sy) > 0.01f)
                    _pendingScrollY += sy * 120f;
            }
            catch { }
        }

        // Release drags when mouse button is up
        if (!_leftHeld)
        {
            _draggingWindow = false;
            _draggingListScroll = false;
        }

        // Handle active window drag
        if (_draggingWindow && _leftHeld)
        {
            _windowRect.x = Mathf.Clamp(_mouseGuiPos.x - _dragOffset.x, 8f, Math.Max(8f, Screen.width - _windowRect.width - 8f));
            _windowRect.y = Mathf.Clamp(_mouseGuiPos.y - _dragOffset.y, 8f, Math.Max(8f, Screen.height - _windowRect.height - 8f));
        }

        // 3. Keyboard shortcuts for focused text field (Enter, Ctrl+V, Backspace hold)
        if (_focusedField != 0)
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null)
                {
                    if ((kb.enterKey != null && kb.enterKey.wasPressedThisFrame) ||
                        (kb.numpadEnterKey != null && kb.numpadEnterKey.wasPressedThisFrame))
                    {
                        _focusedField = 0;
                    }
                    else if (kb.ctrlKey != null && kb.ctrlKey.isPressed &&
                             kb.vKey != null && kb.vKey.wasPressedThisFrame)
                    {
                        PasteFromClipboard();
                    }
                    else if (kb.backspaceKey != null && kb.backspaceKey.isPressed)
                    {
                        if (kb.backspaceKey.wasPressedThisFrame)
                        {
                            ApplyBackspaceOncePerFrame();
                            _backspaceRepeatTimer = 0.40f;
                        }
                        else
                        {
                            _backspaceRepeatTimer -= Time.unscaledDeltaTime;
                            if (_backspaceRepeatTimer <= 0f)
                            {
                                _backspaceRepeatTimer = 0.045f;
                                ApplyBackspace();
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }

    [HideFromIl2Cpp]
    private void EnsureKeyboardTextHook()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null || kb == _hookedKeyboard) return;
            UnhookKeyboardTextInput();
            kb.add_onTextInput(_textInputHandler);
            _hookedKeyboard = kb;
        }
        catch { }
    }

    [HideFromIl2Cpp]
    private void UnhookKeyboardTextInput()
    {
        try
        {
            if (_hookedKeyboard != null && _textInputHandler != null)
                _hookedKeyboard.remove_onTextInput(_textInputHandler);
        }
        catch { }
        _hookedKeyboard = null;
    }

    [HideFromIl2Cpp]
    private void OnKeyboardTextInput(char ch)
    {
        if (!_isOpen || _focusedField == 0) return;

        if (ch == '\b')
        {
            ApplyBackspaceOncePerFrame();
            return;
        }

        if (ch == '\r' || ch == '\n')
        {
            _focusedField = 0;
            return;
        }

        if (char.IsControl(ch)) return;

        if (_focusedField == 1)
        {
            if (_searchText.Length < 40)
            {
                _searchText += ch;
                _listScrollY = 0f;
                RebuildFilteredList();
            }
        }
        else if (_focusedField == 2)
        {
            if (ch >= '0' && ch <= '9' && _amountInput.Length < 7)
            {
                _amountInput += ch;
            }
        }
    }

    [HideFromIl2Cpp]
    private void ApplyBackspaceOncePerFrame()
    {
        if (_lastBackspaceFrame == Time.frameCount) return;
        _lastBackspaceFrame = Time.frameCount;
        ApplyBackspace();
    }

    [HideFromIl2Cpp]
    private void ApplyBackspace()
    {
        if (_focusedField == 1 && _searchText.Length > 0)
        {
            _searchText = _searchText.Substring(0, _searchText.Length - 1);
            _listScrollY = 0f;
            RebuildFilteredList();
        }
        else if (_focusedField == 2 && _amountInput.Length > 0)
        {
            _amountInput = _amountInput.Substring(0, _amountInput.Length - 1);
        }
    }

    [HideFromIl2Cpp]
    private void PasteFromClipboard()
    {
        try
        {
            string clip = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(clip)) return;
            clip = clip.Replace("\r", "").Replace("\n", "");

            if (_focusedField == 1)
            {
                string combined = _searchText + clip;
                _searchText = combined.Length > 40 ? combined.Substring(0, 40) : combined;
                _listScrollY = 0f;
                RebuildFilteredList();
            }
            else if (_focusedField == 2)
            {
                string digits = FilterDigitsOnly(_amountInput + clip);
                _amountInput = digits.Length > 7 ? digits.Substring(0, 7) : digits;
            }
        }
        catch { }
    }

    [HideFromIl2Cpp]
    private void SetBlockerActive(bool active)
    {
        try
        {
            if (active && _blockerCanvasGo == null)
            {
                _blockerCanvasGo = new GameObject("NightbirdLiveAdder_ClickBlocker");
                UnityEngine.Object.DontDestroyOnLoad(_blockerCanvasGo);
                _blockerCanvasGo.hideFlags = HideFlags.HideAndDontSave;

                var canvas = _blockerCanvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32760;

                _blockerCanvasGo.AddComponent<GraphicRaycaster>();

                var panelGo = new GameObject("BlockerRect");
                panelGo.transform.SetParent(_blockerCanvasGo.transform, false);
                var img = panelGo.AddComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 0f);
                img.raycastTarget = true;

                _blockerPanelRt = panelGo.GetComponent<RectTransform>();
                _blockerPanelRt.anchorMin = new Vector2(0f, 1f);
                _blockerPanelRt.anchorMax = new Vector2(0f, 1f);
                _blockerPanelRt.pivot = new Vector2(0f, 1f);
            }

            if (_blockerCanvasGo != null)
                _blockerCanvasGo.SetActive(active);
        }
        catch (Exception ex)
        {
            Plugin.LogSource?.LogWarning($"Click blocker setup skipped: {ex.Message}");
        }
    }

    [HideFromIl2Cpp]
    private void SyncBlockerRect()
    {
        if (!_isOpen || _blockerPanelRt == null) return;
        EnsureWindowRect();
        _blockerPanelRt.anchoredPosition = new Vector2(_windowRect.x, -_windowRect.y);
        _blockerPanelRt.sizeDelta = new Vector2(_windowRect.width, _windowRect.height);
    }

    [HideFromIl2Cpp]
    private bool IsToggleKeyPressedThisFrame()
    {
        if (_lastToggleFrame == Time.frameCount) return false;

        bool pressed = false;
        try
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                var ctrl = GetInputSystemKeyControl(kb, _toggleKeyName);
                if (ctrl != null && ctrl.wasPressedThisFrame)
                    pressed = true;
            }
        }
        catch { }

        if (!pressed)
        {
            try
            {
                if (Input.GetKeyDown(_toggleKeyCode))
                    pressed = true;
            }
            catch { }
        }

        if (pressed)
            _lastToggleFrame = Time.frameCount;

        return pressed;
    }

    [HideFromIl2Cpp]
    private static bool IsEscapePressedThisFrame()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey != null && kb.escapeKey.wasPressedThisFrame)
                return true;
        }
        catch { }

        try
        {
            if (Input.GetKeyDown(KeyCode.Escape))
                return true;
        }
        catch { }

        return false;
    }

    [HideFromIl2Cpp]
    private static KeyControl GetInputSystemKeyControl(Keyboard kb, string keyName)
    {
        if (kb == null || string.IsNullOrWhiteSpace(keyName)) return null;
        return keyName.Trim().ToUpperInvariant() switch
        {
            "F1" => kb.f1Key,
            "F2" => kb.f2Key,
            "F3" => kb.f3Key,
            "F4" => kb.f4Key,
            "F5" => kb.f5Key,
            "F6" => kb.f6Key,
            "F7" => kb.f7Key,
            "F8" => kb.f8Key,
            "F9" => kb.f9Key,
            "F10" => kb.f10Key,
            "F11" => kb.f11Key,
            "F12" => kb.f12Key,
            "INSERT" => kb.insertKey,
            "HOME" => kb.homeKey,
            "END" => kb.endKey,
            "BACKQUOTE" => kb.backquoteKey,
            _ => kb.FindKeyOnCurrentKeyboardLayout(keyName)
        };
    }

    [HideFromIl2Cpp]
    private void EnsureWindowRect()
    {
        float targetW = Mathf.Clamp(Screen.width * 0.68f, 840f, 1080f);
        float targetH = Mathf.Clamp(Screen.height * 0.78f, 560f, 780f);

        if (!_windowInitialized || _windowRect.width > Screen.width - 16f || _windowRect.height > Screen.height - 16f)
        {
            _windowRect = new Rect(
                (Screen.width - targetW) * 0.5f,
                (Screen.height - targetH) * 0.5f,
                targetW,
                targetH);
            _windowInitialized = true;
        }
    }

    [HideFromIl2Cpp]
    private void DrawStartupToast()
    {
        float w = 315f;
        float h = 38f;
        var rect = new Rect(Screen.width - w - 18f, 18f, w, h);
        GUI.DrawTexture(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), _texBorder);
        GUI.DrawTexture(rect, _texBgToast);
        GUI.Label(new Rect(rect.x + 12f, rect.y, rect.width - 16f, rect.height),
            $"夜雀实时添加器已加载  ·  按 [{_toggleKeyName}] 打开", _stToast);
    }

    // =========================================================================
    // Custom InputSystem-driven Immediate-Mode Widgets (works on New InputSystem!)
    // =========================================================================

    [HideFromIl2Cpp]
    private Rect WinRect(float x, float y, float w, float h) =>
        new(_windowRect.x + x, _windowRect.y + y, w, h);

    [HideFromIl2Cpp]
    private bool DrawCustomButton(
        Rect drawRect,
        Rect hitScreenRect,
        string text,
        Texture2D normalTex,
        Texture2D hoverTex,
        bool enabled = true,
        bool small = false,
        Rect? clipScreenRect = null)
    {
        bool insideClip = !clipScreenRect.HasValue || clipScreenRect.Value.Contains(_mouseGuiPos);
        bool hovered = enabled && insideClip && hitScreenRect.Contains(_mouseGuiPos);

        Texture2D bg = !enabled ? _texBgBtnDisabled : (hovered ? hoverTex : normalTex);
        GUI.DrawTexture(drawRect, bg);

        var labelStyle = !enabled
            ? _stBtnLabelDisabled
            : (small ? _stBtnLabelSmall : _stBtnLabel);
        GUI.Label(drawRect, text, labelStyle);

        if (hovered && _pendingClick && !_clickConsumed)
        {
            _clickConsumed = true;
            _focusedField = 0;
            return true;
        }
        return false;
    }

    [HideFromIl2Cpp]
    private void DrawCustomTextField(int fieldId, Rect rect, string value, string placeholder)
    {
        bool focused = _focusedField == fieldId;
        bool hovered = rect.Contains(_mouseGuiPos);

        if (hovered && _pendingClick && !_clickConsumed)
        {
            _clickConsumed = true;
            _focusedField = fieldId;
            focused = true;
        }

        // Border + background
        GUI.DrawTexture(rect, focused ? _texBorderFocus : _texBorder);
        var inner = new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f);
        GUI.DrawTexture(inner, focused ? _texBgInputFocus : _texBgInput);

        bool showCaret = focused && ((int)(Time.unscaledTime * 2f) % 2 == 0);
        if (string.IsNullOrEmpty(value) && !focused)
        {
            GUI.Label(new Rect(inner.x + 6f, inner.y, inner.width - 10f, inner.height), placeholder, _stLabelMuted);
        }
        else
        {
            string display = showCaret ? value + "|" : value;
            GUI.Label(new Rect(inner.x + 6f, inner.y, inner.width - 10f, inner.height), display, _stInputLabel);
        }
    }

    [HideFromIl2Cpp]
    private bool DrawCustomCheckbox(Rect rect, bool value, string label)
    {
        bool hovered = rect.Contains(_mouseGuiPos);
        var box = new Rect(rect.x, rect.y + (rect.height - 16f) * 0.5f, 16f, 16f);

        GUI.DrawTexture(box, hovered ? _texBorderFocus : _texBorder);
        var inner = new Rect(box.x + 1f, box.y + 1f, box.width - 2f, box.height - 2f);
        GUI.DrawTexture(inner, value ? _texBgGold : _texBgInput);
        if (value)
            GUI.Label(box, "✓", _stBtnLabelSmall);

        GUI.Label(new Rect(box.xMax + 6f, rect.y, rect.width - 24f, rect.height), label, _stLabel);

        if (hovered && _pendingClick && !_clickConsumed)
        {
            _clickConsumed = true;
            _focusedField = 0;
            return !value;
        }
        return value;
    }

    // =========================================================================
    // Main Panel Layout & Drawing
    // =========================================================================

    [HideFromIl2Cpp]
    private void DrawPanel()
    {
        float w = _windowRect.width;
        float h = _windowRect.height;
        const float pad = 14f;

        bool ready = StorageService.IsStorageReady();
        int addAmount = ParseAddAmount();

        // Outer shadow + gold frame + main background
        GUI.DrawTexture(new Rect(_windowRect.x - 4f, _windowRect.y - 4f, w + 8f, h + 8f), _texShadow);
        GUI.DrawTexture(new Rect(_windowRect.x - 1f, _windowRect.y - 1f, w + 2f, h + 2f), _texBorder);
        GUI.DrawTexture(_windowRect, _texBgMain);

        // ---------------- 1. Header (draggable) ----------------
        var headerRect = WinRect(0f, 0f, w, 46f);
        GUI.DrawTexture(headerRect, _texBgHeader);
        GUI.DrawTexture(WinRect(0f, 45f, w, 1f), _texBorder);

        GUI.Label(WinRect(pad, 6f, 380f, 22f), "夜雀食堂 · 实时仓库添加器", _stTitle);
        GUI.Label(WinRect(pad, 26f, 430f, 18f), $"仅实时添加模式 · 快捷键 [{_toggleKeyName}] · Esc 关闭 · 拖动标题栏移动", _stSubtitle);

        // Status badge
        GUI.Label(
            WinRect(w - 224f, 11f, 170f, 24f),
            ready ? "● 仓库已就绪（实时）" : "○ 请先进入游戏存档",
            ready ? _stBadgeReady : _stBadgeWait);

        // Close button
        var closeRect = WinRect(w - 42f, 10f, 30f, 26f);
        if (DrawCustomButton(closeRect, closeRect, "×", _texBgBtn, _texBgAccentHover))
        {
            SetOpen(false);
            return;
        }

        // Start dragging if header clicked (outside close button)
        var dragArea = WinRect(0f, 0f, w - 48f, 46f);
        if (_pendingClick && !_clickConsumed && dragArea.Contains(_mouseGuiPos))
        {
            _clickConsumed = true;
            _focusedField = 0;
            _draggingWindow = true;
            _dragOffset = _mouseGuiPos - new Vector2(_windowRect.x, _windowRect.y);
        }

        float y = 56f;

        // ---------------- 2. Category Tabs & Add Quantity Bar ----------------
        float tabW = 190f;
        float tabH = 38f;
        int bevOwned = StorageService.CountOwned(ItemCategory.Beverages, CatalogService.Beverages);
        int ingOwned = StorageService.CountOwned(ItemCategory.Ingredients, CatalogService.Ingredients);

        var bevTabRect = WinRect(pad, y, tabW, tabH);
        if (DrawCustomButton(
                bevTabRect, bevTabRect,
                $"饮品酒柜 ({bevOwned}/{CatalogService.Beverages.Count})",
                _category == ItemCategory.Beverages ? _texBgGold : _texBgCard,
                _category == ItemCategory.Beverages ? _texBgGoldHover : _texBgCardHover))
        {
            if (_category != ItemCategory.Beverages)
            {
                _category = ItemCategory.Beverages;
                _listScrollY = 0f;
                RebuildFilteredList();
            }
        }

        var ingTabRect = WinRect(pad + tabW + 8f, y, tabW, tabH);
        if (DrawCustomButton(
                ingTabRect, ingTabRect,
                $"食材仓库 ({ingOwned}/{CatalogService.Ingredients.Count})",
                _category == ItemCategory.Ingredients ? _texBgGold : _texBgCard,
                _category == ItemCategory.Ingredients ? _texBgGoldHover : _texBgCardHover))
        {
            if (_category != ItemCategory.Ingredients)
            {
                _category = ItemCategory.Ingredients;
                _selectedBevTag = "";
                _listScrollY = 0f;
                RebuildFilteredList();
            }
        }

        // Add amount input + presets
        float rx = pad + tabW * 2f + 22f;
        GUI.Label(WinRect(rx, y + 8f, 64f, 22f), "添加数量", _stLabelBold);
        rx += 66f;

        DrawCustomTextField(2, WinRect(rx, y + 6f, 78f, 26f), _amountInput, "数量");
        rx += 84f;

        for (int i = 0; i < Presets.Length; i++)
        {
            int p = Presets[i];
            float pw = p >= 1000 ? 54f : (p >= 100 ? 46f : 38f);
            if (rx + pw > w - pad) break;
            bool isCur = addAmount == p;
            var pRect = WinRect(rx, y + 7f, pw, 24f);
            if (DrawCustomButton(
                    pRect, pRect, $"+{p}",
                    isCur ? _texBgPillActive : _texBgPill,
                    isCur ? _texBgGoldHover : _texBgBtnHover,
                    true, true))
            {
                _amountInput = p.ToString();
                addAmount = p;
            }
            rx += pw + 4f;
        }

        y += tabH + 10f;

        // ---------------- 3. Search + DLC + Ownership Row ----------------
        float searchW = 216f;
        bool hasSearch = !string.IsNullOrEmpty(_searchText);
        var searchRect = WinRect(pad, y, hasSearch ? searchW - 24f : searchW, 26f);
        DrawCustomTextField(1, searchRect, _searchText, "点击搜索名称 / #编号 / 风味…");

        if (hasSearch)
        {
            var clearRect = WinRect(pad + searchW - 22f, y + 1f, 22f, 24f);
            if (DrawCustomButton(clearRect, clearRect, "×", _texBgBtn, _texBgAccentHover, true, true))
            {
                _searchText = "";
                _listScrollY = 0f;
                RebuildFilteredList();
            }
        }

        // DLC filter pills
        float fx = pad + searchW + 10f;
        var modules = CatalogService.Modules;
        for (int i = 0; i < modules.Count; i++)
        {
            string mod = modules[i];
            string label = CatalogService.GetModuleLabel(mod);
            float mw = label.Length <= 2 ? 42f : 48f;
            var mRect = WinRect(fx, y + 1f, mw, 24f);
            bool active = _selectedModule == mod;
            if (DrawCustomButton(
                    mRect, mRect, label,
                    active ? _texBgPillActive : _texBgPill,
                    active ? _texBgGoldHover : _texBgBtnHover,
                    true, true))
            {
                if (_selectedModule != mod)
                {
                    _selectedModule = mod;
                    _listScrollY = 0f;
                    RebuildFilteredList();
                }
            }
            fx += mw + 4f;
        }

        // Ownership filter pills
        fx += 8f;
        string[] ownLabels = { "全部", "已持有", "未持有" };
        for (int i = 0; i < ownLabels.Length; i++)
        {
            float ow = i == 0 ? 42f : 52f;
            if (fx + ow > w - pad) break;
            var oRect = WinRect(fx, y + 1f, ow, 24f);
            bool active = _ownFilter == i;
            if (DrawCustomButton(
                    oRect, oRect, ownLabels[i],
                    active ? _texBgPillActive : _texBgPill,
                    active ? _texBgGoldHover : _texBgBtnHover,
                    true, true))
            {
                if (_ownFilter != i)
                {
                    _ownFilter = i;
                    _listScrollY = 0f;
                    RebuildFilteredList();
                }
            }
            fx += ow + 4f;
        }

        y += 32f;

        // ---------------- 4. Beverage Flavor Tag Bar (scrollable via wheel & arrows) ----------------
        if (_category == ItemCategory.Beverages && CatalogService.BeverageTags.Count > 0)
        {
            GUI.Label(WinRect(pad, y + 2f, 58f, 22f), "酒水风味", _stLabelMuted);

            var tags = CatalogService.BeverageTags;
            float totalTagW = 52f;
            for (int i = 0; i < tags.Count; i++)
                totalTagW += Math.Max(46f, tags[i].Length * 13f + 16f) + 4f;

            float arrowW = 22f;
            var leftArrow = WinRect(pad + 60f, y + 1f, arrowW, 24f);
            var rightArrow = WinRect(w - pad - arrowW, y + 1f, arrowW, 24f);
            var tagClip = WinRect(pad + 60f + arrowW + 4f, y + 1f, w - pad * 2f - 60f - arrowW * 2f - 8f, 24f);
            float maxTagScroll = Math.Max(0f, totalTagW - tagClip.width);

            if (DrawCustomButton(leftArrow, leftArrow, "‹", _texBgBtn, _texBgBtnHover, _tagScrollX > 1f, true))
                _tagScrollX = Math.Max(0f, _tagScrollX - 180f);
            if (DrawCustomButton(rightArrow, rightArrow, "›", _texBgBtn, _texBgBtnHover, _tagScrollX < maxTagScroll - 1f, true))
                _tagScrollX = Math.Min(maxTagScroll, _tagScrollX + 180f);

            if (tagClip.Contains(_mouseGuiPos) && Mathf.Abs(_pendingScrollY) > 0.01f)
            {
                _tagScrollX = Mathf.Clamp(_tagScrollX - _pendingScrollY * 0.5f, 0f, maxTagScroll);
                _pendingScrollY = 0f;
            }
            _tagScrollX = Mathf.Clamp(_tagScrollX, 0f, maxTagScroll);

            GUI.BeginGroup(tagClip);
            float tx = -_tagScrollX;

            var allTagLocal = new Rect(tx, 0f, 46f, 24f);
            var allTagScreen = new Rect(tagClip.x + tx, tagClip.y, 46f, 24f);
            bool allTagAct = string.IsNullOrEmpty(_selectedBevTag);
            if (DrawCustomButton(
                    allTagLocal, allTagScreen, "全部",
                    allTagAct ? _texBgPillActive : _texBgPill,
                    allTagAct ? _texBgGoldHover : _texBgBtnHover,
                    true, true, tagClip))
            {
                if (!allTagAct)
                {
                    _selectedBevTag = "";
                    _listScrollY = 0f;
                    RebuildFilteredList();
                }
            }
            tx += 50f;

            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i];
                float tw = Math.Max(46f, tag.Length * 13f + 16f);
                if (tx + tw >= 0f && tx <= tagClip.width)
                {
                    var localR = new Rect(tx, 0f, tw, 24f);
                    var screenR = new Rect(tagClip.x + tx, tagClip.y, tw, 24f);
                    bool act = _selectedBevTag == tag;
                    if (DrawCustomButton(
                            localR, screenR, tag,
                            act ? _texBgPillActive : _texBgPill,
                            act ? _texBgGoldHover : _texBgBtnHover,
                            true, true, tagClip))
                    {
                        _selectedBevTag = act ? "" : tag;
                        _listScrollY = 0f;
                        RebuildFilteredList();
                    }
                }
                tx += tw + 4f;
            }
            GUI.EndGroup();

            y += 32f;
        }

        // ---------------- 5. Batch Add Action Bar ----------------
        var actionBarRect = WinRect(pad, y, w - pad * 2f, 34f);
        GUI.DrawTexture(actionBarRect, _texBgCard);
        GUI.DrawTexture(new Rect(actionBarRect.x, actionBarRect.yMax - 1f, actionBarRect.width, 1f), _texBorder);

        GUI.Label(
            new Rect(actionBarRect.x + 10f, actionBarRect.y + 6f, 250f, 22f),
            $"当前筛选：{_filteredItems.Count} 项  （单次实时 +{addAmount:N0}）",
            _stLabel);

        bool newUnlock = DrawCustomCheckbox(
            new Rect(actionBarRect.x + 266f, actionBarRect.y + 6f, 130f, 22f),
            _unlockAlbum,
            "同步解锁图鉴");
        if (newUnlock != _unlockAlbum)
        {
            _unlockAlbum = newUnlock;
            if (Plugin.UnlockAlbumOnAddConfig != null)
                Plugin.UnlockAlbumOnAddConfig.Value = _unlockAlbum;
        }

        float batchBtnW = 216f;
        var batchBtnRect = new Rect(actionBarRect.xMax - batchBtnW - 6f, actionBarRect.y + 4f, batchBtnW, 26f);
        if (DrawCustomButton(
                batchBtnRect, batchBtnRect,
                $"当前筛选全部  +{addAmount:N0}",
                _texBgGold, _texBgGoldHover,
                ready && _filteredItems.Count > 0 && addAmount > 0))
        {
            var res = StorageService.AddBatch(_category, _filteredItems, addAmount, _unlockAlbum);
            SetStatus(res.Message, !res.Success);
            if (_ownFilter != 0) RebuildFilteredList();
        }

        y += 42f;

        // ---------------- 6. Scrollable 2-Column Item Grid + Custom Scrollbar ----------------
        float footerH = 46f;
        float listH = Math.Max(140f, h - y - footerH - 8f);
        float scrollBarW = 12f;
        float gap = 8f;

        var listClip = WinRect(pad, y, w - pad * 2f - scrollBarW - 6f, listH);
        var scrollTrack = WinRect(w - pad - scrollBarW, y, scrollBarW, listH);

        int cols = listClip.width >= 760f ? 2 : 1;
        float cardW = (listClip.width - gap * (cols - 1)) / cols;
        float cardH = 68f;
        int rows = (_filteredItems.Count + cols - 1) / cols;
        float contentH = Math.Max(listH, rows * (cardH + gap));
        float maxScrollY = Math.Max(0f, contentH - listH);

        // Mouse wheel scrolling on list area
        var wheelArea = WinRect(pad, y, w - pad * 2f, listH);
        if (wheelArea.Contains(_mouseGuiPos) && Mathf.Abs(_pendingScrollY) > 0.01f)
        {
            _listScrollY = Mathf.Clamp(_listScrollY - _pendingScrollY * 0.45f, 0f, maxScrollY);
            _pendingScrollY = 0f;
        }

        // Draw custom vertical scrollbar
        GUI.DrawTexture(scrollTrack, _texScrollTrack);
        if (maxScrollY > 1f)
        {
            float thumbH = Mathf.Clamp((listH / contentH) * listH, 32f, listH);
            float trackUsable = Math.Max(1f, listH - thumbH);
            float thumbY = scrollTrack.y + (_listScrollY / maxScrollY) * trackUsable;
            var thumbRect = new Rect(scrollTrack.x + 1f, thumbY, scrollTrack.width - 2f, thumbH);

            if (_pendingClick && !_clickConsumed && scrollTrack.Contains(_mouseGuiPos))
            {
                _clickConsumed = true;
                _focusedField = 0;
                if (thumbRect.Contains(_mouseGuiPos))
                {
                    _draggingListScroll = true;
                    _listScrollDragOffsetY = _mouseGuiPos.y - thumbRect.y;
                }
                else
                {
                    float ratio = Mathf.Clamp01((_mouseGuiPos.y - scrollTrack.y - thumbH * 0.5f) / trackUsable);
                    _listScrollY = ratio * maxScrollY;
                    _draggingListScroll = true;
                    _listScrollDragOffsetY = thumbH * 0.5f;
                }
            }

            if (_draggingListScroll && _leftHeld)
            {
                float newThumbTop = _mouseGuiPos.y - _listScrollDragOffsetY;
                float ratio = Mathf.Clamp01((newThumbTop - scrollTrack.y) / trackUsable);
                _listScrollY = ratio * maxScrollY;
            }

            thumbY = scrollTrack.y + (_listScrollY / maxScrollY) * trackUsable;
            thumbRect = new Rect(scrollTrack.x + 1f, thumbY, scrollTrack.width - 2f, thumbH);
            bool thumbHot = _draggingListScroll || thumbRect.Contains(_mouseGuiPos);
            GUI.DrawTexture(thumbRect, thumbHot ? _texBgGold : _texScrollThumb);
        }
        else
        {
            _listScrollY = 0f;
        }

        _listScrollY = Mathf.Clamp(_listScrollY, 0f, maxScrollY);

        // Draw visible cards inside clipped group
        GUI.BeginGroup(listClip);
        if (_filteredItems.Count == 0)
        {
            GUI.Label(new Rect(12f, 36f, listClip.width - 24f, 30f), "没有符合当前筛选条件的物品", _stLabelMuted);
        }
        else
        {
            int firstRow = Math.Max(0, (int)(_listScrollY / (cardH + gap)) - 1);
            int lastRow = Math.Min(rows - 1, (int)((_listScrollY + listH) / (cardH + gap)) + 1);

            for (int r = firstRow; r <= lastRow; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (idx >= _filteredItems.Count) break;

                    var item = _filteredItems[idx];
                    var localCard = new Rect(c * (cardW + gap), r * (cardH + gap) - _listScrollY, cardW, cardH);
                    var screenCard = new Rect(listClip.x + localCard.x, listClip.y + localCard.y, localCard.width, localCard.height);
                    DrawItemCard(localCard, screenCard, listClip, item, ready, addAmount);
                }
            }
        }
        GUI.EndGroup();

        // ---------------- 7. Footer Status & Description Bar ----------------
        float fy = h - footerH;
        GUI.DrawTexture(WinRect(0f, fy, w, 1f), _texBorder);
        GUI.DrawTexture(WinRect(0f, fy + 1f, w, footerH - 1f), _texBgHeader);

        GUI.Label(
            WinRect(pad, fy + 4f, w - pad * 2f, 20f),
            _statusMessage,
            _statusIsError ? _stBadgeWait : _stLabelGold);

        string descLine = _focusedItem != null
            ? $"[{_focusedItem.Name} #{_focusedItem.Id}]  {_focusedItem.Description}"
            : "提示：实时添加直接写入游戏内存仓库，无需退游或改存档；如仓库面板正打开会自动同步刷新。";
        GUI.Label(WinRect(pad, fy + 24f, w - pad * 2f, 18f), descLine, _stSubtitle);
    }

    [HideFromIl2Cpp]
    private void DrawItemCard(
        Rect localRect,
        Rect screenRect,
        Rect listClip,
        CatalogItem item,
        bool ready,
        int addAmount)
    {
        bool hovered = listClip.Contains(_mouseGuiPos) && screenRect.Contains(_mouseGuiPos);
        if (hovered) _focusedItem = item;

        GUI.DrawTexture(localRect, hovered ? _texBgCardHover : _texBgCard);

        int count = ready ? StorageService.GetCount(_category, item.Id) : 0;
        bool owned = count > 0;
        if (owned)
            GUI.DrawTexture(new Rect(localRect.x, localRect.y + 6f, 3f, localRect.height - 12f), _texBgGold);

        // Pixel icon (36x36)
        var iconRect = new Rect(localRect.x + 10f, localRect.y + 16f, 36f, 36f);
        var tex = CatalogService.GetIcon(item.IconName);
        if (tex != null)
            GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
        else
            GUI.DrawTexture(iconRect, _texBgInput);

        // Text block
        float textX = localRect.x + 54f;
        float btnsTotalW = 132f;
        float textW = Math.Max(110f, localRect.width - 54f - btnsTotalW - 8f);

        string stars = item.Level > 0 ? new string('★', Math.Min(item.Level, 5)) : "";
        string modText = CatalogService.GetModuleLabel(item.Module);
        string titleLine = $"{item.Name}  #{item.Id}  {modText}";
        if (!item.ExistsInGameDb && item.RuntimeSynced)
            titleLine += " [未加载DLC]";

        GUI.Label(new Rect(textX, localRect.y + 7f, textW, 20f), titleLine, _stLabelBold);

        string countText = !ready
            ? "库存: --"
            : (owned ? $"当前库存: {count:N0}" : "当前未持有");
        string metaLine = $"{countText}   {stars}  ¥{item.Price}";
        GUI.Label(new Rect(textX, localRect.y + 27f, textW, 18f), metaLine, owned ? _stLabelGold : _stLabelMuted);

        if (item.Tags != null && item.Tags.Length > 0)
        {
            string tagStr = string.Join(" · ", item.Tags);
            GUI.Label(new Rect(textX, localRect.y + 45f, textW, 18f), tagStr, _stSubtitle);
        }

        // Action buttons: [+1] and [+N]
        bool dbAvailable = !item.RuntimeSynced || item.ExistsInGameDb;
        float btnY = localRect.y + 18f;
        float btnH = 32f;

        var plus1Local = new Rect(localRect.xMax - 128f, btnY, 42f, btnH);
        var plus1Screen = new Rect(screenRect.xMax - 128f, screenRect.y + 18f, 42f, btnH);
        if (DrawCustomButton(
                plus1Local, plus1Screen, "+1",
                _texBgBtn, _texBgBtnHover,
                ready && dbAvailable, false, listClip))
        {
            _focusedItem = item;
            var res = StorageService.AddSingle(_category, item, 1, _unlockAlbum);
            SetStatus(res.Message, !res.Success);
            if (_ownFilter != 0) RebuildFilteredList();
        }

        var plusNLocal = new Rect(localRect.xMax - 82f, btnY, 74f, btnH);
        var plusNScreen = new Rect(screenRect.xMax - 82f, screenRect.y + 18f, 74f, btnH);
        if (DrawCustomButton(
                plusNLocal, plusNScreen, $"+{addAmount}",
                _texBgAccent, _texBgAccentHover,
                ready && dbAvailable && addAmount > 0, false, listClip))
        {
            _focusedItem = item;
            var res = StorageService.AddSingle(_category, item, addAmount, _unlockAlbum);
            SetStatus(res.Message, !res.Success);
            if (_ownFilter != 0) RebuildFilteredList();
        }
    }

    [HideFromIl2Cpp]
    private void RebuildFilteredList()
    {
        _filteredItems.Clear();
        var source = CatalogService.GetItems(_category);
        string q = (_searchText ?? "").Trim().ToLowerInvariant();
        bool ready = StorageService.IsStorageReady();

        for (int i = 0; i < source.Count; i++)
        {
            var item = source[i];

            if (_selectedModule != "ALL" &&
                !string.Equals(item.Module, _selectedModule, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_category == ItemCategory.Beverages && !string.IsNullOrEmpty(_selectedBevTag))
            {
                bool hasTag = false;
                if (item.Tags != null)
                {
                    for (int t = 0; t < item.Tags.Length; t++)
                    {
                        if (item.Tags[t] == _selectedBevTag)
                        {
                            hasTag = true;
                            break;
                        }
                    }
                }
                if (!hasTag) continue;
            }

            if (_ownFilter != 0 && ready)
            {
                int cnt = StorageService.GetCount(_category, item.Id);
                if (_ownFilter == 1 && cnt <= 0) continue;
                if (_ownFilter == 2 && cnt > 0) continue;
            }

            if (!string.IsNullOrEmpty(q) && !item.SearchIndex.Contains(q))
            {
                continue;
            }

            _filteredItems.Add(item);
        }
    }

    [HideFromIl2Cpp]
    private int ParseAddAmount()
    {
        if (int.TryParse(_amountInput, out int val) && val > 0)
            return Math.Min(val, StorageService.MaxObjectCount);
        return 0;
    }

    [HideFromIl2Cpp]
    private static string FilterDigitsOnly(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var chars = new char[s.Length];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] >= '0' && s[i] <= '9')
                chars[n++] = s[i];
        }
        return new string(chars, 0, n);
    }

    [HideFromIl2Cpp]
    private void SetStatus(string msg, bool isError)
    {
        _statusMessage = (isError ? "⚠ " : "✔ ") + msg;
        _statusIsError = isError;
    }

    [HideFromIl2Cpp]
    private void EnsureStyles()
    {
        if (_stylesReady && _texBgMain != null)
            return;

        _stylesReady = true;

        try
        {
            var fontNames = new Il2CppStringArray(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" });
            _cjkFont = Font.CreateDynamicFontFromOSFont(fontNames, 14);
            if (_cjkFont != null)
                _cjkFont.hideFlags = HideFlags.HideAndDontSave;
        }
        catch (Exception ex)
        {
            Plugin.LogSource?.LogWarning($"CreateDynamicFontFromOSFont failed: {ex.Message}");
        }

        // Izakaya dark lantern palette
        _texShadow = MakeTex(new Color(0f, 0f, 0f, 0.45f));
        _texBgMain = MakeTex(new Color(0.10f, 0.08f, 0.12f, 0.97f));
        _texBgHeader = MakeTex(new Color(0.14f, 0.11f, 0.17f, 0.99f));
        _texBgCard = MakeTex(new Color(0.16f, 0.13f, 0.20f, 0.96f));
        _texBgCardHover = MakeTex(new Color(0.22f, 0.18f, 0.28f, 0.99f));
        _texBgInput = MakeTex(new Color(0.07f, 0.05f, 0.09f, 1.00f));
        _texBgInputFocus = MakeTex(new Color(0.11f, 0.08f, 0.14f, 1.00f));
        _texBgBtn = MakeTex(new Color(0.25f, 0.21f, 0.31f, 1.00f));
        _texBgBtnHover = MakeTex(new Color(0.35f, 0.29f, 0.43f, 1.00f));
        _texBgBtnDisabled = MakeTex(new Color(0.17f, 0.15f, 0.20f, 0.70f));
        _texBgGold = MakeTex(new Color(0.76f, 0.54f, 0.18f, 1.00f));
        _texBgGoldHover = MakeTex(new Color(0.88f, 0.66f, 0.25f, 1.00f));
        _texBgAccent = MakeTex(new Color(0.74f, 0.28f, 0.20f, 1.00f));
        _texBgAccentHover = MakeTex(new Color(0.88f, 0.36f, 0.26f, 1.00f));
        _texBgPill = MakeTex(new Color(0.19f, 0.16f, 0.24f, 1.00f));
        _texBgPillActive = MakeTex(new Color(0.70f, 0.49f, 0.17f, 1.00f));
        _texBgToast = MakeTex(new Color(0.12f, 0.09f, 0.15f, 0.94f));
        _texBorder = MakeTex(new Color(0.85f, 0.66f, 0.32f, 0.38f));
        _texBorderFocus = MakeTex(new Color(0.98f, 0.78f, 0.35f, 0.95f));
        _texScrollTrack = MakeTex(new Color(0.07f, 0.06f, 0.09f, 0.90f));
        _texScrollThumb = MakeTex(new Color(0.38f, 0.32f, 0.46f, 0.95f));

        Color cream = new(0.96f, 0.93f, 0.88f, 1f);
        Color muted = new(0.70f, 0.65f, 0.74f, 1f);
        Color disabledText = new(0.45f, 0.42f, 0.48f, 1f);
        Color gold = new(0.97f, 0.78f, 0.36f, 1f);
        Color green = new(0.42f, 0.88f, 0.54f, 1f);
        Color amber = new(0.98f, 0.68f, 0.30f, 1f);

        _stTitle = MakeLabelStyle(16, FontStyle.Bold, gold, TextAnchor.MiddleLeft);
        _stSubtitle = MakeLabelStyle(11, FontStyle.Normal, muted, TextAnchor.MiddleLeft);
        _stLabel = MakeLabelStyle(13, FontStyle.Normal, cream, TextAnchor.MiddleLeft);
        _stLabelMuted = MakeLabelStyle(12, FontStyle.Normal, muted, TextAnchor.MiddleLeft);
        _stLabelBold = MakeLabelStyle(13, FontStyle.Bold, cream, TextAnchor.MiddleLeft);
        _stLabelGold = MakeLabelStyle(12, FontStyle.Bold, gold, TextAnchor.MiddleLeft);

        _stBadgeReady = MakeLabelStyle(12, FontStyle.Bold, green, TextAnchor.MiddleRight);
        _stBadgeWait = MakeLabelStyle(12, FontStyle.Bold, amber, TextAnchor.MiddleRight);

        _stBtnLabel = MakeLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        _stBtnLabelSmall = MakeLabelStyle(12, FontStyle.Bold, cream, TextAnchor.MiddleCenter);
        _stBtnLabelDisabled = MakeLabelStyle(12, FontStyle.Normal, disabledText, TextAnchor.MiddleCenter);
        _stInputLabel = MakeLabelStyle(13, FontStyle.Normal, Color.white, TextAnchor.MiddleLeft);
        _stToast = MakeLabelStyle(13, FontStyle.Bold, gold, TextAnchor.MiddleLeft);
    }

    [HideFromIl2Cpp]
    private GUIStyle MakeLabelStyle(int size, FontStyle fontStyle, Color color, TextAnchor align)
    {
        var st = new GUIStyle(GUI.skin.label);
        st.fontSize = size;
        st.fontStyle = fontStyle;
        st.normal.textColor = color;
        st.alignment = align;
        st.clipping = TextClipping.Clip;
        st.wordWrap = false;
        if (_cjkFont != null) st.font = _cjkFont;
        return st;
    }

    [HideFromIl2Cpp]
    private static Texture2D MakeTex(Color color)
    {
        var tex = new Texture2D(1, 1);
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }
}
