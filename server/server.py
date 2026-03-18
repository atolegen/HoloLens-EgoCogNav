"""
EgoCogNav WebSocket Inference Server

Listens for sensor data from HoloLens (Unity), runs EgoCogNav inference,
and sends back uncertainty estimates.

Usage:
    python server.py --checkpoint /path/to/best_model.pt [--port 8765] [--host 0.0.0.0]

Environment:
    EGOCOGNAV_PATH=/path/to/EgoCogNav-main/src  (required if not passed via CLI)
"""

import asyncio
import json
import argparse
import logging
import signal
import numpy as np
import websockets
from websockets import ServerConnection as WebSocketServerProtocol

from preprocessing import SensorFrame, SensorWindow
from inference_engine import InferenceEngine

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S"
)
log = logging.getLogger(__name__)

# Global inference engine (initialized once at startup)
engine: InferenceEngine = None


async def handle_client(websocket: WebSocketServerProtocol):
    """
    Handles one HoloLens client connection.
    Each connection gets its own SensorWindow (sliding buffer).
    """
    client_addr = websocket.remote_address
    log.info(f"HoloLens connected: {client_addr}")

    window = SensorWindow(window_size=30)
    frame_count = 0

    try:
        async for raw_msg in websocket:
            try:
                msg = json.loads(raw_msg)
            except json.JSONDecodeError as e:
                log.warning(f"Bad JSON from {client_addr}: {e}")
                continue

            # ── Parse incoming sensor frame ──────────────────────────────────
            # Expected format (single frame update):
            # {
            #   "position":   [x, y, z],
            #   "quaternion": [x, y, z, w],
            #   "gyro":       [gx, gy, gz],
            #   "accel":      [ax, ay, az],
            #   "gaze":       [gx, gy]
            # }
            try:
                frame = SensorFrame(
                    position   = msg['position'],
                    quaternion = msg['quaternion'],
                    gyro       = msg['gyro'],
                    accel      = msg['accel'],
                    gaze       = msg.get('gaze', [0.5, 0.5]),
                )
            except (KeyError, ValueError) as e:
                log.warning(f"Malformed frame from {client_addr}: {e}")
                continue

            window.update(frame)
            frame_count += 1

            # ── Run inference once buffer is warm ────────────────────────────
            if window.is_full:
                result = engine.predict(window)

                response = json.dumps({
                    'U_hat':      round(result['U_hat'], 4),
                    'status':     result['status'],
                    'trajectory': result['trajectory'],
                    'frame':      frame_count,
                })
                await websocket.send(response)

                if frame_count % 50 == 0:
                    log.info(f"{client_addr} | frame={frame_count} U_hat={result['U_hat']:.3f} [{result['status']}]")
            else:
                # Still warming up — send buffering status
                buffering = json.dumps({
                    'U_hat':   0.0,
                    'status':  'buffering',
                    'frames':  len(window._frames),
                    'needed':  window.window_size,
                })
                await websocket.send(buffering)

    except websockets.exceptions.ConnectionClosedOK:
        log.info(f"HoloLens disconnected (clean): {client_addr} after {frame_count} frames")
    except websockets.exceptions.ConnectionClosedError as e:
        log.warning(f"HoloLens disconnected (error): {client_addr} | {e}")
    except Exception as e:
        log.error(f"Unexpected error for {client_addr}: {e}", exc_info=True)


async def main(args):
    global engine

    log.info(f"Loading model from: {args.checkpoint}")
    engine = InferenceEngine(
        checkpoint_path=args.checkpoint,
        egocognav_src=args.egocognav_src,
    )
    log.info("Model loaded and ready.")

    # Graceful shutdown on Ctrl+C
    stop_event = asyncio.Event()
    loop = asyncio.get_running_loop()
    try:
        # Unix only
        loop.add_signal_handler(signal.SIGINT,  stop_event.set)
        loop.add_signal_handler(signal.SIGTERM, stop_event.set)
    except NotImplementedError:
        # Windows fallback
        signal.signal(signal.SIGINT,  lambda s, f: loop.call_soon_threadsafe(stop_event.set))
        signal.signal(signal.SIGTERM, lambda s, f: loop.call_soon_threadsafe(stop_event.set))

    log.info(f"Starting WebSocket server on ws://{args.host}:{args.port}")
    log.info("Waiting for HoloLens connection...")

    async with websockets.serve(handle_client, args.host, args.port):
        await stop_event.wait()

    log.info("Server shut down.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="EgoCogNav HoloLens Inference Server")
    parser.add_argument(
        "--checkpoint", required=True,
        help="Path to EgoCogNav .pt checkpoint (e.g. experiments/p11_p12_motion_only/best_model.pt)"
    )
    parser.add_argument("--host", default="0.0.0.0",
                        help="Host to bind (default: 0.0.0.0 = all interfaces)")
    parser.add_argument("--port", type=int, default=8765,
                        help="WebSocket port (default: 8765)")
    parser.add_argument("--egocognav-src", default=None,
                        help="Path to EgoCogNav src/ directory (overrides EGOCOGNAV_PATH env var)")
    args = parser.parse_args()

    asyncio.run(main(args))
