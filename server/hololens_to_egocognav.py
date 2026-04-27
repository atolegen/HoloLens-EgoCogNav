"""
hololens_to_egocognav.py
========================
Copies HoloLens recordings into the EgoCogNav data/PXX/taskY/ tree
and validates the CSV so processor.py can ingest it directly.

Usage:
    # Copy one recording into data/ tree and validate:
    python hololens_to_egocognav.py \
        --src  /path/to/EgoCogNav/P01/task1 \
        --data /path/to/EgoCogNav-main/data

    # Validate only (no copy):
    python hololens_to_egocognav.py --src /path/to/task1 --validate-only

    # Convert coordinate system from Unity (left-handed) to right-handed:
    python hololens_to_egocognav.py --src /path/to/task1 --data /path/to/data --fix-coords

    # Extract video frames synced to CSV (requires ffmpeg on PATH):
    python hololens_to_egocognav.py --src /path/to/task1 --data /path/to/data --extract-frames

After this script: run EgoCogNav's processor.py as usual.
"""

import argparse
import json
import shutil
import subprocess
from pathlib import Path

import numpy as np
import pandas as pd

# Exact columns the EgoCogNav processor.py expects
REQUIRED_COLUMNS = [
    "timestamp_s",
    "tx_world_device", "ty_world_device", "tz_world_device",
    "qx_world_device", "qy_world_device", "qz_world_device", "qw_world_device",
    "gyro_x_radps", "gyro_y_radps", "gyro_z_radps",
    "accel_x_mps2", "accel_y_mps2", "accel_z_mps2",
    "gaze_2d_x_norm", "gaze_2d_y_norm",
    "u_cont",
    "goal_bearing_deg", "goal_distance_m",
    "env_jct", "env_occ_sign", "env_sp_change", "env_crowd",
    "traj_hesitate", "traj_wrong", "traj_backtrack",
    "head_scan", "head_confirm", "head_lookback",
]


def validate(df: pd.DataFrame, meta: dict) -> list[str]:
    issues = []

    missing = [c for c in REQUIRED_COLUMNS if c not in df.columns]
    if missing:
        issues.append(f"Missing columns: {missing}")

    if "timestamp_s" in df.columns:
        dt = df["timestamp_s"].diff().dropna()
        mean_hz = 1.0 / dt.mean() if dt.mean() > 0 else 0
        if abs(mean_hz - 10.0) > 1.5:
            issues.append(f"Sampling rate looks off: {mean_hz:.1f} Hz (expected ~10 Hz)")

    for col in ["qx_world_device", "qy_world_device", "qz_world_device", "qw_world_device"]:
        if col in df.columns and df[col].isna().any():
            issues.append(f"NaNs in {col}")

    for col in ["gaze_2d_x_norm", "gaze_2d_y_norm"]:
        if col in df.columns:
            out = ((df[col] < 0) | (df[col] > 1)).sum()
            if out > 0:
                issues.append(f"{col}: {out} values outside [0,1]")

    if len(df) < 300:
        issues.append(f"Only {len(df)} frames — need ≥300 for at least one window (K=30, T=30, stride=5)")

    return issues


def fix_unity_to_rh(df: pd.DataFrame) -> pd.DataFrame:
    """
    Unity uses a LEFT-HANDED coordinate system (X-right, Y-up, Z-forward).
    EgoCogNav Tobii data is RIGHT-HANDED (X-right, Y-up, Z-backward or similar).

    Simplest convention-matching transform (flip Z):
        tx' = tx,  ty' = ty,  tz' = -tz
        qx' = -qx, qy' = -qy, qz' = qz, qw' = qw  (negate X,Y components)
        gyro_z' = -gyro_z
        accel_z' = -accel_z

    If your data looks correct in the processor output, skip this flag.
    """
    df = df.copy()
    df["tz_world_device"] = -df["tz_world_device"]
    df["qx_world_device"] = -df["qx_world_device"]
    df["qy_world_device"] = -df["qy_world_device"]
    df["gyro_z_radps"]    = -df["gyro_z_radps"]
    df["accel_z_mps2"]    = -df["accel_z_mps2"]
    # Re-normalise quaternions (floating point drift)
    q = df[["qx_world_device", "qy_world_device", "qz_world_device", "qw_world_device"]].values
    norms = np.linalg.norm(q, axis=1, keepdims=True).clip(1e-9)
    q /= norms
    df[["qx_world_device", "qy_world_device", "qz_world_device", "qw_world_device"]] = q
    return df


