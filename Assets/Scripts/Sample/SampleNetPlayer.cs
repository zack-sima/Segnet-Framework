using SegNet;
using UnityEngine;

public class SampleNetPlayer : NetworkBehaviour {

    [SyncVar(hook = nameof(OnTestStringChanged))] private string testString;
    [SyncVar(hook = nameof(OnPositionChanged))] private Vector3 position;

    [Rpc(RpcDirection.LocalClientToServer)]
    public void RpcSyncPosition(Vector3 newPosition) {
        transform.position = newPosition;
        testString = $"Position updated to {newPosition} at {Time.time}";
        position = newPosition;
    }
    [Rpc(RpcDirection.LocalClientToServer)]
    public void RpcSendMessage(string message) {
        testString = message;
    }

    public void OnTestStringChanged(string _, string newStr) {
        // Debug.Log($"Test string changed to {newStr}");
        SampleGameUI.Instance.testTextDisplay.text = $"Position: {position}" +
            $"\nTest String: {newStr}" + $"\nPlayer ID: {OwnerPlayer.PlayerId}";
    }

    private void OnPositionChanged(Vector3 _, Vector3 newPosition) {
        transform.position = newPosition;
        // Debug.Log($"Position changed to {position}");
        SampleGameUI.Instance.testTextDisplay.text = $"Position: {position}" +
            $"\nTest String: {testString}" + $"\nPlayer ID: {OwnerPlayer.PlayerId}";
    }

    void Update() {
        if (IsServer && position != transform.position) {
            position = transform.position; // server authoritative position sync
        }

        //only local player can do controls!
        if (!IsLocalPlayer) return;

        if (Input.GetKey(KeyCode.UpArrow)) {
            transform.position += Vector3.forward * Time.deltaTime;
        }
        if (Input.GetKey(KeyCode.DownArrow)) {
            transform.position += Vector3.back * Time.deltaTime;
        }
        if (Input.GetKey(KeyCode.LeftArrow)) {
            transform.position += Vector3.left * Time.deltaTime;
        }
        if (Input.GetKey(KeyCode.RightArrow)) {
            transform.position += Vector3.right * Time.deltaTime;
        }
        if (Input.GetKeyDown(KeyCode.Space)) {
            RpcSendMessage($"Hello from player {OwnerPlayer.PlayerId} at {Time.time}");
        }
    }
}
