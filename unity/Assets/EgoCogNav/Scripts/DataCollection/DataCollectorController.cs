using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using TMPro;

namespace EgoCogNav.DataCollection
{
    public class DataCollectorController : MonoBehaviour
    {
        [Header("Session")]
        [SerializeField] private string participantId = "P01";
        [SerializeField] private int    taskNumber    = 1;
        [SerializeField] private float  recordingHz   = 10f;

        [Header("Panels")]
        [SerializeField] private GameObject mainMenuPanel;
        [SerializeField] private GameObject anchorSelectPanel;
        [SerializeField] private GameObject recStopPanel;
        [SerializeField] private TMP_Text   recStopLabel;

        [Header("Always-visible button")]
        [SerializeField] private GameObject openMenuButton;

        [Header("Optional")]
        [SerializeField] private ARAnchorManager anchorManager;

        private enum State { Idle, MainMenu, PositionSetting, AnchorSelect, Recording, RecStop }
        private State state = State.Idle;

        private enum AnchorType { Start, Anchor1, Anchor2, Goal }
        private readonly Dictionary<AnchorType, Vector3>    anchorPos     = new Dictionary<AnchorType, Vector3>();
        private readonly Dictionary<AnchorType, GameObject> anchorMarkers = new Dictionary<AnchorType, GameObject>();

        private bool    isRecording  = false;
        private float   sessionStart = 0f;
        private int     frameCount   = 0;
        private string  csvPath, metaPath, taskDir;

        private float videoStartOffset = 0f;
        private TextMeshPro statusText;

        private UnityEngine.XR.InputDevice hmdDevice, eyeDevice;
        private bool hmdFound, eyeFound;

        private const float PanelDist = 0.5f;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Start()
        {
            if (anchorManager == null)
                anchorManager = FindObjectOfType<ARAnchorManager>();

            BuildStatusText();
            SetState(State.Idle);
        }

        private void OnApplicationQuit()
        {
            if (isRecording) DoStopRecording();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && isRecording) DoStopRecording();
        }

        private void Update()
        {
            TryFindDevices();

#if UNITY_EDITOR
            if (Input.GetKeyDown(KeyCode.T)) OpenMainMenu();
            if (Input.GetKeyDown(KeyCode.S)) { PlaceAnchor(AnchorType.Start);   return; }
            if (Input.GetKeyDown(KeyCode.Alpha1)) { PlaceAnchor(AnchorType.Anchor1); return; }
            if (Input.GetKeyDown(KeyCode.Alpha2)) { PlaceAnchor(AnchorType.Anchor2); return; }
            if (Input.GetKeyDown(KeyCode.G)) { PlaceAnchor(AnchorType.Goal);    return; }
            if (Input.GetKeyDown(KeyCode.R))
            {
                if (isRecording) OnStopRecording();
                else             OnRecord();
                return;
            }
#endif
        }

        // ── Public button callbacks — wire these to MRTK3 button OnClicked ────

        public void OpenMainMenu()
        {
            if (isRecording)
                SetState(State.RecStop);
            else
                SetState(State.MainMenu);
        }

        public void OnSetPositions()  { SetState(State.AnchorSelect); }
        public void OnRecord()        { DoStartRecording(); }
        public void OnCancelMenu()    { SetState(State.Idle); }
        public void OnQuitSetting()   { SetState(State.Idle); }
        public void OnStopRecording() { DoStopRecording(); }
        public void OnCancelStop()    { SetState(State.Recording); }

        public void OnPlaceAnchor(int typeInt)
        {
            PlaceAnchor((AnchorType)typeInt);
        }

        // ── State machine ──────────────────────────────────────────────────────

        private void SetState(State next)
        {
            state = next;
            mainMenuPanel?.SetActive(false);
            anchorSelectPanel?.SetActive(false);
            recStopPanel?.SetActive(false);

            bool showOpenBtn = (next == State.Idle || next == State.Recording);
            openMenuButton?.SetActive(showOpenBtn);

            switch (next)
            {
                case State.MainMenu:
                    Show(mainMenuPanel);
                    break;
                case State.AnchorSelect:
                    Show(anchorSelectPanel);
                    break;
                case State.RecStop:
                    if (recStopLabel != null)
                        recStopLabel.text = $"STOP  {frameCount} frames";
                    Show(recStopPanel);
                    break;
            }
        }

        private void Show(GameObject panel)
        {
            if (panel == null) return;
            panel.SetActive(true);
        }

        // ── Anchor placement ───────────────────────────────────────────────────

        private void PlaceAnchor(AnchorType type)
        {
            if (Camera.main == null) return;
            Vector3 pos = Camera.main.transform.position;
            anchorPos[type] = pos;

            if (anchorMarkers.TryGetValue(type, out var old) && old != null) Destroy(old);
            anchorMarkers[type] = SpawnMarker(pos, MarkerColor(type), type.ToString());

#if !UNITY_EDITOR
            if (anchorManager != null)
            {
                var go = new GameObject($"Anchor_{type}");
                go.transform.position = pos;
                go.AddComponent<ARAnchor>();
            }
#endif
        }

