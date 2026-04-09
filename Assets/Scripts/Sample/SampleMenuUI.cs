using UnityEngine;
using SegNet;

public class SampleMenuUI : MonoBehaviour {
    public void StartHost() {
        NetworkManager.Instance.StartHost();
    }
    public void StartClient() {
        NetworkManager.Instance.StartClient();
    }
    public void StartServer() {
        NetworkManager.Instance.StartServer();
    }
}
