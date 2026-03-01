// UncertaintyDisplay.cs
// AR overlay that shows the EgoCogNav uncertainty estimate on HoloLens.
//
// Setup in Unity Editor:
//   1. Add this component to a Canvas GameObject (World Space, or Screen Space Overlay)
//   2. Assign the three UI fields in Inspector
//   3. Attach to the same GameObject as EgoCogNavClient (or assign via inspector)
//
// UI layout expected:
//   - ringImage    : Image component used as a colored ring/circle indicator
//   - valueText    : TextMeshPro label showing "U: 0.72"
//   - statusText   : TextMeshPro label showing "Uncertain" / "Confident" / "Buffering..."
//   - connectionText: TextMeshPro label showing WS connection state

using System;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_TEXTMESHPRO
using TMPro;
#endif

namespace EgoCogNav
{
    public class UncertaintyDisplay : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private Image ringImage;

#if UNITY_TEXTMESHPRO
        [SerializeField] private TMP_Text valueText;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text connectionText;
#else
        [SerializeField] private Text valueText;
        [SerializeField] private Text statusText;
        [SerializeField] private Text connectionText;
#endif

        [Header("Colors")]
        [SerializeField] private Color colorConfident = new Color(0.18f, 0.80f, 0.44f);   // green
        [SerializeField] private Color colorModerate  = new Color(0.95f, 0.77f, 0.06f);   // yellow
        [SerializeField] private Color colorUncertain = new Color(0.91f, 0.30f, 0.24f);   // red
        [SerializeField] private Color colorBuffering = new Color(0.60f, 0.60f, 0.60f);   // grey

        [Header("Smoothing")]
        [Tooltip("How fast the displayed U_hat lerps to the new value (higher = faster).")]
        [SerializeField] private float smoothSpeed = 6f;

        [Header("HUD positioning (World Space Canvas)")]
        [SerializeField] private Vector3 hudOffset = new Vector3(0.12f, -0.08f, 0.35f);
        [SerializeField] private bool followHead = true;

        // ── Internal state ────────────────────────────────────────────────────
        private float displayedU    = 0f;
        private float targetU       = 0f;
        private string currentStatus = "buffering";
        private string connectionStatus = "disconnected";

        private EgoCogNavClient client;
        private Camera mainCamera;

        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            mainCamera = Camera.main;

            client = GetComponent<EgoCogNavClient>();
            if (client == null)
                client = FindObjectOfType<EgoCogNavClient>();

            if (client != null)
            {
                client.OnUncertaintyReceived += OnUncertaintyReceived;
                client.OnStatusChanged       += OnStatusChanged;
            }
            else
            {
                Debug.LogWarning("[UncertaintyDisplay] EgoCogNavClient not found.");
            }
        }

        private void OnDestroy()
        {
            if (client != null)
            {
                client.OnUncertaintyReceived -= OnUncertaintyReceived;
                client.OnStatusChanged       -= OnStatusChanged;
            }
        }

        private void Update()
        {
            // Smooth U_hat towards target
            displayedU = Mathf.Lerp(displayedU, targetU, Time.deltaTime * smoothSpeed);

            UpdateRingColor();
            UpdateTexts();

            if (followHead && mainCamera != null)
                FollowHead();
        }

        // ─────────────────────────────────────────────────────────────────────

        private void OnUncertaintyReceived(float U_hat)
        {
            targetU = Mathf.Clamp01(U_hat);
        }

        private void OnStatusChanged(string status)
        {
            // Could be "confident", "moderate", "uncertain", "buffering",
            //           "connected", "disconnected", "connecting"
            if (status is "connected" or "disconnected" or "connecting")
                connectionStatus = status;
            else
                currentStatus = status;
        }

        // ── Visuals ───────────────────────────────────────────────────────────

        private void UpdateRingColor()
        {
            if (ringImage == null) return;

            Color targetColor;
            if (connectionStatus != "connected")
            {
                targetColor = colorBuffering;
            }
            else if (currentStatus == "buffering")
            {
                targetColor = colorBuffering;
            }
            else
            {
                // Blend: 0→confident (green), 0.5→moderate (yellow), 1→uncertain (red)
                if (displayedU < 0.5f)
                    targetColor = Color.Lerp(colorConfident, colorModerate, displayedU * 2f);
                else
                    targetColor = Color.Lerp(colorModerate, colorUncertain, (displayedU - 0.5f) * 2f);
            }

            ringImage.color = Color.Lerp(ringImage.color, targetColor, Time.deltaTime * smoothSpeed);
        }

        private void UpdateTexts()
        {
            if (valueText != null)
            {
                if (connectionStatus != "connected")
                    valueText.text = "--";
                else if (currentStatus == "buffering")
                    valueText.text = "...";
                else
                    valueText.text = $"U: {displayedU:F2}";
            }

            if (statusText != null)
            {
                statusText.text = currentStatus switch
                {
                    "confident"  => "Confident",
                    "moderate"   => "Moderate",
                    "uncertain"  => "Uncertain",
                    "buffering"  => "Warming up...",
                    _            => currentStatus
                };
            }

            if (connectionText != null)
            {
                connectionText.text = connectionStatus switch
                {
                    "connected"    => "● Connected",
                    "connecting"   => "◌ Connecting...",
                    "disconnected" => "○ Disconnected",
                    _              => connectionStatus
                };
            }
        }

        private void FollowHead()
        {
            // Billboard: position HUD at fixed offset from camera, always facing camera
            Transform cam = mainCamera.transform;
            transform.position = cam.position + cam.TransformDirection(hudOffset);
            transform.rotation = Quaternion.LookRotation(transform.position - cam.position);
        }

        // ── Editor helper: visualize offset in scene view ─────────────────────
        private void OnDrawGizmosSelected()
        {
            if (Camera.main == null) return;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(
                Camera.main.transform.position + Camera.main.transform.TransformDirection(hudOffset),
                0.02f);
        }
    }
}