        private Color MarkerColor(AnchorType t) => t switch
        {
            AnchorType.Start   => Color.green,
            AnchorType.Anchor1 => Color.cyan,
            AnchorType.Anchor2 => Color.yellow,
            AnchorType.Goal    => new Color(1f, 0.5f, 0f),
            _                  => Color.white
        };

        private GameObject SpawnMarker(Vector3 pos, Color color, string label)
        {
            var root = new GameObject($"Marker_{label}");
            root.transform.position = pos;
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.SetParent(root.transform, false);
            sphere.transform.localScale = Vector3.one * 0.08f;
            Destroy(sphere.GetComponent<Collider>());
            sphere.GetComponent<Renderer>().material.color = color;
            var lgo = new GameObject("Lbl");
            lgo.transform.SetParent(root.transform, false);
            lgo.transform.localPosition = Vector3.up * 0.12f;
            lgo.transform.localScale    = Vector3.one * 0.003f;
            var tmp = lgo.AddComponent<TextMeshPro>();
            tmp.text = label; tmp.fontSize = 18; tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            return root;
        }

        // ── Recording ──────────────────────────────────────────────────────────

        private void DoStartRecording()
        {
            taskDir  = Path.Combine(Application.persistentDataPath, "EgoCogNav", participantId, $"task{taskNumber}");
            Directory.CreateDirectory(taskDir);
            csvPath  = Path.Combine(taskDir, "data.csv");
            metaPath = Path.Combine(taskDir, "metadata.json");

            File.WriteAllText(csvPath,
                "timestamp_s,tx_world_device,ty_world_device,tz_world_device," +
                "qx_world_device,qy_world_device,qz_world_device,qw_world_device," +
                "gyro_x_radps,gyro_y_radps,gyro_z_radps," +
                "accel_x_mps2,accel_y_mps2,accel_z_mps2," +
                "gaze_2d_x_norm,gaze_2d_y_norm," +
                "u_cont,goal_bearing_deg,goal_distance_m," +
                "env_jct,env_occ_sign,env_sp_change,env_crowd," +
                "traj_hesitate,traj_wrong,traj_backtrack," +
                "head_scan,head_confirm,head_lookback\n");

            sessionStart = Time.realtimeSinceStartup;
            frameCount   = 0;
            isRecording  = true;
            WriteMetadata(0f);
            InvokeRepeating(nameof(RecordFrame), 0f, 1f / recordingHz);
            SetState(State.Recording);
        }

        private void DoStopRecording()
        {
            CancelInvoke(nameof(RecordFrame));
            isRecording = false;
            WriteMetadata(Time.realtimeSinceStartup - sessionStart);
            ShowStatus("", Color.white);
            SetState(State.Idle);
        }

        private void RecordFrame()
        {
            if (Camera.main == null) return;
            Transform  cam  = Camera.main.transform;
            float      ts   = Time.realtimeSinceStartup - sessionStart;
            Vector3    pos  = cam.position;
            Quaternion rot  = cam.rotation;
            Vector3    gyro = GetGyro();
            Vector3    acc  = GetAccel();
            (float gx, float gy) = GetGaze2D();

            float bearing = 0f, dist = 0f;
            if (anchorPos.TryGetValue(AnchorType.Goal, out Vector3 gp))
                ComputeGoalFeatures(pos, cam.forward, gp, out bearing, out dist);

            var sb = new StringBuilder(256);
            sb.Append(ts.ToString("F4")).Append(',')
              .Append(pos.x.ToString("F6")).Append(',').Append(pos.y.ToString("F6")).Append(',').Append(pos.z.ToString("F6")).Append(',')
              .Append(rot.x.ToString("F7")).Append(',').Append(rot.y.ToString("F7")).Append(',').Append(rot.z.ToString("F7")).Append(',').Append(rot.w.ToString("F7")).Append(',')
              .Append(gyro.x.ToString("F6")).Append(',').Append(gyro.y.ToString("F6")).Append(',').Append(gyro.z.ToString("F6")).Append(',')
              .Append(acc.x.ToString("F6")).Append(',').Append(acc.y.ToString("F6")).Append(',').Append(acc.z.ToString("F6")).Append(',')
              .Append(gx.ToString("F6")).Append(',').Append(gy.ToString("F6")).Append(',')
              .Append("0.0,")
              .Append(bearing.ToString("F4")).Append(',').Append(dist.ToString("F4")).Append(',')
              .Append("0,0,0,0,0,0,0,0,0,0\n");

            try { File.AppendAllText(csvPath, sb.ToString()); }
            catch (Exception e) { Debug.LogWarning($"[DataCollector] {e.Message}"); }
            frameCount++;
        }

