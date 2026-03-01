// EgoCogNavClient.cs
// WebSocket client that streams sensor frames to the EgoCogNav inference server
// and fires OnUncertaintyReceived when a result comes back.
//
// Requires: NativeWebSocket (com.endel.nativewebsocket)
//   Install via Packages/manifest.json or Unity Package Manager
//   GitHub: https://github.com/endel/NativeWebSocket

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using NativeWebSocket;

namespace EgoCogNav
{
    /// <summary>Parsed response from the inference server.</summary>
    [Serializable]
    public class ServerResponse
    {
        public float  U_hat;
        public string status;
        public int    frame;
        // trajectory omitted for brevity (add if needed)
    }

    public class EgoCogNavClient : MonoBehaviour
    {
        [Header("Server")]
        [SerializeField] private string serverUrl = "ws://192.168.1.100:8765";
        [SerializeField] private float  reconnectDelay = 3f;

        [Header("Buffer")]
        [SerializeField] private int windowSize = 30;

        // ── Events ────────────────────────────────────────────────────────────
        public event Action<float>  OnUncertaintyReceived;
        public event Action<string> OnStatusChanged;

        // ── State ─────────────────────────────────────────────────────────────
        private WebSocket websocket;
        private Queue<SensorFrame> frameBuffer = new Queue<SensorFrame>();
        private SensorCollector collector;

        private string connectionStatus = "disconnected";
        public string ConnectionStatus => connectionStatus;

        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            collector = GetComponent<SensorCollector>();
            if (collector == null)
                collector = FindObjectOfType<SensorCollector>();

            if (collector == null)
                Debug.LogError("[EgoCogNavClient] SensorCollector not found on this GameObject or in scene.");
            else
                collector.OnNewFrame += OnSensorFrame;
        }

        private void Start()
        {
            StartCoroutine(ConnectLoop());
        }

        private void Update()
        {
            // NativeWebSocket requires DispatchMessageQueue() on the main thread
#if !UNITY_WEBGL || UNITY_EDITOR
            websocket?.DispatchMessageQueue();
#endif
        }

        private void OnDestroy()
        {
            if (collector != null)
                collector.OnNewFrame -= OnSensorFrame;
            websocket?.Close();
        }

        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator ConnectLoop()
        {
            while (true)
            {
                yield return StartCoroutine(ConnectOnce());
                SetStatus("disconnected");
                Debug.Log($"[EgoCogNavClient] Reconnecting in {reconnectDelay}s...");
                yield return new WaitForSeconds(reconnectDelay);
            }
        }

        private IEnumerator ConnectOnce()
        {
            SetStatus("connecting");
            Debug.Log($"[EgoCogNavClient] Connecting to {serverUrl}");

            websocket = new WebSocket(serverUrl);

            websocket.OnOpen += () =>
            {
                SetStatus("connected");
                Debug.Log("[EgoCogNavClient] Connected to server.");
            };

            websocket.OnMessage += OnMessage;

            websocket.OnError += (err) =>
            {
                Debug.LogWarning($"[EgoCogNavClient] WebSocket error: {err}");
            };

            websocket.OnClose += (code) =>
            {
                Debug.Log($"[EgoCogNavClient] Connection closed: {code}");
            };

            // Initiate connection (non-blocking)
            var connectTask = websocket.Connect();

            // Wait until the connection resolves or times out
            float timeout = 10f;
            float elapsed = 0f;
            while (!connectTask.IsCompleted && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!connectTask.IsCompleted || connectTask.IsFaulted)
            {
                Debug.LogWarning("[EgoCogNavClient] Connection timed out or failed.");
                websocket.Close();
                yield break;
            }

            // Stay alive until disconnect
            while (websocket.State == WebSocketState.Open)
                yield return null;
        }

        // ── Sensor frame handler ──────────────────────────────────────────────

        private void OnSensorFrame(SensorFrame frame)
        {
            if (websocket == null || websocket.State != WebSocketState.Open)
                return;

            // Add to sliding window
            frameBuffer.Enqueue(frame);
            while (frameBuffer.Count > windowSize)
                frameBuffer.Dequeue();

            if (frameBuffer.Count < windowSize)
                return;  // still warming up

            // Serialize and send
            string json = BuildJsonPayload(frame);
            websocket.SendText(json);
        }

        /// <summary>
        /// Sends the LATEST single frame to the server.
        /// The server maintains its own sliding window of 30 frames.
        /// </summary>
        private string BuildJsonPayload(SensorFrame f)
        {
            // Convert Unity's left-handed coords to right-handed if needed.
            // Server expects: position [x,y,z], quaternion [x,y,z,w]
            var sb = new StringBuilder(256);
            sb.Append("{");
            sb.AppendFormat("\"position\":[{0},{1},{2}],",
                f.position.x.ToString("F6"),
                f.position.y.ToString("F6"),
                f.position.z.ToString("F6"));
            sb.AppendFormat("\"quaternion\":[{0},{1},{2},{3}],",
                f.rotation.x.ToString("F6"),
                f.rotation.y.ToString("F6"),
                f.rotation.z.ToString("F6"),
                f.rotation.w.ToString("F6"));
            sb.AppendFormat("\"gyro\":[{0},{1},{2}],",
                f.angularVelocity.x.ToString("F6"),
                f.angularVelocity.y.ToString("F6"),
                f.angularVelocity.z.ToString("F6"));
            sb.AppendFormat("\"accel\":[{0},{1},{2}],",
                f.acceleration.x.ToString("F6"),
                f.acceleration.y.ToString("F6"),
                f.acceleration.z.ToString("F6"));
            sb.AppendFormat("\"gaze\":[{0},{1}],",
                f.gaze.x.ToString("F4"),
                f.gaze.y.ToString("F4"));
            sb.AppendFormat("\"timestamp\":{0}", f.timestamp.ToString("F3"));
            sb.Append("}");
            return sb.ToString();
        }

        // ── Receive handler ───────────────────────────────────────────────────

        private void OnMessage(byte[] bytes)
        {
            string json = Encoding.UTF8.GetString(bytes);
            try
            {
                var response = JsonUtility.FromJson<ServerResponse>(json);
                if (response.status != "buffering")
                {
                    OnUncertaintyReceived?.Invoke(response.U_hat);
                    OnStatusChanged?.Invoke(response.status);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EgoCogNavClient] Failed to parse response: {e.Message}\nJSON: {json}");
            }
        }

        private void SetStatus(string status)
        {
            connectionStatus = status;
            OnStatusChanged?.Invoke(status);
        }
    }
}
