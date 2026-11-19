// CarEnvTarget.cs — marks the player's car for CarEnv.cs (which trackside reflection cube the paint uses). Added by
// CarModel.CreateFromAsset to the non-rival car; the last one enabled wins (the full / light player car swap).
using UnityEngine;

public class CarEnvTarget : MonoBehaviour
{
    void OnEnable() { CarEnv.Target = transform; }
    void OnDisable() { if (CarEnv.Target == transform) CarEnv.Target = null; }
}
