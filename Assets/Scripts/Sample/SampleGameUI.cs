using TMPro;
using UnityEngine;

public class SampleGameUI : MonoBehaviour {
    public static SampleGameUI Instance { get; private set; }
    public TMP_Text testTextDisplay;
    public void ExitToMenu() {
        SegNet.NetworkManager.Instance.StopGame();
    }
    private void Awake() {
        Instance = this;
    }
}