"""
Preprocessing: raw HoloLens sensor data → EgoCogNav model inputs

The available checkpoint (p11_p12_motion_only) needs:
  - past_base_deltas  [1, 30, 3]  z-score normalized body motion deltas
  - imu               [1, 30, 6]  z-score normalized gyro + accel
  - gaze_2d           [1, 30, 2]  raw [0,1] screen coordinates
"""

import numpy as np
from collections import deque
from scipy.spatial.transform import Rotation
import torch

# ── Normalization statistics (from EgoCogNav dataset/ego/normalization_stats.json) ──
BASE_MEAN = np.array([0.006215027533471584, -0.03298071026802063, -0.00011351043940521777], dtype=np.float32)
BASE_STD  = np.array([0.0708707869052887,    0.07983461767435074,  0.17561568319797516],    dtype=np.float32)

IMU_MEAN  = np.array([0.04581765457987785,  0.01815127767622471, -0.030352314934134483,
                      -0.01910555735230446, -9.667417526245117,  -1.2149766683578491],    dtype=np.float32)
IMU_STD   = np.array([0.2815835475921631,   0.6102176308631897,   0.22797097265720367,
                       0.9048970341682434,   1.5859758853912354,   2.1833596229553223],   dtype=np.float32)

WINDOW_SIZE = 30  # 3.0 seconds @ 10 Hz


def quat_to_yaw(q_xyzw: np.ndarray) -> float:
    """Convert quaternion [x, y, z, w] to yaw angle (ZYX Euler convention)."""
    r = Rotation.from_quat(q_xyzw)
    euler = r.as_euler('ZYX')  # [yaw, pitch, roll]
    return float(euler[0])


def wrap_angle(angle: float) -> float:
    """Wrap angle to [-pi, pi]."""
    return float(np.arctan2(np.sin(angle), np.cos(angle)))


class SensorFrame:
    """One timestep of raw HoloLens sensor data."""
    __slots__ = ('position', 'quaternion', 'gyro', 'accel', 'gaze')

    def __init__(self,
                 position: list,    # [x, y, z] metres, world frame
                 quaternion: list,  # [x, y, z, w]
                 gyro: list,        # [gx, gy, gz] rad/s, device frame
                 accel: list,       # [ax, ay, az] m/s², device frame (includes gravity)
                 gaze: list):       # [gaze_x, gaze_y] normalized [0,1]
        self.position  = np.array(position,   dtype=np.float32)
        self.quaternion = np.array(quaternion, dtype=np.float32)
        self.gyro      = np.array(gyro,        dtype=np.float32)
        self.accel     = np.array(accel,       dtype=np.float32)
        self.gaze      = np.array(gaze,        dtype=np.float32)


class SensorWindow:
    """
    Circular buffer that accumulates SensorFrames and converts them
    into the batch dict expected by EgoCogNavModel.forward().
    """

    def __init__(self, window_size: int = WINDOW_SIZE):
        self.window_size = window_size
        self._frames: deque = deque(maxlen=window_size)

    @property
    def is_full(self) -> bool:
        return len(self._frames) == self.window_size

    def update(self, frame: SensorFrame):
        """Append one new sensor frame (oldest is dropped when full)."""
        self._frames.append(frame)

    def build_batch(self) -> dict | None:
        """
        Build the model input batch dict from the current window.
        Returns None if the window is not yet full.
        """
        if not self.is_full:
            return None

        frames = list(self._frames)  # oldest → newest

        # ── 1. Body motion deltas (Δx, Δy, Δyaw) ──────────────────────────
        positions  = np.stack([f.position  for f in frames])   # [30, 3]
        quats      = np.stack([f.quaternion for f in frames])  # [30, 4]
        yaws       = np.array([quat_to_yaw(q) for q in quats], dtype=np.float32)  # [30]

        pos_deltas = np.diff(positions, axis=0, prepend=positions[[0]])   # [30, 3]
        yaw_deltas = np.diff(yaws,      prepend=yaws[[0]])                 # [30]
        yaw_deltas = np.array([wrap_angle(d) for d in yaw_deltas], dtype=np.float32)

        base_deltas = np.column_stack([pos_deltas[:, 0],   # Δx
                                       pos_deltas[:, 1],   # Δy
                                       yaw_deltas])        # Δψ  →  [30, 3]
        base_deltas_norm = (base_deltas - BASE_MEAN) / (BASE_STD + 1e-8)

        # ── 2. IMU (gyro + accel) ───────────────────────────────────────────
        imu_raw  = np.stack([np.concatenate([f.gyro, f.accel]) for f in frames])  # [30, 6]
        imu_raw  = np.nan_to_num(imu_raw, nan=0.0)
        imu_norm = (imu_raw - IMU_MEAN) / (IMU_STD + 1e-8)

        # ── 3. Gaze (already [0,1], no normalization) ───────────────────────
        gaze = np.stack([f.gaze for f in frames])          # [30, 2]
        gaze = np.nan_to_num(gaze, nan=0.5)                # center if missing

        # ── 4. Build batch dict with batch dim ─────────────────────────────
        batch = {
            'past_base_deltas': torch.tensor(base_deltas_norm[np.newaxis], dtype=torch.float32),  # [1, 30, 3]
            'imu':              torch.tensor(imu_norm[np.newaxis],          dtype=torch.float32),  # [1, 30, 6]
            'gaze_2d':          torch.tensor(gaze[np.newaxis],              dtype=torch.float32),  # [1, 30, 2]
        }
        return batch
