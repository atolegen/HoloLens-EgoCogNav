// NavigationArrow.cs
// AR directional indicator shown at an anchor point.
// Displays turn direction as a 3D arrow + text label.
// Visibility is controlled externally (by UncertaintyDisplay when U_hat is high).
//
// Directions supported:
//   TurnLeft, TurnRight, GoStraight, StairsUp, StairsDown, ElevatorUp, ElevatorDown

using UnityEngine;
using TMPro;

namespace EgoCogNav
{
    public class NavigationArrow : MonoBehaviour
    {
        [Header("References (auto-created if empty)")]
        [SerializeField] private GameObject  arrowMesh;
        [SerializeField] private TMP_Text    directionLabel;

        [Header("Appearance")]
        [SerializeField] private Color colorNormal  = new Color(0.0f, 0.75f, 1.0f);   // cyan
        [SerializeField] private Color colorUrgent  = new Color(1.0f, 0.45f, 0.0f);   // orange
        [SerializeField] private float bobAmplitude = 0.04f;
        [SerializeField] private float bobSpeed     = 1.5f;

        // ── State ──────────────────────────────────────────────────────────────
        private string       anchorName;
        private NavDirection direction;
        private bool         isVisible   = false;
        private bool         isUrgent    = false;
        private Camera       mainCamera;
        private float        bobOffset;
        private Vector3      baseLocalPos;
        private Renderer[]   renderers;

        // ── Public API ─────────────────────────────────────────────────────────

        public void Setup(string name, NavDirection dir)
        {
            anchorName = name;
            direction  = dir;
            EnsureComponents();
            ApplyDirection();
        }

        /// <summary>Show or hide this arrow.</summary>
        public void SetVisible(bool visible)
        {
            isVisible = visible;
            gameObject.SetActive(visible);
        }

        /// <summary>Urgent = orange color + faster bob (called when U_hat is high).</summary>
        public void SetUrgent(bool urgent)
        {
            isUrgent = urgent;
            UpdateColor();
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Start()
        {
            mainCamera   = Camera.main;
            baseLocalPos = transform.localPosition;
            bobOffset    = Random.value * Mathf.PI * 2f;   // stagger multiple arrows
            renderers    = GetComponentsInChildren<Renderer>();
            UpdateColor();
        }

        private void Update()
        {
            if (!isVisible) return;

            Billboard();
            Bob();
        }

        // ── Visuals ────────────────────────────────────────────────────────────

        private void Billboard()
        {
            if (mainCamera == null) return;
            Vector3 dir = transform.position - mainCamera.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(dir);
        }

        private void Bob()
        {
            float speed = isUrgent ? bobSpeed * 2f : bobSpeed;
            float y     = Mathf.Sin(Time.time * speed + bobOffset) * bobAmplitude;
            transform.localPosition = baseLocalPos + Vector3.up * y;
        }

        private void UpdateColor()
        {
            Color c = isUrgent ? colorUrgent : colorNormal;
            foreach (var r in renderers ?? new Renderer[0])
            {
                if (r.material != null)
                    r.material.color = c;
            }
        }

        private void ApplyDirection()
        {
            // Rotate arrow mesh to point in the correct direction
            if (arrowMesh != null)
            {
                arrowMesh.transform.localRotation = direction switch
                {
                    NavDirection.TurnLeft     => Quaternion.Euler(0,  -90, 0),
                    NavDirection.TurnRight    => Quaternion.Euler(0,   90, 0),
                    NavDirection.GoStraight   => Quaternion.Euler(0,    0, 0),
                    NavDirection.StairsUp     => Quaternion.Euler(-45,  0, 0),
                    NavDirection.StairsDown   => Quaternion.Euler( 45,  0, 0),
                    NavDirection.ElevatorUp   => Quaternion.Euler(-45,  0, 0),
                    NavDirection.ElevatorDown => Quaternion.Euler( 45,  0, 0),
                    _                         => Quaternion.identity
                };
            }

            // Set label text
            if (directionLabel != null)
            {
                directionLabel.text = direction switch
                {
                    NavDirection.TurnLeft     => "← Turn Left",
                    NavDirection.TurnRight    => "Turn Right →",
                    NavDirection.GoStraight   => "↑ Straight",
                    NavDirection.StairsUp     => "↑ Stairs Up",
                    NavDirection.StairsDown   => "↓ Stairs Down",
                    NavDirection.ElevatorUp   => "↑ Elevator Up",
                    NavDirection.ElevatorDown => "↓ Elevator Down",
                    _                         => ""
                };
            }
        }

        private void EnsureComponents()
        {
            // Auto-create arrow mesh if not assigned
            if (arrowMesh == null)
            {
                arrowMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
                arrowMesh.name = "ArrowMesh";
                arrowMesh.transform.SetParent(transform, false);
                arrowMesh.transform.localScale    = new Vector3(0.06f, 0.06f, 0.18f);
                arrowMesh.transform.localPosition = Vector3.zero;
                Destroy(arrowMesh.GetComponent<Collider>());

                var mat = new Material(Shader.Find("Unlit/Color"));
                mat.color = colorNormal;
                arrowMesh.GetComponent<Renderer>().material = mat;
            }

            // Auto-create label if not assigned
            if (directionLabel == null)
            {
                var labelGo = new GameObject("DirectionLabel");
                labelGo.transform.SetParent(transform, false);
                labelGo.transform.localPosition = Vector3.up * 0.15f;
                labelGo.transform.localScale    = Vector3.one * 0.003f;

                var canvas = labelGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;

                var tmp = labelGo.AddComponent<TextMeshPro>();
                tmp.fontSize  = 36;
                tmp.color     = Color.white;
                tmp.alignment = TextAlignmentOptions.Center;
                directionLabel = tmp;
            }
        }
    }
}
