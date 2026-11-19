namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// Adds the blendshape classes and structs a scene written before blendshapes does not declare
/// the target shape under a mesh, the controller and its channels
/// and info entries, the face thumbnail classes, the track and the two-value key.
/// </summary>
public static class BlendshapeSchema
{
    public const string TargetClass = "CBlendshape";
    public const string ChannelClass = "CBlendshapeChannel";
    public const string InfoClass = "CBlendshapeInfoBlendshape";
    public const string ControllerClass = "CBlendshapeController";
    public const string SimpleControllerClass = "CBlendshapeSimpleController";
    public const string FaceControllerClass = "CBlendshapeFaceController";
    public const string FaceClass = "CBlendshapeFace";
    public const string SightClass = "CBlendShapesSight";
    public const string ThumbnailClass = "CThumbnailData";
    public const string HiddenInTreeClass = "CObject3DHiddenInTree";
    public const string TrackClass = "CAnimTrackBlendshape";
    public const string KeyStruct = "CKeyBlendshape";
    public const string TangentListStruct = "CTangentInfoListBlendshape";

    /// <summary><c>CMesh</c>, whose three blendshape point-of-view members <see cref="EnsureMeshMembers"/> writes.</summary>
    public const string MeshClass = "CMesh";
    /// <summary>The blendshape camera's transform relative to its anchor (the spelling).</summary>
    public const string CameraPositionMember = "BlendshapeCameraPositionFromAnchor";
    /// <summary>The blendshape camera's target distance.</summary>
    public const string CameraDistanceMember = "BlendshapeCameraDistanceFromAnchor";
    /// <summary>The object the blendshape camera's transform is relative to (optional: no sample declares it).</summary>
    public const string CameraAnchorMember = "BlendshapeCameraAnchor";

