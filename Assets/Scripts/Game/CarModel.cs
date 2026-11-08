// CarModel.cs — placeholder formula car built from primitives (forward = +z, right = +x, origin at CG on the ground).
// Will be replaced by a proper model in the graphics phase; dimensions follow the F2 car.
using UnityEngine;

public class CarModel : MonoBehaviour
{
    public Transform body;
    public Transform[] frontPivots = new Transform[2], frontSpin = new Transform[2], rearSpin = new Transform[2];
    public Transform helmet;

    public static CarModel Create(double wheelbase, double frontWeight, double wheelRadius)
    {
        var root = new GameObject("Car");
        var cm = root.AddComponent<CarModel>();
        cm.body = new GameObject("Body").transform; cm.body.SetParent(root.transform, false);
        var paint = WorldBuilder.Mat(new Color(0.12f, 0.71f, 0.66f), null, 0.75f, 0.3f);
        var orange = WorldBuilder.Mat(new Color(1f, 0.48f, 0.1f), null, 0.6f, 0.2f);
        var carbon = WorldBuilder.Mat(new Color(0.1f, 0.11f, 0.13f), null, 0.5f);
        float L = (float)wheelbase, b = L * (float)frontWeight, a = L - b, R = (float)wheelRadius;

        void Part(PrimitiveType t, Vector3 pos, Vector3 size, Material m, Vector3 euler = default)
        {
            var p = GameObject.CreatePrimitive(t); Destroy(p.GetComponent<Collider>());
            p.transform.SetParent(cm.body, false); p.transform.localPosition = pos; p.transform.localScale = size; p.transform.localEulerAngles = euler;
            p.GetComponent<MeshRenderer>().sharedMaterial = m;
        }
        Part(PrimitiveType.Cube, new Vector3(0, 0.36f, 0.3f), new Vector3(0.62f, 0.42f, 2.6f), paint);      // tub
        Part(PrimitiveType.Cube, new Vector3(0, 0.27f, 2.15f), new Vector3(0.34f, 0.26f, 1.5f), paint);     // nose
        Part(PrimitiveType.Cube, new Vector3(0.56f, 0.32f, -0.15f), new Vector3(0.42f, 0.36f, 1.7f), paint); // sidepods
        Part(PrimitiveType.Cube, new Vector3(-0.56f, 0.32f, -0.15f), new Vector3(0.42f, 0.36f, 1.7f), paint);
        Part(PrimitiveType.Cube, new Vector3(0, 0.58f, -1.15f), new Vector3(0.44f, 0.46f, 1.5f), paint);    // engine cover
        Part(PrimitiveType.Cube, new Vector3(0, 0.1f, 2.8f), new Vector3(1.9f, 0.05f, 0.5f), orange);       // front wing
        Part(PrimitiveType.Cube, new Vector3(0.95f, 0.2f, 2.78f), new Vector3(0.04f, 0.22f, 0.55f), carbon);
        Part(PrimitiveType.Cube, new Vector3(-0.95f, 0.2f, 2.78f), new Vector3(0.04f, 0.22f, 0.55f), carbon);
        Part(PrimitiveType.Cube, new Vector3(0, 0.98f, -2.3f), new Vector3(1.05f, 0.07f, 0.42f), orange);   // rear wing
        Part(PrimitiveType.Cube, new Vector3(0, 0.84f, -2.4f), new Vector3(1.05f, 0.08f, 0.25f), orange);
        Part(PrimitiveType.Cube, new Vector3(0.53f, 0.75f, -2.3f), new Vector3(0.04f, 0.6f, 0.5f), carbon);
        Part(PrimitiveType.Cube, new Vector3(-0.53f, 0.75f, -2.3f), new Vector3(0.04f, 0.6f, 0.5f), carbon);
        Part(PrimitiveType.Cube, new Vector3(0, 0.06f, -0.3f), new Vector3(1.4f, 0.04f, 0.9f), carbon);     // floor
        Part(PrimitiveType.Cylinder, new Vector3(0, 0.66f, 0.42f), new Vector3(0.62f, 0.03f, 0.62f), carbon); // halo ring (approx.)
        {
            var h = GameObject.CreatePrimitive(PrimitiveType.Sphere); Destroy(h.GetComponent<Collider>());
            h.transform.SetParent(cm.body, false); h.transform.localPosition = new Vector3(0, 0.72f, 0.1f); h.transform.localScale = Vector3.one * 0.32f;
            h.GetComponent<MeshRenderer>().sharedMaterial = WorldBuilder.Mat(new Color(1f, 0.82f, 0.25f), null, 0.8f);
            cm.helmet = h.transform;
        }
        var tyre = WorldBuilder.Mat(new Color(0.08f, 0.08f, 0.08f), null, 0.15f);
        var rim = WorldBuilder.Mat(new Color(0.6f, 0.64f, 0.68f), null, 0.7f, 0.7f);
        Transform Wheel(float x, float z, float width, out Transform spin)
        {
            var pivot = new GameObject("WheelPivot").transform; pivot.SetParent(root.transform, false); pivot.localPosition = new Vector3(x, R, z);
            spin = new GameObject("Spin").transform; spin.SetParent(pivot, false);
            var t = GameObject.CreatePrimitive(PrimitiveType.Cylinder); Destroy(t.GetComponent<Collider>());
            t.transform.SetParent(spin, false); t.transform.localEulerAngles = new Vector3(0, 0, 90); t.transform.localScale = new Vector3(2 * R, width / 2, 2 * R);
            t.GetComponent<MeshRenderer>().sharedMaterial = tyre;
            var r = GameObject.CreatePrimitive(PrimitiveType.Cylinder); Destroy(r.GetComponent<Collider>());
            r.transform.SetParent(spin, false); r.transform.localEulerAngles = new Vector3(0, 0, 90); r.transform.localScale = new Vector3(1.24f * R, width / 2 + 0.01f, 1.24f * R);
            r.GetComponent<MeshRenderer>().sharedMaterial = rim;
            var s = GameObject.CreatePrimitive(PrimitiveType.Cube); Destroy(s.GetComponent<Collider>());
            s.transform.SetParent(spin, false); s.transform.localScale = new Vector3(width + 0.04f, 0.06f, R * 1.1f);
            s.GetComponent<MeshRenderer>().sharedMaterial = orange;
            return pivot;
        }
        cm.frontPivots[0] = Wheel(0.8f, a, 0.3f, out cm.frontSpin[0]);
        cm.frontPivots[1] = Wheel(-0.8f, a, 0.3f, out cm.frontSpin[1]);
        Wheel(0.78f, -b, 0.4f, out cm.rearSpin[0]);
        Wheel(-0.78f, -b, 0.4f, out cm.rearSpin[1]);
        return cm;
    }

    // physics -> visuals. heading: rad CCW from +x (physics); delta: + = left; spin: wheel angle (rad)
    public void Apply(double x, double y, double heading, double delta, double frontSpinRad, double rearSpinRad, double axLong, double ay)
    {
        transform.position = new Vector3((float)x, 0, (float)y);
        transform.rotation = Quaternion.Euler(0, 90f - (float)(heading * Mathf.Rad2Deg), 0);
        // body pitch/roll from g-forces: nose dives under braking, rolls to the outside in corners
        body.localRotation = Quaternion.Euler(Mathf.Clamp((float)-axLong * 0.09f, -1.7f, 1.7f), 0, Mathf.Clamp((float)-ay * 0.07f, -1.7f, 1.7f));
        float steer = -(float)(delta * Mathf.Rad2Deg);
        foreach (var p in frontPivots) p.localRotation = Quaternion.Euler(0, steer, 0);
        foreach (var s in frontSpin) s.localRotation = Quaternion.Euler((float)(frontSpinRad * Mathf.Rad2Deg), 0, 0);
        foreach (var s in rearSpin) s.localRotation = Quaternion.Euler((float)(rearSpinRad * Mathf.Rad2Deg), 0, 0);
    }
}
