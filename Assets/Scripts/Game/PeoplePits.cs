// PeoplePits.cs — the people around the pit boxes that are not the stop crew (PitCrew.cs): engineers on stools at the
// garage desks and on the pit-wall stands (headsets, seated, now and then talking), a mechanic in each detailed garage
// (arms folded / working at the trolley), and a few team members walking along the garage fronts (they turn back
// before a box where a stop is on). Placed by PitGarage (Spot) in the detailed bays; rigs from People.cs.
// Cost: animated only within ~120 m of the camera (every other frame beyond ~40 m, with the LOD1 mesh), frozen beyond,
// hidden beyond ~230 m; one contact-shadow mesh for all of them.
using System.Collections.Generic;
using UnityEngine;

public sealed class PeoplePits : MonoBehaviour
{
    public struct Spot { public int kind, seed, pose; public Livery liv; public Vector3 pos, fwd; } // kind 1 engineer, 2 mechanic; pose 0 seated, 1 arms folded, 2 talking, 3 working
    sealed class Agent
    {
        public People.Rig rig; public Spot s; public bool walker, posed;
        public Vector3 a, b; public float len, x, dir = 1f, pause, dist, sp;
    }
    readonly List<Agent> agents = new List<Agent>();
    People.Blobs blobs; People.PoseBuf buf = new People.PoseBuf(), buf2 = new People.PoseBuf();
    int frame;

    public static PeoplePits Create(Transform parent, List<Spot> spots, int walkers, int[] order, Livery[] liv)
    {
        if (!People.Ready || Trackside.Bays.Count == 0) return null;
        var go = new GameObject("PitPeople"); go.transform.SetParent(parent, false);
        var pp = go.AddComponent<PeoplePits>(); pp.Init(spots, walkers, order, liv);
        return pp;
    }

    void Init(List<Spot> spots, int walkers, int[] order, Livery[] liv)
    {
        foreach (var s in spots)
        {
            int body = People.BodyOf(s.seed, s.kind == 1 ? 0.35f : 0.15f);
            int gear = s.kind == 1 ? People.GHeadset : Hash(s.seed, 4) < 0.55f ? People.GCap : People.GBare;
            var r = new People.Rig(transform, body, gear, "Person") { scale = People.HeightOf(s.seed, body) };
            r.Dress(s.kind == 1 ? People.OEngineer : People.OMechanic, s.liv, s.seed);
            agents.Add(new Agent { rig = r, s = s });
        }
        // walkers along the garage fronts near the player's box (team colours of the boxes they start at)
        var bays = Trackside.Bays; int n = bays.Count;
        for (int i = 0; i < walkers && n >= 2 && order != null; i++)
        {
            int b0 = order[Mathf.Min(order.Length - 1, i % Mathf.Min(order.Length, 6))];
            int b1 = Mathf.Clamp(b0 + ((i & 1) == 0 ? 3 : -3), 0, n - 1); if (b1 == b0) b1 = Mathf.Clamp(b0 + ((i & 1) == 0 ? -3 : 3), 0, n - 1);
            var A = bays[b0]; var B = bays[b1];
            float off = A.garageDist - 0.55f - 0.35f * (i % 2);
            Vector3 pa = A.centre + A.side.normalized * off - A.fwd.normalized * (1.5f * (i % 3)), pb = B.centre + B.side.normalized * (B.garageDist - 0.55f - 0.35f * (i % 2));
            int seed = 900 + i * 31, body = People.BodyOf(seed, 0.3f);
            int outfit = i == 3 ? People.OMarshal : (i & 1) == 0 ? People.OMechanic : People.OEngineer;
            int gear = outfit == People.OEngineer ? People.GHeadset : Hash(seed, 4) < 0.5f ? People.GCap : People.GBare;
            var r = new People.Rig(transform, body, gear, "Walker") { scale = People.HeightOf(seed, body) };
            r.Dress(outfit, liv != null && b0 < liv.Length ? liv[b0] : Livery.Player[0], seed);
            var ag = new Agent { rig = r, walker = true, a = pa, b = pb, len = Vector3.Distance(pa, pb), sp = 1.15f + 0.25f * Hash(seed, 5) };
            ag.s.seed = seed; ag.x = ag.len * Hash(seed, 6); ag.dir = (i & 1) == 0 ? 1f : -1f;
            agents.Add(ag);
        }
        blobs = new People.Blobs(transform, agents.Count, "PitPeopleShadows");
    }

    static float Hash(int a, int b) { float x = Mathf.Sin(a * 12.9898f + b * 78.233f) * 43758.5453f; return x - Mathf.Floor(x); }

    void OnDestroy() { blobs?.Destroy(); }

