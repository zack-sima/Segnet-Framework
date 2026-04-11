using SegNet;
using UnityEngine;

public class SampleNetPlayer : NetworkBehaviour {

    [SyncVar] private string testString;
    [SyncVar(hook = nameof(OnPositionChanged))] private Vector3 position;

    [Rpc(RpcDirection.LocalClientToServer)]
    public void RpcSyncPosition(Vector3 newPosition) {
        transform.position = newPosition;
        testString = $"Position updated to {newPosition} at {Time.time}";
        position = newPosition;
    }

    private void OnPositionChanged(Vector3 _, Vector3 newPosition) {
        if (IsLocalPlayer) return; // local player already has the latest position
        transform.position = newPosition;
        // Debug.Log($"Position changed to {position}");
        SampleGameUI.Instance.testTextDisplay.text = $"Position: {position}" +
            $"\nTest String: {testString}";
    }

    void Update() {
        //only local player can do controls!
        if (!IsLocalPlayer) return;

        if (Input.GetKey(KeyCode.UpArrow)) {
            transform.position += Vector3.forward * Time.deltaTime;
            // RpcSyncPosition(transform.position);
        }
        if (Input.GetKey(KeyCode.DownArrow)) {
            transform.position += Vector3.back * Time.deltaTime;
            // RpcSyncPosition(transform.position);
        }
        if (Input.GetKey(KeyCode.LeftArrow)) {
            transform.position += Vector3.left * Time.deltaTime;
            // RpcSyncPosition(transform.position);
        }
        if (Input.GetKey(KeyCode.RightArrow)) {
            transform.position += Vector3.right * Time.deltaTime;
            // RpcSyncPosition(transform.position);
        }
    }
}
