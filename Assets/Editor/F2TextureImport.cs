// F2TextureImport.cs — import settings for the photo textures in Assets/Resources/Photo (graphics account; Docs/ASSETS.md
// "Photo textures", made by Tools/Photo/make_photos.py). The project has no .meta files in git and no editor session, so
// the build's import is where compression, mips and filtering are decided; this postprocessor sets them by file name:
//   default        sRGB albedo, mips (box), trilinear, aniso 4, repeat; Android ASTC 6x6
//   *_big*         big tiling surfaces: ASTC 8x8
//   *_hq* / *_ui*  smooth gradients or UI-like art: ASTC 4x4 (_ui: no mips, clamp)
//   *_a8*          anisotropic level 8 (*_a2*: 2)
//   *_lin*         linear data (sRGB off)
//   *_cut*         alpha-tested cut-outs: clamp, mips keep the alpha coverage at 0.5
//   sky_*          sky panoramas: no mips (always magnified on screen), bilinear, U repeat / V clamp, aniso 0, up to 8192
// WebGL keeps the project's own compressed format (DXT / BC), the same sizes. Nothing else is touched.
using UnityEditor;
using UnityEngine;

public class F2TextureImport : AssetPostprocessor
{
    public const string Folder = "Assets/Resources/Photo/";

    public override uint GetVersion() => 2;

    void OnPreprocessTexture()
    {
        if (!assetPath.Replace('\\', '/').StartsWith(Folder)) return;
        var ti = (TextureImporter)assetImporter;
        string n = System.IO.Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
        bool sky = n.StartsWith("sky_"), ui = n.Contains("_ui"), hq = n.Contains("_hq"), big = n.Contains("_big");
        bool linear = n.Contains("_lin"), cut = n.Contains("_cut");
        int aniso = sky ? 0 : n.Contains("_a8") ? 8 : n.Contains("_a2") ? 2 : 4;

        ti.textureType = TextureImporterType.Default;
        ti.textureShape = TextureImporterShape.Texture2D;
        ti.sRGBTexture = !linear;
        ti.isReadable = false;
        ti.alphaSource = TextureImporterAlphaSource.FromInput;
        ti.alphaIsTransparency = cut;
        ti.mipmapEnabled = !sky && !ui;
        ti.mipmapFilter = TextureImporterMipFilter.BoxFilter;
        ti.mipMapsPreserveCoverage = cut;
        ti.alphaTestReferenceValue = 0.5f;
        ti.streamingMipmaps = false;
        ti.npotScale = TextureImporterNPOTScale.ToNearest;
        ti.maxTextureSize = sky ? 8192 : 4096;
        ti.filterMode = sky || ui ? FilterMode.Bilinear : FilterMode.Trilinear;
        ti.anisoLevel = aniso;
        if (sky) { ti.wrapModeU = TextureWrapMode.Repeat; ti.wrapModeV = TextureWrapMode.Clamp; ti.wrapModeW = TextureWrapMode.Clamp; }
        else ti.wrapMode = cut || ui ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
        ti.textureCompression = TextureImporterCompression.Compressed;

        var android = new TextureImporterPlatformSettings
        {
            name = "Android",
            overridden = true,
            maxTextureSize = sky ? 8192 : 4096,
            format = hq || ui ? TextureImporterFormat.ASTC_4x4 : big ? TextureImporterFormat.ASTC_8x8 : TextureImporterFormat.ASTC_6x6,
            textureCompression = TextureImporterCompression.CompressedHQ,
            compressionQuality = 100,
            resizeAlgorithm = TextureResizeAlgorithm.Mitchell,
        };
        ti.SetPlatformTextureSettings(android);
        var webgl = new TextureImporterPlatformSettings
        {
            name = "WebGL",
            overridden = true,
            maxTextureSize = sky ? 8192 : 4096,
            format = TextureImporterFormat.Automatic,
            textureCompression = TextureImporterCompression.Compressed,
            compressionQuality = 50,
            resizeAlgorithm = TextureResizeAlgorithm.Mitchell,
        };
        ti.SetPlatformTextureSettings(webgl);
    }
}
