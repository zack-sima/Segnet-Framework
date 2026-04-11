using SegNet;
using UnityEngine;

public class SampleNetPlayer : NetworkBehaviour {

    [SyncVar(hook = nameof(OnTestStringChanged))] private string testString;
    [SyncVar(hook = nameof(OnPositionChanged))] private Vector3 position;
    [SyncVar(hook = nameof(OnListChanged)), Capacity(10)] private SyncList<string> recentMessages;

    [Rpc(RpcDirection.LocalClientToServer)]
    public void RpcSyncPosition(Vector3 newPosition) {
        transform.position = newPosition;
        testString = $"Position updated to {newPosition} at {Time.time}";
        position = newPosition;
    }
    [Rpc(RpcDirection.LocalClientToServer)]
    public void RpcSendMessage(string message) {
        recentMessages.Add(message);
        testString = message;

        //append to sync array
        if (recentMessages.Count >= 10)
            recentMessages.RemoveAt(0);
    }

    public void OnTestStringChanged() {
        OnListChanged();
    }
    public void OnListChanged() {
        // Debug.Log($"Recent messages list changed: {changeEvent}");
        SampleGameUI.Instance.testTextDisplay.text = $"Position: {position}" +
            $"\nTest String: {testString}" + $"\nPlayer ID: {OwnerPlayer.PlayerId}";

        if (recentMessages.Count > 0) {
            SampleGameUI.Instance.testTextDisplay.text += "\nRecent Messages:";
            foreach (var msg in recentMessages)
                SampleGameUI.Instance.testTextDisplay.text += $"\n- {msg}";
        }
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
