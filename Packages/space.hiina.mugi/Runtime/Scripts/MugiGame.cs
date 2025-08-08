using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Data;
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
        public float gameStartTime; // Time.time when game started, for resilient timing

        // Computed properties based on gameStartTime
        public float timeRemaining
        {
            get
            {
                if (gameState == STATE_COUNTDOWN)
                {
                    float elapsed = Time.time - gameStartTime;
                    return Mathf.Max(0f, countdownTime - elapsed);
                }
                else if (gameState == STATE_RUNNING)
                {
                    float elapsed = Time.time - gameStartTime;
                    return Mathf.Max(0f, gameTimeLimit - elapsed);
                }
                return gameTimeLimit;
            }
        }

        [UdonSynced]
        public int activePlayers = 0;

        // Player tracking using DataDictionary for O(1) lookups
        // Note: DataDictionary cannot be directly synced, so we use JSON serialization
        [UdonSynced]
        private string _playerDataJson = "{}";

        // Runtime dictionaries (populated from JSON)
        private DataDictionary playerScoresDict = new DataDictionary(); // playerId -> score
        private DataDictionary playerTeamsDict = new DataDictionary(); // playerId -> teamIndex
        private DataList activePlayerIds = new DataList(); // ordered list for iteration

        // Legacy arrays for backward compatibility (will be removed later)
        [System.NonSerialized]
        public int[] playerIds = new int[8];

        [System.NonSerialized]
        public int[] playerTeams = new int[8];

        [System.NonSerialized]
        public int[] playerScores = new int[8];

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
        private float lastUpdateTime = 0f;

        private VRCPlayerApi[] playerApiCache = new VRCPlayerApi[8]; // Cache for performance
        private bool gameRunning = false;
        private bool hasLoggedConfigErrors = false;

        void Start()
        {
            // Initialize data structures
            InitializePlayerData();

            // Validate configuration
            ValidateConfiguration();

            // Take ownership if no one owns this
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            // Initialize lifecycle objects
            UpdateLifecycleObjects();
        }

        private void InitializePlayerData()
        {
            // Initialize dictionaries
            if (playerScoresDict == null)
                playerScoresDict = new DataDictionary();
            if (playerTeamsDict == null)
                playerTeamsDict = new DataDictionary();
            if (activePlayerIds == null)
                activePlayerIds = new DataList();

            // Initialize legacy arrays for backward compatibility
            for (int i = 0; i < 8; i++)
            {
                playerIds[i] = -1;
                playerTeams[i] = -1;
                playerScores[i] = 0;
            }

            // Deserialize player data if we have any
            DeserializePlayerData();
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
                UpdateLegacyArrays();
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
            UpdateLegacyArrays();
            RequestSerialization();
        }

        // ========== LOBBY MANAGEMENT (called by LobbyUI) ==========

        public void RequestJoinGame(int playerId)
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

            // Add player to game
            int teamAssignment = useTeams ? -1 : 0; // -1 = no team assigned, 0 = FFA
            playerTeamsDict[playerId] = teamAssignment;
            playerScoresDict[playerId] = 0;
            activePlayerIds.Add(playerId);
            activePlayers++;

            UpdateLegacyArrays();
            RequestSerialization();

            // Trigger callbacks
            TriggerCallbacks(onPlayerJoinCallbacks, "OnMugiPlayerJoin");
        }

        public void RequestLeaveGame(int playerId)
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
            UpdateLegacyArrays();
            RequestSerialization();

            // Trigger callbacks
            TriggerCallbacks(onPlayerLeaveCallbacks, "OnMugiPlayerLeave");

            // Check if we need to find new master or abort game
            VRCPlayerApi leavingPlayer = VRCPlayerApi.GetPlayerById(playerId);
            if (leavingPlayer != null && Networking.IsOwner(leavingPlayer, gameObject))
            {
                PromoteNewMaster();
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
                RequestJoinGame(playerId);
            }

            // Update player's team
            playerTeamsDict[playerId] = teamIndex;
            UpdateLegacyArrays();
            RequestSerialization();
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
            gameStartTime = Time.time;
            timeWarningTriggered = false;

            // Reset all scores
            DataList playerIds = playerScoresDict.GetKeys();
            for (int i = 0; i < playerIds.Count; i++)
            {
                playerScoresDict[playerIds[i].Int] = 0;
            }
            UpdateLegacyArrays();

            RequestSerialization();
            UpdateLifecycleObjects();

            TriggerCallbacks(onCountdownCallbacks, "OnMugiCountdown");
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
            UpdateLifecycleObjects();

            TriggerCallbacks(onEndCallbacks, "OnMugiEnd");

            // Return to lobby after a delay
            SendCustomEventDelayedSeconds(nameof(ReturnToLobby), 10f);
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

        void Update()
        {
            // Only master handles timing
            if (!Networking.IsOwner(gameObject))
                return;

            // Throttle updates to once per second
            if (Time.time - lastUpdateTime < 1f)
                return;
            lastUpdateTime = Time.time;

            CheckGameProgress();
        }

        private void CheckGameProgress()
        {
            if (gameState == STATE_COUNTDOWN)
            {
                if (timeRemaining <= 0)
                {
                    // Start the actual game
                    gameState = STATE_RUNNING;
                    gameStartTime = Time.time; // Reset timer for game duration
                    gameRunning = true;
                    timeWarningTriggered = false;
                    RequestSerialization();
                    UpdateLifecycleObjects();

                    TriggerCallbacks(onStartCallbacks, "OnMugiStart");
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
            UpdateLegacyArrays();

            RequestSerialization();
            UpdateLifecycleObjects();
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

        private void SerializePlayerData()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            // Create JSON structure for player data
            DataDictionary playerData = new DataDictionary()
            {
                { "scores", playerScoresDict.DeepClone() },
                { "teams", playerTeamsDict.DeepClone() },
                { "activeIds", activePlayerIds.DeepClone() },
            };

            if (VRCJson.TrySerializeToJson(playerData, JsonExportType.Minify, out DataToken result))
            {
                _playerDataJson = result.String;
            }
            else
            {
                Debug.LogError($"[MugiGame] Failed to serialize player data: {result}");
            }
        }

        private void DeserializePlayerData()
        {
            if (string.IsNullOrEmpty(_playerDataJson) || _playerDataJson == "{}")
            {
                // Initialize empty data structures
                playerScoresDict.Clear();
                playerTeamsDict.Clear();
                activePlayerIds.Clear();
                activePlayers = 0;
                return;
            }

            if (VRCJson.TryDeserializeFromJson(_playerDataJson, out DataToken result))
            {
                DataDictionary data = result.DataDictionary;

                if (data.TryGetValue("scores", out DataToken scoresToken))
                    playerScoresDict = scoresToken.DataDictionary;
                else
                    playerScoresDict = new DataDictionary();

                if (data.TryGetValue("teams", out DataToken teamsToken))
                    playerTeamsDict = teamsToken.DataDictionary;
                else
                    playerTeamsDict = new DataDictionary();

                if (data.TryGetValue("activeIds", out DataToken idsToken))
                    activePlayerIds = idsToken.DataList;
                else
                    activePlayerIds = new DataList();

                activePlayers = activePlayerIds.Count;

                // Update legacy arrays for backward compatibility
                UpdateLegacyArrays();
            }
            else
            {
                Debug.LogError($"[MugiGame] Failed to deserialize player data: {result}");
                InitializePlayerData();
            }
        }

        private void UpdateLegacyArrays()
        {
            // Clear legacy arrays
            for (int i = 0; i < 8; i++)
            {
                playerIds[i] = -1;
                playerTeams[i] = -1;
                playerScores[i] = 0;
            }

            // Populate from DataDictionary
            for (int i = 0; i < activePlayerIds.Count && i < 8; i++)
            {
                int playerId = activePlayerIds[i].Int;
                playerIds[i] = playerId;

                if (playerTeamsDict.TryGetValue(playerId, out DataToken teamToken))
                    playerTeams[i] = teamToken.Int;

                if (playerScoresDict.TryGetValue(playerId, out DataToken scoreToken))
                    playerScores[i] = scoreToken.Int;
            }
        }

        public override void OnPreSerialization()
        {
            SerializePlayerData();
        }

        public override void OnDeserialization()
        {
            DeserializePlayerData();
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

                // Reset timing tracking for new master
                lastUpdateTime = Time.time;

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
        }

        // Master rotation: find next player by lowest ID
        private void PromoteNewMaster()
        {
            VRCPlayerApi[] allPlayers = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
            VRCPlayerApi.GetPlayers(allPlayers);

            VRCPlayerApi newMaster = null;
            int lowestId = int.MaxValue;

            // Find active game player with lowest ID
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

            // If no game players, find any player with lowest ID
            if (newMaster == null)
            {
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
        }
    }
}
