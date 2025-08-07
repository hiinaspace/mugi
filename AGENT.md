# AGENT.md - MUGI Framework Development Guide

## Project Overview

**MUGI (MiniUdonGameInterface)** is a VRChat Udon framework designed to simplify the creation of 5-minute multiplayer VR minigames for VRChat worlds. This project was created for the VRChat Game Jam 2025 but is designed to be reusable beyond the jam.

### Core Goals
- Enable creators to build multiplayer VR minigames quickly
- Provide standardized lobby, scoring, and player management systems
- Handle complex networking/synchronization automatically
- Support 2-8 players with team or free-for-all modes
- Work within VRChat's 20m³ space constraints

## Critical: UdonSharp Limitations

**UdonSharp is NOT full C#**. It compiles to Udon assembly with significant restrictions:

### ❌ **NOT Supported:**
- **Generic classes/methods** (`List<T>`, `Dictionary<K,V>`)
- **Inheritance** (only `UdonSharpBehaviour` base class allowed)
- **Interfaces** (`IInterface`)
- **Method overloads** (multiple methods with same name)
- **Properties** (use public fields instead)
- **Custom enums** (only Unity-predefined enums)
- **Lambdas/delegates**
- **LINQ**
- **async/await**
- **Exceptions** (try/catch/throw)

### ✅ **Supported Patterns:**
```csharp
// ✅ Public fields instead of properties
public float gameTime = 300f;

// ✅ Explicit getter/setter methods instead of properties  
public float GetGameTime() { return gameTime; }
public void SetGameTime(float time) { gameTime = time; }

// ✅ Arrays instead of generic collections
public VRCPlayerApi[] players = new VRCPlayerApi[8];
public int[] playerScores = new int[8];

// ✅ Single method signatures (no overloads)
public void IncrementScore(int playerId, int amount) { /* */ }
public void IncrementScoreByOne(int playerId) { /* */ }  // Different name required
```

## Network Synchronization Patterns

### Synced Variables
```csharp
[UdonSynced] public bool gameRunning;
[UdonSynced, FieldChangeCallback(nameof(OnScoreChanged))] 
private int _syncedScore;

// FieldChangeCallback requires property-style pattern
public int SyncedScore
{
    get => _syncedScore;
    set 
    {
        _syncedScore = value;
        UpdateScoreDisplay();
    }
}

public void OnScoreChanged() 
{
    // Called when _syncedScore changes via network
    UpdateScoreDisplay();
}
```

### Critical Network Constraints
- **Synced variables are expensive** - limit usage
- **String max length ~50 characters** 
- **Events arrive before synced variables update** - causes race conditions
- **No networked instantiation** - use object pooling
- **Manual sync mode recommended** for complex behaviors

### Ownership Patterns
```csharp
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class MugiController : UdonSharpBehaviour
{
    public void TakeOwnership()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        // Now this player can modify synced variables
        RequestSerialization();
    }
}
```

## MUGI Framework Architecture

### MugiController API Patterns
```csharp
// Configuration (set in inspector)
public int minPlayers = 2;
public int maxPlayers = 8;
public float gameTimeLimit = 300f;

// Runtime state (read-only for games)
public bool isGameRunning;
public int activePlayers;
public int[] playerScores = new int[8];

// Callbacks (assign UdonBehaviours in inspector)
public UdonBehaviour[] onStartCallbacks;
public UdonBehaviour[] onEndCallbacks;

// Game control methods
public void IncrementPlayerScore(int playerId, int amount)
{
    if (playerId >= 0 && playerId < playerScores.Length)
    {
        playerScores[playerId] += amount;
        RequestSerialization();
    }
}
```

### Event System Pattern
Since method overloads aren't supported, use explicit method names:
```csharp
// In your game behaviour
public void OnMugiGameStart()
{
    Debug.Log("Game started!");
    EnableGameplay();
}

public void OnMugiGameEnd()
{
    Debug.Log("Game ended!");
    DisableGameplay();
}

// MugiController calls these via SendCustomEvent
public void NotifyGameStart()
{
    foreach (UdonBehaviour callback in onStartCallbacks)
    {
        if (callback != null)
            callback.SendCustomEvent("OnMugiGameStart");
    }
}
```

## Common Gotchas & Solutions

### 1. Race Conditions
**Problem:** Network events arrive before synced variable updates
```csharp
// ❌ This creates race conditions
public void UpdateScoreAndNotify(int newScore)
{
    syncedScore = newScore;  // Syncs eventually
    SendCustomNetworkEvent(NetworkEventTarget.All, "OnScoreUpdated");  // Immediate
}
```

**Solution:** Use FieldChangeCallback or local detection
```csharp
// ✅ FieldChangeCallback ensures proper order
[UdonSynced, FieldChangeCallback(nameof(OnScoreSync))]
private int _syncedScore;

public void OnScoreSync()
{
    // This runs when the synced value actually changes
    UpdateScoreDisplay();
    SendCustomNetworkEvent(NetworkEventTarget.All, "OnScoreChanged");
}
```