    void Update()
    {
        var cam = Camera.main; if (cam == null || agents.Count == 0) return;
        Vector3 cp = cam.transform.position; frame++;
        float now = Time.time, dt = Mathf.Min(Time.deltaTime, 0.1f);
        blobs.Begin(); bool anyBlob = false;
        for (int i = 0; i < agents.Count; i++)
        {
            var ag = agents[i]; var r = ag.rig;
            if (ag.walker) Walk(ag, dt);
            Vector3 at = ag.walker ? Vector3.Lerp(ag.a, ag.b, ag.x / Mathf.Max(ag.len, 0.01f)) : ag.s.pos;
            float d = (at - cp).magnitude;
            if (d > 230f) { r.Show(false); continue; }
            r.Show(true);
            r.SetLod(d > 40f ? 1 : 0);
            bool animate = d < 120f || !ag.posed;
            if (animate && d > 40f && ag.posed && ((frame + i) & 1) == 1) animate = false;
            if (animate) { Pose(ag, now); ag.posed = true; }
            if (d < 120f)
            {
                Vector3 pel = r.Pelvis; pel.y = ag.walker ? r.tr.position.y : ag.s.pos.y; // (seated: the floor under the stool)
                blobs.Set(i, pel, r.tr.forward, 0.42f * r.scale, 0.8f, ag.s.pose == 0 && !ag.walker ? 0.45f : 0.7f); anyBlob = true;
            }
        }
        blobs.Flush(); blobs.Show(anyBlob);
    }

    // walking up and down the garage fronts; turning back before a box with a stop on, a short pause at each end
    void Walk(Agent ag, float dt)
    {
        if (ag.pause > 0f) { ag.pause -= dt; return; }
        int ab = PitCrew.ActiveBay;
        if (ab >= 0 && ab < Trackside.Bays.Count)
        {
            Vector3 c = Trackside.Bays[ab].centre, ax = (ag.b - ag.a) / Mathf.Max(ag.len, 0.01f);
            float sb = Vector3.Dot(c - ag.a, ax), lat = (c - ag.a - ax * sb).magnitude;
            if (lat < 9f && sb > -8f && sb < ag.len + 8f)
            {
                float ahead = (sb - ag.x) * ag.dir;
                if (ahead > 0f && ahead < 7.5f) { ag.dir = -ag.dir; ag.pause = 0.6f; }
            }
        }
        float step = ag.sp * dt;
        ag.x += ag.dir * step; ag.dist += step;
        if (ag.x >= ag.len) { ag.x = ag.len; ag.dir = -1f; ag.pause = 1.5f + 2f * Hash(ag.s.seed, (int)ag.dist); }
        else if (ag.x <= 0f) { ag.x = 0f; ag.dir = 1f; ag.pause = 1.5f + 2f * Hash(ag.s.seed, (int)ag.dist + 1); }
    }

    void Pose(Agent ag, float now)
    {
        var r = ag.rig; float t = now + ag.s.seed * 0.731f;
        if (ag.walker)
        {
            Vector3 p = Vector3.Lerp(ag.a, ag.b, ag.x / Mathf.Max(ag.len, 0.01f)); p.y = WorldBuilder.H(p.x, p.z);
            Vector3 f = (ag.b - ag.a) * ag.dir; float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            r.Place(p, yaw);
            if (ag.pause > 0f) People.Sample(People.Idle, t, buf);
            else People.Sample(People.Walk, ag.dist / (1.66f * ag.sp / 1.25f) * People.Length(People.Walk), buf);
            r.Apply(buf);
            return;
        }
        var s = ag.s; Vector3 pos = s.pos; Vector3 fw = s.fwd; fw.y = 0; if (fw.sqrMagnitude < 1e-6f) fw = Vector3.forward; fw.Normalize();
        float yw = Mathf.Atan2(fw.x, fw.z) * Mathf.Rad2Deg;
        switch (s.pose)
        {
            case 0: // seated on a stool (pos = the stool's foot): the pelvis over the seat
            {
                r.Place(pos + fw * (0.3f * r.scale) + Vector3.up * 0.17f, yw);
                // now and then talking on the radio
                float talk = Mathf.Clamp01(Mathf.Sin(t * 0.21f) * 3f - 1.2f);
                People.Sample(People.Sit, t, buf);
                if (talk > 0f) { People.Sample(People.SitTalk, t, buf2); People.Blend(buf, buf2, talk, buf); }
                break;
            }
            case 1: r.Place(pos, yw); People.Sample(People.FoldArms, t, buf); break;
            case 2: r.Place(pos, yw); People.Sample(s.kind == 1 ? People.Headset : People.Talk, t, buf); break;
            default: r.Place(pos, yw); People.Sample(People.Interact, t, buf); break;
        }
        r.Apply(buf);
    }
}
