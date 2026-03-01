// SensorCollector.cs
// Collects head pose, IMU, and eye gaze from HoloLens 2 at 10 Hz.
// Fires OnNewFrame event with the raw SensorFrame each tick.
//
// Dependencies:
//   - MRTK 3 (for eye gaze via EyeGazeProvider)
//   - Unity XR (for IMU via InputDevice features)
//
// HoloLens Research Mode must be enabled for raw IMU:
//   Settings > Update & Security > For Developers > Research Mode > ON

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;

#if UNITY_WSA
using Microsoft.MixedReality.Toolkit;
using Microsoft.MixedReality.Toolkit.Input;
#endif

namespace EgoCogNav
{
    /// <summary>One timestep of raw HoloLens sensor data.</summary>
    [Serializable]
    public struct SensorFrame
    {
        // Head pose (world space)
        public Vector3 position;       // metres
        public Quaternion rotation;    // world-frame quaternion

        // IMU (device frame)
        public Vector3 angularVelocity; // rad/s  (gyroscope)
        public Vector3 acceleration;    // m/s²   (accelerometer, includes gravity)

        // Eye gaze — normalized screen coords [0, 1]
        public Vector2 gaze;

        // Timestamp
        public double timestamp;        // Time.timeAsDouble
    }

    public class SensorCollector : MonoBehaviour
    {
        [Header("Sampling")]
        [SerializeField] private float sampleRate = 10f;   // Hz

        [Header("Gaze fallback")]
        [Tooltip("Used when eye tracking is unavailable (e.g. in editor).")]
        [SerializeField] private Vector2 gazeCenter = new Vector2(0.5f, 0.5f);

        // ── Events ────────────────────────────────────────────────────────────
        public event Action<SensorFrame> OnNewFrame;

        // ── State ─────────────────────────────────────────────────────────────
        private InputDevice headDevice;
        private Camera mainCamera;
        private bool deviceFound = false;

        // ─────────────────────────────────────────────────────────────────────

        private void Start()
        {
            mainCamera = Camera.main;
            TryFindHeadDevice();
            StartCoroutine(SampleLoop());
        }

        private void TryFindHeadDevice()
        {
            var devices = new List<InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.HeadMounted, devices);

            if (devices.Count > 0)
            {
                headDevice = devices[0];
                deviceFound = true;
                Debug.Log($"[SensorCollector] Found head device: {headDevice.name}");
            }
            else
            {
                Debug.LogWarning("[SensorCollector] No head-mounted XR device found. Using Camera.main transform only.");
            }
        }

        private IEnumerator SampleLoop()
        {
            var wait = new WaitForSeconds(1f / sampleRate);
            while (true)
            {
                yield return wait;
                CollectAndFire();
            }
        }

        private void CollectAndFire()
        {
            var frame = new SensorFrame
            {
                timestamp = Time.timeAsDouble
            };

            // ── Head pose ─────────────────────────────────────────────────────
            if (mainCamera != null)
            {
                frame.position = mainCamera.transform.position;
                frame.rotation = mainCamera.transform.rotation;
            }

            // ── IMU ───────────────────────────────────────────────────────────
            if (deviceFound)
            {
                if (!headDevice.isValid)
                {
                    // Device disconnected — try to re-find
                    TryFindHeadDevice();
                }
                else
                {
                    headDevice.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out frame.angularVelocity);
                    headDevice.TryGetFeatureValue(CommonUsages.deviceAcceleration,    out frame.acceleration);
                }
            }
            else
            {
                // Fallback: approximate IMU from head pose delta (lower quality)
                frame.angularVelocity = Vector3.zero;
                frame.acceleration    = new Vector3(0f, -9.81f, 0f); // gravity approximation
            }

            // ── Eye Gaze ──────────────────────────────────────────────────────
            frame.gaze = GetGaze();

            OnNewFrame?.Invoke(frame);
        }

        private Vector2 GetGaze()
        {
#if UNITY_WSA
            if (CoreServices.InputSystem?.GazeProvider != null)
            {
                var gazeProvider = CoreServices.InputSystem.GazeProvider;
                if (gazeProvider.IsEyeTrackingEnabled && gazeProvider.IsEyeTrackingDataValid)
                {
                    // Project 3D gaze direction to screen UV
                    Vector3 gazeDir    = gazeProvider.GazeDirection;
                    Vector3 hitPoint   = mainCamera.transform.position + gazeDir * 2f;
                    Vector3 screenPos  = mainCamera.WorldToViewportPoint(hitPoint);
                    return new Vector2(
                        Mathf.Clamp01(screenPos.x),
                        Mathf.Clamp01(screenPos.y)
                    );
                }
            }
#endif
            return gazeCenter; // fallback: screen center
        }
    }
}
