"""
Gymnasium environment for ULTRAKILL via the BepInEx TCP bridge.

Connects to UltrabotMod running inside the game on port 7865.
Sends actions, receives observations and rewards.
"""
from __future__ import annotations

import struct
import socket
import time
from typing import Any

import gymnasium as gym
import numpy as np
from gymnasium import spaces


# Must match C# GameStateReader constants
PLAYER_FEATURES = 41
NUM_RAYS = 24           # 12 horizontal + 4 vertical + 8 diagonal
NAV_FEATURES = 5        # dirXYZ + distance + hasPath
SPATIAL_FEATURES = NUM_RAYS + NAV_FEATURES  # 29
MAX_ENEMIES = 10
PER_ENEMY = 10
MAX_PROJECTILES = 8
PER_PROJECTILE = 8
OBS_SIZE = PLAYER_FEATURES + SPATIAL_FEATURES + MAX_ENEMIES * PER_ENEMY + MAX_PROJECTILES * PER_PROJECTILE
# 41 + 29 + 100 + 64 = 234

# Must match C# ActionExecutor.ActionSize
ACTION_SIZE = 22

# Protocol message types
MSG_STEP = 0
MSG_RESET = 1
MSG_CLOSE = 2
MSG_SET_SPEED = 3
MSG_GET_INFO = 4


class UltrakillEnv(gym.Env):
    """Gymnasium environment that communicates with the ULTRAKILL BepInEx mod."""

    metadata = {"render_modes": []}

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 7865,
        time_scale: float = 1.0,
        max_episode_steps: int = 10_000,
    ):
        super().__init__()

        self.host = host
        self.port = port
        self.time_scale = time_scale
        self.max_episode_steps = max_episode_steps

        # Action space: 4 continuous (move_fwd, move_right, look_yaw, look_pitch)
        # + 18 binary buttons
        # We use Box for everything, threshold at 0.5 for buttons
        self.action_space = spaces.Box(
            low=-1.0,
            high=1.0,
            shape=(ACTION_SIZE,),
            dtype=np.float32,
        )

        # Observation space: flat float vector from game state
        self.observation_space = spaces.Box(
            low=-np.inf,
            high=np.inf,
            shape=(OBS_SIZE,),
            dtype=np.float32,
        )

        self._sock: socket.socket | None = None
        self._steps = 0

    def _connect(self) -> None:
        """Connect to the game's TCP bridge."""
        if self._sock is not None:
            return

        for attempt in range(30):
            try:
                sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                sock.settimeout(30.0)
                sock.connect((self.host, self.port))
                self._sock = sock
                print(f"[UltrakillEnv] Connected to game on {self.host}:{self.port}")
                return
            except ConnectionRefusedError:
                print(f"[UltrakillEnv] Waiting for game... ({attempt + 1}/30)")
                time.sleep(2.0)

        raise ConnectionError(
            f"Could not connect to ULTRAKILL mod on {self.host}:{self.port}. "
            "Make sure the game is running with UltrabotMod installed."
        )

    def _send(self, data: bytes) -> None:
        self._sock.sendall(data)

    def _recv_exact(self, n: int) -> bytes:
        buf = bytearray()
        while len(buf) < n:
            chunk = self._sock.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("Game disconnected")
            buf.extend(chunk)
        return bytes(buf)

    def _send_msg(self, msg_type: int, payload: bytes = b"") -> None:
        self._send(struct.pack("<i", msg_type) + payload)

    def reset(
        self,
        *,
        seed: int | None = None,
        options: dict[str, Any] | None = None,
    ) -> tuple[np.ndarray, dict]:
        super().reset(seed=seed)
        self._connect()
        self._steps = 0

        # Set time scale
        self._send_msg(MSG_SET_SPEED, struct.pack("<f", self.time_scale))
        # Read ack
        self._recv_exact(4)

        # Send reset
        self._send_msg(MSG_RESET)

        # Receive initial observation
        obs = self._recv_observation()
        return obs, {}

    def step(self, action: np.ndarray) -> tuple[np.ndarray, float, bool, bool, dict]:
        self._steps += 1
        action = np.asarray(action, dtype=np.float32).flatten()

        # Send step with action
        payload = action.tobytes()
        self._send_msg(MSG_STEP, payload)

        # Receive response
        obs, reward, done, info = self._recv_step_response()

        truncated = self._steps >= self.max_episode_steps
        return obs, reward, done, truncated, info

    def close(self) -> None:
        if self._sock is not None:
            try:
                self._send_msg(MSG_CLOSE)
            except Exception:
                pass
            self._sock.close()
            self._sock = None

    def get_level_info(self) -> str:
        """Request text info from the game (for LLM context)."""
        self._send_msg(MSG_GET_INFO)
        str_len = struct.unpack("<i", self._recv_exact(4))[0]
        return self._recv_exact(str_len).decode("utf-8")

    # --- Protocol helpers ---

    def _recv_observation(self) -> np.ndarray:
        """Receive [obs_size int][obs floats]."""
        obs_size = struct.unpack("<i", self._recv_exact(4))[0]
        obs_bytes = self._recv_exact(obs_size * 4)
        return np.frombuffer(obs_bytes, dtype=np.float32).copy()

    def _recv_step_response(self) -> tuple[np.ndarray, float, bool, dict]:
        """Receive [obs_size int][obs floats][reward float][done byte][style int][kills int][rank int]."""
        obs_size = struct.unpack("<i", self._recv_exact(4))[0]
        obs_bytes = self._recv_exact(obs_size * 4)
        obs = np.frombuffer(obs_bytes, dtype=np.float32).copy()

        reward = struct.unpack("<f", self._recv_exact(4))[0]
        done = struct.unpack("B", self._recv_exact(1))[0] == 1

        style_score = struct.unpack("<i", self._recv_exact(4))[0]
        kills = struct.unpack("<i", self._recv_exact(4))[0]
        rank_index = struct.unpack("<i", self._recv_exact(4))[0]

        info = {
            "style_score": style_score,
            "kills": kills,
            "rank_index": rank_index,
        }
        return obs, reward, done, info
