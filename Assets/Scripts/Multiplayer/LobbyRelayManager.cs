using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// Gestiona las salas multijugador utilizando Unity Lobby y Unity Relay de forma sincronizada.
    /// El Host crea una asignación de Relay, guarda el código de Relay en un Lobby y obtiene un PIN de Lobby.
    /// El Cliente se une al Lobby mediante el PIN, extrae el código de Relay y se conecta al servidor del Host.
    /// </summary>
    public class LobbyRelayManager : MonoBehaviour
    {
        public static LobbyRelayManager Instance { get; private set; }

        [Header("Configuración de la Sala")]
        [Tooltip("Número máximo de jugadores permitidos en el Lobby (incluyendo al host).")]
        [SerializeField] private int maxPlayers = 4;
        
        [Tooltip("Nombre por defecto de la sala en la nube.")]
        [SerializeField] private string defaultLobbyName = "SalaMultijugador";

        private Lobby _currentLobby;
        private Coroutine _heartbeatCoroutine;
        private Coroutine _lobbyPollCoroutine;
        private bool _isAuthenticationInProgress = false;

        private void Awake()
        {
            // Patrón Singleton
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            // Inicializa los servicios de Unity automáticamente al iniciar
            await InitializeUnityServicesAsync();
        }

        /// <summary>
        /// Inicializa los servicios de Unity y realiza una autenticación anónima.
        /// </summary>
        private async Task InitializeUnityServicesAsync()
        {
            if (_isAuthenticationInProgress) return;
            _isAuthenticationInProgress = true;

            try
            {
                if (UnityServices.State == ServicesInitializationState.Uninitialized)
                {
                    await UnityServices.InitializeAsync();
                    Debug.Log("[LobbyRelayManager] Unity Services inicializados correctamente.");
                }

                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                    Debug.Log($"[LobbyRelayManager] Jugador autenticado de forma anónima. Player ID: {AuthenticationService.Instance.PlayerId}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LobbyRelayManager] Error al inicializar servicios o autenticación: {e.Message}\n" +
                               "Verifica que el proyecto esté vinculado a una Unity Organization en Project Settings > Services.");
            }
            finally
            {
                _isAuthenticationInProgress = false;
            }
        }

        /// <summary>
        /// Crea una asignación en Relay, crea un Lobby asociado a esa asignación y devuelve el PIN de Lobby de 6 caracteres.
        /// </summary>
        /// <returns>El código PIN del Lobby si tiene éxito, o null si falla.</returns>
        public async Task<string> CreateLobbyAndRelay()
        {
            try
            {
                // 1. Asegurar autenticación
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    await InitializeUnityServicesAsync();
                }

                Debug.Log("[LobbyRelayManager] Creando asignación en el servidor Relay...");
                
                // 2. Crear la asignación de Relay (excluye al host de las conexiones máximas)
                int maxRelayConnections = maxPlayers - 1;
                Allocation relayAllocation = await RelayService.Instance.CreateAllocationAsync(maxRelayConnections);

                // 3. Obtener el Join Code de Relay
                string relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(relayAllocation.AllocationId);
                Debug.Log($"[LobbyRelayManager] Relay creado correctamente. Join Code: {relayJoinCode}");

                // 4. Configurar el transporte de Netcode para usar Relay
                UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                transport.SetHostRelayData(
                    relayAllocation.RelayServer.IpV4,
                    (ushort)relayAllocation.RelayServer.Port,
                    relayAllocation.AllocationIdBytes,
                    relayAllocation.Key,
                    relayAllocation.ConnectionData
                );

                Debug.Log("[LobbyRelayManager] Creando sala en Unity Lobby...");

                // 5. Configurar los datos personalizados del Lobby (guardamos el RelayJoinCode de forma privada para los miembros)
                CreateLobbyOptions lobbyOptions = new CreateLobbyOptions
                {
                    IsPrivate = false, // Permite buscarlo públicamente si se quiere
                    Data = new Dictionary<string, DataObject>
                    {
                        {
                            "RelayJoinCode", 
                            new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode)
                        }
                    }
                };

                // 6. Crear el Lobby en la nube
                _currentLobby = await LobbyService.Instance.CreateLobbyAsync(defaultLobbyName, maxPlayers, lobbyOptions);
                Debug.Log($"[LobbyRelayManager] Lobby creado con éxito. ID: {_currentLobby.Id} | PIN/LobbyCode: {_currentLobby.LobbyCode}");

                // 7. Iniciar el loop de Heartbeats para que el Lobby no expire (requisito de Unity Lobbies)
                _heartbeatCoroutine = StartCoroutine(HeartbeatLobbyCoroutine(_currentLobby.Id, 15f));
                
                // 8. Iniciar el polling del Lobby para recibir actualizaciones de jugadores conectados
                _lobbyPollCoroutine = StartCoroutine(PollLobbyCoroutine(_currentLobby.Id, 5f));

                // 9. Iniciar el NetworkManager como HOST (Servidor + Cliente local)
                if (NetworkManager.Singleton.StartHost())
                {
                    Debug.Log("[LobbyRelayManager] NetworkManager iniciado como HOST correctamente.");
                    return _currentLobby.LobbyCode;
                }
                else
                {
                    Debug.LogError("[LobbyRelayManager] No se pudo iniciar el NetworkManager como Host.");
                    await ShutdownMultiplayer();
                    return null;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LobbyRelayManager] Error al crear Lobby + Relay: {e.Message}");
                await ShutdownMultiplayer();
                return null;
            }
        }

        /// <summary>
        /// Se une a un Lobby existente mediante su PIN, recupera el Join Code de Relay y conecta el cliente.
        /// </summary>
        /// <param name="lobbyCode">PIN de 6 caracteres del Lobby ingresado por el jugador.</param>
        /// <returns>Verdadero si el proceso de unión se inició con éxito, falso si ocurrió un error.</returns>
        public async Task<bool> JoinLobbyAndRelay(string lobbyCode)
        {
            if (string.IsNullOrEmpty(lobbyCode))
            {
                Debug.LogError("[LobbyRelayManager] El código PIN del Lobby está vacío.");
                return false;
            }

            try
            {
                // 1. Asegurar autenticación
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    await InitializeUnityServicesAsync();
                }

                lobbyCode = lobbyCode.Trim().ToUpper();
                Debug.Log($"[LobbyRelayManager] Buscando y uniéndose al Lobby con PIN: {lobbyCode}...");

                // 2. Unirse al Lobby en la nube utilizando el PIN de sala
                _currentLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(lobbyCode);
                Debug.Log($"[LobbyRelayManager] Unido al Lobby con éxito. ID: {_currentLobby.Id}");

                // 3. Extraer el RelayJoinCode que el Host guardó en los metadatos del Lobby
                if (_currentLobby.Data.TryGetValue("RelayJoinCode", out DataObject dataEntry))
                {
                    string relayJoinCode = dataEntry.Value;
                    Debug.Log($"[LobbyRelayManager] Código Relay recuperado del Lobby: {relayJoinCode}. Conectando a Relay...");

                    // 4. Resolver la asignación de cliente en Relay
                    JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);

                    // 5. Configurar el transporte de Netcode con los datos de Relay
                    UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                    transport.SetClientRelayData(
                        joinAllocation.RelayServer.IpV4,
                        (ushort)joinAllocation.RelayServer.Port,
                        joinAllocation.AllocationIdBytes,
                        joinAllocation.Key,
                        joinAllocation.ConnectionData,
                        joinAllocation.HostConnectionData
                    );

                    // 6. Iniciar el NetworkManager como CLIENTE
                    if (NetworkManager.Singleton.StartClient())
                    {
                        Debug.Log("[LobbyRelayManager] NetworkManager iniciado como CLIENTE.");
                        
                        // Iniciar polling del lobby para actualizaciones de estado de sala
                        _lobbyPollCoroutine = StartCoroutine(PollLobbyCoroutine(_currentLobby.Id, 5f));
                        return true;
                    }
                    else
                    {
                        Debug.LogError("[LobbyRelayManager] No se pudo iniciar el NetworkManager como Cliente.");
                        await ShutdownMultiplayer();
                        return false;
                    }
                }
                else
                {
                    Debug.LogError("[LobbyRelayManager] El Lobby no contiene información de conexión de Relay.");
                    await ShutdownMultiplayer();
                    return false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LobbyRelayManager] Error al unirse al Lobby + Relay: {e.Message}");
                await ShutdownMultiplayer();
                return false;
            }
        }

        /// <summary>
        /// Apaga el multijugador de forma limpia, abandonando o destruyendo el Lobby en la nube
        /// y desconectando la red local de Netcode.
        /// </summary>
        public async Task ShutdownMultiplayer()
        {
            // 1. Detener corrutinas en segundo plano
            if (_heartbeatCoroutine != null)
            {
                StopCoroutine(_heartbeatCoroutine);
                _heartbeatCoroutine = null;
            }
            if (_lobbyPollCoroutine != null)
            {
                StopCoroutine(_lobbyPollCoroutine);
                _lobbyPollCoroutine = null;
            }

            // 2. Gestionar la salida limpia del Lobby en la nube
            if (_currentLobby != null)
            {
                try
                {
                    string playerId = AuthenticationService.Instance.PlayerId;

                    // Si somos el creador/host, destruimos el Lobby para todos los jugadores
                    if (_currentLobby.HostId == playerId)
                    {
                        await LobbyService.Instance.DeleteLobbyAsync(_currentLobby.Id);
                        Debug.Log($"[LobbyRelayManager] Lobby {_currentLobby.Id} eliminado de la nube por el Host.");
                    }
                    else
                    {
                        // Si somos un cliente, simplemente abandonamos la sala
                        await LobbyService.Instance.RemovePlayerAsync(_currentLobby.Id, playerId);
                        Debug.Log($"[LobbyRelayManager] Cliente abandonó el Lobby {_currentLobby.Id} en la nube.");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[LobbyRelayManager] Error al limpiar el Lobby en la nube (puede haber expirado): {e.Message}");
                }
                finally
                {
                    _currentLobby = null;
                }
            }

            // 3. Apagar Netcode
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.Shutdown();
                Debug.Log("[LobbyRelayManager] Conexión de red de Netcode apagada (Shutdown).");
            }
        }

        #region Corrutinas (Heartbeat y Polling)

        /// <summary>
        /// Corrutina para enviar pings regulares de Heartbeat a la API de Lobbies.
        /// Previene que el Lobby expire después de 30 segundos de inactividad.
        /// Solo la ejecuta el Host.
        /// </summary>
        private IEnumerator HeartbeatLobbyCoroutine(string lobbyId, float waitTimeSeconds)
        {
            var delay = new WaitForSecondsRealtime(waitTimeSeconds);
            while (true)
            {
                yield return delay;
                
                Task task = null;
                try
                {
                    task = LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[LobbyRelayManager] Heartbeat falló al iniciar: {e.Message}");
                    continue;
                }

                yield return new WaitUntil(() => task.IsCompleted);

                if (task.Exception != null)
                {
                    Debug.LogWarning($"[LobbyRelayManager] Error en Heartbeat: {task.Exception.Message}");
                }
            }
        }

        /// <summary>
        /// Corrutina para solicitar actualizaciones del estado del Lobby en la nube periódicamente.
        /// Permite detectar si el Host cerró la sala o si se unieron/fueron expulsados jugadores.
        /// Tolera hasta 3 errores consecutivos antes de desconectar.
        /// </summary>
        private IEnumerator PollLobbyCoroutine(string lobbyId, float waitTimeSeconds)
        {
            var delay = new WaitForSecondsRealtime(waitTimeSeconds);
            int consecutiveErrors = 0;
            const int maxConsecutiveErrors = 3;

            while (true)
            {
                yield return delay;

                Task<Lobby> getLobbyTask = null;
                try
                {
                    getLobbyTask = LobbyService.Instance.GetLobbyAsync(lobbyId);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[LobbyRelayManager] Error al iniciar polling: {e.Message}");
                    consecutiveErrors++;
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        Debug.LogError("[LobbyRelayManager] Demasiados errores de polling. Desconectando...");
                        _currentLobby = null;
                        _ = ShutdownMultiplayer();
                        yield break;
                    }
                    continue;
                }

                yield return new WaitUntil(() => getLobbyTask.IsCompleted);

                if (getLobbyTask.Exception != null)
                {
                    consecutiveErrors++;
                    Debug.LogWarning($"[LobbyRelayManager] Error de polling ({consecutiveErrors}/{maxConsecutiveErrors}): {getLobbyTask.Exception.Message}");
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        Debug.LogError("[LobbyRelayManager] Lobby no encontrado tras múltiples intentos. Desconectando...");
                        _currentLobby = null;
                        _ = ShutdownMultiplayer();
                        yield break;
                    }
                }
                else
                {
                    consecutiveErrors = 0;
                    _currentLobby = getLobbyTask.Result;
                }
            }
        }

        #endregion

        #region Clase Auxiliar de Optimización
        
        // Optimizador para evitar recolector de basura al usar WaitForSeconds
        private class WaitForSecondsSecondsRealtime : CustomYieldInstruction
        {
            private readonly float _waitTime;
            private float _timeTracker;

            public override bool keepWaiting
            {
                get
                {
                    _timeTracker += Time.unscaledDeltaTime;
                    return _timeTracker < _waitTime;
                    
                }
            }

            public WaitForSecondsSecondsRealtime(float time)
            {
                _waitTime = time;
                Reset();
            }

            public override void Reset()
            {
                _timeTracker = 0f;
            }
        }

        #endregion
    }
}
