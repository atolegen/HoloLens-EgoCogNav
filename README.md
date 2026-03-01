# HoloLens-EgoCogNav

Real-time uncertainty estimation on **HoloLens 2** powered by [EgoCogNav](https://github.com/atolegen/EgoCogNav).

Streams head pose, IMU, and eye gaze from the HoloLens to a local Python inference server, which runs the EgoCogNav model and sends back an uncertainty score displayed as an AR overlay.

---

## Architecture

```
HoloLens 2 (Unity + MRTK 3)           Local PC (Python + PyTorch)
┌────────────────────────────┐         ┌───────────────────────────┐
│  SensorCollector.cs        │  JSON   │  server.py                │
│  - Head pose @ 10 Hz       ├────────►│  - asyncio WebSocket      │
│  - IMU (XR InputDevice)    │  WS     │  - preprocessing.py       │
│  - Eye gaze (MRTK)         │◄────────┤  - inference_engine.py    │
│                            │  U_hat  └───────────────────────────┘
│  UncertaintyDisplay.cs     │
│  - Color ring HUD          │
│  - green → red (0 → 1)     │
└────────────────────────────┘
```

**Protocol:** WebSocket on port 8765
**Rate:** 10 Hz (100 ms per frame)
**Checkpoint used:** `p11_p12_motion_only` (motion + IMU + gaze, no video)

---

## Repository Structure

```
├── server/
│   ├── server.py             # WebSocket inference server (entry point)
│   ├── inference_engine.py   # Loads EgoCogNav model, runs forward pass
│   ├── preprocessing.py      # Raw sensor data → normalized tensors
│   └── requirements.txt
│
└── unity/
    ├── Assets/EgoCogNav/Scripts/
    │   ├── SensorCollector.cs    # Collects HoloLens sensors at 10 Hz
    │   ├── EgoCogNavClient.cs    # WebSocket send/receive
    │   └── UncertaintyDisplay.cs # AR HUD overlay
    └── Packages/manifest.json    # MRTK 3 + NativeWebSocket dependencies
```

---

## Quick Start

### Prerequisites

| Tool | Version |
|------|---------|
| Python | 3.11+ |
| PyTorch | 2.2+ (CUDA recommended) |
| Unity | 2022.3 LTS |
| MRTK | 3.1.0 |
| HoloLens 2 | OS 22H2+ |

---

### 1. Python Inference Server

```bash
# Clone this repo
git clone https://github.com/atolegen/HoloLens-EgoCogNav.git
cd HoloLens-EgoCogNav/server

# Install dependencies
pip install -r requirements.txt

# Point the server to EgoCogNav source
export EGOCOGNAV_PATH=/path/to/EgoCogNav-main/src

# Start the server (uses best_model.pt from EgoCogNav experiments/)
python server.py \
  --checkpoint /path/to/EgoCogNav-main/experiments/p11_p12_motion_only/best_model.pt \
  --port 8765

# Expected output:
# [InferenceEngine] Using device: cuda
# [InferenceEngine] Model ready | uncertainty=True video=False imu=True gaze=True
# Starting WebSocket server on ws://0.0.0.0:8765
# Waiting for HoloLens connection...
```

Find your PC's local IP address (shown in the server log, or run `ipconfig`).

---

### 2. Unity Project Setup

1. **Open Unity Hub** → Add project → select `unity/` folder
2. Unity 2022.3 LTS will open the project and resolve packages (may take ~5 min)
3. **File → Build Settings** → Switch to **Universal Windows Platform**
   - Target Device: HoloLens
   - Architecture: ARM64
4. **Edit → Project Settings → XR Plug-in Management** → check **OpenXR**
5. Add a **Mixed Reality Feature Tool** to import MRTK 3 if not auto-resolved

#### Scene Setup

1. Create a new scene (or use SampleScene)
2. Add an empty **GameObject** named `EgoCogNavManager`
3. Attach three components to it:
   - `SensorCollector`
   - `EgoCogNavClient` → set **Server URL** to `ws://YOUR_PC_IP:8765`
   - `UncertaintyDisplay`
4. Create a **World Space Canvas** for the HUD:
   - Add a `UI → Image` for the ring → assign to `UncertaintyDisplay.ringImage`
   - Add two `TextMeshPro → Text` objects → assign to `valueText` and `statusText`
5. Save scene and include in Build Settings

#### Build & Deploy

```
File → Build Settings → Build → select output folder
```
Deploy the `.appx` to HoloLens 2 via Device Portal or Visual Studio.

---

### 3. HoloLens Research Mode (for IMU)

Raw IMU access requires Research Mode:

1. On HoloLens 2: **Settings → Update & Security → For Developers**
2. Enable **Research Mode**
3. Reboot the device

Without Research Mode, the app falls back to approximate IMU values (lower accuracy).

---

## JSON Protocol

### Unity → Server (one frame per 100ms)

```json
{
  "position":   [1.23, 0.95, 3.41],
  "quaternion": [0.0, 0.12, 0.0, 0.99],
  "gyro":       [0.01, -0.02, 0.003],
  "accel":      [0.1, -9.8, 0.5],
  "gaze":       [0.52, 0.48],
  "timestamp":  1234567.890
}
```

### Server → Unity

```json
{
  "U_hat":      0.73,
  "status":     "uncertain",
  "trajectory": [[0.01, 0.0, 0.02], ...],
  "frame":      142
}
```

**Status values:** `buffering` | `confident` (U < 0.33) | `moderate` (0.33–0.66) | `uncertain` (U > 0.66)

---

## Uncertainty Display

| U_hat | Color | Label |
|-------|-------|-------|
| 0.0 – 0.33 | Green | Confident |
| 0.33 – 0.66 | Yellow | Moderate |
| 0.66 – 1.0 | Red | Uncertain |
| N/A | Grey | Buffering / Disconnected |

The ring color smoothly interpolates — no hard cutoffs.

---

## Normalization Constants

The server applies z-score normalization using stats from the EgoCogNav training set:

| Feature | Mean | Std |
|---------|------|-----|
| body Δx | 0.006215 | 0.070871 |
| body Δy | -0.032981 | 0.079835 |
| body Δψ | -0.000114 | 0.175616 |
| gyro x | 0.04582 | 0.28158 |
| gyro y | 0.01815 | 0.61022 |
| gyro z | -0.03035 | 0.22797 |
| accel x | -0.01911 | 0.90490 |
| accel y | -9.66742 | 1.58598 |
| accel z | -1.21498 | 2.18336 |

---

## Troubleshooting

| Issue | Fix |
|-------|-----|
| `Cannot find EgoCogNav src/` | Set `EGOCOGNAV_PATH` env var |
| `Connection refused` | Check firewall — allow port 8765 on PC |
| IMU reads zeros | Enable Research Mode on HoloLens |
| Gaze always center | Complete eye calibration in HoloLens settings |
| High latency | Use 5GHz WiFi; consider reducing `sampleRate` |

---

## Related

- [EgoCogNav](https://github.com/atolegen/EgoCogNav) — the prediction model
