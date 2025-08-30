using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace Hiinaspace.Mugi
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MugiLobbyUI : UdonSharpBehaviour
    {
        [Header("Game Reference")]
        public MugiGame mugiGame;

        [Header("UI References")]
        public TextMeshProUGUI lobbyTitle;
        public GameObject lobbyListViewport;
        public GameObject lobbyInProgress;
        public Transform lobbyList;

        [Header("Buttons")]
        public Button lobbyStartButton;
        public Button lobbyEndButton;

        [Header("Team Row References (4 max teams)")]
        public GameObject[] teamRows = new GameObject[4];

        [Header("Player Row References (8 max players)")]
        public GameObject[] playerRows = new GameObject[8];

        [Header("In Progress UI")]
        public TextMeshProUGUI gameProgressText;

        // State tracking
        private bool endGameConfirmation = false;
        private float endGameConfirmationTime = 0f;
        private const float CONFIRMATION_TIMEOUT = 3f;
        private bool slowUpdateScheduled = false;

        // Cached component references (discovered in Start)
        private TextMeshProUGUI[] playerNameTexts = new TextMeshProUGUI[8];
        private Button[] playerJoinButtons = new Button[8];
        private Button[] playerLeaveButtons = new Button[8];
        private TextMeshProUGUI[] teamNameTexts = new TextMeshProUGUI[4];
        private Button[] teamJoinButtons = new Button[4];

        void Start()
        {
            // Cache component references and log errors if missing
            CacheComponentReferences();

            // Initial UI refresh
            RefreshLobbyUI();
        }

        private void CacheComponentReferences()
        {
            // Cache player row components
            for (int i = 0; i < playerRows.Length; i++)
            {
                if (playerRows[i] != null)
                {
                    Transform nameChild = playerRows[i].transform.Find("LobbyPlayerName");
                    if (nameChild != null)
                        playerNameTexts[i] = nameChild.GetComponent<TextMeshProUGUI>();
                    else
                        Debug.LogError(
                            $"[MugiLobbyUI] Missing LobbyPlayerName child in playerRows[{i}]"
                        );

                    Transform joinChild = playerRows[i].transform.Find("LobbyJoinButton");
                    if (joinChild != null)
                        playerJoinButtons[i] = joinChild.GetComponent<Button>();
                    else
                        Debug.LogError(
                            $"[MugiLobbyUI] Missing LobbyJoinButton child in playerRows[{i}]"
                        );

                    Transform leaveChild = playerRows[i].transform.Find("LobbyLeaveButton");
                    if (leaveChild != null)
                        playerLeaveButtons[i] = leaveChild.GetComponent<Button>();
                    else
                        Debug.LogError(
                            $"[MugiLobbyUI] Missing LobbyLeaveButton child in playerRows[{i}]"
                        );
                }
            }

            // Cache team row components
            for (int i = 0; i < teamRows.Length; i++)
            {
                if (teamRows[i] != null)
                {
                    Transform nameChild = teamRows[i].transform.Find("LobbyTeamName");
                    if (nameChild != null)
                        teamNameTexts[i] = nameChild.GetComponent<TextMeshProUGUI>();
                    else
                        Debug.LogError(
                            $"[MugiLobbyUI] Missing LobbyTeamName child in teamRows[{i}]"
                        );

                    Transform joinChild = teamRows[i].transform.Find("LobbyTeamJoinButton");
                    if (joinChild != null)
                        teamJoinButtons[i] = joinChild.GetComponent<Button>();
                    else
                        Debug.LogError(
                            $"[MugiLobbyUI] Missing LobbyTeamJoinButton child in teamRows[{i}]"
                        );
                }
            }
        }

        void OnEnable()
        {
            if (!slowUpdateScheduled)
            {
                slowUpdateScheduled = true;
                SendCustomEventDelayedSeconds(nameof(SlowUpdate), 0.1f);
            }
        }

        void OnDisable()
        {
            slowUpdateScheduled = false;
        }

        // Button listeners are set up statically in Unity scene

        public void SlowUpdate()
        {
            RefreshLobbyUI();

            // Handle end game confirmation timeout
            if (endGameConfirmation)
            {
                endGameConfirmationTime += 1f;
                if (endGameConfirmationTime >= CONFIRMATION_TIMEOUT)
                {
                    endGameConfirmation = false;
                    endGameConfirmationTime = 0f;
                }
            }

            // Schedule next update only if GameObject is still enabled
            if (gameObject.activeInHierarchy && slowUpdateScheduled)
            {
                SendCustomEventDelayedSeconds(nameof(SlowUpdate), 1f);
            }
            else
            {
                slowUpdateScheduled = false;
            }
        }

        // ========== VRCHAT EVENTS ==========

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // Opportunistically update UI when players join/leave
            RefreshLobbyUI();
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            RefreshLobbyUI();
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            // Game master might have changed
            RefreshLobbyUI();
        }

        // ========== UI REFRESH LOGIC ==========

        public void RefreshLobbyUI()
        {
            if (mugiGame == null)
                return;

            int gameState = mugiGame.gameState;

            // Show/hide main panels based on game state
            bool showLobby = (gameState == MugiGame.STATE_LOBBY);
            bool showProgress = (
                gameState == MugiGame.STATE_COUNTDOWN || gameState == MugiGame.STATE_RUNNING
            );

            if (lobbyListViewport != null)
                lobbyListViewport.SetActive(showLobby);
            if (lobbyInProgress != null)
                lobbyInProgress.SetActive(showProgress);

            if (showLobby)
            {
                RefreshLobbyView();
            }

            if (showProgress)
            {
                RefreshProgressView();
            }

            // Update start/end buttons
            RefreshControlButtons();
        }

        private void RefreshLobbyView()
        {
            // Update lobby title
            if (lobbyTitle != null)
            {
                int playerCount = mugiGame.activePlayers;
                int maxPlayers = mugiGame.maxPlayers;
                lobbyTitle.text = $"Lobby ({playerCount}/{maxPlayers} players)";
            }

            if (mugiGame.useTeams)
            {
                RefreshTeamMode();
            }
            else
            {
                RefreshFFAMode();
            }
        }

        private void RefreshProgressView()
        {
            if (gameProgressText == null)
                return;

            int gameState = mugiGame.gameState;
            float timeRemaining = mugiGame.GetRemainingTime();

            if (gameState == MugiGame.STATE_COUNTDOWN)
            {
                gameProgressText.text =
                    $"Game Starting in <color=yellow>{Mathf.Ceil(timeRemaining)}</color> seconds...";
            }
            else if (gameState == MugiGame.STATE_RUNNING)
            {
                int minutes = Mathf.FloorToInt(timeRemaining / 60);
                int seconds = Mathf.FloorToInt(timeRemaining % 60);
                string timeColor = timeRemaining <= 30f ? "red" : "yellow";
                gameProgressText.text =
                    $"Game In Progress! <color={timeColor}>{minutes:00}:{seconds:00}</color> Remaining";
            }
        }

        private void RefreshControlButtons()
        {
            bool isGameMaster = Networking.IsOwner(Networking.LocalPlayer, mugiGame.gameObject);
            bool gameReady = mugiGame.IsGameReady();
            int gameState = mugiGame.gameState;

            // Start button: enabled if game master and game is ready and in lobby
            if (lobbyStartButton != null)
            {
                lobbyStartButton.gameObject.SetActive(
                    isGameMaster && gameReady && gameState == MugiGame.STATE_LOBBY
                );
            }

            // End button: enabled if game master and game is countdown/running
            if (lobbyEndButton != null)
            {
                bool canEnd =
                    isGameMaster
                    && (
                        gameState == MugiGame.STATE_COUNTDOWN || gameState == MugiGame.STATE_RUNNING
                    );
                lobbyEndButton.gameObject.SetActive(canEnd);

                if (canEnd && endGameConfirmation)
                {
                    TextMeshProUGUI buttonText =
                        lobbyEndButton.GetComponentInChildren<TextMeshProUGUI>();
                    if (buttonText != null)
                        buttonText.text = "Really Stop Game?";
                }
                else if (canEnd)
                {
                    TextMeshProUGUI buttonText =
                        lobbyEndButton.GetComponentInChildren<TextMeshProUGUI>();
                    if (buttonText != null)
                        buttonText.text = "Stop Game";
                }
            }
        }

        private void RefreshTeamMode()
        {
            // Show team headers, hide unused ones
            for (int i = 0; i < teamRows.Length; i++)
            {
                bool showTeam = i < mugiGame.maxTeams;
                if (teamRows[i] != null)
                    teamRows[i].SetActive(showTeam);

                if (showTeam)
                {
                    // Count current players on this team - optimized lookup
                    int teamCount = 0;
                    VRCPlayerApi[] playersInGame = mugiGame.GetPlayersInGame();
                    for (int j = 0; j < playersInGame.Length; j++)
                    {
                        if (
                            playersInGame[j] != null
                            && mugiGame.GetPlayerTeam(playersInGame[j]) == i
                        )
                        {
                            teamCount++;
                        }
                    }

                    // Get team constraints
                    int maxForTeam =
                        (i < mugiGame.maxPlayersPerTeam.Length)
                            ? mugiGame.maxPlayersPerTeam[i]
                            : mugiGame.maxPlayers;

                    // Update team name with player count
                    if (teamNameTexts[i] != null)
                    {
                        string teamName = mugiGame.GetTeamName(i);
                        teamNameTexts[i].text = $"{teamName} ({teamCount}/{maxForTeam})";
                    }

                    // Update join button - disable if team is full or player already on team
                    if (teamJoinButtons[i] != null)
                    {
                        VRCPlayerApi localPlayer = Networking.LocalPlayer;
                        int localPlayerTeam = mugiGame.GetPlayerTeam(localPlayer);
                        bool teamFull = teamCount >= maxForTeam;
                        bool alreadyOnTeam = (localPlayerTeam == i);

                        teamJoinButtons[i].interactable = !teamFull && !alreadyOnTeam;
                    }
                }
            }

            // Arrange players under their teams using GameObject reordering
            RefreshTeamPlayerArrangement();

            // Hide all player join buttons in team mode
            for (int i = 0; i < playerJoinButtons.Length; i++)
            {
                if (playerJoinButtons[i] != null)
                    playerJoinButtons[i].gameObject.SetActive(false);
            }
        }

        private void RefreshFFAMode()
        {
            // Hide all team rows
            for (int i = 0; i < teamRows.Length; i++)
            {
                if (teamRows[i] != null)
                    teamRows[i].SetActive(false);
            }

            // Show players in simple order
            RefreshFFAPlayerArrangement();
        }

        private void RefreshTeamPlayerArrangement()
        {
            VRCPlayerApi[] playersInGame = mugiGame.GetPlayersInGame();
            VRCPlayerApi localPlayer = Networking.LocalPlayer;

            // First, disable all player rows
            for (int i = 0; i < playerRows.Length; i++)
            {
                if (playerRows[i] != null)
                    playerRows[i].SetActive(false);
            }

            int currentRowIndex = 0;

            // For each team
            for (int teamIndex = 0; teamIndex < mugiGame.maxTeams; teamIndex++)
            {
                // Position team header
                if (teamRows[teamIndex] != null)
                {
                    teamRows[teamIndex].transform.SetSiblingIndex(currentRowIndex);
                    currentRowIndex++;
                }

                // Add players for this team
                for (int playerIndex = 0; playerIndex < playersInGame.Length; playerIndex++)
                {
                    VRCPlayerApi player = playersInGame[playerIndex];
                    if (player == null)
                        continue;

                    int playerTeam = mugiGame.GetPlayerTeam(player);
                    if (playerTeam == teamIndex && currentRowIndex < playerRows.Length)
                    {
                        // Show and position this player row
                        playerRows[currentRowIndex].SetActive(true);
                        playerRows[currentRowIndex].transform.SetSiblingIndex(currentRowIndex);

                        // Update player row content
                        RefreshPlayerRow(currentRowIndex, player, localPlayer);
                        currentRowIndex++;
                    }
                }
            }
        }

        private void RefreshFFAPlayerArrangement()
        {
            VRCPlayerApi[] playersInGame = mugiGame.GetPlayersInGame();
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            bool localPlayerInGame = mugiGame.IsPlayerInGame(localPlayer);

            int currentRowIndex = 0;

            // Show existing players
            for (int i = 0; i < playersInGame.Length && currentRowIndex < playerRows.Length; i++)
            {
                VRCPlayerApi player = playersInGame[i];
                if (player != null)
                {
                    playerRows[currentRowIndex].SetActive(true);
                    RefreshPlayerRow(currentRowIndex, player, localPlayer);
                    currentRowIndex++;
                }
            }

            // Show join button for local player if they're not in game and there's space
            if (
                !localPlayerInGame
                && currentRowIndex < playerRows.Length
                && mugiGame.activePlayers < mugiGame.maxPlayers
            )
            {
                playerRows[currentRowIndex].SetActive(true);
                RefreshPlayerRow(currentRowIndex, null, localPlayer); // null = join row
                currentRowIndex++;
            }

            // Hide remaining rows
            for (int i = currentRowIndex; i < playerRows.Length; i++)
            {
                if (playerRows[i] != null)
                    playerRows[i].SetActive(false);
            }
        }

        private void RefreshPlayerRow(int rowIndex, VRCPlayerApi player, VRCPlayerApi localPlayer)
        {
            if (rowIndex >= playerRows.Length)
                return;

            bool isJoinRow = (player == null);
            bool isLocalPlayer = (player == localPlayer);
            bool isGameMaster = (player != null && Networking.IsOwner(player, mugiGame.gameObject));

            // Update player name
            if (playerNameTexts[rowIndex] != null)
            {
                if (isJoinRow)
                {
                    playerNameTexts[rowIndex].text = "";
                    playerNameTexts[rowIndex].gameObject.SetActive(false);
                }
                else
                {
                    string displayName = player.displayName;
                    if (isGameMaster)
                        displayName += " *";
                    playerNameTexts[rowIndex].text = displayName;
                    playerNameTexts[rowIndex].gameObject.SetActive(true);
                }
            }

            // Update join button
            if (playerJoinButtons[rowIndex] != null)
            {
                playerJoinButtons[rowIndex].gameObject.SetActive(isJoinRow);
            }

            // Update leave button
            if (playerLeaveButtons[rowIndex] != null)
            {
                playerLeaveButtons[rowIndex].gameObject.SetActive(isLocalPlayer && !isJoinRow);
            }
        }

        // ========== PUBLIC METHODS (for UI Button events) ==========

        public void OnStartGamePressed()
        {
            if (mugiGame != null)
            {
                mugiGame.StartGame();
            }
        }

        public void OnEndGamePressed()
        {
            if (mugiGame == null)
                return;

            if (endGameConfirmation)
            {
                // Second press - actually end the game
                mugiGame.EndGameEarly();
                endGameConfirmation = false;
                endGameConfirmationTime = 0f;
            }
            else
            {
                // First press - start confirmation
                endGameConfirmation = true;
                endGameConfirmationTime = 0f;
            }
        }

        public void JoinGame()
        {
            if (mugiGame != null)
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer != null)
                {
                    mugiGame.RequestJoinGame(localPlayer.playerId);
                }
            }
        }

        public void LeaveGame()
        {
            if (mugiGame != null)
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer != null)
                {
                    mugiGame.RequestLeaveGame(localPlayer.playerId);
                }
            }
        }

        public void JoinTeam0()
        {
            OnTeamJoinPressed(0);
        }

        public void JoinTeam1()
        {
            OnTeamJoinPressed(1);
        }

        public void JoinTeam2()
        {
            OnTeamJoinPressed(2);
        }

        public void JoinTeam3()
        {
            OnTeamJoinPressed(3);
        }

        private void OnTeamJoinPressed(int teamIndex)
        {
            if (mugiGame != null)
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer != null)
                {
                    mugiGame.RequestJoinTeam(localPlayer.playerId, teamIndex);
                }
            }
        }
    }
}
