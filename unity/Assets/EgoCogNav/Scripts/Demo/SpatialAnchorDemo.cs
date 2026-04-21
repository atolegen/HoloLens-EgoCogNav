// SpatialAnchorDemo.cs
//
// PLACE MODE  : combo-box style direction selector on a floating Canvas panel.
//               Gaze at ◄ / ► + air-tap to cycle direction.
//               Gaze at [PLACE ANCHOR] + air-tap to confirm.
//               Gaze at [MODE] to switch to Collect mode.
//
// COLLECT MODE: auto-logs head / eye-gaze / orientation every 0.5 s to CSV.
//               Gaze at [MARK] + air-tap to tag a named waypoint.
//
// Editor: Q/E = cycle dir, Space = action, Tab = toggle mode.
//
// SCENE SETUP:
//   Run  Mixed Reality → EgoCogNav → Setup MRTK3 Scene  (once).
//   This adds the MRTK XR Rig and links ARAnchorManager automatically.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;
using MixedReality.Toolkit;
using MixedReality.Toolkit.Subsystems;

#if !UNITY_EDITOR
using Microsoft.MixedReality.OpenXR;
#endif

namespace EgoCogNav.Demo
{
    public class SpatialAnchorDemo : MonoBehaviour
    {
        // ── Inspector ──────────────────────────────────────────────────────────
        [Header("Settings")]
        [SerializeField] private float placementDistance = 1.5f;

        [Header("ARAnchorManager (auto-found if empty)")]
        [SerializeField] private ARAnchorManager anchorManager;

        [Header("MRTK3 Button Labels (drag TMP child of each button here)")]
        [SerializeField] private TMP_Text dirLabel;   // shows current direction
        [SerializeField] private TMP_Text modeLabel;  // shows current mode
        [SerializeField] private TMP_Text actLabel;   // shows PLACE / MARK

        [Header("Gesture-controlled objects")]
        [Tooltip("Drag your ButtonPanel GameObject here. Left hand raised = show/hide.")]
        [SerializeField] private GameObject buttonPanel;

        // ── Mode ───────────────────────────────────────────────────────────────
        private enum AppMode { Place, Collect }
        private AppMode mode = AppMode.Place;

        private static readonly NavDirection[] Directions =
        {
            NavDirection.Start,      NavDirection.Goal,        NavDirection.FinalPoint,
            NavDirection.TurnLeft,   NavDirection.TurnRight,   NavDirection.GoStraight,
            NavDirection.StairsUp,   NavDirection.StairsDown,
            NavDirection.ElevatorUp, NavDirection.ElevatorDown,
            NavDirection.Point1,     NavDirection.Point2,      NavDirection.Point3
        };
        private int dirIndex = 0;
        private NavDirection SelectedDir => Directions[dirIndex];

        // ── Anchor state ───────────────────────────────────────────────────────
        [Serializable] private class AnchorEntry     { public string name; public NavDirection direction; }
        [Serializable] private class AnchorEntryList { public List<AnchorEntry> entries = new List<AnchorEntry>(); }
        private AnchorEntryList metadata = new AnchorEntryList();
        private string          metaPath;

        private readonly Dictionary<string, GameObject> visuals          = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, Transform>  anchorTransforms = new Dictionary<string, Transform>();
        private readonly Dictionary<TrackableId, (string name, NavDirection dir)> pendingById
            = new Dictionary<TrackableId, (string, NavDirection)>();

        private int  anchorCounter = 0;
        private bool storeReady    = false;

        // ── Local reference frame ──────────────────────────────────────────────
        private Vector3 localOrigin  = Vector3.zero;
        private Vector3 localRight   = Vector3.right;
        private Vector3 localForward = Vector3.forward;
        private bool    frameReady   = false;

        // ── Eye gaze ───────────────────────────────────────────────────────────
        private UnityEngine.XR.InputDevice eyeDevice;
        private bool                       eyeDeviceFound = false;

        // ── Gesture state ──────────────────────────────────────────────────────
        private bool  hudVisible      = true;
        private bool  panelVisible    = true;
        private bool  rightWasRaised  = false;
        private bool  leftWasRaised   = false;
        private float gestureCooldown = 0f;
        private GameObject hudGo;

        // ── CSV ────────────────────────────────────────────────────────────────
        private string anchorCsvPath;
        private string collectCsvPath;
        private string sessionId;
        private float  collectTimer = 0f;
        private const float CollectInterval = 0.5f;

        // ── Panel (WorldSpace Canvas) ──────────────────────────────────────────
        private class PanelBtn
        {
            public string  id;
            public GameObject go;
            public TMP_Text   label;
            public Image      img;
            public Color      baseColor;
        }
        private readonly List<PanelBtn> panelBtns = new List<PanelBtn>();
        private PanelBtn hoveredBtn  = null;
        private bool     tapCooldown = false;
        private Canvas   panelCanvas;

