// TimeOfDay.cs — the time-of-day setting (Morning / Afternoon / Evening / Night), saved as "timeOfDay".
// Night is for street circuits only (floodlit, like a real night race); elsewhere it falls back to Evening.
// It is read when a circuit is built (Sky.Setup: sun angle and colour, sky, fog, grade; LightBake: the bake), so a
// change takes effect at the next load of the circuit.
using UnityEngine;

public static class TimeOfDay
{
    public enum Kind { Morning, Afternoon, Evening, Night }
    public static readonly string[] Names = { "Morning", "Afternoon", "Evening", "Night" };
    public const int Default = 1;                          // Afternoon = the game's original light

    static int idx = -1;
    public static int Index
    {
        get { if (idx < 0) idx = Mathf.Clamp(PlayerPrefs.GetInt("timeOfDay", Default), 0, Names.Length - 1); return idx; }
        set { idx = Mathf.Clamp(value, 0, Names.Length - 1); PlayerPrefs.SetInt("timeOfDay", idx); }
    }

    // the time used on a circuit: Night only on street circuits
    public static Kind For(bool street) => Index == (int)Kind.Night && !street ? Kind.Evening : (Kind)Index;
    public static int Count(bool street) => street ? Names.Length : Names.Length - 1;

    // what the circuit that is built now uses (set by Sky.Setup / Sky.Create)
    public static Kind Current = Kind.Afternoon;
    public static bool Night => Current == Kind.Night;
    public static GameObject BuiltRoot; public static bool BuiltStreet;

    // Changes the setting. If the circuit that is built now would look different, its world is thrown away, so
    // GameManager.LoadMap builds it again (it rebuilds whenever its world is gone). now = destroy at once (in a
    // session, right before LoadMap); otherwise at the end of the frame (start menu: rebuilt when the session starts).
    public static void Set(int v, bool now)
    {
        Index = v;
        if (BuiltRoot == null || For(BuiltStreet) == Current) return;
        if (now) Object.DestroyImmediate(BuiltRoot); else Object.Destroy(BuiltRoot);
        BuiltRoot = null;
    }
}
