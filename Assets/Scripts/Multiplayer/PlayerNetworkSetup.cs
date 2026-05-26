using Unity.Netcode;
using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// Se adjunta al Prefab del Jugador.
    /// Reemplaza el antiguo script de red (PlayerNetwork) utilizando Netcode for GameObjects.
    /// Configura la cámara para el jugador local y desactiva el movimiento para jugadores remotos.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(PlayerController))]
    public class PlayerNetworkSetup : NetworkBehaviour
    {
        private PlayerController playerController;

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            playerController = GetComponent<PlayerController>();

            if (IsOwner)
            {
                Debug.Log($"[PlayerNetworkSetup] Inicializando jugador local (NetworkClientID: {OwnerClientId})");
                // Configura la cámara principal para que siga a nuestro personaje local
                playerController.SetupCamera();
            }
            else
            {
                Debug.Log($"[PlayerNetworkSetup] Desactivando componentes del jugador remoto (NetworkClientID: {OwnerClientId})");
                // Desactiva el script de movimiento en los personajes de otros jugadores conectados
                playerController.enabled = false;
            }
        }

        public void StartGame()
        {
            if (IsOwner)
            {
                StartGameServerRpc();
            }
        }

        [ServerRpc]
        private void StartGameServerRpc()
        {
            StartGameClientRpc();
        }

        [ClientRpc]
        private void StartGameClientRpc()
        {
            // Ocultar el panel de Lobby en la UI para todos
            if (MultiplayerMenuUI.Instance != null)
            {
                MultiplayerMenuUI.Instance.HideLobbyPanel();
            }

            // Activar movimiento y bloquear cursor en el jugador local
            if (IsOwner)
            {
                playerController.EnableGameplay();
            }
        }
    }
}
