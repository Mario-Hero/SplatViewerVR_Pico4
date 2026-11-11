// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// World-space SMB connection editor with a built-in on-screen keyboard, so a
/// share can be configured inside the headset without adb or a JSON editor.
///
/// VR controls:
///   Left stick up/down   → previous / next field
///   Right stick          → move the keyboard cursor
///   Right trigger or A   → type the highlighted key
///   Right B              → backspace
///   Left X               → save and test the connection
///   Left Y               → close
///
/// Desktop fallback:
///   F2 → open,  Esc → close,  mouse/keyboard not required (arrows type).
/// </summary>
public class SmbSettingsPanel : MonoBehaviour
{
    // ── Layout ────────────────────────────────────────────────────────────────

    const int PW = 1240, PH = 800;
    const float SCALE = 0.001f;
    const float SPAWN_DIST = 1.5f;
    const int PAD = 14;
    const int TITLE_H = 34;
    const int FIELD_H = 46;
    const int ROW_H = 58;
    const int KEY_W = 116;
    const int KEY_GAP = 3;
    const int FONT_TITLE = 24;
    const int FONT_FIELD = 22;
    const int FONT_KEY = 22;
    const int FONT_HINT = 15;

    static readonly Color COL_BG      = new Color(0.06f, 0.06f, 0.08f, 0.96f);
    static readonly Color COL_TITLE   = new Color(0.75f, 0.75f, 0.75f);
    static readonly Color COL_LABEL   = new Color(0.62f, 0.62f, 0.62f);
    static readonly Color COL_VALUE   = new Color(0.85f, 0.92f, 1.0f);
    static readonly Color COL_FIELD   = new Color(1f, 1f, 1f, 0.04f);
    static readonly Color COL_FIELD_ON= new Color(0.18f, 0.36f, 0.78f, 0.55f);
    static readonly Color COL_KEY     = new Color(1f, 1f, 1f, 0.10f);
    static readonly Color COL_KEY_SEL = new Color(0.24f, 0.48f, 0.92f, 0.90f);
    static readonly Color COL_CLEAR   = new Color(0f, 0f, 0f, 0f);
    static readonly Color COL_HINT    = new Color(0.45f, 0.45f, 0.45f);
    static readonly Color COL_OK      = new Color(0.45f, 0.95f, 0.55f);
    static readonly Color COL_ERR     = new Color(1f, 0.52f, 0.45f);

    // ── Keyboard definition ───────────────────────────────────────────────────

