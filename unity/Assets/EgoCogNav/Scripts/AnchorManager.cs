// AnchorManager.cs
// Places and persists navigation waypoints using Unity XR + JSON storage.
// Each anchor has a NavDirection (Left, Right, StairsUp, etc.)
// Positions are saved to Application.persistentDataPath and restored on startup.
//
// Usage:
//   AnchorManager.Instance.BeginPlacement("Corridor_A", NavDirection.TurnLeft, floor: 1)
//   → air tap → saved
//   AnchorManager.Instance.GetNearestAnchor(position)

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.InputSystem;

namespace EgoCogNav
{
    public enum NavDirection
    {
        TurnLeft,
        TurnRight,
        GoStraight,
        StairsUp,
        StairsDown,
        ElevatorUp,
        ElevatorDown,
        Start,
        Goal,
        Point1,
        Point2,
        Point3,
        FinalPoint
    }

    [Serializable]
    public class AnchorRecord
    {
        public string       name;
        public NavDirection direction;
        public int          floor;
        public float        px, py, pz;   // world position
        public float        rx, ry, rz, rw; // world rotation
    }

    [Serializable]
    public class AnchorRecordList
    {
        public List<AnchorRecord> anchors = new List<AnchorRecord>();
    }

    public class AnchorEntry
    {
        public string         Name;
        public NavDirection   Direction;
        public int            Floor;
        public Transform      Transform;
        public NavigationArrow Arrow;
    }

    public class AnchorManager : MonoBehaviour
    {
        public static AnchorManager Instance { get; private set; }

        [Header("Placement")]
        [SerializeField] private float placementDistance = 1.5f;

        [Header("Arrow Prefab (optional)")]
        [SerializeField] private GameObject navigationArrowPrefab;

        // ── State ──────────────────────────────────────────────────────────────
        private readonly List<AnchorEntry> entries   = new();
        private AnchorRecordList           records   = new();
        private bool                       placing   = false;
        private string                     pendingName      = "";
        private NavDirection               pendingDirection = NavDirection.GoStraight;
        private int                        pendingFloor     = 1;
        private bool                       tapCooldown      = false;
        private InputAction                tapAction        = null;

        private static readonly string SaveFile = "nav_anchors.json";

        // ── Public API ─────────────────────────────────────────────────────────

        public void BeginPlacement(string anchorName, NavDirection direction, int floor = 1)
        {
            pendingName      = anchorName;
            pendingDirection = direction;
            pendingFloor     = floor;
            placing          = true;
            Debug.Log($"[AnchorManager] Ready to place '{anchorName}' ({direction}) — air tap to confirm");
        }

        public void CancelPlacement() => placing = false;

        public AnchorEntry GetNearestAnchor(Vector3 worldPosition)
        {
            AnchorEntry best  = null;
            float       bestD = float.MaxValue;
            foreach (var e in entries)
            {
                if (e.Transform == null) continue;
                float d = Vector3.Distance(worldPosition, e.Transform.position);
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        public IReadOnlyList<AnchorEntry> GetAllAnchors() => entries;

        public void DeleteAnchor(string anchorName)
        {
            var e = entries.Find(x => x.Name == anchorName);
            if (e == null) return;
            if (e.Transform != null) Destroy(e.Transform.gameObject);
            entries.Remove(e);
            records.anchors.RemoveAll(r => r.name == anchorName);
            Save();
            Debug.Log($"[AnchorManager] Deleted '{anchorName}'");
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            tapAction = new InputAction(name: "AnchorTap", type: InputActionType.Button);
            tapAction.AddBinding("<XRController>{RightHand}/triggerPressed");
            tapAction.AddBinding("<XRController>{LeftHand}/triggerPressed");
            tapAction.AddBinding("<XRController>{RightHand}/selectPressed");
            tapAction.AddBinding("<XRController>{LeftHand}/selectPressed");
            tapAction.Enable();
        }

        private void OnDestroy()
        {
            tapAction?.Disable();
            tapAction?.Dispose();
        }

        private void Start()
        {
            LoadAnchors();
        }

        private void Update()
        {
            if (!placing || tapCooldown) return;
            if (DetectTap())
            {
                placing     = false;
                tapCooldown = true;
                Invoke(nameof(ResetCooldown), 0.5f);
                PlaceAnchor();
            }
        }

        private void ResetCooldown() => tapCooldown = false;

        // ── Input ──────────────────────────────────────────────────────────────

        private bool DetectTap()
        {
#if UNITY_EDITOR
            return Input.GetMouseButtonDown(0);
#else
            return tapAction != null && tapAction.WasPressedThisFrame();
#endif
        }

        // ── Placement ──────────────────────────────────────────────────────────

        private void PlaceAnchor()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3    pos = cam.transform.position + cam.transform.forward * placementDistance;
            Quaternion rot = Quaternion.LookRotation(cam.transform.forward, Vector3.up);

            var record = new AnchorRecord
            {
                name      = pendingName,
                direction = pendingDirection,
                floor     = pendingFloor,
                px = pos.x, py = pos.y, pz = pos.z,
                rx = rot.x, ry = rot.y, rz = rot.z, rw = rot.w
            };

            records.anchors.RemoveAll(r => r.name == pendingName);
            records.anchors.Add(record);
            Save();

            SpawnEntry(record);
            Debug.Log($"[AnchorManager] Placed and saved '{pendingName}' at {pos:F2}");
        }

        // ── Persistence ────────────────────────────────────────────────────────

        private void LoadAnchors()
        {
            string path = Path.Combine(Application.persistentDataPath, SaveFile);
            if (!File.Exists(path)) { Debug.Log("[AnchorManager] No saved anchors."); return; }

            try
            {
                records = JsonUtility.FromJson<AnchorRecordList>(File.ReadAllText(path));
                foreach (var r in records.anchors)
                {
                    SpawnEntry(r);
                    Debug.Log($"[AnchorManager] Loaded '{r.name}' floor {r.floor} → {r.direction}");
                }
                Debug.Log($"[AnchorManager] Loaded {records.anchors.Count} anchor(s).");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AnchorManager] Load error: {e.Message}");
            }
        }

        private void Save()
        {
            File.WriteAllText(
                Path.Combine(Application.persistentDataPath, SaveFile),
                JsonUtility.ToJson(records, true));
        }

        // ── Spawn ──────────────────────────────────────────────────────────────

        private AnchorEntry SpawnEntry(AnchorRecord record)
        {
            var pos = new Vector3(record.px, record.py, record.pz);
            var rot = new Quaternion(record.rx, record.ry, record.rz, record.rw);

            var go = new GameObject($"Anchor_{record.name}");
            go.transform.SetPositionAndRotation(pos, rot);

            GameObject arrowGo = navigationArrowPrefab != null
                ? Instantiate(navigationArrowPrefab, go.transform)
                : new GameObject("NavigationArrow");

            if (navigationArrowPrefab == null)
                arrowGo.transform.SetParent(go.transform, false);

            var arrow = arrowGo.GetComponent<NavigationArrow>()
                     ?? arrowGo.AddComponent<NavigationArrow>();

            arrow.Setup(record.name, record.direction);
            arrow.SetVisible(false);

            var entry = new AnchorEntry
            {
                Name      = record.name,
                Direction = record.direction,
                Floor     = record.floor,
                Transform = go.transform,
                Arrow     = arrow
            };
            entries.Add(entry);
            return entry;
        }
    }
}
