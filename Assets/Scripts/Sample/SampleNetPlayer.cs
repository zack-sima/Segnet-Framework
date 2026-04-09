using SegNet;
using UnityEngine;

public class SampleNetPlayer : NetworkBehaviour {
    void Update() {
        //only local player can do controls! TODO: add sync direction local -> server for client to take effect
        if (OwnerPlayer == null || !OwnerPlayer.IsLocal) return;

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
    }
}