        // ── HUD (status + log + position) ──────────────────────────────────────
        private TMP_Text statusText;
        private TMP_Text logText;
        private TMP_Text posText;
        private readonly List<string> logLines = new List<string>();

#if !UNITY_EDITOR
        private XRAnchorStore anchorStore;
#endif

        // ═════════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ═════════════════════════════════════════════════════════════════════

        private void Start()
        {
            sessionId      = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            metaPath       = Path.Combine(Application.persistentDataPath, "anchors_meta.json");
            anchorCsvPath  = Path.Combine(Application.persistentDataPath, $"anchors_{sessionId}.csv");
            collectCsvPath = Path.Combine(Application.persistentDataPath, $"collect_{sessionId}.csv");

            InitAnchorCsv();
            InitCollectCsv();
            EnsureEventSystem();
            CreateHUD();

            if (anchorManager == null) anchorManager = FindObjectOfType<ARAnchorManager>();
            if (anchorManager == null) { Log("ERROR: No ARAnchorManager!"); return; }
            anchorManager.anchorsChanged += OnAnchorsChanged;

            TryFindEyeDevice();
            RefreshPanel();

#if UNITY_EDITOR
            storeReady = true;
            LoadEditorAnchors();
#else
            StartCoroutine(InitStoreCoroutine());
#endif
        }

        private void OnDestroy()
        {
            if (anchorManager != null) anchorManager.anchorsChanged -= OnAnchorsChanged;
        }

