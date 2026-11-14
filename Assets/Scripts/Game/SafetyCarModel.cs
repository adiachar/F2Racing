// SafetyCarModel.cs — the safety car you see: the game's car model (CarModel, through its public API only) in a
// silver livery with a light bar on the roll hoop. The lamps are unlit (Sprites/Default), so they glow like
// emissive lights in any light, and flash amber in turn while the safety car is out; they go dark on its last lap
// ("SC in this lap"), as on the real car. Where it drives comes from RaceControl.ScPose (on the racing line).
using UnityEngine;

public class SafetyCarModel : MonoBehaviour
{
    CarModel model; Material lampA, lampB; float t, spin;
    static readonly Color Amber = new Color(1f, 0.55f, 0.05f), Off = new Color(0.18f, 0.12f, 0.05f);

    public static SafetyCarModel Create(double wheelbase, double frontWeight, double wheelRadius)
    {
        var silver = new Color(0.78f, 0.80f, 0.83f);
        var m = CarModel.CreateFromAsset("Car/car_lod", silver, true) ?? CarModel.Create(wheelbase, frontWeight, wheelRadius);
        if (m == null) return null;
        m.gameObject.name = "SafetyCar";
        m.SetLivery(new Livery("Safety Car", silver, new Color(0.10f, 0.11f, 0.12f), new Color(0.85f, 0.10f, 0.10f)));
        m.SetStripe(new Color(0.85f, 0.10f, 0.10f));
        var sc = m.gameObject.AddComponent<SafetyCarModel>();
        sc.model = m; sc.BuildLights((float)wheelRadius);
        return sc;
    }

    void BuildLights(float wheelR)
    {
        var sh = Shader.Find("Sprites/Default");
        var dark = new Material(sh) { color = new Color(0.06f, 0.06f, 0.07f) };
        lampA = new Material(sh) { color = Amber }; lampB = new Material(sh) { color = Amber };
        // a bar across the top of the roll hoop / airbox, with a lamp at each end and a green one in the middle
        float y = 1.02f, z = -0.25f;
        Part(PrimitiveType.Cube, new Vector3(0, y, z), new Vector3(0.62f, 0.05f, 0.16f), dark);
        Part(PrimitiveType.Cube, new Vector3(-0.24f, y + 0.055f, z), new Vector3(0.16f, 0.07f, 0.13f), lampA);
        Part(PrimitiveType.Cube, new Vector3(0.24f, y + 0.055f, z), new Vector3(0.16f, 0.07f, 0.13f), lampB);
        Part(PrimitiveType.Cube, new Vector3(0, y + 0.045f, z), new Vector3(0.12f, 0.05f, 0.11f), new Material(sh) { color = new Color(0.1f, 0.9f, 0.3f) });
    }

    void Part(PrimitiveType type, Vector3 pos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        var col = go.GetComponent<Collider>(); if (col != null) Destroy(col);
        go.transform.SetParent(transform, false); go.transform.localPosition = pos; go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>(); r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    public void Show(bool on) { if (gameObject.activeSelf != on) gameObject.SetActive(on); }

    // pose on the track (physics x, y, heading), speed for the wheels, lights flashing or off
    public void Place(double x, double y, double heading, double v, float dt, bool lightsOn)
    {
        t += dt; spin = Mathf.Repeat(spin + Mathf.Clamp((float)(v / 0.355) * dt, -0.5f, 0.5f), Mathf.PI * 2);
        model.Apply(x, y, heading, 0, spin, spin, 0, 0);
        model.SetBrake(false);
        bool a = Mathf.Repeat(t * 3f, 1f) < 0.5f;
        lampA.color = lightsOn && a ? Amber : Off;
        lampB.color = lightsOn && !a ? Amber : Off;
    }
}
