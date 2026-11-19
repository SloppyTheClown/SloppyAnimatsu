using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>
/// The class and struct catalogue an imported scene is written with
/// The member lists are the ones in
/// only what an import can create is included, because the
/// <c>.akt</c> writer emits the classes actually used and the structs they reach.
/// </summary>
public static class FbxImportSchema
{
    public static AktSchema Create()
    {
        var schema = new AktSchema();

        void Class(string name, string? parent, params (string Name, string Type)[] members)
        {
            var def = new AktClassDef("class " + name);
            foreach (var (member, type) in members) def.Members.Add(new AktMember(member, type));
            schema.AddClass(def, parent == null ? null : schema.FindClass("class " + parent));
        }

        void Struct(string name, params (string Name, string Type)[] members)
        {
            var def = new AktStructDef("class " + name);
            foreach (var (member, type) in members) def.Members.Add(new AktMember(member, type));
            schema.AddStruct(def);
        }

        // ---- objects ---------------------------------------------------------------------
        Class("CDataObject", null, ("Name", "CAtom"));
        Class("CDummy", "CDataObject");
        Class("CObject3D", "CDataObject", ("Flags", "uint"), ("LTM", "Mat4f"), ("Constraint", "Reference<class CController3D>"));
        Class("CObject3DHiddenInTree", "CObject3D", ("HierarchyChangedNotifier", "bool"));

        Class("CScene", "CDataObject",
            ("CurrentCharacter", "Reference<class CCharacter>"), ("CurrentBlendshapeMesh", "Reference<class CMesh>"),
            ("InterfaceContextIndex", "uchar"), ("FrameRate", "uint"), ("FrameRateIndex", "int"),
            ("SizeUnit", "int"), ("GridSize", "uint"), ("UnitSubdivision", "uint"),
            ("JointRenderSize", "float"), ("JointRenderSizeHighDensity", "float"),
            ("PivotRenderSize", "float"), ("UnselectedPivotRenderSize", "float"), ("TargetRenderSize", "float"),
            ("BankAngleMax", "float"), ("TwistAngleMax", "float"), ("RollAngleBackMax", "float"),
            ("RollAnglePreMax", "float"), ("RollAngleToesMax", "float"), ("RollAngleFullMax", "float"),
            ("SnapAngleThreshold", "float"), ("LayoutIndex", "int"), ("ToggledLayoutIndex", "int"),
            ("ToggledLocalViewIndex", "int"), ("CameraList", "RefArray<class CCameraBase>"),
            ("ViewCamIndices", "vector<int>"), ("SavePath", "QString"), ("FBXPath", "QString"),
            ("AKNMPath", "QString"), ("Time", "int"));

        Class("CCameraBase", "CObject3D",
            ("CameraType", "int"), ("RenderMode", "uchar"), ("RenderFlags", "uint"),
            ("_matrix", "Mat4f"), ("_targetDistance", "float"));
        Class("CCamera", "CCameraBase", ("_ratio", "float"));
        Class("CCameraOrtho", "CCameraBase",
            ("CentimetersInHeight", "float"), ("WorldOffsetX", "float"), ("WorldOffsetY", "float"));

        Class("CCharacter", "CObject3D",
            ("Controllers", "RefArray<class CObject3D>"), ("CurrentAnim", "Reference<class CAnimation>"),
            ("JointArray", "RefArray<class CJoint>"), ("CurrentPose", "Reference<class CCharacterPose>"),
            ("RefPose", "Reference<class CCharacterPose>"), ("RefPoseOld", "Reference<class CCharacterPose>"),
            ("AudioObject", "Reference<class CAudioObject>"), ("StartMode", "bool"),
            ("HiddenVerticesCounter", "uint"), ("RigidModified", "bool"), ("SmoothModified", "bool"));
        Class("CJoint", "CObject3D", ("SmoothMethod", "int"));

        Class("CMesh", "CObject3D",
            ("VertexArray", "vector<Vec4f>"), ("PolygonArray", "vector<CPolygon>"),
            ("VertexIArray", "vector<uint>"),
            ("NormalArray", "vector<Vec4f>"), ("NormalIArray", "vector<uint>"), ("NormalLayersCount", "uint"),
            ("TangentArray", "vector<Vec4f>"), ("TangentIArray", "vector<uint>"), ("TangentLayersCount", "uint"),
            ("BinormalArray", "vector<Vec4f>"), ("BinormalIArray", "vector<uint>"), ("BinormalLayersCount", "uint"),
            ("SmoothingArray", "vector<int>"), ("SmoothingLayersCount", "uint"),
            ("ColorArray", "vector<Vec4f>"), ("ColorIArray", "vector<uint>"), ("ColorLayersCount", "uint"),
            ("UVArray", "vector<Vec2f>"), ("UVIArray", "vector<uint>"), ("UVLayersCount", "uint"),
            ("UVLayersNameArray", "vector<QString>"), ("MaterialArray", "RefArray<class CMaterial>"),
            ("SkinnedMeshIsLocked", "bool"), ("SkinnedWithAkeytsu", "bool"),
            ("SmoothSkinningProcessed", "bool"), ("Skinned", "bool"),
            ("SkinWeightArray", "vector<CVertexWeightArray>"), ("RigidSkinWeightArray", "vector<CVertexWeightArray>"),
            ("BindPose", "Reference<class CCharacterPose>"), ("SavedSelectedVerticesToHide", "vector<uint>"),
            ("BlendshapeTargetArray", "RefArray<class CBlendshape>"),
            ("BlendshapeCameraPositionFromAnchor", "Mat4f"), ("BlendshapeCameraDistanceFromAnchor", "float"),
            ("BlendshapeFaces", "RefArray<class CBlendshapeFace>"));

        Class("CMaterial", "CDataObject",
            ("Ambient", "CColorComponent"), ("Diffuse", "CColorComponent"), ("Specular", "CColorComponent"),
            ("Emissive", "CColorComponent"), ("Transparency", "CColorComponent"), ("NormalMap", "CColorComponent"),
            ("Bump", "CColorComponent"), ("Displacement", "CColorComponent"), ("VectorDisplacement", "CColorComponent"),
            ("Reflection", "CColorComponent"),
            ("AmbientFactor", "CScalarComponent"), ("DiffuseFactor", "CScalarComponent"),
            ("SpecularFactor", "CScalarComponent"), ("EmissiveFactor", "CScalarComponent"),
            ("TransparencyFactor", "CScalarComponent"), ("BumpFactor", "CScalarComponent"),
            ("DisplacementFactor", "CScalarComponent"), ("VectorDisplacementFactor", "CScalarComponent"),
            ("ReflectionFactor", "CScalarComponent"), ("Shininess", "CScalarComponent"));
        Class("CTexture", "CDataObject", ("Data", "vector<uchar>"));

        Class("CCharacterPose", "CDataObject", ("Matrices", "vector<Mat4f>"));
        Class("CAnimation", "CDataObject",
            ("StackList", "RefArray<class CAnimStack>"), ("CurrentStack", "Reference<class CAnimStack>"),
            ("CurrentTime", "int"));
        Class("CAnimStack", "CDataObject",
            ("TimeArray", "vector<CAnimStackKey>"), ("Flags", "uint"), ("FlagsNoModify", "uint"),
            ("Color", "RGBAi"), ("Opacity", "float"), ("Tweenage", "float"),
            ("SpaceSwitchingStack", "Reference<class CAnimStackSpaceSwitching>"));
        Class("CAnimTrackBase", "CDataObject",
            ("ChannelMask", "uint"), ("Target", "Reference<class CObject3D>"),
            ("KeyList", "vector<Key>"), ("TInList", "vector<Key>"), ("TOutList", "vector<Key>"),
            ("TangentInfoArray", "vector<TangentInfoList>"));
        Class("CAnimTrackMRS", "CAnimTrackBase");

        // ---- structs ---------------------------------------------------------------------
        Struct("CAnimStackKey", ("Time", "int"), ("Flags", "uint"), ("FadeIn", "ushort"), ("FadeOut", "ushort"));
        Struct("CColorComponent", ("Value", "RGBAf"), ("Texture", "Reference<class CTexture>"));
        Struct("CScalarComponent", ("Value", "float"), ("Texture", "Reference<class CTexture>"));
        Struct("CPolygon", ("IndexArray", "vector<uint>"), ("MatIndex", "uint"));
        Struct("CVertexWeight", ("BoneIndex", "uint"), ("Weight", "float"));
        Struct("CVertexWeightArray", ("WeightArray", "vector<CVertexWeight>"));

        Struct("CKeyBase");
        Struct("CMRSKey",
            ("TranslationX", "float"), ("TranslationY", "float"), ("TranslationZ", "float"),
            ("Rotation", "Quatf"), ("ScaleX", "float"), ("ScaleY", "float"), ("ScaleZ", "float"));
        Struct("CKeyMRS", ("Key", "CMRSKey"), ("Space", "Reference<class CObject3D>"),
            ("TrackMRS", "Reference<class CAnimTrackMRS>"));
        Struct("CSmartPointer<class CKeyBase>");

        Struct("CTangentInfo", ("TangentMode", "uint"));
        Struct("CTangentInfoListBase", ("TangentInfoChannels", "vector<CTangentInfo>"));
        Struct("CTangentInfoListMRS");
        Struct("CSmartPointer<class CTangentInfoListBase>");

        return schema;
    }