    /// <summary>
    /// Adds the classes and structs needed to write blendshape targets, controllers and tracks.
    /// Returns whether anything was added. The classes are derived from whatever the scene already
    /// has (<c>CObject3D</c>, <c>CDataObject</c>, <c>CController3D</c>, <c>CAnimTrackBase</c>,
    /// <c>CKeyBase</c>, <c>CTangentInfoListBase</c>); a class the scene cannot derive from is left
    /// out rather than invented.
    /// </summary>
    public static bool Ensure(AktSchema schema)
    {
        bool added = false;
        var objectClass = schema.FindClass("CObject3D");
        var dataClass = schema.FindClass("CDataObject");
        if (objectClass == null || dataClass == null)
            throw new InvalidOperationException("The scene schema has no CObject3D/CDataObject to derive a blendshape from.");

        if (schema.FindClass(HiddenInTreeClass) == null)
        {
            var def = new AktClassDef(HiddenInTreeClass);
            def.Members.Add(new AktMember("HierarchyChangedNotifier", "bool"));
            schema.AddClass(def, objectClass);
            added = true;
        }

        if (schema.FindClass(TargetClass) == null)
        {
            var def = new AktClassDef(TargetClass);
            // The members and their order are the, read back from a scene it saved
            // No IsConnectedToAController: the app registers the name but has no
            // serializer for it, and a file that lists it crashes the reader.
            def.Members.Add(new AktMember("VertexIArray", "vector<uint>"));
            def.Members.Add(new AktMember("VertexArray", "vector<Vec4f>"));
            def.Members.Add(new AktMember("NormalArray", "vector<Vec4f>"));
            def.Members.Add(new AktMember("MaxLength", "float"));
            schema.AddClass(def, objectClass);
            added = true;
        }

        if (schema.FindClass(InfoClass) == null)
        {
            var def = new AktClassDef(InfoClass);
            def.Members.Add(new AktMember("Blendshape", $"Reference<class {TargetClass}>"));
            def.Members.Add(new AktMember("Position", "float"));
            schema.AddClass(def, dataClass);
            added = true;
        }

        if (schema.FindClass(ChannelClass) == null)
        {
            var def = new AktClassDef(ChannelClass);
            // No BlendshapeInfoArray: the app registers the name but has no serializer for it (the
            // list is derived from the channel's children), and a file that lists it crashes the
            // reader.
            def.Members.Add(new AktMember("Direction", "uint"));
            def.Members.Add(new AktMember("Attenuation", "float"));
            schema.AddClass(def, dataClass);
            added = true;
        }

        var controller3D = schema.FindClass("CController3D");
        if (controller3D == null)
        {
            var def = new AktClassDef("CController3D");
            def.Members.Add(new AktMember("TargetRenderSizeRatio", "float"));
            schema.AddClass(def, objectClass);
            controller3D = schema.FindClass("CController3D")!;
            added = true;
        }

        if (schema.FindClass(ControllerClass) == null)
        {
            var def = new AktClassDef(ControllerClass);
            // The order. No Opened: that is
            // panel state, registered without a serializer, and listing it crashes the reader.
            def.Members.Add(new AktMember("BlendshapeChannelArray", $"RefArray<class {ChannelClass}>"));
            def.Members.Add(new AktMember("Mesh", "Reference<class CMesh>"));
            def.Members.Add(new AktMember("IsUp", "bool"));
            schema.AddClass(def, controller3D!);
            added = true;
        }
        var controller = schema.FindClass(ControllerClass)!;

        if (schema.FindClass(SimpleControllerClass) == null)
        {
            schema.AddClass(new AktClassDef(SimpleControllerClass), controller);
            added = true;
        }
        if (schema.FindClass(FaceControllerClass) == null)
        {
            var def = new AktClassDef(FaceControllerClass);
            // The order.
            def.Members.Add(new AktMember("InteractiveFaces", "vector<uint>"));
            def.Members.Add(new AktMember("Click", "uint"));
            schema.AddClass(def, controller);
            added = true;
        }
        if (schema.FindClass(SightClass) == null)
        {
            schema.AddClass(new AktClassDef(SightClass), objectClass);
            added = true;
        }

        if (schema.FindStruct(KeyStruct) == null && schema.FindStruct("CKeyBase") != null)
        {
            var def = new AktStructDef(KeyStruct);
            def.Members.Add(new AktMember("_values", "vector<float>"));
            schema.AddStruct(def);
            added = true;
        }
        if (schema.FindStruct(TangentListStruct) == null && schema.FindStruct("CTangentInfoListBase") != null)
        {
            schema.AddStruct(new AktStructDef(TangentListStruct));
            added = true;
        }
        if (schema.FindClass(TrackClass) == null && schema.FindClass("CAnimTrackBase") != null)
        {
            schema.AddClass(new AktClassDef(TrackClass), schema.FindClass("CAnimTrackBase")!);
            added = true;
        }

        // Thumbnail data (a face's texture) may be absent in a scene with no textures; a face can
        // still be written, as a plain CDataObject with an Index of its own (SloppyAnimatsu choice: such
        // a face has no thumbnail picture, which every scene lacks anyway).
        if (schema.FindClass(ThumbnailClass) == null && schema.FindClass("CTexture") != null)
        {
            var def = new AktClassDef(ThumbnailClass);
            // The order.
            def.Members.Add(new AktMember("Texture", "Reference<class CTexture>"));
            def.Members.Add(new AktMember("Index", "int"));
            schema.AddClass(def, dataClass);
            added = true;
        }
        if (schema.FindClass(FaceClass) == null && schema.FindStruct(KeyStruct) != null)
        {
            var thumbnail = schema.FindClass(ThumbnailClass);
            var def = new AktClassDef(FaceClass);
            // The order.
            def.Members.Add(new AktMember("KeyList", $"vector<{KeyStruct}>"));
            def.Members.Add(new AktMember("BlendshapeFaceControllerArray", $"RefArray<class {FaceControllerClass}>"));
            if (thumbnail == null) def.Members.Add(new AktMember("Index", "int"));
            schema.AddClass(def, thumbnail ?? dataClass);
            added = true;
        }
        return added;
    }

    /// <summary>
    /// Adds the blendshape point-of-view members a scene's <c>CMesh</c> class does not declare: the
    /// camera transform relative to its anchor and the target distance, spelled and typed as the files
    /// does and in its order (<c>10_portedOldAnims.akt</c>'s <c>CMesh</c> has both and no anchor).
    /// They are appended, because the anchor's own position in the list is unknown, and the app reads
    /// a member by name from the file, so the order only changes the bytes
    /// A scene that already has them is left as
    /// it is. <paramref name="withAnchor"/> also appends <c>BlendshapeCameraAnchor</c>, which no
    /// sample scene has: its position is inferred and it is only written where a caller asks for an
    /// anchor, since a member the app registers without a serializer would crash its reader.
    /// </summary>
    public static bool EnsureMeshMembers(AktSchema schema, bool withAnchor = false)
    {
        if (schema.FindClass(MeshClass) is not { } mesh) return false;
        bool added = false;
        if (!mesh.Members.Any(m => m.Name == CameraPositionMember))
        {
            mesh.Members.Add(new AktMember(CameraPositionMember, "Mat4f"));
            added = true;
        }
        if (!mesh.Members.Any(m => m.Name == CameraDistanceMember))
        {
            mesh.Members.Add(new AktMember(CameraDistanceMember, "float"));
            added = true;
        }
        if (withAnchor && !mesh.Members.Any(m => m.Name == CameraAnchorMember))
        {
            mesh.Members.Add(new AktMember(CameraAnchorMember, "Reference<class CObject3D>"));
            added = true;
        }
        return added;
    }
}
