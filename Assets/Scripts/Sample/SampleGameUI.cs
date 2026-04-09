using UnityEngine;

public class SampleGameUI : MonoBehaviour {
    public void ExitToMenu() {
        SegNet.NetworkManager.Instance.StopGame();
    }
}
