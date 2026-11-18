// Par.cs — parallel loops that fall back to plain loops where threads don't exist (WebGL) or would only add overhead.
// The game sets Par.Enabled = false on WebGL at startup (see Par.Init below via UNITY_WEBGL in the game assembly).
using System;

namespace FormulaSim
{
    public static class Par
    {
        // true by default; the WebGL player has no worker threads, and Parallel.For there can hang the page
        public static bool Enabled = System.Environment.ProcessorCount > 1;

        public static void For(int from, int to, Action<int> body)
        {
            if (!Enabled || to - from < 2) { for (int i = from; i < to; i++) body(i); return; }
            System.Threading.Tasks.Parallel.For(from, to, body);
        }
    }
}
