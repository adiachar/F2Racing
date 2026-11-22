// MapEditMarker.cs: one of Adi's edit markers in the Unity editor (F2Racing > Map Editor, Assets/Editor/F2MapEditor.cs).
// Only the editor makes them (they live in the play-mode scene while editing); the game itself never creates one.
// Its transform is the edit: position = centre (x, z), Y rotation = yaw, scale = size:
//   Remove area: scale x = width, z = depth (m); Tree: scale y x 2 = height (m); Building: scale = width, height, depth (m).
using UnityEngine;

public class MapEditMarker : MonoBehaviour
{
    public enum Kind { RemoveArea, Tree, Building }
    public enum BuildingType { Unknown = 0, Houses = 1, OfficeShops = 2, Industrial = 3, StandOrSports = 4, Civic = 5, Church = 6, GarageShed = 7, Tower = 8 }
    public enum Roof { Flat = 0, Gabled = 1, Hipped = 2, Dome = 3, Pyramid = 4 }

    public Kind kind;
    [Header("Buildings only")]
    public BuildingType buildingType = BuildingType.Houses;
    public Roof roof = Roof.Flat;
    [Tooltip("Leave alpha at 0 for the town's usual wall colours")]
    public Color wallColour = new Color(0f, 0f, 0f, 0f);
}
