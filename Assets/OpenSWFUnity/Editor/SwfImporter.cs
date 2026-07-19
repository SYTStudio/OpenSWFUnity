using System.IO;
using OpenSWFUnity.Runtime;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace OpenSWFUnity.Editor
{
    [ScriptedImporter(1, "swf")]
    public sealed class SwfImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            SwfAsset asset = ScriptableObject.CreateInstance<SwfAsset>();
            asset.bytes = File.ReadAllBytes(ctx.assetPath);
            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);
        }
    }
}