def resample_to_10hz(df: pd.DataFrame) -> pd.DataFrame:
    """Resample to exactly 10 Hz using linear interpolation on timestamp."""
    t_start = df["timestamp_s"].iloc[0]
    t_end   = df["timestamp_s"].iloc[-1]
    t_new   = np.arange(t_start, t_end, 0.1)

    df = df.set_index("timestamp_s").sort_index()
    numeric_cols = df.select_dtypes(include=[np.number]).columns
    df_num = df[numeric_cols].reindex(
        df.index.union(t_new)
    ).interpolate(method="index").reindex(t_new)

    df_out = df_num.reset_index().rename(columns={"index": "timestamp_s"})
    # Renormalise quaternions after interpolation
    q = df_out[["qx_world_device", "qy_world_device", "qz_world_device", "qw_world_device"]].values
    norms = np.linalg.norm(q, axis=1, keepdims=True).clip(1e-9)
    q /= norms
    df_out[["qx_world_device", "qy_world_device", "qz_world_device", "qw_world_device"]] = q
    return df_out


def extract_frames(src: Path, dst: Path, meta: dict) -> None:
    """Extract video frames at 10 Hz, skipping the video_start_offset."""
    video = src / "video.mp4"
    if not video.exists():
        print("  ⚠  video.mp4 not found — skipping frame extraction")
        return

    offset = meta.get("video_start_offset_s", 0.0)
    frames_dir = dst / "frames"
    frames_dir.mkdir(exist_ok=True)

    cmd = [
        "ffmpeg", "-y",
        "-ss", str(offset),
        "-i", str(video),
        "-vf", "fps=10,scale=640:480",
        "-q:v", "2",
        str(frames_dir / "frame_%04d.jpg")
    ]
    print(f"  Extracting frames (offset={offset:.3f}s) → {frames_dir}")
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        print(f"  ⚠  ffmpeg error: {result.stderr[-200:]}")
    else:
        n = len(list(frames_dir.glob("*.jpg")))
        print(f"  ✓  {n} frames extracted")


def main():
    ap = argparse.ArgumentParser(description="Prepare HoloLens data for EgoCogNav training")
    ap.add_argument("--src",             required=True,  help="Source task folder (contains data.csv + metadata.json)")
    ap.add_argument("--data",            default=None,   help="Root EgoCogNav data/ directory (omit to skip copy)")
    ap.add_argument("--validate-only",   action="store_true")
    ap.add_argument("--fix-coords",      action="store_true", help="Flip Z to convert Unity LH → RH coordinate system")
    ap.add_argument("--resample",        action="store_true", help="Resample to exactly 10 Hz via interpolation")
    ap.add_argument("--extract-frames",  action="store_true", help="Extract video frames at 10 Hz using ffmpeg")
    args = ap.parse_args()

    src = Path(args.src)
    csv_src  = src / "data.csv"
    meta_src = src / "metadata.json"

    if not csv_src.exists():
        print(f"ERROR: {csv_src} not found"); return
    if not meta_src.exists():
        print(f"ERROR: {meta_src} not found"); return

    print(f"\n{'='*60}")
    print(f"Source : {src}")
    print(f"{'='*60}")

    # Load
    df   = pd.read_csv(csv_src)
    meta = json.loads(meta_src.read_text())

    participant = meta.get("participant", src.parent.name)
    task        = meta.get("task", 1)
    print(f"Participant : {participant}")
    print(f"Task        : task{task}")
    print(f"Frames      : {len(df)}")
    print(f"Duration    : {meta.get('duration_seconds', '?')} s")

    # Optional transforms
    if args.resample:
        before = len(df)
        df = resample_to_10hz(df)
        print(f"Resampled   : {before} → {len(df)} frames at 10 Hz")

    if args.fix_coords:
        df = fix_unity_to_rh(df)
        print("Coordinates : flipped Z (Unity LH → RH)")

    # Validate
    print(f"\n--- Validation ---")
    issues = validate(df, meta)
    if issues:
        for iss in issues:
            print(f"  ⚠  {iss}")
    else:
        print("  ✓  All checks passed")

    if args.validate_only:
        return

    if args.data is None:
        print("\nNo --data directory given, skipping copy.")
        return

    # Copy to data/PXX/taskY/
    dst = Path(args.data) / participant / f"task{task}"
    dst.mkdir(parents=True, exist_ok=True)

    df.to_csv(dst / "data.csv", index=False)
    shutil.copy(meta_src, dst / "metadata.json")
    if (src / "video.mp4").exists():
        shutil.copy(src / "video.mp4", dst / "video.mp4")

    if args.extract_frames:
        extract_frames(src, dst, meta)

    print(f"\n{'='*60}")
    print(f"Written to  : {dst}")
    print(f"  data.csv  : {len(df)} rows, {len(df.columns)} columns")
    print(f"  metadata  : {(dst / 'metadata.json').stat().st_size} bytes")
    print(f"{'='*60}")
    print(f"\nNext step:")
    print(f"  cd EgoCogNav-main")
    print(f"  python src/egocognav/preprocessing/processor.py")
    print(f"  # (update data_path in processor.py to point to your data/ folder)")


if __name__ == "__main__":
    main()