    static readonly string[][] KEYS =
    {
        new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
        new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
        new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "." },
        new[] { "z", "x", "c", "v", "b", "n", "m", "-", "_", "/" },
        new[] { "SHIFT", "SPACE", "DEL", "CLR", "SAVE", "CLOSE" },
    };

    const int MAX_KEYS_PER_ROW = 10;
    const int KEY_ROWS = 5;

    // ── Fields ────────────────────────────────────────────────────────────────

    enum Field { Host = 0, Share = 1, Folder = 2, User = 3, Password = 4 }

    static readonly string[] FieldLabels = { "Host / IP", "Share", "Folder", "User", "Password" };
    static readonly string[] FieldHints =
    {
        "192.168.1.102",
        "shared folder name",
        "optional sub-folder",
        "Windows account",
        "account password",
    };

    // ── State ─────────────────────────────────────────────────────────────────

    readonly string[] _values = new string[5];
    int _field;
    int _kx;
    int _ky = 1;
    bool _shift;
    bool _isOpen;
    bool _toggleReady = true;
    float _lstickCD;
    float _rstickCD;
    bool _trigReady = true;
    bool _backReady = true;
    bool _saveReady = true;
    bool _closeReady = true;

    string _status;
    bool _statusIsError;
    Task<SmbTestResult> _testTask;
    readonly object _statusLock = new object();

    struct SmbTestResult
    {
        public bool ok;
        public string message;
    }

    // ── UI objects ────────────────────────────────────────────────────────────

    GameObject _root;
    Text _titleText;
    Text[] _fieldLabels;
    Text[] _fieldValues;
    Image[] _fieldBgs;
    Text _statusText;
    Text _hintText;
    Text[,] _keyTexts;
    Image[,] _keyBgs;
    float[,] _keyX;
    float[,] _keyW;
    static Font _font;

    /// <summary>True when the panel is visible.</summary>
    public bool IsOpen => _isOpen;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        LoadFromConfig();
        CacheFont();
        BuildUI();
        _root.SetActive(false);
    }

    void Update()
    {
        PumpTest();

        HandleToggle();
        if (!_isOpen) return;

        HandleFieldNav();
        HandleKeyNav();
        HandleType();
        HandleBackspace();
        HandleSave();
        HandleClose();
    }

    // ── Load / Save ───────────────────────────────────────────────────────────

    void LoadFromConfig()
    {
        SmbSourceConfig.Reload();
        var sources = SmbSourceConfig.GetEnabledSources();
        var source = sources.Count > 0 ? sources[0] : null;

        _values[(int)Field.Host] = source != null ? source.host : "";
        _values[(int)Field.Share] = source != null ? source.share : "";
        _values[(int)Field.Folder] = source != null ? source.path : "";
        _values[(int)Field.User] = source != null ? source.username : "";
        _values[(int)Field.Password] = source != null ? source.password : "";

        if (_values[(int)Field.Host].Length == 0)
            _status = "Enter the host address of your share, then SAVE.";
        else
            _status = "Loaded share '" + _values[(int)Field.Share] + "'.";
        _statusIsError = false;
    }

    void SaveAndTest()
    {
        string host = _values[(int)Field.Host].Trim();
        string share = _values[(int)Field.Share].Trim();

        if (host.Length == 0 || share.Length == 0)
        {
            _status = "Host and share are required.";
            _statusIsError = true;
            RefreshFields();
            return;
        }

        var source = new SmbSource
        {
            enabled = true,
            id = SplatFileSystem.SanitizeSegment(
                string.IsNullOrEmpty(_values[(int)Field.User]) ? host : _values[(int)Field.User]),
            name = host + "/" + share,
            host = host,
            share = share,
            path = _values[(int)Field.Folder].Trim(),
            domain = "",
            username = _values[(int)Field.User].Trim(),
            password = _values[(int)Field.Password],
        };
        if (string.IsNullOrEmpty(source.id)) source.id = "share";

        WriteConfig(source);
        SmbSourceConfig.Reload();

        _status = "Saved. Testing connection...";
        _statusIsError = false;
        RefreshStatus();

        // Connecting blocks on the network, so it must stay off the main thread.
        _testTask = Task.Run(delegate
        {
            try
            {
                using (var client = new SmbShareClient(source))
                {
                    var entries = client.ListDirectory("");
                    return new SmbTestResult
                    {
                        ok = true,
                        message = "Connected: " + entries.Count + " item(s) in " + share + "."
                    };
                }
            }
            catch (Exception ex)
            {
                return new SmbTestResult { ok = false, message = ex.Message };
            }
        });
    }

    void WriteConfig(SmbSource source)
    {
        string path = SmbSourceConfig.ConfigPath;
        string dir = System.IO.Path.GetDirectoryName(path);
        try
        {
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

            var doc = new SmbSourceDocument();
            doc.shares.Add(source);
            string json = JsonUtility.ToJson(doc, true);
            System.IO.File.WriteAllText(path, json);
            Debug.Log("[SMB] Wrote connection to " + path);
        }
        catch (Exception ex)
        {
            _status = "Cannot write config: " + ex.Message;
            _statusIsError = true;
            Debug.LogWarning("[SMB] Cannot write config " + path + ": " + ex.Message);
        }
    }

    void PumpTest()
    {
        if (_testTask == null || !_testTask.IsCompleted) return;

        var task = _testTask;
        _testTask = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            _status = result.message;
            _statusIsError = !result.ok;
        }
        catch (Exception ex)
        {
            _status = "Connection failed: " + ex.Message;
            _statusIsError = true;
        }
        RefreshStatus();
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    void HandleToggle()
    {
        bool pressed = false;
        if (XRSettings.isDeviceActive)
            pressed = ReadButton(XRNode.LeftHand, CommonUsages.menuButton);
        else
            pressed = Input.GetKeyDown(KeyCode.F2);

        if (pressed && _toggleReady)
        {
            _toggleReady = false;
            Toggle();
        }
        else if (!pressed)
        {
            _toggleReady = true;
        }
    }

    public void Toggle()
    {
        if (_root == null) return;

        if (!_isOpen)
        {
            // Re-read the JSON so an edit made outside the app is not lost.
            LoadFromConfig();

            // Two world-space panels in front of the user would overlap.
            var browser = FindAnyObjectByType<VRFileBrowser>();
            if (browser != null && browser.IsOpen) browser.ToggleBrowser();
        }

        _isOpen = !_isOpen;
        _root.SetActive(_isOpen);
        if (_isOpen)
        {
            PositionInFront();
            RefreshFields();
            RefreshKeys();
            RefreshStatus();
            RefreshHint();
        }
    }

    void HandleFieldNav()
    {
        float y = XRSettings.isDeviceActive
            ? ReadStickY(XRNode.LeftHand)
            : Axis(KeyCode.UpArrow, KeyCode.DownArrow);

        _lstickCD -= Time.deltaTime;
        if (Mathf.Abs(y) > 0.6f && _lstickCD <= 0f)
        {
            _field = (_field + (y < 0f ? 1 : -1) + 5) % 5;
            RefreshFields();
            _lstickCD = 0.22f;
        }
        else if (Mathf.Abs(y) <= 0.3f)
        {
            _lstickCD = 0f;
        }
    }

    void HandleKeyNav()
    {
        float x = XRSettings.isDeviceActive
            ? ReadStickX(XRNode.RightHand)
            : Axis(KeyCode.LeftArrow, KeyCode.RightArrow);
        float y = XRSettings.isDeviceActive
            ? ReadStickY(XRNode.RightHand)
            : Axis(KeyCode.PageUp, KeyCode.PageDown);

        _rstickCD -= Time.deltaTime;

        if (Mathf.Abs(x) > 0.6f && _rstickCD <= 0f)
        {
            int count = KEYS[_ky].Length;
            _kx = (_kx + (x > 0f ? 1 : -1) + count) % count;
            RefreshKeys();
            _rstickCD = 0.16f;
        }
        else if (Mathf.Abs(y) > 0.6f && _rstickCD <= 0f)
        {
            _ky = (_ky + (y < 0f ? 1 : -1) + KEY_ROWS) % KEY_ROWS;
            _kx = Mathf.Clamp(_kx, 0, KEYS[_ky].Length - 1);
            RefreshKeys();
            _rstickCD = 0.22f;
        }
        else if (Mathf.Abs(x) <= 0.3f && Mathf.Abs(y) <= 0.3f)
        {
            _rstickCD = 0f;
        }
    }

    void HandleType()
    {
        bool pressed = false;
        if (XRSettings.isDeviceActive)
            pressed = ReadButton(XRNode.RightHand, CommonUsages.triggerButton)
                || ReadButton(XRNode.RightHand, CommonUsages.primaryButton);
        else
            pressed = Input.GetKeyDown(KeyCode.Return);

        if (pressed && _trigReady)
        {
            _trigReady = false;
            PressKey(KEYS[_ky][_kx]);
        }
        else if (!pressed)
        {
            _trigReady = true;
        }
    }

    void PressKey(string key)
    {
        switch (key)
        {
            case "SHIFT":
                _shift = !_shift;
                RefreshKeys();
                return;
            case "SPACE":
                Append(" ");
                return;
            case "DEL":
                Backspace();
                return;
            case "CLR":
                _values[_field] = "";
                RefreshFields();
                return;
            case "SAVE":
                SaveAndTest();
                return;
            case "CLOSE":
                Toggle();
                return;
        }

        Append(_shift ? key.ToUpperInvariant() : key);
    }

    void Append(string text)
    {
        _values[_field] = (_values[_field] ?? "") + text;
        RefreshFields();
    }

    void HandleBackspace()
    {
        bool pressed = false;
        if (XRSettings.isDeviceActive)
            pressed = ReadButton(XRNode.RightHand, CommonUsages.secondaryButton);
        else
            pressed = Input.GetKeyDown(KeyCode.Backspace);

        if (pressed && _backReady)
        {
            _backReady = false;
            Backspace();
        }
        else if (!pressed)
        {
            _backReady = true;
        }
    }

    void Backspace()
    {
        string value = _values[_field] ?? "";
        if (value.Length > 0)
            _values[_field] = value.Substring(0, value.Length - 1);
        RefreshFields();
    }

    void HandleSave()
    {
        bool pressed = false;
        if (XRSettings.isDeviceActive)
            pressed = ReadButton(XRNode.LeftHand, CommonUsages.primaryButton); // X
        else
            pressed = Input.GetKeyDown(KeyCode.F5);

        if (pressed && _saveReady)
        {
            _saveReady = false;
            SaveAndTest();
        }
        else if (!pressed)
        {
            _saveReady = true;
        }
    }

    void HandleClose()
    {
        bool pressed = false;
        if (XRSettings.isDeviceActive)
            pressed = ReadButton(XRNode.LeftHand, CommonUsages.secondaryButton); // Y
        else
            pressed = Input.GetKeyDown(KeyCode.Escape);

        if (pressed && _closeReady)
        {
            _closeReady = false;
            Toggle();
        }
        else if (!pressed)
        {
            _closeReady = true;
        }
    }

    // ── UI building ───────────────────────────────────────────────────────────

    void BuildUI()
    {
        _root = new GameObject("SmbSettingsPanel");
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        _root.AddComponent<CanvasScaler>();

        var crt = _root.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(PW, PH);
        _root.transform.localScale = Vector3.one * SCALE;

        var bg = MakeChild(_root.transform, "BG");
        bg.AddComponent<Image>().color = COL_BG;
        Stretch(bg);

        float y = -PAD;

        _titleText = MakeText(bg.transform, "Title", "SMB SHARE SETUP", FONT_TITLE, COL_TITLE,
            PAD, y, PW - PAD * 2, TITLE_H, TextAnchor.MiddleCenter);
        y -= TITLE_H + 4;

        var sep = MakeChild(bg.transform, "Sep");
        sep.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);
        SetRect(sep, PAD, y, PW - PAD * 2, 1);
        y -= 8;

        // Fields
        _fieldLabels = new Text[5];
        _fieldValues = new Text[5];
        _fieldBgs = new Image[5];

        const int labelW = 190;
        for (int i = 0; i < 5; i++)
        {
            var row = MakeChild(bg.transform, "Field" + i);
            _fieldBgs[i] = row.AddComponent<Image>();
            _fieldBgs[i].color = COL_FIELD;
            SetRect(row, PAD, y, PW - PAD * 2, FIELD_H - 6);

            _fieldLabels[i] = MakeText(bg.transform, "FLabel" + i, FieldLabels[i],
                FONT_FIELD, COL_LABEL, PAD + 12, y, labelW, FIELD_H - 6);

            _fieldValues[i] = MakeText(bg.transform, "FValue" + i, "",
                FONT_FIELD, COL_VALUE, PAD + labelW + 12, y,
                PW - PAD * 2 - labelW - 24, FIELD_H - 6);
            _fieldValues[i].horizontalOverflow = HorizontalWrapMode.Wrap;

            y -= FIELD_H;
        }

        y -= 6;

        // Keyboard
        _keyTexts = new Text[KEY_ROWS, MAX_KEYS_PER_ROW];
        _keyBgs = new Image[KEY_ROWS, MAX_KEYS_PER_ROW];
        _keyX = new float[KEY_ROWS, MAX_KEYS_PER_ROW];
        _keyW = new float[KEY_ROWS, MAX_KEYS_PER_ROW];

        int usable = PW - PAD * 2;
        for (int r = 0; r < KEY_ROWS; r++)
        {
            int count = KEYS[r].Length;
            float slot = (float)usable / count;
            float keyW = slot - KEY_GAP;

            for (int c = 0; c < count; c++)
            {
                float x = PAD + slot * c;
                _keyX[r, c] = x;
                _keyW[r, c] = keyW;

                var keyGo = MakeChild(bg.transform, "Key" + r + "_" + c);
                _keyBgs[r, c] = keyGo.AddComponent<Image>();
                _keyBgs[r, c].color = COL_KEY;
                SetRect(keyGo, x, y, keyW, ROW_H - 4);

                _keyTexts[r, c] = MakeText(bg.transform, "KeyText" + r + "_" + c, KEYS[r][c],
                    KEYS[r][c].Length > 1 ? 18 : FONT_KEY, Color.white,
                    x, y, keyW, ROW_H - 4, TextAnchor.MiddleCenter);
            }
            y -= ROW_H;
        }

        y -= 6;

        _statusText = MakeText(bg.transform, "Status", "", 16, COL_VALUE,
            PAD, y, PW - PAD * 2, 24, TextAnchor.MiddleCenter);
        y -= 26;

        _hintText = MakeText(bg.transform, "Hint", "", FONT_HINT, COL_HINT, PAD + 6, y,
            PW - PAD * 2 - 12, 32, TextAnchor.MiddleCenter);

        RefreshFields();
        RefreshKeys();
        RefreshStatus();
        RefreshHint();
    }

    void RefreshFields()
    {
        if (_fieldBgs == null) return;
        for (int i = 0; i < 5; i++)
        {
            bool active = i == _field;
            _fieldBgs[i].color = active ? COL_FIELD_ON : COL_FIELD;

            string value = _values[i] ?? "";
            if (i == (int)Field.Password && value.Length > 0)
                value = new string('*', Mathf.Min(value.Length, 24));
            if (value.Length == 0)
                value = i == _field ? "|" : FieldHints[i];
            else if (active)
                value += "|";

            _fieldValues[i].text = value;
            _fieldValues[i].color = (value.Length == 0 || (i == _field && (_values[i] ?? "").Length == 0))
                ? new Color(0.45f, 0.45f, 0.45f)
                : COL_VALUE;
        }
    }

    void RefreshKeys()
    {
        if (_keyBgs == null) return;
        for (int r = 0; r < KEY_ROWS; r++)
        {
            for (int c = 0; c < MAX_KEYS_PER_ROW; c++)
            {
                bool exists = c < KEYS[r].Length;
                _keyBgs[r, c].gameObject.SetActive(exists);
                _keyTexts[r, c].gameObject.SetActive(exists);
                if (!exists) continue;

                bool selected = r == _ky && c == _kx;
                _keyBgs[r, c].color = selected ? COL_KEY_SEL : COL_KEY;

                string label = KEYS[r][c];
                if (label.Length == 1 && _shift) label = label.ToUpperInvariant();
                _keyTexts[r, c].text = label;
            }
        }
    }

    void RefreshStatus()
    {
        if (_statusText == null) return;
        _statusText.text = _status ?? "";
        _statusText.color = _statusIsError ? COL_ERR : COL_OK;
    }

    void RefreshHint()
    {
        if (_hintText == null) return;
        _hintText.text = XRSettings.isDeviceActive
            ? "L-Stick: field    R-Stick: move    Trigger/A: type    B: delete    X: save+test    Y: close"
            : "F2: open    Arrows: field / key    Return: type    Backspace: delete    F5: save+test    Esc: close";
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    void PositionInFront()
    {
        var cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        _root.transform.position = cam.transform.position + fwd * SPAWN_DIST;
        _root.transform.rotation = Quaternion.LookRotation(fwd);
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    static void CacheFont()
    {
        if (_font != null) return;
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Arial", 14);
    }

    static GameObject MakeChild(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void SetRect(GameObject go, float x, float y, float w, float h)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot     = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    static Text MakeText(Transform parent, string name, string text,
        int fontSize, Color color, float x, float y, float w, float h,
        TextAnchor align = TextAnchor.MiddleLeft)
    {
        var go = MakeChild(parent, name);
        SetRect(go, x, y, w, h);
        var t = go.AddComponent<Text>();
        t.text      = text;
        t.fontSize  = fontSize;
        t.color     = color;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow   = VerticalWrapMode.Truncate;
        if (_font != null) t.font = _font;
        return t;
    }

    // ── XR input helpers ──────────────────────────────────────────────────────

    static float Axis(KeyCode negative, KeyCode positive)
    {
        float v = 0f;
        if (Input.GetKey(negative)) v -= 1f;
        if (Input.GetKey(positive)) v += 1f;
        return v;
    }

    static bool ReadButton(XRNode node, InputFeatureUsage<bool> usage)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(usage, out bool v))
            return v;
        return false;
    }

    static float ReadStickX(XRNode node)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v.x;
        return 0f;
    }

    static float ReadStickY(XRNode node)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v.y;
        return 0f;
    }
}
