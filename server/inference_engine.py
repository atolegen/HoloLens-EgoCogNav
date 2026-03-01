"""
InferenceEngine: loads the EgoCogNav model from a checkpoint and runs real-time inference.

Usage:
    engine = InferenceEngine(
        checkpoint_path="/path/to/best_model.pt",
        egocognav_src="/path/to/EgoCogNav-main/src"
    )
    result = engine.predict(sensor_window)  # sensor_window is a SensorWindow instance
    print(result['U_hat'])  # float in [0, 1]
"""

import sys
import os
import torch
import numpy as np
from pathlib import Path


class InferenceEngine:
    """
    Loads EgoCogNav model from a .pt checkpoint and runs forward inference.
    The checkpoint must contain 'config' and 'model_state_dict' keys.
    """

    def __init__(self, checkpoint_path: str, egocognav_src: str | None = None, device: str = "auto"):
        """
        Args:
            checkpoint_path: Path to best_model.pt
            egocognav_src: Path to EgoCogNav's src/ directory.
                           Falls back to EGOCOGNAV_PATH env var, then auto-detection.
            device: 'auto' (use CUDA if available), 'cpu', or 'cuda'
        """
        # ── Resolve EgoCogNav source path ───────────────────────────────────
        src_path = egocognav_src or os.environ.get("EGOCOGNAV_PATH")
        if src_path is None:
            # Try to auto-detect relative to this script
            candidates = [
                Path(__file__).parent.parent.parent / "EgoCogNav-main" / "EgoCogNav-main" / "src",
                Path(__file__).parent.parent / "EgoCogNav" / "src",
                Path.home() / "Downloads" / "EgoCogNav-main" / "EgoCogNav-main" / "src",
            ]
            for c in candidates:
                if c.exists():
                    src_path = str(c)
                    break

        if src_path is None or not Path(src_path).exists():
            raise RuntimeError(
                "Cannot find EgoCogNav src/ directory. "
                "Set EGOCOGNAV_PATH env var or pass egocognav_src argument.\n"
                "Example: export EGOCOGNAV_PATH=/path/to/EgoCogNav-main/src"
            )

        if str(src_path) not in sys.path:
            sys.path.insert(0, str(src_path))
            print(f"[InferenceEngine] Added to sys.path: {src_path}")

        # ── Device ──────────────────────────────────────────────────────────
        if device == "auto":
            self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        else:
            self.device = torch.device(device)
        print(f"[InferenceEngine] Using device: {self.device}")

        # ── Load checkpoint ──────────────────────────────────────────────────
        print(f"[InferenceEngine] Loading checkpoint: {checkpoint_path}")
        ckpt = torch.load(checkpoint_path, map_location=self.device, weights_only=False)

        if 'config' not in ckpt:
            raise KeyError("Checkpoint missing 'config' key. Was it saved by EgoCogNav trainer?")
        if 'model_state_dict' not in ckpt:
            raise KeyError("Checkpoint missing 'model_state_dict' key.")

        config = ckpt['config']
        print(f"[InferenceEngine] Config experiment: {config.get('experiment_name', 'unknown')}")

        # ── Build model ──────────────────────────────────────────────────────
        from egocognav.model.egocognav_model import create_model_and_loss_v2
        self.model, _ = create_model_and_loss_v2(config)
        self.model.load_state_dict(ckpt['model_state_dict'])
        self.model.to(self.device)
        self.model.eval()

        # Cache which modalities are active
        model_cfg = config.get('model', {})
        self.enable_uncertainty = model_cfg.get('enable_uncertainty', False)
        self.enable_video       = model_cfg.get('enable_video', False)
        self.enable_imu         = model_cfg.get('enable_imu', True)
        self.enable_gaze        = model_cfg.get('enable_gaze', False)
        self.enable_goal        = model_cfg.get('enable_goal', False)
        self.T_future           = model_cfg.get('T_future', 10)

        print(f"[InferenceEngine] Model ready | uncertainty={self.enable_uncertainty} "
              f"video={self.enable_video} imu={self.enable_imu} gaze={self.enable_gaze}")

    @torch.no_grad()
    def predict(self, sensor_window) -> dict:
        """
        Run inference on a SensorWindow.

        Args:
            sensor_window: SensorWindow instance (must be full, i.e. 30 frames)

        Returns:
            dict with keys:
                'U_hat'      : float in [0,1]  (uncertainty estimate)
                'trajectory' : list[list[float]]  (T_future x 3 body deltas, denormalized)
                'status'     : str  ('confident' / 'uncertain' / 'unknown')
        """
        batch = sensor_window.build_batch()
        if batch is None:
            return {'U_hat': 0.0, 'trajectory': [], 'status': 'buffering'}

        # Move to device
        batch = {k: v.to(self.device) for k, v in batch.items()}

        outputs = self.model(batch)

        # ── Uncertainty ───────────────────────────────────────────────────────
        U_hat = 0.0
        if self.enable_uncertainty and 'U_hat' in outputs:
            U_hat = float(outputs['U_hat'][0].cpu().item())

        # ── Trajectory (denormalize) ─────────────────────────────────────────
        trajectory = []
        if 'future_base_deltas' in outputs:
            from preprocessing import BASE_MEAN, BASE_STD
            traj_norm = outputs['future_base_deltas'][0].cpu().numpy()  # [T, 3]
            traj = traj_norm * BASE_STD + BASE_MEAN
            trajectory = traj.tolist()

        # ── Status label ──────────────────────────────────────────────────────
        if U_hat < 0.33:
            status = 'confident'
        elif U_hat < 0.66:
            status = 'moderate'
        else:
            status = 'uncertain'

        return {
            'U_hat':      U_hat,
            'trajectory': trajectory,
            'status':     status,
        }