        private void WriteMetadata(float dur)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"participant\": \"{participantId}\",");
            sb.AppendLine($"  \"task\": {taskNumber},");
            sb.AppendLine("  \"task_type\": \"goal_directed\",");
            sb.AppendLine($"  \"sampling_rate_hz\": {recordingHz:F1},");
            sb.AppendLine($"  \"duration_seconds\": {dur:F3},");
            sb.AppendLine($"  \"n_samples\": {frameCount},");
            sb.AppendLine("  \"source\": \"HoloLens 2 via OpenXR + Unity\",");
            sb.AppendLine($"  \"recorded_at\": \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",");
            sb.AppendLine($"  \"video_start_offset_s\": {videoStartOffset:F3},");
            sb.AppendLine("  \"anchors\": {");
            bool first = true;
            foreach (var kv in anchorPos)
            {
                if (!first) sb.AppendLine(",");
                sb.Append($"    \"{kv.Key}\": [{kv.Value.x:F4}, {kv.Value.y:F4}, {kv.Value.z:F4}]");
                first = false;
            }
            sb.AppendLine("\n  },");
            sb.AppendLine("  \"notes\": [\"u_cont=0 unlabelled\", \"behavior labels all zero\"]");
            sb.AppendLine("}");
            try
            {
                File.WriteAllText(metaPath, sb.ToString());
            }
            catch (Exception e)
            {
                ShowStatus($"JSON save failed:\n{e.Message}", Color.red);
            }
        }

        // ── Status text ───────────────────────────────────────────────────────

        private void BuildStatusText()
        {
            var go = new GameObject("StatusText");
            statusText = go.AddComponent<TextMeshPro>();
            statusText.fontSize  = 0.05f;
            statusText.color     = Color.yellow;
            statusText.alignment = TextAlignmentOptions.Center;
            statusText.text      = "";
        }

        private void ShowStatus(string msg, Color color)
        {
            if (statusText == null || Camera.main == null) return;
            statusText.text  = msg;
            statusText.color = color;
            Transform cam = Camera.main.transform;
            statusText.transform.position = cam.position + cam.forward * 0.6f + cam.up * 0.2f;
            statusText.transform.rotation = Quaternion.LookRotation(cam.forward);
        }

        private void LateUpdate()
        {
            if (statusText == null || Camera.main == null) return;
            if (string.IsNullOrEmpty(statusText.text)) return;
            Transform cam = Camera.main.transform;
            statusText.transform.position = cam.position + cam.forward * 0.6f + cam.up * 0.2f;
            statusText.transform.rotation = Quaternion.LookRotation(cam.forward);
        }

        // ── Sensors ────────────────────────────────────────────────────────────

        private void TryFindDevices()
        {
            if (!hmdFound)
            {
                var l = new List<UnityEngine.XR.InputDevice>();
                UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
                    UnityEngine.XR.InputDeviceCharacteristics.HeadMounted, l);
                if (l.Count > 0) { hmdDevice = l[0]; hmdFound = true; }
            }
            if (!eyeFound)
            {
                var l = new List<UnityEngine.XR.InputDevice>();
                UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
                    UnityEngine.XR.InputDeviceCharacteristics.EyeTracking, l);
                if (l.Count > 0) { eyeDevice = l[0]; eyeFound = true; }
            }
        }

        private Vector3 GetGyro()
        {
            if (hmdFound && hmdDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceAngularVelocity, out Vector3 v)) return v;
            return Vector3.zero;
        }

        private Vector3 GetAccel()
        {
            if (hmdFound && hmdDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceAcceleration, out Vector3 v)) return v;
            return Vector3.zero;
        }

        private (float x, float y) GetGaze2D()
        {
            if (eyeFound && eyeDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.eyesData, out UnityEngine.XR.Eyes eyes))
                if (eyes.TryGetFixationPoint(out Vector3 fix))
                {
                    var vp = Camera.main.WorldToViewportPoint(fix);
                    if (vp.z > 0f) return (Mathf.Clamp01(vp.x), Mathf.Clamp01(vp.y));
                }
            return (0.5f, 0.5f);
        }

        private static void ComputeGoalFeatures(Vector3 pos, Vector3 fwd, Vector3 goal,
                                                  out float bearingDeg, out float distM)
        {
            Vector3 d = goal - pos;
            distM = new Vector2(d.x, d.z).magnitude;
            float goalYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float headYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            bearingDeg = goalYaw - headYaw;
            while (bearingDeg >  180f) bearingDeg -= 360f;
            while (bearingDeg < -180f) bearingDeg += 360f;
        }

        // ── Panel positioning ──────────────────────────────────────────────────

        private void PositionInFront(GameObject panel)
        {
            if (Camera.main == null) return;
            Transform cam = Camera.main.transform;
            panel.transform.position = cam.position + cam.forward * PanelDist;
            panel.transform.rotation = Quaternion.LookRotation(cam.forward);
        }

        private IEnumerator FallIn(Transform t)
        {
            Vector3 end   = t.position;
            Vector3 start = end + Vector3.up * 0.25f;
            t.position = start;
            float elapsed = 0f;
            while (elapsed < 0.25f)
            {
                elapsed += Time.deltaTime;
                t.position = Vector3.LerpUnclamped(start, end, Mathf.SmoothStep(0f, 1f, elapsed / 0.25f));
                yield return null;
            }
            t.position = end;
        }
    }
}