### 2. Array Bounds
**Problem:** VRChat doesn't have great bounds checking
```csharp
// ✅ Always validate array access
public void SetPlayerScore(int playerId, int score)
{
    if (playerId >= 0 && playerId < playerScores.Length && playerId < activePlayers)
    {
        playerScores[playerId] = score;
    }
    else
    {
        Debug.LogError($"Invalid playerId: {playerId}");
    }
}
```

### 3. Component References
```csharp
// ❌ Generic GetComponent not supported
// var udon = GetComponent<UdonBehaviour>();

// ✅ Use explicit type casting
UdonBehaviour udon = (UdonBehaviour)GetComponent(typeof(UdonBehaviour));

// ✅ Or better: assign in inspector
[SerializeField] private UdonBehaviour targetBehaviour;
```

## Testing & Debugging

### Local Testing
- Use VRChat ClientSim for basic functionality testing
- Test with multiple players via ClientSim's multiple player simulation
- Check Unity Console for UdonSharp compilation errors

### Common Runtime Errors
- **NullReferenceException**: Always null-check before method calls
- **IndexOutOfRangeException**: Validate array indices  
- **Network sync failures**: Check ownership before modifying synced variables
- **Method not found**: Ensure SendCustomEvent target methods are public

### Performance Considerations
- **Avoid Update() when possible** - use events instead
- **Limit Debug.Log calls** - they impact VR performance
- **Pool objects instead of instantiation**
- **Minimize synced variables**

## Example Implementation Pattern

```csharp
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace Space.Hiina.Mugi.Examples
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ButtonMasherGame : UdonSharpBehaviour
    {
        [Header("MUGI Integration")]
        public UdonBehaviour mugiController;
        
        [Header("Game Objects")]
        public GameObject[] playerButtons;
        
        private bool gameActive = false;
        
        // Called by MugiController via onStartCallbacks
        public void OnMugiGameStart()
        {
            gameActive = true;
            EnableAllButtons();
        }
        
        // Called by MugiController via onEndCallbacks  
        public void OnMugiGameEnd()
        {
            gameActive = false;
            DisableAllButtons();
        }
        
        // Called by button interaction
        public void OnButtonPressed()
        {
            if (!gameActive) return;
            
            int playerId = GetLocalPlayerId();
            if (playerId >= 0)
            {
                // Call MugiController method to increment score
                mugiController.SendCustomEvent("IncrementScore");
                mugiController.SetProgramVariable("targetPlayerId", playerId);
                mugiController.SetProgramVariable("scoreAmount", 1);
            }
        }
        
        private int GetLocalPlayerId()
        {
            // Implementation to find local player's ID in the game
            return 0; // Simplified
        }
        
        private void EnableAllButtons()
        {
            foreach (GameObject button in playerButtons)
            {
                if (button != null)
                    button.SetActive(true);
            }
        }
        
        private void DisableAllButtons()
        {
            foreach (GameObject button in playerButtons)
            {
                if (button != null)
                    button.SetActive(false);
            }
        }
    }
}
```

## Build & Testing Workflow

1. **Always test in Unity first** - catch compilation errors early
2. **Use ClientSim** for basic multiplayer simulation  
3. **Test in VRChat** with real players for networking validation
4. **Check performance** - aim for 90fps in VR
5. **Validate boundaries** - ensure game fits in 20m³ cube

## Key Files Structure

```
Packages/space.hiina.mugi/
├── Runtime/
│   ├── MugiGame.prefab              # Base prefab for all games
│   ├── Scripts/
│   │   ├── MugiController.cs        # Core framework logic
│   │   ├── MugiLobbyUI.cs          # Lobby interface
│   │   └── MugiPlayerObject.cs     # Per-player tracking
│   └── Handpixies/                  # Example game implementation
└── Samples/
    └── ButtonMasher/                # Reference implementation
```

## Development Guidelines

1. **Start simple** - get basic interaction working first
2. **Test networking early** - sync issues are hard to debug later  
3. **Use MugiController callbacks** - don't reinvent the wheel
4. **Follow UdonSharp constraints** - don't fight the platform
5. **Performance first** - VR can't tolerate frame drops
6. **Document public methods** - other developers will use your game

## Resources

- [UdonSharp Documentation](https://github.com/vrchat-community/udonsharp)
- [VRChat Creator Companion](https://vcc.docs.vrchat.com/)
- [VRChat SDK Documentation](https://creators.vrchat.com/)
- [Udon Node Graph](https://creators.vrchat.com/worlds/udon/) (for understanding underlying system)

## When Working on This Codebase

- **Always validate UdonSharp compatibility** before implementing patterns
- **Test with ClientSim first**, then VRChat
- **Respect the 20m³ space limit** for all game designs
- **Use the MugiController API** rather than custom networking
- **Follow the namespace pattern**: `Space.Hiina.Mugi.*`
- **Remember**: This needs to work in VR at 90fps with multiple players

---

*Generated for VRChat Game Jam 2025 - MUGI Framework v1.0*