    /// <summary>The colour every channel starts at (the SDK's phong defaults, as in the samples).</summary>
    public static readonly (string Channel, float R, float G, float B, float A)[] ColorDefaults =
    [
        ("Ambient", 0.05088f, 0.05088f, 0.05088f, 1f),
        ("Diffuse", 0.8f, 0.8f, 0.8f, 1f),
        ("Specular", 0.8f, 0.8f, 0.8f, 1f),
        ("Emissive", 1f, 1f, 1f, 1f),
        ("Transparency", 0f, 0f, 0f, 1f),
        ("NormalMap", 0f, 0f, 0f, 1f),
        ("Bump", 0f, 0f, 0f, 1f),
        ("Displacement", 0f, 0f, 0f, 1f),
        ("VectorDisplacement", 0f, 0f, 0f, 1f),
        ("Reflection", 0.8f, 0.8f, 0.8f, 1f),
    ];

    /// <summary>The factor every scalar channel starts at (the SDK's phong defaults, as in the samples).</summary>
    public static readonly (string Channel, float Value)[] ScalarDefaults =
    [
        ("AmbientFactor", 0f),
        ("DiffuseFactor", 1f),
        ("SpecularFactor", 0.25f),
        ("EmissiveFactor", 0f),
        ("TransparencyFactor", 0f),
        ("BumpFactor", 0f),
        ("DisplacementFactor", 1f),
        ("VectorDisplacementFactor", 0f),
        ("ReflectionFactor", 0f),
        ("Shininess", 25f),
    ];
}
