using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace Hiinaspace.Mugi
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MugiScoreboard : UdonSharpBehaviour
    {
        [Header("Game Reference")]
        public MugiGame mugiGame;

        [Header("UI References")]
        public TextMeshProUGUI scoreboardTitle;

        [Header("Team Row References (4 max teams)")]
        public GameObject[] teamRows = new GameObject[4];

        [Header("Player Row References (8 max players)")]
        public GameObject[] playerRows = new GameObject[8];

        // State tracking
        private bool slowUpdateScheduled = false;

        // Cached component references (discovered in Start)
        private TextMeshProUGUI[] teamRankTexts = new TextMeshProUGUI[4];
        private TextMeshProUGUI[] teamNameTexts = new TextMeshProUGUI[4];
        private TextMeshProUGUI[] teamScoreTexts = new TextMeshProUGUI[4];
        private TextMeshProUGUI[] playerRankTexts = new TextMeshProUGUI[8];
        private TextMeshProUGUI[] playerNameTexts = new TextMeshProUGUI[8];
        private TextMeshProUGUI[] playerScoreTexts = new TextMeshProUGUI[8];

        void Start()
        {
            // Cache component references and log errors if missing
            CacheComponentReferences();

            // Initial UI refresh
            RefreshScoreboard();

            // on very first enable, clear the score grid
            RefreshScoreGrid();
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

        private void CacheComponentReferences()
        {
            // Cache team row components
            for (int i = 0; i < teamRows.Length; i++)
            {
                if (teamRows[i] != null)
                {
                    Transform rankChild = teamRows[i].transform.Find("Rank");
                    if (rankChild != null)
                        teamRankTexts[i] = rankChild.GetComponent<TextMeshProUGUI>();
                    else
                        Debug.LogError(
                            $"[MugiScoreboard] Missing ScoreTeamRank child in teamRows[{i}]"
                        );

                    Transform nameChild = teamRows[i].transform.Find("Name");
                    if (nameChild != null)
                        teamNameTexts[i] = nameChild.GetComponent<TextMeshProUGUI>();
                    else
                        Debug.LogError(
                            $"[MugiScoreboard] Missing ScoreTeamName child in teamRows[{i}]"
                        );

                    Transform scoreChild = teamRows[i].transform.Find("Score");
                    if (scoreChild != null)
                        teamScoreTexts[i] = scoreChild.GetComponent<TextMeshProUGUI>();
                    else
                        Debug.LogError(
                            $"[MugiScoreboard] Missing ScoreTeamScore child in teamRows[{i}]"
                        );
                }
            }

            // Cache player row components
            for (int i = 0; i < playerRows.Length; i++)
            {
                if (playerRows[i] != null)
                {
                    Transform rankChild = playerRows[i].transform.Find("Rank");
                    if (rankChild != null)
                        playerRankTexts[i] = rankChild.GetComponent<TextMeshProUGUI>();
                    else
                        Debug.LogError(
                            $"[MugiScoreboard] Missing ScorePlayerRank child in playerRows[{i}]"
                        );

                    Transform nameChild = playerRows[i].transform.Find("Name");
                    if (nameChild != null)
                        playerNameTexts[i] = nameChild.GetComponent<TextMeshProUGUI>();
                    else
                        Debug.LogError(
                            $"[MugiScoreboard] Missing ScorePlayerName child in playerRows[{i}]"
                        );

                    Transform scoreChild = playerRows[i].transform.Find("Score");
                    if (scoreChild != null)
                        playerScoreTexts[i] = scoreChild.GetComponent<TextMeshProUGUI>();
                    else
                        Debug.LogError(
                            $"[MugiScoreboard] Missing ScorePlayerScore child in playerRows[{i}]"
                        );
                }
            }
        }

        public void SlowUpdate()
        {
            RefreshScoreboard();

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
            RefreshScoreboard();
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            RefreshScoreboard();
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            // Game master might have changed
            RefreshScoreboard();
        }

        // ========== UI REFRESH LOGIC ==========

        public void RefreshScoreboard()
        {
            if (mugiGame == null)
                return;

            // Always update title
            RefreshTitle();

            // Update score grid during countdown/running/ended states to propagate final scores
            int gameState = mugiGame.gameState;
            if (
                gameState == MugiGame.STATE_COUNTDOWN
                || gameState == MugiGame.STATE_RUNNING
                || gameState == MugiGame.STATE_ENDED
            )
            {
                RefreshScoreGrid();
            }
        }

        private void RefreshTitle()
        {
            if (scoreboardTitle == null || mugiGame == null)
                return;

            int gameState = mugiGame.gameState;
            float timeRemaining = mugiGame.GetRemainingTime();

            if (gameState == MugiGame.STATE_COUNTDOWN)
            {
                scoreboardTitle.text =
                    $"Scoreboard (<color=yellow>{Mathf.Ceil(timeRemaining)}</color> seconds)";
            }
            else if (gameState == MugiGame.STATE_RUNNING)
            {
                int minutes = Mathf.FloorToInt(timeRemaining / 60);
                int seconds = Mathf.FloorToInt(timeRemaining % 60);
                string timeColor = timeRemaining <= 30f ? "red" : "yellow";
                scoreboardTitle.text =
                    $"Scoreboard (<color={timeColor}>{minutes:00}:{seconds:00}</color> remaining)";
            }
            else if (gameState == MugiGame.STATE_ENDED)
            {
                scoreboardTitle.text = "Scoreboard (finished!)";
            }
            else // STATE_LOBBY
            {
                scoreboardTitle.text = "Scoreboard (waiting to start)";
            }
        }

        private void RefreshScoreGrid()
        {
            if (mugiGame.useTeams)
            {
                RefreshTeamMode();
            }
            else
            {
                RefreshFFAMode();
            }
        }

        private void RefreshTeamMode()
        {
            // Get team scores and sort them
            int[] teamIndices = new int[mugiGame.maxTeams];
            int[] teamScores = new int[mugiGame.maxTeams];
            int[] teamRanks = new int[mugiGame.maxTeams];

            // Populate team data
            for (int i = 0; i < mugiGame.maxTeams; i++)
            {
                teamIndices[i] = i;
                teamScores[i] = mugiGame.GetTeamScore(i);
            }

            // Sort teams by score (descending)
            SortTeamsByScore(teamIndices, teamScores, mugiGame.maxTeams);

            // Calculate team ranks (handle ties)
            CalculateRanks(teamScores, teamRanks, mugiGame.maxTeams);

            // Display teams and players
            int currentRowIndex = 0;

            for (int sortedIndex = 0; sortedIndex < mugiGame.maxTeams; sortedIndex++)
            {
                int teamIndex = teamIndices[sortedIndex];
                int teamScore = teamScores[sortedIndex];
                int teamRank = teamRanks[sortedIndex];

                // Show team header
                if (teamRows[teamIndex] != null)
                {
                    teamRows[teamIndex].SetActive(true);
                    teamRows[teamIndex].transform.SetSiblingIndex(currentRowIndex);

                    if (teamRankTexts[teamIndex] != null)
                        teamRankTexts[teamIndex].text = teamRank.ToString();
                    if (teamNameTexts[teamIndex] != null)
                        teamNameTexts[teamIndex].text = mugiGame.GetTeamName(teamIndex);
                    if (teamScoreTexts[teamIndex] != null)
                        teamScoreTexts[teamIndex].text = teamScore.ToString();

                    currentRowIndex++;
                }

                // Get players in this team and sort them
                VRCPlayerApi[] playersInGame = mugiGame.GetPlayersInGame();
                VRCPlayerApi[] teamPlayers = new VRCPlayerApi[8];
                int[] teamPlayerScores = new int[8];
                int[] teamPlayerRanks = new int[8];
                int teamPlayerCount = 0;

                // Find players in this team
                for (int i = 0; i < playersInGame.Length; i++)
                {
                    VRCPlayerApi player = playersInGame[i];
                    if (player != null && mugiGame.GetPlayerTeam(player) == teamIndex)
                    {
                        teamPlayers[teamPlayerCount] = player;
                        teamPlayerScores[teamPlayerCount] = mugiGame.GetPlayerScore(player);
                        teamPlayerCount++;
                    }
                }

                // Sort team players by score
                SortPlayersByScore(teamPlayers, teamPlayerScores, teamPlayerCount);

                // Calculate ranks within team
                CalculateRanks(teamPlayerScores, teamPlayerRanks, teamPlayerCount);

                // Display team players
                for (
                    int playerIndex = 0;
                    playerIndex < teamPlayerCount && currentRowIndex < playerRows.Length;
                    playerIndex++
                )
                {
                    VRCPlayerApi player = teamPlayers[playerIndex];
                    int playerScore = teamPlayerScores[playerIndex];
                    int playerRank = teamPlayerRanks[playerIndex];

                    playerRows[currentRowIndex].SetActive(true);
                    playerRows[currentRowIndex].transform.SetSiblingIndex(currentRowIndex);

                    if (playerRankTexts[currentRowIndex] != null)
                        playerRankTexts[currentRowIndex].text = playerRank.ToString();
                    if (playerNameTexts[currentRowIndex] != null)
                        playerNameTexts[currentRowIndex].text = player.displayName;
                    if (playerScoreTexts[currentRowIndex] != null)
                        playerScoreTexts[currentRowIndex].text = playerScore.ToString();

                    currentRowIndex++;
                }
            }

            // Hide unused team rows
            for (int i = 0; i < teamRows.Length; i++)
            {
                bool teamUsed = false;
                for (int j = 0; j < mugiGame.maxTeams; j++)
                {
                    if (teamIndices[j] == i)
                    {
                        teamUsed = true;
                        break;
                    }
                }
                if (!teamUsed && teamRows[i] != null)
                    teamRows[i].SetActive(false);
            }

            // Hide unused player rows
            for (int i = currentRowIndex; i < playerRows.Length; i++)
            {
                if (playerRows[i] != null)
                    playerRows[i].SetActive(false);
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

            // Get all players and their scores
            VRCPlayerApi[] playersInGame = mugiGame.GetPlayersInGame();
            int[] playerScores = new int[playersInGame.Length];
            int[] playerRanks = new int[playersInGame.Length];

            // Get scores for all players
            for (int i = 0; i < playersInGame.Length; i++)
            {
                if (playersInGame[i] != null)
                    playerScores[i] = mugiGame.GetPlayerScore(playersInGame[i]);
            }

            // Sort players by score
            SortPlayersByScore(playersInGame, playerScores, playersInGame.Length);

            // Calculate ranks
            CalculateRanks(playerScores, playerRanks, playersInGame.Length);

            // Display players
            for (int i = 0; i < playersInGame.Length && i < playerRows.Length; i++)
            {
                VRCPlayerApi player = playersInGame[i];
                if (player != null && playerRows[i] != null)
                {
                    playerRows[i].SetActive(true);

                    if (playerRankTexts[i] != null)
                        playerRankTexts[i].text = playerRanks[i].ToString();
                    if (playerNameTexts[i] != null)
                        playerNameTexts[i].text = player.displayName;
                    if (playerScoreTexts[i] != null)
                        playerScoreTexts[i].text = playerScores[i].ToString();
                }
            }

            // Hide unused player rows
            for (int i = playersInGame.Length; i < playerRows.Length; i++)
            {
                if (playerRows[i] != null)
                    playerRows[i].SetActive(false);
            }
        }

        // ========== SORTING AND RANKING UTILITIES ==========

        private void SortTeamsByScore(int[] teamIndices, int[] teamScores, int count)
        {
            // Simple bubble sort (descending order)
            for (int i = 0; i < count - 1; i++)
            {
                for (int j = 0; j < count - i - 1; j++)
                {
                    if (teamScores[j] < teamScores[j + 1])
                    {
                        // Swap scores
                        int tempScore = teamScores[j];
                        teamScores[j] = teamScores[j + 1];
                        teamScores[j + 1] = tempScore;

                        // Swap indices
                        int tempIndex = teamIndices[j];
                        teamIndices[j] = teamIndices[j + 1];
                        teamIndices[j + 1] = tempIndex;
                    }
                }
            }
        }

        private void SortPlayersByScore(VRCPlayerApi[] players, int[] scores, int count)
        {
            // Simple bubble sort (descending order)
            for (int i = 0; i < count - 1; i++)
            {
                for (int j = 0; j < count - i - 1; j++)
                {
                    if (scores[j] < scores[j + 1])
                    {
                        // Swap scores
                        int tempScore = scores[j];
                        scores[j] = scores[j + 1];
                        scores[j + 1] = tempScore;

                        // Swap players
                        VRCPlayerApi tempPlayer = players[j];
                        players[j] = players[j + 1];
                        players[j + 1] = tempPlayer;
                    }
                }
            }
        }

        private void CalculateRanks(int[] scores, int[] ranks, int count)
        {
            // Calculate ranks handling ties
            // Same scores get same rank, next rank skips appropriately
            for (int i = 0; i < count; i++)
            {
                int rank = 1;
                for (int j = 0; j < i; j++)
                {
                    if (scores[j] > scores[i])
                        rank++;
                }
                ranks[i] = rank;
            }
        }
    }
}
