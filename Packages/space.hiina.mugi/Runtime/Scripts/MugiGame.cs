using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace Space.Hiina.Mugi
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class MugiGame : UdonSharpBehaviour
    {
        // Game state constants (UdonSharp doesn't support custom enums or static classes)
        public const int STATE_LOBBY = 0;
        public const int STATE_COUNTDOWN = 1;
        public const int STATE_RUNNING = 2;
        public const int STATE_ENDED = 3;

        [Header("Game Configuration")]
        public int minPlayers = 2;
        public int maxPlayers = 8;
        public bool useTeams = false;
        public int maxTeams = 2;
        public string[] teamNames = { "Team 1", "Team 2", "Team 3", "Team 4" };
        public float gameTimeLimit = 300f; // 5 minutes
        public float countdownTime = 3f;

        [Header("Runtime State (Read-Only)")]
        [UdonSynced]
        public int gameState = STATE_LOBBY;

        [UdonSynced]
        public float timeRemaining;

        [UdonSynced]
        public int activePlayers = 0;

        // Player tracking - using VRCPlayerApi directly causes sync issues, so we'll use player IDs
        [UdonSynced]
        public int[] playerIds = new int[8]; // VRCPlayerApi.playerId for each slot

        [UdonSynced]
        public int[] playerTeams = new int[8]; // team assignment for each player slot, -1 = not assigned

        [UdonSynced]
        public int[] playerScores = new int[8];

        // Callbacks for game events
        [Header("Event Callbacks")]
        public UdonBehaviour[] onCountdownCallbacks;
        public UdonBehaviour[] onStartCallbacks;
        public UdonBehaviour[] onEndCallbacks;
        public UdonBehaviour[] onPlayerJoinCallbacks;
        public UdonBehaviour[] onPlayerLeaveCallbacks;
        public UdonBehaviour[] onTimeWarningCallbacks;

        private VRCPlayerApi[] playerApiCache = new VRCPlayerApi[8]; // Cache for performance
        private bool gameRunning = false;

        void Start()
        {
            // Initialize arrays
            for (int i = 0; i < 8; i++)
            {
                playerIds[i] = -1;
                playerTeams[i] = -1;
                playerScores[i] = 0;
            }

            timeRemaining = gameTimeLimit;

            // Take ownership if no one owns this
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }
        }

        // ========== PUBLIC API METHODS ==========

        public bool IsGameReady()
        {
            if (activePlayers < minPlayers)
                return false;

            if (useTeams)
            {
                // Check that we have players in at least 2 teams
                bool[] teamsUsed = new bool[maxTeams];
                int teamsWithPlayers = 0;

                for (int i = 0; i < activePlayers; i++)
                {
                    int team = playerTeams[i];
                    if (team >= 0 && team < maxTeams && !teamsUsed[team])
                    {
                        teamsUsed[team] = true;
                        teamsWithPlayers++;
                    }
                }

                return teamsWithPlayers >= 2;
            }

            return true; // FFA just needs minimum players
        }

        public VRCPlayerApi[] GetPlayersInGame()
        {
            VRCPlayerApi[] result = new VRCPlayerApi[activePlayers];
            int resultIndex = 0;

            for (int i = 0; i < activePlayers; i++)
            {
                if (playerIds[i] != -1)
                {
                    VRCPlayerApi player = VRCPlayerApi.GetPlayerById(playerIds[i]);
                    if (player != null)
                    {
                        result[resultIndex] = player;
                        resultIndex++;
                    }
                }
            }

            return result;
        }

        public int GetPlayerTeam(VRCPlayerApi player)
        {
            if (player == null)
                return -1;

            int playerId = player.playerId;
            for (int i = 0; i < activePlayers; i++)
            {
                if (playerIds[i] == playerId)
                {
                    return playerTeams[i];
                }
            }
            return -1; // Player not in game
        }

        public bool IsPlayerInGame(VRCPlayerApi player)
        {
            return GetPlayerTeam(player) >= -1; // -1 means in game but no team, < -1 means not in game
        }

        public float GetRemainingTime()
        {
            return timeRemaining;
        }

        public string GetTeamName(int teamIndex)
        {
            if (teamIndex >= 0 && teamIndex < teamNames.Length)
            {
                return teamNames[teamIndex];
            }
            return "Unknown Team";
        }

        // ========== NETWORK EVENTS (called by LobbyUI) ==========

        public void RequestJoinGame(int playerId)
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_LOBBY)
                return;
            if (activePlayers >= maxPlayers)
                return;

            // Check if player is already in game
            for (int i = 0; i < activePlayers; i++)
            {
                if (playerIds[i] == playerId)
                    return; // Already in game
            }

            // Add player to first available slot
            for (int i = 0; i < maxPlayers; i++)
            {
                if (playerIds[i] == -1)
                {
                    playerIds[i] = playerId;
                    playerTeams[i] = useTeams ? -1 : 0; // -1 = no team assigned, 0 = FFA
                    activePlayers++;
                    RequestSerialization();

                    // Trigger callbacks
                    TriggerCallbacks(onPlayerJoinCallbacks);
                    return;
                }
            }
        }

        public void RequestLeaveGame(int playerId)
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_LOBBY)
                return;

            // Find and remove player
            for (int i = 0; i < maxPlayers; i++)
            {
                if (playerIds[i] == playerId)
                {
                    // Shift players down to remove gaps
                    for (int j = i; j < maxPlayers - 1; j++)
                    {
                        playerIds[j] = playerIds[j + 1];
                        playerTeams[j] = playerTeams[j + 1];
                        playerScores[j] = playerScores[j + 1];
                    }

                    // Clear last slot
                    playerIds[maxPlayers - 1] = -1;
                    playerTeams[maxPlayers - 1] = -1;
                    playerScores[maxPlayers - 1] = 0;

                    activePlayers--;
                    RequestSerialization();

                    // Trigger callbacks
                    TriggerCallbacks(onPlayerLeaveCallbacks);
                    return;
                }
            }
        }

        public void RequestJoinTeam(int playerId, int teamIndex)
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_LOBBY)
                return;
            if (!useTeams)
                return;
            if (teamIndex < 0 || teamIndex >= maxTeams)
                return;

            // Find player and update team
            for (int i = 0; i < activePlayers; i++)
            {
                if (playerIds[i] == playerId)
                {
                    playerTeams[i] = teamIndex;
                    RequestSerialization();
                    return;
                }
            }

            // If player not in game yet, add them
            RequestJoinGame(playerId);
            // Try again after they're added
            for (int i = 0; i < activePlayers; i++)
            {
                if (playerIds[i] == playerId)
                {
                    playerTeams[i] = teamIndex;
                    RequestSerialization();
                    return;
                }
            }
        }

        public void StartGame()
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_LOBBY)
                return;
            if (!IsGameReady())
                return;

            gameState = STATE_COUNTDOWN;
            timeRemaining = countdownTime;
            RequestSerialization();

            TriggerCallbacks(onCountdownCallbacks);
            SendCustomEvent(nameof(CountdownTick));
        }

        public void EndGame()
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_RUNNING && gameState != STATE_COUNTDOWN)
                return;

            gameState = STATE_ENDED;
            gameRunning = false;
            RequestSerialization();

            TriggerCallbacks(onEndCallbacks);

            // Return to lobby after a delay
            SendCustomEventDelayedSeconds(nameof(ReturnToLobby), 10f);
        }

        // ========== GAME CONTROL METHODS ==========

        public void IncrementScore(int playerId, int amount)
        {
            if (!Networking.IsOwner(gameObject))
                return;

            for (int i = 0; i < activePlayers; i++)
            {
                if (playerIds[i] == playerId)
                {
                    playerScores[i] += amount;
                    RequestSerialization();
                    return;
                }
            }
        }

        public void SetScore(int playerId, int value)
        {
            if (!Networking.IsOwner(gameObject))
                return;

            for (int i = 0; i < activePlayers; i++)
            {
                if (playerIds[i] == playerId)
                {
                    playerScores[i] = value;
                    RequestSerialization();
                    return;
                }
            }
        }

        // ========== INTERNAL METHODS ==========

        public void CountdownTick()
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_COUNTDOWN)
                return;

            timeRemaining -= 1f;

            if (timeRemaining <= 0)
            {
                // Start the actual game
                gameState = STATE_RUNNING;
                timeRemaining = gameTimeLimit;
                gameRunning = true;
                RequestSerialization();

                TriggerCallbacks(onStartCallbacks);
                SendCustomEvent(nameof(GameTick));
            }
            else
            {
                RequestSerialization();
                SendCustomEventDelayedSeconds(nameof(CountdownTick), 1f);
            }
        }

        public void GameTick()
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_RUNNING)
                return;

            timeRemaining -= 1f;

            // Check for time warning (30 seconds left)
            if (timeRemaining == 30f)
            {
                TriggerCallbacks(onTimeWarningCallbacks);
            }

            if (timeRemaining <= 0)
            {
                // Game time expired
                EndGame();
            }
            else
            {
                RequestSerialization();
                SendCustomEventDelayedSeconds(nameof(GameTick), 1f);
            }
        }

        public void ReturnToLobby()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            gameState = STATE_LOBBY;
            timeRemaining = gameTimeLimit;
            gameRunning = false;

            // Clear scores but keep players
            for (int i = 0; i < maxPlayers; i++)
            {
                playerScores[i] = 0;
            }

            RequestSerialization();
        }

        private void TriggerCallbacks(UdonBehaviour[] callbacks)
        {
            if (callbacks == null)
                return;

            for (int i = 0; i < callbacks.Length; i++)
            {
                if (callbacks[i] != null)
                {
                    callbacks[i].SendCustomEvent("OnMugiCallback");
                }
            }
        }

        // ========== UNITY/VRCHAT EVENTS ==========

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (!Networking.IsOwner(gameObject))
                return;

            // Remove player from game if they were participating
            RequestLeaveGame(player.playerId);
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            // New owner should validate game state
            if (Networking.IsOwner(gameObject))
            {
                Debug.Log($"MugiGame: Ownership transferred to {player.displayName}");
            }
        }
    }
}