        private void Update()
        {
            UpdateGestures();
            UpdateHover();
            HandleInput();
            UpdatePositionHUD();
            TryFindEyeDevice();

            if (mode == AppMode.Collect)
            {
                collectTimer += Time.deltaTime;
                if (collectTimer >= CollectInterval) { collectTimer = 0f; LogCollectRow(false); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // INPUT
        // ═════════════════════════════════════════════════════════════════════

        private InputAction tapAction;

        private void OnEnable()
        {
            tapAction = new InputAction(name: "AirTap", type: InputActionType.Button);
            tapAction.AddBinding("<XRController>{RightHand}/triggerPressed");
            tapAction.AddBinding("<XRController>{LeftHand}/triggerPressed");
            tapAction.AddBinding("<XRController>{RightHand}/selectPressed");
            tapAction.AddBinding("<XRController>{LeftHand}/selectPressed");
            tapAction.Enable();
        }

        private void OnDisable() { tapAction?.Disable(); tapAction?.Dispose(); tapAction = null; }

        private bool TapThisFrame()
        {
#if UNITY_EDITOR
            return Input.GetMouseButtonDown(0);
#else
            return tapAction != null && tapAction.WasPressedThisFrame();
#endif
        }

        private void HandleInput()
        {
            if (Input.GetKeyDown(KeyCode.Tab))   { ToggleMode();    return; }
            if (Input.GetKeyDown(KeyCode.Q))     { CycleDir(-1);    return; }
            if (Input.GetKeyDown(KeyCode.E))     { CycleDir(+1);    return; }
            if (Input.GetKeyDown(KeyCode.Space)) { ConfirmAction(); return; }

            if (tapCooldown || !TapThisFrame()) return;
            StartCooldown();

            // Button.onClick fires via MRTK3/EventSystem when pointing at a button.
            // Only run ConfirmAction when the tap lands on empty space.
            if (hoveredBtn == null) ConfirmAction();
        }

        private void StartCooldown() { tapCooldown = true; Invoke(nameof(ResetCooldown), 0.4f); }
        private void ResetCooldown() => tapCooldown = false;

        // ═════════════════════════════════════════════════════════════════════
        // PANEL — WorldSpace Canvas
        //
        //  ┌──────────────────────────────────────────┐
        //  │           MODE: PLACE                    │  blue  — tap to switch
        //  ├──────────────────────────────────────────┤
        //  │  ◄   │       ★ Start        │   ►       │  dir selector
        //  ├──────────────────────────────────────────┤
        //  │             PLACE ANCHOR                 │  green — tap to confirm
        //  └──────────────────────────────────────────┘
        //
        // Canvas 400 × 200 px, localScale 0.001 → 0.4 m × 0.2 m in world
        // FollowCamera component handles smooth follow + facing
        // ═════════════════════════════════════════════════════════════════════

        private void CreatePanel()
        {
            var panelGo = new GameObject("AnchorPanel");

            panelCanvas = panelGo.AddComponent<Canvas>();
            panelCanvas.renderMode = RenderMode.WorldSpace;

            var scaler = panelGo.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;

            panelGo.AddComponent<GraphicRaycaster>();

            var rt = panelGo.GetComponent<RectTransform>();
            rt.sizeDelta  = new Vector2(400f, 200f);
            rt.localScale = Vector3.one * 0.001f;  // 1 px = 1 mm

            // Background
            var bg = new GameObject("BG");
            bg.transform.SetParent(panelGo.transform, false);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0.04f, 0.08f, 0.18f, 0.92f);
            bgImg.raycastTarget = false;
            var bgRt = bg.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

            // Row y-centres (canvas space, origin = canvas centre)
            const float rowY0 =  70f;   // MODE
            const float rowY1 =   0f;   // direction selector
            const float rowY2 = -70f;   // action

            const float rowH  =  50f;
            const float fullW = 370f;

            // Row 0 — mode toggle
            AddBtn("MODE", new Vector2(0f, rowY0), new Vector2(fullW, rowH),
                   "MODE: PLACE", new Color(0.15f, 0.45f, 0.80f));

            // Row 1 — ◄  [direction]  ►
            const float arrowW = 50f;
            const float gap    =  8f;
            const float dirW   = fullW - (arrowW + gap) * 2f;

            AddBtn("PREV", new Vector2(-(dirW * .5f + gap + arrowW * .5f), rowY1),
                   new Vector2(arrowW, rowH), "◄", new Color(0.35f, 0.35f, 0.45f));

            AddBtn("DIR",  new Vector2(0f, rowY1),
                   new Vector2(dirW, rowH), "★ Start", new Color(0.75f, 0.60f, 0.10f));

            AddBtn("NEXT", new Vector2(dirW * .5f + gap + arrowW * .5f, rowY1),
                   new Vector2(arrowW, rowH), "►", new Color(0.35f, 0.35f, 0.45f));

            // Row 2 — action button
            AddBtn("ACT", new Vector2(0f, rowY2), new Vector2(fullW, rowH),
                   "PLACE ANCHOR", new Color(0.18f, 0.60f, 0.28f));

            // FollowCamera keeps panel in front of user and facing them
            var fc = panelGo.AddComponent<FollowCamera>();
            // Use reflection-free public fields via default values in FollowCamera;
            // defaults: distance=0.6, followSpeed=3, offset=(0,-0.1,0) — fine for panel
        }

        private void AddBtn(string id, Vector2 pos, Vector2 size, string lbl, Color color)
        {
            var go = new GameObject($"Btn_{id}");
            go.transform.SetParent(panelCanvas.transform, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta        = size;

            var img = go.AddComponent<Image>();
            img.color         = color;
            img.raycastTarget = true;

            // Unity Button — MRTK3 hand ray fires onClick automatically
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor      = color;
            colors.highlightedColor = new Color(Mathf.Min(color.r+0.3f,1f), Mathf.Min(color.g+0.3f,1f), Mathf.Min(color.b+0.3f,1f), 1f);
            colors.pressedColor     = new Color(color.r*0.7f, color.g*0.7f, color.b*0.7f, 1f);
            colors.selectedColor    = colors.highlightedColor;
            btn.colors = colors;
            string capturedId = id;
            btn.onClick.AddListener(() => { if (!tapCooldown) { StartCooldown(); ActivateBtn(capturedId); } });

            // Label
            var lgo = new GameObject("Label");
            lgo.transform.SetParent(go.transform, false);
            var lrt = lgo.AddComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;

            var tmp = lgo.AddComponent<TextMeshProUGUI>();
            tmp.text               = lbl;
            tmp.fontSize           = size.y >= 48f ? 18f : 14f;
            tmp.color              = Color.white;
            tmp.fontStyle          = FontStyles.Bold;
            tmp.alignment          = TextAlignmentOptions.Center;
            tmp.raycastTarget      = false;
            tmp.enableWordWrapping = false;

            panelBtns.Add(new PanelBtn { id = id, go = go, label = tmp, img = img, baseColor = color });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GAZE / HOVER — uses centre-of-screen EventSystem raycast.
        // Works with MRTK3 hand rays (XRUIInputModule) and gaze-tap fallback.
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateHover()
        {
            if (EventSystem.current == null || Camera.main == null) return;

            // Set canvas camera if not yet assigned
            if (panelCanvas != null && panelCanvas.worldCamera == null)
                panelCanvas.worldCamera = Camera.main;

            // Screen centre = gaze point on HoloLens
            var ptr = new PointerEventData(EventSystem.current)
            {
                position = new Vector2(Screen.width * .5f, Screen.height * .5f)
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(ptr, hits);

            PanelBtn newHover = null;
            foreach (var h in hits)
            {
                var found = panelBtns.FirstOrDefault(b =>
                    b.go == h.gameObject ||
                    h.gameObject.transform.IsChildOf(b.go.transform));
                if (found != null) { newHover = found; break; }
            }
            hoveredBtn = newHover;

            // Highlight
            foreach (var btn in panelBtns)
            {
                if (btn.img == null) continue;
                btn.img.color = btn == hoveredBtn
                    ? new Color(
                        Mathf.Min(btn.baseColor.r + 0.25f, 1f),
                        Mathf.Min(btn.baseColor.g + 0.25f, 1f),
                        Mathf.Min(btn.baseColor.b + 0.25f, 1f), 1f)
                    : btn.baseColor;
            }
        }

        private void ActivateBtn(string id)
        {
            switch (id)
            {
                case "MODE": ToggleMode();    break;
                case "PREV": CycleDir(-1);    break;
                case "NEXT": CycleDir(+1);    break;
                case "DIR":  CycleDir(+1);    break;
                case "ACT":  ConfirmAction(); break;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // MODE / DIRECTION
        // ═════════════════════════════════════════════════════════════════════

        public void ToggleMode()
        {
            mode = mode == AppMode.Place ? AppMode.Collect : AppMode.Place;
            collectTimer = 0f;
            RefreshPanel();
            Log($"Mode → {mode}");
        }

        public void PrevDir() => CycleDir(-1);
        public void NextDir() => CycleDir(+1);
        private void CycleDir(int delta)
        {
            dirIndex = (dirIndex + delta + Directions.Length) % Directions.Length;
            RefreshPanel();
        }

        public void ConfirmAction()
        {
            if (mode == AppMode.Place) PlaceAnchor();
            else                       MarkCollectPoint();
        }

        private void RefreshPanel()
        {
            // Update the MRTK3 button labels if assigned in Inspector
            if (dirLabel  != null) dirLabel.text  = DirLabel(SelectedDir);
            if (modeLabel != null) modeLabel.text = mode == AppMode.Place ? "MODE: PLACE" : "MODE: COLLECT";
            if (actLabel  != null) actLabel.text  = mode == AppMode.Place ? "PLACE ANCHOR" : "MARK POINT";

            // Also update code-generated panel buttons if still present
            SetLabel("MODE", mode == AppMode.Place ? "MODE: PLACE" : "MODE: COLLECT");
            SetLabel("DIR",  DirLabel(SelectedDir));
            SetLabel("ACT",  mode == AppMode.Place ? "PLACE ANCHOR" : "MARK POINT");
            SetColor("ACT",  mode == AppMode.Place
                ? new Color(0.18f, 0.60f, 0.28f)
                : new Color(0.65f, 0.35f, 0.10f));
            SetVisible("PREV", mode == AppMode.Place);
            SetVisible("DIR",  mode == AppMode.Place);
            SetVisible("NEXT", mode == AppMode.Place);
        }

        private void SetLabel(string id, string lbl)
        {
            var b = panelBtns.FirstOrDefault(x => x.id == id);
            if (b?.label != null) b.label.text = lbl;
        }

        private void SetColor(string id, Color c)
        {
            var b = panelBtns.FirstOrDefault(x => x.id == id);
            if (b == null) return;
            b.baseColor = c;
            if (b.img != null) b.img.color = c;
        }

        private void SetVisible(string id, bool visible)
        {
            var b = panelBtns.FirstOrDefault(x => x.id == id);
            b?.go?.SetActive(visible);
        }

        private static string DirLabel(NavDirection d) => d switch
        {
            NavDirection.TurnLeft     => "← Turn Left",
            NavDirection.TurnRight    => "Turn Right →",
            NavDirection.GoStraight   => "↑ Go Straight",
            NavDirection.StairsUp     => "▲ Stairs Up",
            NavDirection.StairsDown   => "▼ Stairs Down",
            NavDirection.ElevatorUp   => "⬆ Elevator Up",
            NavDirection.ElevatorDown => "⬇ Elevator Down",
            NavDirection.Start        => "★ Start",
            NavDirection.Goal         => "⚑ Goal",
            NavDirection.Point1       => "① Point 1",
            NavDirection.Point2       => "② Point 2",
            NavDirection.Point3       => "③ Point 3",
            NavDirection.FinalPoint   => "✓ Final Point",
            _                         => d.ToString()
        };

        // ═════════════════════════════════════════════════════════════════════
        // PLACE ANCHOR
        // ═════════════════════════════════════════════════════════════════════

        private void PlaceAnchor()
        {
            if (!storeReady) { Log("Store not ready."); return; }
            Camera cam = Camera.main;
            if (cam == null) return;

            string     name = $"Anchor_{anchorCounter++}";
            Vector3    pos  = cam.transform.position + cam.transform.forward * placementDistance;
            Quaternion rot  = Quaternion.identity;

#if UNITY_EDITOR
            PlaceAnchorEditor(name, pos, rot);
#else
            PlaceAnchorDevice(name, pos, rot);
#endif
            LogAnchorRow(name, SelectedDir, pos);
        }

#if !UNITY_EDITOR
        private void PlaceAnchorDevice(string name, Vector3 pos, Quaternion rot)
        {
            var go     = new GameObject(name);
            go.transform.SetPositionAndRotation(pos, rot);
            var anchor = go.AddComponent<ARAnchor>();
            StartCoroutine(PersistAfterFrame(anchor, name, SelectedDir));
        }

        private IEnumerator PersistAfterFrame(ARAnchor anchor, string name, NavDirection direction)
        {
            yield return null;
            bool ok = anchorStore.TryPersistAnchor(anchor.trackableId, name);
            Debug.Log($"[AnchorDemo] TryPersistAnchor '{name}': {ok}");
            var visual = SpawnVisual(name, direction, anchor.transform.position, anchor.transform.rotation);
            visual.transform.SetParent(anchor.transform, true);
            visuals[name]          = visual;
            anchorTransforms[name] = anchor.transform;
            RebuildLocalFrame();
            metadata.entries.RemoveAll(e => e.name == name);
            metadata.entries.Add(new AnchorEntry { name = name, direction = direction });
            SaveMeta();
            Log($"Placed '{name}' [{direction}]");
            SetStatus($"{visuals.Count} anchor(s)");
        }
#endif

        // ═════════════════════════════════════════════════════════════════════
        // COLLECT MODE
        // ═════════════════════════════════════════════════════════════════════

        private void MarkCollectPoint() { LogCollectRow(true); Log("Marked!"); }

        private void LogCollectRow(bool isMarked)
        {
            if (Camera.main == null) return;
            Transform cam   = Camera.main.transform;
            Vector3   pos   = cam.position;
            Vector3   euler = cam.eulerAngles;
            Vector3   gaze  = GetEyeGaze();
            Vector3   local = ToLocalFrame(pos);
            var (nearName, _, dist) = NearestAnchor(pos);

            string line =
                $"{sessionId},{Time.realtimeSinceStartup:F2}," +
                $"{pos.x:F4},{pos.y:F4},{pos.z:F4}," +
                $"{euler.x:F2},{euler.y:F2},{euler.z:F2}," +
                $"{gaze.x:F4},{gaze.y:F4},{gaze.z:F4}," +
                $"{nearName ?? "none"},{dist:F4}," +
                $"{local.x:F4},{local.y:F4},{local.z:F4}," +
                $"{(isMarked ? "1" : "0")}\n";
            try { File.AppendAllText(collectCsvPath, line); } catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // POSITION HUD
        // ═════════════════════════════════════════════════════════════════════

        private void UpdatePositionHUD()
        {
            if (posText == null || Camera.main == null) return;
            Vector3 head  = Camera.main.transform.position;
            Vector3 local = ToLocalFrame(head);
            var (nearName, _, dist) = NearestAnchor(head);

            posText.text = nearName != null
                ? $"nearest: {nearName}  {dist:F2} m\nX:{local.x:F2}  Y:{local.y:F2}  Z:{local.z:F2}" +
                  (frameReady ? "" : "  (need ≥2 anchors)")
                : $"X:{head.x:F2}  Y:{head.y:F2}  Z:{head.z:F2}\n(no anchors yet)";
        }

        // ═════════════════════════════════════════════════════════════════════
        // EYE GAZE
        // ═════════════════════════════════════════════════════════════════════

        private void TryFindEyeDevice()
        {
            if (eyeDeviceFound) return;
            var devs = new List<UnityEngine.XR.InputDevice>();
            UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
                UnityEngine.XR.InputDeviceCharacteristics.EyeTracking, devs);
            if (devs.Count > 0) { eyeDevice = devs[0]; eyeDeviceFound = true; }
        }

        private Vector3 GetEyeGaze()
        {
            if (!eyeDeviceFound)
                return Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
            if (eyeDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.eyesData,
                    out UnityEngine.XR.Eyes eyes))
                if (eyes.TryGetFixationPoint(out Vector3 fix)) return fix;
            return Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
        }

        // ═════════════════════════════════════════════════════════════════════
        // LOCAL REFERENCE FRAME
        // ═════════════════════════════════════════════════════════════════════

        private void RebuildLocalFrame()
        {
            if (anchorTransforms.Count < 2) { frameReady = false; return; }
            var sorted = anchorTransforms.Keys.OrderBy(ParseNum)
                .Select(k => anchorTransforms[k]).Where(t => t != null).ToList();
            if (sorted.Count < 2) { frameReady = false; return; }
            localOrigin = sorted[0].position;
            Vector3 dir = sorted[1].position - localOrigin; dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) { frameReady = false; return; }
            localForward = dir.normalized;
            localRight   = Vector3.Cross(Vector3.up, localForward).normalized;
            frameReady   = true;
        }

        private Vector3 ToLocalFrame(Vector3 world)
        {
            if (!frameReady) return world;
            Vector3 off = world - localOrigin;
            return new Vector3(Vector3.Dot(off, localRight), off.y, Vector3.Dot(off, localForward));
        }

        private (string, Vector3, float) NearestAnchor(Vector3 from)
        {
            string best = null; Vector3 bPos = Vector3.zero; float bDist = float.MaxValue;
            foreach (var kv in anchorTransforms)
            {
                if (kv.Value == null) continue;
                float d = Vector3.Distance(from, kv.Value.position);
                if (d < bDist) { bDist = d; best = kv.Key; bPos = kv.Value.position; }
            }
            return best != null ? (best, bPos, bDist) : (null, Vector3.zero, float.MaxValue);
        }

        // ═════════════════════════════════════════════════════════════════════
        // AR FOUNDATION STORE + RESTORE
        // ═════════════════════════════════════════════════════════════════════

#if !UNITY_EDITOR
        private IEnumerator InitStoreCoroutine()
        {
            Log("Waiting for AR subsystem...");
            float t = 15f;
            while (anchorManager.subsystem == null && t > 0f) { t -= Time.deltaTime; yield return null; }
            if (anchorManager.subsystem == null) { Log("AR subsystem unavailable."); yield break; }

            var task = XRAnchorStore.LoadAnchorStoreAsync(anchorManager.subsystem);
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted || task.Result == null)
            { Log($"Store error: {task.Exception?.GetBaseException().Message}"); yield break; }

            anchorStore = task.Result;
            storeReady  = true;
            Debug.Log($"[AnchorDemo] Store ready. OS anchors: {anchorStore.PersistedAnchorNames.Count}");
            LoadMeta();
            RestoreFromStore();
        }

        private void RestoreFromStore()
        {
            if (metadata.entries.Count == 0)
            { Log("No saved anchors."); SetStatus("Place first anchor"); return; }

            int req = 0;
            foreach (var e in metadata.entries)
            {
                if (!anchorStore.PersistedAnchorNames.Any(n => n == e.name))
                { Debug.LogWarning($"[AnchorDemo] '{e.name}' not in OS store"); continue; }
                TrackableId id = anchorStore.LoadAnchor(e.name);
                if (id != TrackableId.invalidId)
                {
                    pendingById[id] = (e.name, e.direction);
                    anchorCounter   = Mathf.Max(anchorCounter, ParseNum(e.name) + 1);
                    req++;
                }
            }
            Log(req == 0 ? "No OS anchors — place new ones." : $"Locating {req} anchor(s)...");
            SetStatus(req == 0 ? "No OS anchors" : $"Locating {req}...");
        }
#endif

        private void OnAnchorsChanged(ARAnchorsChangedEventArgs args)
        {
            foreach (var anchor in args.added)
            {
                if (!pendingById.TryGetValue(anchor.trackableId, out var info)) continue;
                pendingById.Remove(anchor.trackableId);
                var visual = SpawnVisual(info.name, info.dir, anchor.transform.position, anchor.transform.rotation);
                visual.transform.SetParent(anchor.transform, true);
                visuals[info.name]          = visual;
                anchorTransforms[info.name] = anchor.transform;
                RebuildLocalFrame();
                Log($"Restored '{info.name}' [{info.dir}]");
                SetStatus($"{visuals.Count} anchor(s)");
            }
            foreach (var anchor in args.removed)
            {
                foreach (var kv in visuals)
                    if (kv.Value != null && kv.Value.transform.parent == anchor.transform)
                    { Destroy(kv.Value); visuals.Remove(kv.Key); anchorTransforms.Remove(kv.Key); break; }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // EDITOR FALLBACK
        // ═════════════════════════════════════════════════════════════════════

#if UNITY_EDITOR
        [Serializable] private class EdAnchor { public string name; public NavDirection direction; public float px, py, pz; }
        [Serializable] private class EdList   { public List<EdAnchor> list = new List<EdAnchor>(); }
        private string edPath => Path.Combine(Application.persistentDataPath, "anchors_editor.json");
        private EdList edData = new EdList();

        private void LoadEditorAnchors()
        {
            if (!File.Exists(edPath))
            { Log("No saved anchors."); SetStatus("Space=place  Tab=mode  Q/E=dir"); return; }
            try { edData = JsonUtility.FromJson<EdList>(File.ReadAllText(edPath)); } catch { return; }
            foreach (var a in edData.list)
            {
                var go = new GameObject($"EdAnchor_{a.name}");
                go.transform.position = new Vector3(a.px, a.py, a.pz);
                SpawnVisual(a.name, a.direction, go.transform.position, Quaternion.identity);
                anchorTransforms[a.name] = go.transform;
                anchorCounter = Mathf.Max(anchorCounter, ParseNum(a.name) + 1);
            }
            RebuildLocalFrame();
            Log($"Restored {edData.list.Count} [editor]");
            SetStatus($"{edData.list.Count} anchor(s)");
        }

        private void PlaceAnchorEditor(string name, Vector3 pos, Quaternion rot)
        {
            var go = new GameObject($"EdAnchor_{name}"); go.transform.position = pos;
            SpawnVisual(name, SelectedDir, pos, rot);
            anchorTransforms[name] = go.transform;
            RebuildLocalFrame();
            edData.list.RemoveAll(a => a.name == name);
            edData.list.Add(new EdAnchor { name = name, direction = SelectedDir, px = pos.x, py = pos.y, pz = pos.z });
            File.WriteAllText(edPath, JsonUtility.ToJson(edData, true));
            Log($"Placed '{name}' [{SelectedDir}]");
            SetStatus($"{visuals.Count} anchor(s)");
        }
#endif

        // ═════════════════════════════════════════════════════════════════════
        // SPAWN VISUAL
        // ═════════════════════════════════════════════════════════════════════

        private GameObject SpawnVisual(string name, NavDirection direction, Vector3 pos, Quaternion rot)
        {
            try
            {
                var go = new GameObject($"Visual_{name}");
                go.transform.SetPositionAndRotation(pos, rot);

                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.SetParent(go.transform, false);
                sphere.transform.localScale = Vector3.one * 0.10f;
                Destroy(sphere.GetComponent<Collider>());
                sphere.GetComponent<Renderer>().material.color = DirColor(direction);

                var labelGo = new GameObject("Label");
                labelGo.transform.SetParent(go.transform, false);
                labelGo.transform.localPosition = Vector3.up * 0.20f;
                labelGo.transform.localScale    = Vector3.one * 0.003f;
                var tmp = labelGo.AddComponent<TextMeshPro>();
                tmp.text      = $"{DirLabel(direction)}\n{name}";
                tmp.fontSize  = 18; tmp.color = Color.white;
                tmp.alignment = TextAlignmentOptions.Center;

                visuals[name] = go;
                return go;
            }
            catch (Exception e)
            {
                Debug.LogError($"[AnchorDemo] SpawnVisual: {e.Message}");
                return new GameObject();
            }
        }

        private static Color DirColor(NavDirection d) => d switch
        {
            NavDirection.Start        => Color.green,
            NavDirection.Goal         => new Color(1f, 0.5f, 0f),
            NavDirection.FinalPoint   => Color.red,
            NavDirection.TurnLeft     => Color.cyan,
            NavDirection.TurnRight    => Color.cyan,
            NavDirection.StairsUp     or NavDirection.StairsDown   => Color.yellow,
            NavDirection.ElevatorUp   or NavDirection.ElevatorDown => Color.magenta,
            _                         => Color.white
        };

        // ═════════════════════════════════════════════════════════════════════
        // CSV
        // ═════════════════════════════════════════════════════════════════════

        private void InitAnchorCsv()
        {
            try { File.WriteAllText(anchorCsvPath,
                "session,name,direction,pos_x,pos_y,pos_z,local_x,local_y,local_z\n"); }
            catch { }
        }

        private void InitCollectCsv()
        {
            try { File.WriteAllText(collectCsvPath,
                "session,time_s,head_x,head_y,head_z,pitch,yaw,roll," +
                "gaze_x,gaze_y,gaze_z,nearest,dist,local_x,local_y,local_z,marked\n"); }
            catch { }
        }

        private void LogAnchorRow(string name, NavDirection dir, Vector3 pos)
        {
            Vector3 local = ToLocalFrame(pos);
            string line = $"{sessionId},{name},{dir}," +
                          $"{pos.x:F4},{pos.y:F4},{pos.z:F4}," +
                          $"{local.x:F4},{local.y:F4},{local.z:F4}\n";
            try { File.AppendAllText(anchorCsvPath, line); } catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // PERSISTENCE
        // ═════════════════════════════════════════════════════════════════════

        private void LoadMeta()
        {
            if (!File.Exists(metaPath)) return;
            try
            {
                metadata = JsonUtility.FromJson<AnchorEntryList>(File.ReadAllText(metaPath));
                if (metadata?.entries == null) metadata = new AnchorEntryList();
            }
            catch (Exception e) { Debug.LogError($"[AnchorDemo] LoadMeta: {e.Message}"); }
        }

        private void SaveMeta()
        {
            try { File.WriteAllText(metaPath, JsonUtility.ToJson(metadata, true)); }
            catch (Exception e) { Debug.LogError($"[AnchorDemo] SaveMeta: {e.Message}"); }
        }

        private static int ParseNum(string name)
        {
            int.TryParse(name.Replace("Anchor_", ""), out int n);
            return n;
        }

        // ═════════════════════════════════════════════════════════════════════
        // HUD  (small status strip — separate from the button panel)
        // ═════════════════════════════════════════════════════════════════════

        private void CreateHUD()
        {
            var cgo = new GameObject("HUD");
            hudGo = cgo;
            var c   = cgo.AddComponent<Canvas>();
            c.renderMode = RenderMode.WorldSpace;
            cgo.AddComponent<CanvasScaler>();
            cgo.AddComponent<GraphicRaycaster>();

            var fc = cgo.AddComponent<FollowCamera>();
            // HUD sits a bit higher and closer than the button panel
            // (FollowCamera defaults: 0.6 m, offset (0, -0.1, 0))
            // We override via the serialized fields — but since we create at runtime
            // the serialized values are defaults; that's fine.

            var rt = cgo.GetComponent<RectTransform>();
            rt.sizeDelta  = new Vector2(560f, 110f);
            rt.localScale = Vector3.one * 0.001f;

            var bg = new GameObject("BG"); bg.transform.SetParent(cgo.transform, false);
            var img = bg.AddComponent<Image>(); img.color = new Color(0, 0, 0, 0.5f);
            var brt = bg.GetComponent<RectTransform>();
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = brt.offsetMax = Vector2.zero;

            statusText = MakeTMP(cgo,  35f, 13f, Color.cyan);
            logText    = MakeTMP(cgo,  -5f, 10f, Color.white);
            posText    = MakeTMP(cgo, -45f, 10f, new Color(1f, 0.9f, 0.4f));
        }

        private static TMP_Text MakeTMP(GameObject parent, float y, float size, Color color)
        {
            var go = new GameObject("T"); go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchoredPosition = new Vector2(0, y); rt.sizeDelta = new Vector2(540f, 38f);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.fontSize = size; t.color = color; t.alignment = TextAlignmentOptions.Center;
            return t;
        }

        private void Log(string msg)
        {
            Debug.Log($"[AnchorDemo] {msg}");
            logLines.Add(msg);
            if (logLines.Count > 3) logLines.RemoveAt(0);
            if (logText != null) logText.text = string.Join("\n", logLines);
        }

        private void SetStatus(string msg) { if (statusText != null) statusText.text = msg; }

        // ═════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═════════════════════════════════════════════════════════════════════

        // ═════════════════════════════════════════════════════════════════════
        // GESTURES
        // Right hand raised above head → toggle HUD
        // Left  hand raised above head → toggle button panel
        // Editor shortcuts: H = HUD, B = buttons
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateGestures()
        {
            if (Input.GetKeyDown(KeyCode.H)) { ToggleHUD();   return; }
            if (Input.GetKeyDown(KeyCode.B)) { TogglePanel(); return; }

            if (gestureCooldown > 0f) { gestureCooldown -= Time.deltaTime; return; }

            bool rightRaised = IsHandRaised(UnityEngine.XR.XRNode.RightHand);
            bool leftRaised  = IsHandRaised(UnityEngine.XR.XRNode.LeftHand);

            if (rightRaised && !rightWasRaised) { ToggleHUD();   gestureCooldown = 1.5f; }
            if (leftRaised  && !leftWasRaised)  { TogglePanel(); gestureCooldown = 1.5f; }

            rightWasRaised = rightRaised;
            leftWasRaised  = leftRaised;
        }

        private bool IsHandRaised(UnityEngine.XR.XRNode hand)
        {
            if (Camera.main == null) return false;
            try
            {
                var sub = XRSubsystemHelpers.GetFirstRunningSubsystem<HandsAggregatorSubsystem>();
                if (sub == null) return false;
                if (!sub.TryGetJoint(TrackedHandJoint.Wrist, hand, out HandJointPose wrist)) return false;
                return wrist.Position.y > Camera.main.transform.position.y + 0.15f;
            }
            catch { return false; }
        }

        private void ToggleHUD()
        {
            hudVisible = !hudVisible;
            if (hudGo != null) hudGo.SetActive(hudVisible);
            Debug.Log($"[AnchorDemo] HUD {(hudVisible ? "shown" : "hidden")}");
        }

        private void TogglePanel()
        {
            panelVisible = !panelVisible;
            if (buttonPanel != null) buttonPanel.SetActive(panelVisible);
            Debug.Log($"[AnchorDemo] Panel {(panelVisible ? "shown" : "hidden")}");
        }

        private static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }
    }
}
