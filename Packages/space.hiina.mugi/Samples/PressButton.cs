using Space.Hiina.Mugi;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace Space.Hiina.Mugi.Examples
{
    public class PressButton : UdonSharpBehaviour
    {
        public MugiGame mugiGame; // Reference to the MugiGame controller

        public override void Interact()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (mugiGame != null && localPlayer != null)
            {
                // Only allow interaction if game is running and player is in the game
                if (
                    mugiGame.gameState == MugiGame.STATE_RUNNING
                    && mugiGame.IsPlayerInGame(localPlayer)
                )
                {
                    // This now uses proper [NetworkCallable] events with parameters!
                    mugiGame.IncrementScore(localPlayer.playerId, 1);
                }
                else if (mugiGame.gameState != MugiGame.STATE_RUNNING)
                {
                    Debug.Log("PressButton: Game is not running - button press ignored");
                }
                else if (!mugiGame.IsPlayerInGame(localPlayer))
                {
                    Debug.Log("PressButton: Player not in game - button press ignored");
                }
            }
            else
            {
                Debug.LogError("MugiGame reference is not set or no local player!");
            }
        }

        // Example callback methods that can be hooked up in MugiGame inspector
        public void OnMugiStart()
        {
            Debug.Log("PressButton: Game started!");
            // Game-specific logic when game starts
        }

        public void OnMugiEnd()
        {
            Debug.Log("PressButton: Game ended!");
            // Game-specific logic when game ends
        }

        public void OnMugiCountdown()
        {
            Debug.Log("PressButton: Game countdown started!");
            // Game-specific logic during countdown
        }

        public void OnMugiTimeWarning()
        {
            Debug.Log("PressButton: 30 seconds remaining!");
            // Game-specific logic for time warning
        }

        public void OnMugiPlayerJoin()
        {
            Debug.Log("PressButton: A player joined the game!");
            // Game-specific logic when player joins
        }

        public void OnMugiPlayerLeave()
        {
            Debug.Log("PressButton: A player left the game!");
            // Game-specific logic when player leaves
        }
    }
}
