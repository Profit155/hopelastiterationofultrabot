"""
Run the trained ULTRAKILL agent.

Usage:
    python play.py                              # play with latest checkpoint
    python play.py --weights checkpoints/best   # play with specific weights
"""
from __future__ import annotations

import argparse
from pathlib import Path

import torch
from stable_baselines3 import PPO

from ultrakill_env import UltrakillEnv


def main() -> None:
    parser = argparse.ArgumentParser(description="Run trained ULTRAKILL agent")
    parser.add_argument("--weights", type=Path, default=Path("checkpoints/latest"), help="Model weights path")
    parser.add_argument("--speed", type=float, default=1.0, help="Game time scale")
    args = parser.parse_args()

    device = "cuda" if torch.cuda.is_available() else "cpu"
    env = UltrakillEnv(time_scale=args.speed)
    model = PPO.load(str(args.weights), env=env, device=device)

    print("[Play] Agent loaded. Playing...")
    obs, _ = env.reset()

    total_reward = 0.0
    steps = 0

    try:
        while True:
            action, _ = model.predict(obs, deterministic=True)
            obs, reward, terminated, truncated, info = env.step(action)
            total_reward += reward
            steps += 1

            if steps % 100 == 0:
                print(
                    f"Step {steps} | Reward: {total_reward:.1f} | "
                    f"Style: {info.get('style_score', 0)} | "
                    f"Kills: {info.get('kills', 0)} | "
                    f"Rank: {info.get('rank_index', 0)}"
                )

            if terminated or truncated:
                print(f"Episode done. Total reward: {total_reward:.1f}, Steps: {steps}")
                total_reward = 0.0
                steps = 0
                obs, _ = env.reset()
    except KeyboardInterrupt:
        print("\n[Play] Stopped.")
    finally:
        env.close()


if __name__ == "__main__":
    main()
