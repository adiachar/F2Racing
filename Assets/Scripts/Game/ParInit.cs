// ParInit.cs — turn off parallel loops on WebGL (no worker threads; Parallel.For can hang the page) before anything loads.
using UnityEngine;

static class ParInit
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        FormulaSim.Par.Enabled = false;
#else
        FormulaSim.Par.Enabled = SystemInfo.processorCount > 1;
#endif
    }
}
