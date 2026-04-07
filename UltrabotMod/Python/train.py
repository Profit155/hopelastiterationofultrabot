"""
Training script for the ULTRAKILL RL agent.

Usage:
    python train.py                     # train with defaults
    python train.py --timesteps 500000  # custom step count
    python train.py --speed 3.0         # 3x game speed
    python train.py --resume            # resume from last checkpoint
"""
from __future__ import annotations

import argparse
import signal
from pathlib import Path

import torch
from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import CheckpointCallback, BaseCallback
from stable_baselines3.common.monitor import Monitor

from ultrakill_env import UltrakillEnv


class StyleLogCallback(BaseCallback):
    """Logs ULTRAKILL-specific metrics to TensorBoard."""

    def _on_step(self) -> bool:
        infos = self.locals.get("infos", [])
        for info in infos:
            if "style_score" in info:
                self.logger.record("ultrakill/style_score", info["style_score"])
                self.logger.record("ultrakill/kills", info["kills"])
                self.logger.record("ultrakill/rank_index", info["rank_index"])
        return True


def main() -> None:
    parser = argparse.ArgumentParser(description="Train ULTRAKILL RL agent")
    parser.add_argument("--timesteps", type=int, default=1_000_000, help="Total training timesteps")
    parser.add_argument("--speed", type=float, default=1.0, help="Game time scale (1.0 = normal)")
    parser.add_argument("--lr", type=float, default=3e-4, help="Learning rate")
    parser.add_argument("--batch-size", type=int, default=256, help="Mini-batch size")
    parser.add_argument("--n-steps", type=int, default=4096, help="Steps per PPO rollout")
    parser.add_argument("--resume", action="store_true", help="Resume from last checkpoint")
    parser.add_argument("--checkpoint-dir", type=Path, default=Path("checkpoints"), help="Checkpoint directory")
    parser.add_argument("--log-dir", type=Path, default=Path("logs"), help="TensorBoard log directory")
    args = parser.parse_args()

    args.checkpoint_dir.mkdir(exist_ok=True)
    args.log_dir.mkdir(exist_ok=True)

    # Create environment
    env = Monitor(
        UltrakillEnv(time_scale=args.speed),
        str(args.log_dir / "monitor"),
    )

    device = "cuda" if torch.cuda.is_available() else "cpu"
    print(f"[Train] Device: {device}")
    print(f"[Train] Game speed: {args.speed}x")
    print(f"[Train] Total timesteps: {args.timesteps}")

    if args.resume and (args.checkpoint_dir / "latest.zip").exists():
        print("[Train] Resuming from checkpoint...")
        model = PPO.load(
            args.checkpoint_dir / "latest",
            env=env,
            device=device,
        )
    else:
        model = PPO(
            "MlpPolicy",
            env,
            learning_rate=args.lr,
            batch_size=args.batch_size,
            n_steps=args.n_steps,
            n_epochs=10,
            gamma=0.99,
            gae_lambda=0.95,
            clip_range=0.2,
            ent_coef=0.05,          # encourage action exploration
            vf_coef=0.5,
            max_grad_norm=0.5,
            verbose=1,
            device=device,
            tensorboard_log=str(args.log_dir),
            policy_kwargs=dict(
                net_arch=dict(pi=[256, 256], vf=[256, 256]),
            ),
        )

    # Callbacks
    checkpoint_cb = CheckpointCallback(
        save_freq=10_000,
        save_path=str(args.checkpoint_dir),
        name_prefix="ultrakill_ppo",
    )
    style_cb = StyleLogCallback()

    # Train
    print("[Train] Starting training. Press Ctrl+C to stop.")
    try:
        model.learn(
            total_timesteps=args.timesteps,
            callback=[checkpoint_cb, style_cb],
            reset_num_timesteps=not args.resume,
        )
    except KeyboardInterrupt:
        print("\n[Train] Interrupted by user.")
    finally:
        # Always save and clean up properly
        try:
            model.save(args.checkpoint_dir / "latest")
            print(f"[Train] Model saved to {args.checkpoint_dir / 'latest.zip'}")
        except Exception as e:
            print(f"[Train] WARNING: Could not save model: {e}")
        try:
            env.close()
            print("[Train] Environment closed cleanly.")
        except Exception as e:
            print(f"[Train] WARNING: Error closing environment: {e}")


if __name__ == "__main__":
    main()
