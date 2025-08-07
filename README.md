# MUGI (Mini Udon Game Interface)

> ⚠️ **Work in Progress** - This package is under active development for VRChat Game Jam 2025. APIs and structure may change.

MUGI is a framework for creating multiplayer minigames as prefabs in VRChat worlds. It provides lobby management, game lifecycle handling, scoring systems, and player tracking to simplify the development of 5-minute multiplayer VR experiences.

Originally developed for [VRChat Game Jam 2025](https://jam.vrg.party), MUGI is designed to be reusable for games beyond the jam.

## ✨ Features

- **Lobby System** - Join/leave, team selection, game master controls
- **Game Lifecycle** - Automated state management (Lobby → Countdown → Running → Ending)
- **Scoring & Teams** - Built-in score tracking and team management
- **VRChat Networking** - Synchronized multiplayer state
- **Prefab-Based** - Games as portable prefabs that can be placed at origin
- **Callback System** - Hook into game events without modifying core logic

## 🚀 Quick Start

### Installation

Add to your VRChat world project via VPM:
```
https://hiina.github.io/mugi/
```

Or add to `vpm-manifest.json`:
```json
{
  "dependencies": {
    "space.hiina.mugi": "^0.1.0"
  }
}
```

### Basic Usage

1. Create a Prefab Variant of the `MugiGame` prefab
2. Add your game objects as children
3. Configure min/max players and teams in the MugiController
4. Set up callback UdonBehaviours to respond to game events
5. Use `MugiController.IncrementScore()` and other methods in your game logic

## 📋 Package Contents

### Core Prefabs
- **MugiGame** - Main game controller prefab (create variants of this)
- **MugiLobbyUI** - Default lobby interface
- **MugiScoreboard** - Score display and timer
- **MugiPlayerObject** - Per-player tracking object

### Scripts
- **MugiGame (Controller)** - Main game logic and networking
- **MugiLobbyUI** - Lobby interface controller  
- **MugiScoreboard** - Score and timer display
- **MugiPlayerObject** - Individual player state

## 🎮 Game Lifecycle

*Documentation in progress*

States: Lobby → Countdown → Running → Ending/Aborted → (back to Lobby)

## 🔌 Callback System

*Documentation in progress*

Available callbacks:
- `OnMugiCountdown` - Game starting countdown
- `OnMugiStart` - Game begins
- `OnMugiEnd` - Game finished
- `OnMugiPlayerJoin` - Player joins
- `OnMugiPlayerLeave` - Player leaves
- `OnMugiTimeWarning` - Time running out

## 🌐 Networking & Synchronization  

*Documentation in progress*

- Game master ownership model
- Score synchronization
- Late joiner handling

## 📐 Technical Requirements

- **Space**: Designed for 20m × 20m × 20m game areas
- **Players**: 2-8 simultaneous players
- **Duration**: 5-minute maximum game time
- **VRChat SDK**: Worlds 3.8.x+
- **Unity**: 2022.3+

## 🔧 Development Status

This package is being actively developed for VRChat Game Jam 2025. Current focus areas:

- [ ] Core MugiController implementation
- [ ] UI system completion
- [ ] Networking synchronization
- [ ] Example game implementations
- [ ] Documentation and samples

## 🤝 Contributing

Since this is under active development for the game jam, please reach out before contributing:
- **Discord**: [VRChat Game Jam 2025 Server](https://jam.vrg.party)
- **Issues**: Use GitHub issues for bugs and feature requests

## 📜 License

[License TBD - will be specified before jam launch]

---

For more information about VRChat Game Jam 2025, visit https://jam.vrg.party