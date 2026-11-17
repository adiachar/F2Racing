// SteerFilter.cs — turns the steering input, read once per rendered frame, into a smooth signal for the 240 Hz physics.
//
// Touch and tilt are read once per frame (30-60 times a second), but the physics runs 4-8 steps per frame. Fed the
// raw value, the car got one jump in steering per frame and then held still for the other steps (a staircase): the
// wheel moved in small steps, most visibly when turning fast or at 30 fps. Here:
//  1. the new frame's value is reached by a straight ramp across that frame's physics steps (no staircase), and
//  2. the ramp runs through a critically damped spring with half of the ramp's own speed fed forward. That is a
//     smooth, frame-rate independent filter: it rounds the corners where one frame's ramp meets the next (so the
//     wheel's speed has no jumps), lags a steady swipe by only 1/Omega (8 ms), and never overshoots.
// The car's own steering-speed limiter (FormulaCar, the "Steering speed" / "Return to centre" settings) comes after
// this, unchanged. Tilt steering sets its own mode (GameManager): Real-time turns the spring off (the ramp alone: no
// lag), Smooth runs it at a faster omega. No UnityEngine here: Tools/PhysicsCheck measures it.
using System;

namespace FormulaSim
{
    public sealed class SteerFilter
    {
        public const double Omega = 120;  // rad/s: spring speed
        const double Feed = 0.5;          // share of the ramp's speed fed forward (0 = plain spring, lag 2/Omega; above 0.5 it overshoots)

        public double omega = Omega;  // spring speed in use (tilt in Smooth mode runs it faster: GameManager.TiltOmega)
        public bool Spring = true;    // false: the straight ramp alone (real-time tilt, already smooth and must not lag)

        double from, to, u, uv, x, v; int n, k; bool init;

        public double Value => x;

        public void Reset(double s) { from = to = u = x = s; uv = v = 0; n = k = 0; init = true; }

        // once per frame, before its physics steps: the frame's input and how many steps the frame will run
        public void BeginFrame(double goal, int steps, double h)
        {
            if (double.IsNaN(goal)) goal = 0;
            if (!init || double.IsNaN(x)) Reset(goal);
            from = u; to = goal; n = steps; k = 0;
            uv = n > 0 ? (to - from) / (n * h) : 0;
        }

        // one physics step of length h; returns the steering for that step
        public double Step(double h)
        {
            if (k >= n) { uv = 0; from = u = to; } // more steps than planned: hold the frame's value
            k++;
            if (!Spring)
            {
                u = k >= n ? to : from + (to - from) * k / n;
                x = u; v = uv; // the spring sits on the ramp, so switching it back on is seamless
                return Math.Max(-1, Math.Min(1, x));
            }
            double w = omega > 1 ? omega : Omega;
            // the target moves at the steady speed uv during the step, so the error to it is stepped exactly:
            // e'' = -w^2 e - 2w e' - 2w(1 - Feed) uv, which rests at eq (the ramp lag) and decays critically damped
            double eq = -2 * (1 - Feed) * uv / w;
            double d = x - u - eq, dv = v - uv;
            double ex = Math.Exp(-w * h), t = (dv + w * d) * h;
            dv = (dv - w * t) * ex; d = (d + t) * ex;
            u = k >= n ? to : from + (to - from) * k / n;
            x = u + eq + d; v = uv + dv;
            return Math.Max(-1, Math.Min(1, x));
        }
    }
}
