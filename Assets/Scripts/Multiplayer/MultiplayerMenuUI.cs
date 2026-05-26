using System;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer
{
    /// <summary>
    /// Gestiona la UI del menú multijugador interactuando con el LobbyRelayManager.
    /// Permite iniciar el hosting (creando un Lobby + Relay y mostrando el PIN)
    /// y unirse escribiendo el PIN del Lobby.
    /// </summary>
    public class MultiplayerMenuUI : MonoBehaviour
    {
        public static MultiplayerMenuUI Instance { get; private set; }

        [Header("Paneles de UI")]
        [SerializeField] private GameObject menuPanel;          // Panel principal del menú
        [SerializeField] private GameObject lobbyActivePanel;   // Panel que se muestra una vez dentro de la partida/sala

        [Header("Elementos de Entrada")]
        [SerializeField] private TMP_InputField pinInputField;  // Campo donde el cliente escribe el PIN del Lobby

        [Header("Elementos de Texto")]
        [SerializeField] private TextMeshProUGUI pinDisplayLabel; // Texto que muestra el PIN generado al Host
        [SerializeField] private TextMeshProUGUI statusText;      // Texto para mostrar el estado actual (ej: "Conectando...")

        [Header("Botones")]
        [SerializeField] private Button hostButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private Button disconnectButton;
        [SerializeField] private Button startGameButton;        // Botón para comenzar la partida (solo visible para Host)

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            // Asegurarse de que el estado inicial de los paneles sea correcto
            if (menuPanel != null) menuPanel.SetActive(true);
            if (lobbyActivePanel != null) lobbyActivePanel.SetActive(false);

            // Asegurarse de ocultar el botón de comenzar por defecto
            if (startGameButton != null) startGameButton.gameObject.SetActive(false);

            // Asignar listeners por código
            if (hostButton != null) hostButton.onClick.AddListener(OnHostButtonClicked);
            if (joinButton != null) joinButton.onClick.AddListener(OnJoinButtonClicked);
            if (disconnectButton != null) disconnectButton.onClick.AddListener(OnDisconnectButtonClicked);
            if (startGameButton != null) startGameButton.onClick.AddListener(OnStartGameButtonClicked);

            UpdateStatusText("Listo para jugar. Conexión anónima iniciada.");

            // Suscribirse a eventos del NetworkManager para reaccionar a la conexión de red
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback += OnNetworkClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback += OnNetworkClientDisconnected;
            }
        }

        private void OnDestroy()
        {
            // Limpieza de suscripciones de red al destruir el objeto
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= OnNetworkClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnNetworkClientDisconnected;
            }
        }

        /// <summary>
        /// Se ejecuta al presionar el botón "Crear Partida (Host)".
        /// </summary>
        public async void OnHostButtonClicked()
        {
            SetButtonsInteractable(false);
            UpdateStatusText("Reservando servidor y creando sala en la nube...");

            // Intentar crear la sesión a través de LobbyRelayManager
            string generatedLobbyPin = await LobbyRelayManager.Instance.CreateLobbyAndRelay();

            if (!string.IsNullOrEmpty(generatedLobbyPin))
            {
                // Éxito al crear la sala
                UpdateStatusText("¡Sala creada! Esperando jugadores...");
                
                if (pinDisplayLabel != null)
                {
                    pinDisplayLabel.text = $"PIN DE SALA:\n<color=#FFD700>{generatedLobbyPin}</color>";
                }

                // Activar el botón de Comenzar Partida para el Host
                if (startGameButton != null)
                {
                    startGameButton.gameObject.SetActive(true);
                }

                // Cambiar de panel de UI
                if (menuPanel != null) menuPanel.SetActive(false);
                if (lobbyActivePanel != null) lobbyActivePanel.SetActive(true);
            }
            else
            {
                // Fallo al crear la sala
                UpdateStatusText("<color=red>Error al crear la sala multijugador.</color>");
                SetButtonsInteractable(true);
            }
        }

        /// <summary>
        /// Se ejecuta al presionar el botón "Unirse (Client)".
        /// </summary>
        public async void OnJoinButtonClicked()
        {
            if (pinInputField == null || string.IsNullOrEmpty(pinInputField.text))
            {
                UpdateStatusText("<color=orange>Introduce un PIN válido antes de unirte.</color>");
                return;
            }

            string code = pinInputField.text.Trim();
            
            if (code.Length != 6)
            {
                UpdateStatusText("<color=orange>El PIN de sala debe tener 6 caracteres.</color>");
                return;
            }

            SetButtonsInteractable(false);
            UpdateStatusText($"Buscando sala con PIN: {code.ToUpper()}...");

            // Intentar unirse a través de LobbyRelayManager
            bool success = await LobbyRelayManager.Instance.JoinLobbyAndRelay(code);

            if (success)
            {
                UpdateStatusText("Unido al Lobby. Conectando al servidor del juego...");
                // Nota: El cambio final de UI al panel activo se realiza cuando 
                // se activa el Callback de conexión de Netcode (OnNetworkClientConnected)
            }
            else
            {
                UpdateStatusText("<color=red>PIN incorrecto, sala llena o no disponible.</color>");
                SetButtonsInteractable(true);
            }
        }

        /// <summary>
        /// Se ejecuta al presionar el botón "Salir de la partida".
        /// </summary>
        public async void OnDisconnectButtonClicked()
        {
            UpdateStatusText("Abandonando partida y eliminando sala...");
            
            // Apaga el multijugador limpiamente (sale del lobby en la nube y desconecta Netcode)
            await LobbyRelayManager.Instance.ShutdownMultiplayer();
            
            // Regresar UI al menú principal
            if (menuPanel != null) menuPanel.SetActive(true);
            if (lobbyActivePanel != null) lobbyActivePanel.SetActive(false);
            if (pinDisplayLabel != null) pinDisplayLabel.text = "PIN DE SALA: -";
            if (pinInputField != null) pinInputField.text = "";
            if (startGameButton != null) startGameButton.gameObject.SetActive(false);

            // Reactivar cursor al volver al menú principal
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            SetButtonsInteractable(true);
            UpdateStatusText("Desconectado.");
        }

        #region Callbacks de Red (Netcode)

        private void OnNetworkClientConnected(ulong clientId)
        {
            // Este evento se dispara cuando un cliente (o el propio host) se conecta con éxito al NetworkManager
            if (NetworkManager.Singleton.IsServer)
            {
                Debug.Log($"[UI] Cliente con ID {clientId} se ha conectado a la partida.");
                UpdateStatusText($"¡Un jugador se ha unido! Total: {NetworkManager.Singleton.ConnectedClients.Count}");

                // Asegurar que el Host ve el botón de Comenzar Partida
                if (startGameButton != null)
                {
                    startGameButton.gameObject.SetActive(true);
                }
            }
            else
            {
                // Si somos el cliente que se conecta exitosamente
                if (clientId == NetworkManager.Singleton.LocalClientId)
                {
                    UpdateStatusText("<color=green>¡Te has conectado con éxito!</color>");
                    
                    if (menuPanel != null) menuPanel.SetActive(false);
                    if (lobbyActivePanel != null) lobbyActivePanel.SetActive(true);
                    if (pinDisplayLabel != null)
                    {
                        pinDisplayLabel.text = $"PIN DE SALA: Conectado";
                    }

                    // Asegurar que los clientes no ven el botón de Comenzar Partida
                    if (startGameButton != null)
                    {
                        startGameButton.gameObject.SetActive(false);
                    }
                }
            }
        }

        private void OnNetworkClientDisconnected(ulong clientId)
        {
            if (NetworkManager.Singleton.IsServer)
            {
                Debug.Log($"[UI] Cliente con ID {clientId} se ha desconectado.");
                UpdateStatusText($"Un jugador se ha ido. Total: {NetworkManager.Singleton.ConnectedClients.Count}");
            }
            else
            {
                // Si somos el cliente y fuimos desconectados
                if (clientId == NetworkManager.Singleton.LocalClientId)
                {
                    // Forzar limpieza y actualización de UI
                    _ = LobbyRelayManager.Instance.ShutdownMultiplayer();
                    
                    if (menuPanel != null) menuPanel.SetActive(true);
                    if (lobbyActivePanel != null) lobbyActivePanel.SetActive(false);
                    if (pinDisplayLabel != null) pinDisplayLabel.text = "PIN DE SALA: -";
                    if (pinInputField != null) pinInputField.text = "";
                    if (startGameButton != null) startGameButton.gameObject.SetActive(false);

                    // Reactivar cursor tras desconexión
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;

                    SetButtonsInteractable(true);
                    UpdateStatusText("<color=red>Te has desconectado de la partida o el Host cerró la sala.</color>");
                }
            }
        }

        #endregion

        #region Métodos de Utilidad UI

        public void HideLobbyPanel()
        {
            if (lobbyActivePanel != null) lobbyActivePanel.SetActive(false);
            if (menuPanel != null) menuPanel.SetActive(false);
        }

        private void OnStartGameButtonClicked()
        {
            // Buscar nuestro PlayerNetworkSetup local y pedirle que inicie la partida
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClient != null)
            {
                var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
                if (localPlayer != null)
                {
                    var setup = localPlayer.GetComponent<PlayerNetworkSetup>();
                    if (setup != null)
                    {
                        UpdateStatusText("Iniciando partida para todos los jugadores...");
                        setup.StartGame();
                    }
                }
            }
        }

        private void UpdateStatusText(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }
            Debug.Log($"[UI Status] {message}");
        }

        private void SetButtonsInteractable(bool state)
        {
            if (hostButton != null) hostButton.interactable = state;
            if (joinButton != null) joinButton.interactable = state;
        }

        #endregion
    }
}
