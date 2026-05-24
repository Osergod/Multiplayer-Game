using UnityEngine;
using FishNet.Object;

// Player Network
public class PlayerNetwork : NetworkBehaviour
{
    private PlayerController playerController;

    public override void OnStartClient()
    {
        base.OnStartClient();

        playerController = GetComponent<PlayerController>();

        if (base.IsOwner)
        {
            playerController.SetupCamera();
        }
        else
        {
            playerController.enabled = false;
        }
    }
}