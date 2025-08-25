using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Data;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

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
        public int[] minPlayersPerTeam = { 1, 1, 1, 1 }; // min players for each team slot
        public int[] maxPlayersPerTeam = { 4, 4, 4, 4 }; // max players for each team slot
        public string[] teamNames = { "Team 1", "Team 2", "Team 3", "Team 4" };
        public float gameTimeLimit = 300f; // 5 minutes
        public float countdownTime = 3f;

        [Header("Lifecycle GameObjects")]
        public GameObject[] enableDuringGame; // Objects to enable only during countdown/running

        [Header("Runtime State (Read-Only)")]
        [UdonSynced]
        public int gameState = STATE_LOBBY;

        [UdonSynced]
        public float gameStartTime; // Server time in seconds when game started, for resilient timing

        // Computed properties based on gameStartTime
        public float timeRemaining
        {
            get
            {
                if (gameState == STATE_COUNTDOWN)
                {
                    float elapsed = (float)Networking.GetServerTimeInSeconds() - gameStartTime;
                    return Mathf.Max(0f, countdownTime - elapsed);
                }
                else if (gameState == STATE_RUNNING)
                {
                    float elapsed = (float)Networking.GetServerTimeInSeconds() - gameStartTime;
                    return Mathf.Max(0f, gameTimeLimit - elapsed);
                }
                return gameTimeLimit;
            }
        }

        [UdonSynced]
        public int activePlayers = 0;

        // Player tracking: UdonSynced arrays for network state + DataDictionary for fast lookups
        [UdonSynced]
        public int[] syncedPlayerIds = new int[8]; // VRCPlayerApi.playerId for each slot

        [UdonSynced]
        public int[] syncedPlayerTeams = new int[8]; // team assignment for each player slot

        [UdonSynced]
        public int[] syncedPlayerScores = new int[8]; // scores for each player slot

        [UdonSynced]
        public int syncedActiveCount = 0; // number of active players

        // Runtime dictionaries for O(1) lookups (populated from synced arrays)
        private DataDictionary playerScoresDict = new DataDictionary(); // playerId -> score
        private DataDictionary playerTeamsDict = new DataDictionary(); // playerId -> teamIndex
        private DataList activePlayerIds = new DataList(); // ordered list of playerIds

        // Callbacks for game events
        [Header("Event Callbacks")]
        public UdonBehaviour[] onCountdownCallbacks;
        public UdonBehaviour[] onStartCallbacks;
        public UdonBehaviour[] onEndCallbacks;
        public UdonBehaviour[] onPlayerJoinCallbacks;
        public UdonBehaviour[] onPlayerLeaveCallbacks;
        public UdonBehaviour[] onTimeWarningCallbacks;

        // Internal state
        private bool timeWarningTriggered = false;
        private bool slowUpdateScheduled = false;
        private int previousGameState = STATE_LOBBY; // Track state changes for client sync

        private VRCPlayerApi[] playerApiCache = new VRCPlayerApi[8]; // Cache for performance
        private bool gameRunning = false;
        private bool hasLoggedConfigErrors = false;

        void Start()
        {
            // Initialize data structures
            InitializePlayerData();

            // Validate configuration
            ValidateConfiguration();

            // Initialize lifecycle objects and callbacks for all clients
            OnGameStateChanged();

            // Store initial state for change detection
            previousGameState = gameState;
        }

        void OnEnable()
        {
            // Only master schedules slow updates for game timing
            if (Networking.IsOwner(gameObject) && !slowUpdateScheduled)
            {
                slowUpdateScheduled = true;
                SendCustomEventDelayedSeconds(nameof(SlowUpdate), 0.1f);
            }
        }

        void OnDisable()
        {
            slowUpdateScheduled = false;
        }

        private void InitializePlayerData()
        {
            // Initialize synced arrays
            for (int i = 0; i < 8; i++)
            {
                syncedPlayerIds[i] = -1;
                syncedPlayerTeams[i] = -1;
                syncedPlayerScores[i] = 0;
            }
            syncedActiveCount = 0;
            activePlayers = 0;

            // Initialize dictionaries
            if (playerScoresDict == null)
                playerScoresDict = new DataDictionary();
            if (playerTeamsDict == null)
                playerTeamsDict = new DataDictionary();
            if (activePlayerIds == null)
                activePlayerIds = new DataList();

            // Populate dictionaries from synced arrays
            UpdateDictionariesFromSyncedArrays();
        }

        // ========== PUBLIC API METHODS ==========

        public bool IsGameReady()
        {
            if (activePlayers < minPlayers)
                return false;

            if (useTeams)
            {
                // Count players per team using DataDictionary
                int[] teamCounts = new int[maxTeams];
                DataList playerIds = playerTeamsDict.GetKeys();

                for (int i = 0; i < playerIds.Count; i++)
                {
                    int playerId = playerIds[i].Int;
                    if (playerTeamsDict.TryGetValue(playerId, out DataToken teamToken))
                    {
                        int team = teamToken.Int;
                        if (team >= 0 && team < maxTeams)
                        {
                            teamCounts[team]++;
                        }
                    }
                }

                // Check if we have at least 2 teams with players
                int teamsWithPlayers = 0;
                for (int i = 0; i < maxTeams; i++)
                {
                    if (teamCounts[i] > 0)
                    {
                        teamsWithPlayers++;

                        // Check min/max constraints for this team
                        int minForTeam = (i < minPlayersPerTeam.Length) ? minPlayersPerTeam[i] : 1;
                        int maxForTeam =
                            (i < maxPlayersPerTeam.Length) ? maxPlayersPerTeam[i] : maxPlayers;

                        if (teamCounts[i] < minForTeam || teamCounts[i] > maxForTeam)
                        {
                            return false;
                        }
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

            for (int i = 0; i < activePlayerIds.Count; i++)
            {
                int playerId = activePlayerIds[i].Int;
                VRCPlayerApi player = VRCPlayerApi.GetPlayerById(playerId);
                if (player != null && resultIndex < result.Length)
                {
                    result[resultIndex] = player;
                    resultIndex++;
                }
            }

            return result;
        }

        public int GetPlayerTeam(VRCPlayerApi player)
        {
            if (player == null)
                return -1;

            if (playerTeamsDict.TryGetValue(player.playerId, out DataToken teamToken))
            {
                return teamToken.Int;
            }
            return -1; // Player not in game
        }

        public bool IsPlayerInGame(VRCPlayerApi player)
        {
            if (player == null)
                return false;
            return playerTeamsDict.ContainsKey(player.playerId);
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

        public int GetPlayerScore(VRCPlayerApi player)
        {
            if (player == null)
                return 0;

            if (playerScoresDict.TryGetValue(player.playerId, out DataToken scoreToken))
            {
                return scoreToken.Int;
            }
            return 0; // Player not in game
        }

        public int GetPlayerScore(int playerId)
        {
            if (playerScoresDict.TryGetValue(playerId, out DataToken scoreToken))
            {
                return scoreToken.Int;
            }
            return 0; // Player not in game
        }

        public int GetTeamScore(int teamIndex)
        {
            if (teamIndex < 0 || teamIndex >= maxTeams)
                return 0;

            int totalScore = 0;
            DataList playerIds = playerTeamsDict.GetKeys();

            for (int i = 0; i < playerIds.Count; i++)
            {
                int playerId = playerIds[i].Int;
                if (
                    playerTeamsDict.TryGetValue(playerId, out DataToken teamToken)
                    && teamToken.Int == teamIndex
                )
                {
                    if (playerScoresDict.TryGetValue(playerId, out DataToken scoreToken))
                    {
                        totalScore += scoreToken.Int;
                    }
                }
            }
            return totalScore;
        }

        // ========== NETWORK EVENTS ==========

        [NetworkCallable]
        public void NetworkIncrementScore(int playerId, int amount)
        {
            // Only master processes score changes
            if (!Networking.IsOwner(gameObject))
                return;

            // Validate game state and player
            if (gameState != STATE_RUNNING)
                return;

            if (!playerScoresDict.ContainsKey(playerId))
                return; // Player not in game

            // Update score
            if (playerScoresDict.TryGetValue(playerId, out DataToken currentScore))
            {
                playerScoresDict[playerId] = currentScore.Int + amount;
                UpdateSyncedArraysFromDictionaries();
                RequestSerialization();
            }
        }

        [NetworkCallable]
        public void NetworkSetScore(int playerId, int value)
        {
            // Only master processes score changes
            if (!Networking.IsOwner(gameObject))
                return;

            // Validate game state and player
            if (gameState != STATE_RUNNING)
                return;

            if (!playerScoresDict.ContainsKey(playerId))
                return; // Player not in game

            // Set score
            playerScoresDict[playerId] = value;
            UpdateSyncedArraysFromDictionaries();
            RequestSerialization();
        }

        // ========== LOBBY MANAGEMENT (called by LobbyUI) ==========

        [NetworkCallable]
        public void NetworkAddPlayer(int playerId)
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_LOBBY)
                return;
            if (activePlayers >= maxPlayers)
                return;

            // Check if player is already in game
            if (playerTeamsDict.ContainsKey(playerId))
                return; // Already in game

            // Assign ownership to first player if no owner exists
            if (!Networking.IsOwner(gameObject))
            {
                VRCPlayerApi newPlayer = VRCPlayerApi.GetPlayerById(playerId);
                if (newPlayer != null)
                {
                    Networking.SetOwner(newPlayer, gameObject);
                    Debug.Log(
                        $"MugiGame: {newPlayer.displayName} became game master (first player)"
                    );
                }
            }

            // Add player to game
            int teamAssignment = useTeams ? -1 : 0; // -1 = no team assigned, 0 = FFA
            playerTeamsDict[playerId] = teamAssignment;
            playerScoresDict[playerId] = 0;
            activePlayerIds.Add(playerId);
            activePlayers++;

            UpdateSyncedArraysFromDictionaries();
            RequestSerialization();

            Debug.Log($"MugiGame: Player {playerId} joined the game");

            // Trigger callbacks
            TriggerCallbacks(onPlayerJoinCallbacks, "OnMugiPlayerJoin");
        }

        public void RequestJoinGame(int playerId)
        {
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetworkAddPlayer), playerId);
        }

        [NetworkCallable]
        public void NetworkRemovePlayer(int playerId)
        {
            if (!Networking.IsOwner(gameObject))
                return;

            // Allow leaving during any state (handles disconnects during game)

            // Check if player is in game
            if (!playerTeamsDict.ContainsKey(playerId))
                return; // Player not in game

            // Remove player from game
            playerTeamsDict.Remove(playerId);
            playerScoresDict.Remove(playerId);

            // Remove from active player list
            for (int i = 0; i < activePlayerIds.Count; i++)
            {
                if (activePlayerIds[i].Int == playerId)
                {
                    activePlayerIds.RemoveAt(i);
                    break;
                }
            }

            activePlayers = activePlayerIds.Count;
            UpdateSyncedArraysFromDictionaries();
            RequestSerialization();

            Debug.Log($"MugiGame: Player {playerId} left the game");

            // Trigger callbacks
            TriggerCallbacks(onPlayerLeaveCallbacks, "OnMugiPlayerLeave");

            // Check if we need to find new master or abort game
            VRCPlayerApi leavingPlayer = VRCPlayerApi.GetPlayerById(playerId);
            if (leavingPlayer != null && Networking.IsOwner(leavingPlayer, gameObject))
            {
                PromoteNewMaster();
            }
        }

        public void RequestLeaveGame(int playerId)
        {
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetworkRemovePlayer), playerId);
        }

        [NetworkCallable]
        public void NetworkAddPlayerToTeam(int playerId, int teamIndex)
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_LOBBY)
                return;
            if (!useTeams)
                return;
            if (teamIndex < 0 || teamIndex >= maxTeams)
                return;

            // Check team capacity
            int teamCount = 0;
            DataList playerIds = playerTeamsDict.GetKeys();
            for (int i = 0; i < playerIds.Count; i++)
            {
                if (
                    playerTeamsDict.TryGetValue(playerIds[i].Int, out DataToken teamToken)
                    && teamToken.Int == teamIndex
                )
                {
                    teamCount++;
                }
            }

            int maxForTeam =
                (teamIndex < maxPlayersPerTeam.Length) ? maxPlayersPerTeam[teamIndex] : maxPlayers;
            if (teamCount >= maxForTeam)
                return; // Team is full

            // Check if player is in game, if not add them first
            if (!playerTeamsDict.ContainsKey(playerId))
            {
                NetworkAddPlayer(playerId);
            }

            // Update player's team
            playerTeamsDict[playerId] = teamIndex;

            Debug.Log(
                $"MugiGame: Player {playerId} joined team {teamIndex} ({GetTeamName(teamIndex)})"
            );

            UpdateSyncedArraysFromDictionaries();
            RequestSerialization();
        }

        public void RequestJoinTeam(int playerId, int teamIndex)
        {
            SendCustomNetworkEvent(
                NetworkEventTarget.Owner,
                nameof(NetworkAddPlayerToTeam),
                playerId,
                teamIndex
            );
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
            gameStartTime = (float)Networking.GetServerTimeInSeconds();
            timeWarningTriggered = false;

            // Reset all scores
            DataList playerIds = playerScoresDict.GetKeys();
            for (int i = 0; i < playerIds.Count; i++)
            {
                playerScoresDict[playerIds[i].Int] = 0;
            }

            UpdateSyncedArraysFromDictionaries();
            RequestSerialization();

            // Handle client-side effects immediately on master
            OnGameStateChanged();
        }

        public void EndGameEarly()
        {
            if (!Networking.IsOwner(gameObject))
                return;
            if (gameState != STATE_RUNNING && gameState != STATE_COUNTDOWN)
                return;

            EndGameInternal();
        }

        private void EndGameInternal()
        {
            gameState = STATE_ENDED;
            gameRunning = false;
            RequestSerialization();

            // Handle client-side effects immediately on master
            OnGameStateChanged();

            // Return to lobby after a delay
            SendCustomEventDelayedSeconds(nameof(ReturnToLobby), 3f);
        }

        // ========== GAME CONTROL METHODS (for game developers) ==========

        public void IncrementScore(int playerId, int amount)
        {
            // Validate game state and player
            if (gameState != STATE_RUNNING)
                return;

            if (!playerScoresDict.ContainsKey(playerId))
                return; // Player not in game

            // Send to game master for processing with proper parameters
            SendCustomNetworkEvent(
                NetworkEventTarget.Owner,
                nameof(NetworkIncrementScore),
                playerId,
                amount
            );
        }

        public void SetScore(int playerId, int value)
        {
            // Validate game state and player
            if (gameState != STATE_RUNNING)
                return;

            if (!playerScoresDict.ContainsKey(playerId))
                return; // Player not in game

            // Send to game master for processing with proper parameters
            SendCustomNetworkEvent(
                NetworkEventTarget.Owner,
                nameof(NetworkSetScore),
                playerId,
                value
            );
        }

        // ========== INTERNAL METHODS ==========

        public void SlowUpdate()
        {
            // Only master handles game timing and state transitions
            if (Networking.IsOwner(gameObject))
            {
                CheckGameProgress();

                // Schedule next update only if GameObject is still enabled and we're the owner
                if (gameObject.activeInHierarchy && slowUpdateScheduled)
                {
                    SendCustomEventDelayedSeconds(nameof(SlowUpdate), 1f);
                }
                else
                {
                    slowUpdateScheduled = false;
                }
            }
        }

        private void CheckGameProgress()
        {
            // Master-only game state transitions and timing logic
            if (gameState == STATE_COUNTDOWN)
            {
                if (timeRemaining <= 0)
                {
                    // Start the actual game
                    gameState = STATE_RUNNING;
                    gameStartTime = (float)Networking.GetServerTimeInSeconds(); // Reset timer for game duration
                    gameRunning = true;
                    timeWarningTriggered = false;
                    RequestSerialization();

                    // Handle client-side effects immediately on master
                    OnGameStateChanged();
                }
            }
            else if (gameState == STATE_RUNNING)
            {
                // Check for time warning (30 seconds left)
                if (!timeWarningTriggered && timeRemaining <= 30f)
                {
                    timeWarningTriggered = true;
                    TriggerCallbacks(onTimeWarningCallbacks, "OnMugiTimeWarning");
                }

                if (timeRemaining <= 0)
                {
                    // Game time expired
                    EndGameInternal();
                }

                // Check if we need to abort due to insufficient players
                if (activePlayers < minPlayers)
                {
                    Debug.Log("MugiGame: Aborting game due to insufficient players");
                    ReturnToLobby(); // Go directly to lobby, not through end state
                }
            }
        }

        private void OnGameStateChanged()
        {
            // Client-side effects that should run on ALL clients when game state changes
            UpdateLifecycleObjects();

            // Trigger appropriate callbacks based on current state
            if (gameState == STATE_COUNTDOWN)
            {
                TriggerCallbacks(onCountdownCallbacks, "OnMugiCountdown");
            }
            else if (gameState == STATE_RUNNING)
            {
                TriggerCallbacks(onStartCallbacks, "OnMugiStart");
            }
            else if (gameState == STATE_ENDED)
            {
                TriggerCallbacks(onEndCallbacks, "OnMugiEnd");
            }
        }

        public void ReturnToLobby()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            gameState = STATE_LOBBY;
            gameRunning = false;
            timeWarningTriggered = false;

            // Clear scores but keep players
            DataList playerIds = playerScoresDict.GetKeys();
            for (int i = 0; i < playerIds.Count; i++)
            {
                playerScoresDict[playerIds[i].Int] = 0;
            }

            UpdateSyncedArraysFromDictionaries();
            RequestSerialization();

            // Handle client-side effects immediately on master
            OnGameStateChanged();
        }

        private void TriggerCallbacks(UdonBehaviour[] callbacks, string eventName)
        {
            if (callbacks == null || string.IsNullOrEmpty(eventName))
                return;

            for (int i = 0; i < callbacks.Length; i++)
            {
                if (callbacks[i] != null)
                {
                    callbacks[i].SendCustomEvent(eventName);
                }
            }
        }

        private void UpdateLifecycleObjects()
        {
            if (enableDuringGame == null)
                return;

            bool shouldEnable = (gameState == STATE_COUNTDOWN || gameState == STATE_RUNNING);

            for (int i = 0; i < enableDuringGame.Length; i++)
            {
                if (enableDuringGame[i] != null)
                {
                    enableDuringGame[i].SetActive(shouldEnable);
                }
            }
        }

        private void UpdateSyncedArraysFromDictionaries()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            // Clear synced arrays
            for (int i = 0; i < 8; i++)
            {
                syncedPlayerIds[i] = -1;
                syncedPlayerTeams[i] = -1;
                syncedPlayerScores[i] = 0;
            }

            // Populate synced arrays from dictionaries
            for (int i = 0; i < activePlayerIds.Count && i < 8; i++)
            {
                int playerId = activePlayerIds[i].Int;
                syncedPlayerIds[i] = playerId;

                if (playerTeamsDict.TryGetValue(playerId, out DataToken teamToken))
                    syncedPlayerTeams[i] = teamToken.Int;

                if (playerScoresDict.TryGetValue(playerId, out DataToken scoreToken))
                    syncedPlayerScores[i] = scoreToken.Int;
            }

            syncedActiveCount = activePlayerIds.Count;
            activePlayers = syncedActiveCount;
        }

        private void UpdateDictionariesFromSyncedArrays()
        {
            // Clear dictionaries
            playerScoresDict.Clear();
            playerTeamsDict.Clear();
            activePlayerIds.Clear();

            // Populate dictionaries from synced arrays
            for (int i = 0; i < syncedActiveCount && i < 8; i++)
            {
                int playerId = syncedPlayerIds[i];
                if (playerId != -1)
                {
                    activePlayerIds.Add(playerId);
                    playerTeamsDict[playerId] = syncedPlayerTeams[i];
                    playerScoresDict[playerId] = syncedPlayerScores[i];
                }
            }

            activePlayers = activePlayerIds.Count;
        }

        public override void OnPreSerialization()
        {
            UpdateSyncedArraysFromDictionaries();
        }

        public override void OnDeserialization()
        {
            UpdateDictionariesFromSyncedArrays();

            // Check if game state changed and trigger client-side effects
            if (gameState != previousGameState)
            {
                previousGameState = gameState;
                OnGameStateChanged();
            }
        }

        private void ValidateConfiguration()
        {
            if (hasLoggedConfigErrors)
                return;

            hasLoggedConfigErrors = true;

            // Validate team array lengths
            if (useTeams)
            {
                if (maxTeams <= 1)
                {
                    Debug.LogError(
                        "[MugiGame] maxTeams must be > 1 when useTeams is true. Setting maxTeams = 2."
                    );
                    maxTeams = 2;
                }

                if (minPlayersPerTeam.Length < maxTeams)
                {
                    Debug.LogError(
                        $"[MugiGame] minPlayersPerTeam array length ({minPlayersPerTeam.Length}) is less than maxTeams ({maxTeams}). Some teams will use default min of 1."
                    );
                }

                if (maxPlayersPerTeam.Length < maxTeams)
                {
                    Debug.LogError(
                        $"[MugiGame] maxPlayersPerTeam array length ({maxPlayersPerTeam.Length}) is less than maxTeams ({maxTeams}). Some teams will use default max of {maxPlayers}."
                    );
                }

                if (teamNames.Length < maxTeams)
                {
                    Debug.LogError(
                        $"[MugiGame] teamNames array length ({teamNames.Length}) is less than maxTeams ({maxTeams}). Some teams will show as 'Unknown Team'."
                    );
                }
            }

            // Validate basic constraints
            if (minPlayers > maxPlayers)
            {
                Debug.LogError(
                    $"[MugiGame] minPlayers ({minPlayers}) cannot be greater than maxPlayers ({maxPlayers}). Setting minPlayers = maxPlayers."
                );
                minPlayers = maxPlayers;
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
            // New owner should validate game state and continue timing
            if (Networking.IsOwner(gameObject))
            {
                Debug.Log($"MugiGame: Ownership transferred to {player.displayName}");

                // Start slow updates for new master if game is active
                if (
                    (gameState == STATE_RUNNING || gameState == STATE_COUNTDOWN)
                    && !slowUpdateScheduled
                )
                {
                    slowUpdateScheduled = true;
                    SendCustomEventDelayedSeconds(nameof(SlowUpdate), 0.1f);
                }

                // Check if game should continue or abort
                if (gameState == STATE_RUNNING || gameState == STATE_COUNTDOWN)
                {
                    if (activePlayers < minPlayers)
                    {
                        Debug.Log("MugiGame: New master aborting game due to insufficient players");
                        ReturnToLobby();
                    }
                }
            }
            else
            {
                // No longer owner, stop slow updates
                slowUpdateScheduled = false;
            }
        }

        // Master rotation: prioritize active game players, then fallback to any player
        private void PromoteNewMaster()
        {
            VRCPlayerApi newMaster = null;
            int lowestId = int.MaxValue;

            // First priority: Find active game player with lowest ID
            for (int i = 0; i < activePlayerIds.Count; i++)
            {
                int playerId = activePlayerIds[i].Int;
                VRCPlayerApi player = VRCPlayerApi.GetPlayerById(playerId);
                if (player != null && player.playerId < lowestId)
                {
                    lowestId = player.playerId;
                    newMaster = player;
                }
            }

            // Second priority: If no game players available, find any player with lowest ID
            if (newMaster == null)
            {
                VRCPlayerApi[] allPlayers = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
                VRCPlayerApi.GetPlayers(allPlayers);

                lowestId = int.MaxValue;
                for (int i = 0; i < allPlayers.Length; i++)
                {
                    VRCPlayerApi player = allPlayers[i];
                    if (player != null && player.playerId < lowestId)
                    {
                        lowestId = player.playerId;
                        newMaster = player;
                    }
                }
            }

            if (newMaster != null)
            {
                Networking.SetOwner(newMaster, gameObject);
                Debug.Log($"MugiGame: Promoted {newMaster.displayName} to game master");
            }
            else
            {
                Debug.LogWarning("MugiGame: No suitable player found for game master promotion");
            }
        }
    }
}
