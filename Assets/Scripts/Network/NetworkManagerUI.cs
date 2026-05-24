using UnityEngine;
using FishNet;
using System.Net;
using System.Net.Sockets;
using TMPro;

public class NetworkManagerUI : MonoBehaviour
{
    [Header("Connection")]
    public string ipAddress = "127.0.0.1";
    public ushort port = 7770;

    [Header("UI")]
    public TMP_InputField ipInputField;
    public TMP_Text ipText;

    [Header("Menu")]
    public GameObject menuUI;

    private bool menuOpen = true;

    void Start()
    {
        if (ipText != null)
        {
            ipText.text = "IP: " + GetLocalIPAddress();
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            menuOpen = !menuOpen;

            menuUI.SetActive(menuOpen);

            Cursor.lockState = menuOpen ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = menuOpen;
        }
    }

    public void StartHost()
    {
        InstanceFinder.TransportManager.Transport.SetPort(port);

        InstanceFinder.ServerManager.StartConnection();
        InstanceFinder.ClientManager.StartConnection();

        HideMenu();

        Debug.Log("HOST STARTED");
        Debug.Log("LOCAL IP: " + GetLocalIPAddress());
    }

    public void JoinServer()
    {
        InstanceFinder.TransportManager.Transport.SetClientAddress(ipAddress);
        InstanceFinder.TransportManager.Transport.SetPort(port);

        InstanceFinder.ClientManager.StartConnection();

        HideMenu();

        Debug.Log("CONNECTED TO: " + ipAddress);
    }

    public void SetIP(string newIP)
    {
        ipAddress = newIP;
    }

    private void HideMenu()
    {
        menuOpen = false;

        menuUI.SetActive(false);

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    public string GetLocalIPAddress()
    {
        IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());

        foreach (IPAddress ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return ip.ToString();
            }
        }

        return "No IP Found";
    }
}