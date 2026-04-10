using SegNet;
using UnityEngine;

public class SampleNetPlayer : NetworkBehaviour {

    [Rpc(RpcDirection.ClientToServer)]
    public void RpcSyncPosition(Vector3 newPosition) {
        transform.position = newPosition;
    }

    void Update() {
        //only local player can do controls!
        if (OwnerPlayer == null || !OwnerPlayer.IsLocal) return;

        if (Input.GetKey(KeyCode.UpArrow)) {
            transform.position += Vector3.forward * Time.deltaTime;
            RpcSyncPosition(transform.position);
        }
        if (Input.GetKey(KeyCode.DownArrow)) {
            transform.position += Vector3.back * Time.deltaTime;
            RpcSyncPosition(transform.position);
        }
        if (Input.GetKey(KeyCode.LeftArrow)) {
            transform.position += Vector3.left * Time.deltaTime;
            RpcSyncPosition(transform.position);
        }
        if (Input.GetKey(KeyCode.RightArrow)) {
            transform.position += Vector3.right * Time.deltaTime;
            RpcSyncPosition(transform.position);
        }
    }
}
