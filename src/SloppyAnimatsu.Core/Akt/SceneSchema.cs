namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// The class and struct schema of a brand-new scene. SloppyAnimatsu builds the
/// twelve classes and one struct here, with the member spellings the reference files carry, so a
/// scene created from this schema writes the same class and struct sections the reference files have.
/// </summary>
/// <remarks>
/// The members are only the ones a scene needs. Reference member types name classes the scene does
/// not contain yet (<c>CMesh</c>, <c>CJoint</c>,...), which is fine: a reference is an index and a
/// null reference names no object. Editors that add such objects extend the schema
/// (<see cref="CameraSchema"/>, <see cref="ControllerSchema"/>,...).
/// </remarks>
public static class SceneSchema
{
    public static AktSchema Create()
    {
        var schema = new AktSchema();

        var dataObject = Class(schema, "class CDataObject", null,
            ("Name", "CAtom"));

        Class(schema, "class CScene", dataObject,
            ("CurrentCharacter", "Reference<class CCharacter>"),
            ("CurrentBlendshapeMesh", "Reference<class CMesh>"),
            ("InterfaceContextIndex", "uchar"),
            ("FrameRate", "uint"),
            ("FrameRateIndex", "int"),
            ("SizeUnit", "int"),
            ("GridSize", "uint"),
            ("UnitSubdivision", "uint"),
            ("JointRenderSize", "float"),
            ("JointRenderSizeHighDensity", "float"),
            ("PivotRenderSize", "float"),
            ("UnselectedPivotRenderSize", "float"),
            ("TargetRenderSize", "float"),
            ("BankAngleMax", "float"),
            ("TwistAngleMax", "float"),
            ("RollAngleBackMax", "float"),
            ("RollAnglePreMax", "float"),
            ("RollAngleToesMax", "float"),
            ("RollAngleFullMax", "float"),
            ("SnapAngleThreshold", "float"),
            ("LayoutIndex", "int"),
            ("ToggledLayoutIndex", "int"),
            ("ToggledLocalViewIndex", "int"),
            ("CameraList", "RefArray<class CCameraBase>"),
            ("ViewCamIndices", "vector<int>"),
            ("SavePath", "QString"),
            ("FBXPath", "QString"),
            ("AKNMPath", "QString"),
            ("Time", "int"));

        Class(schema, "class CAnimStack", dataObject,
            ("TimeArray", "vector<CAnimStackKey>"),
            ("Flags", "uint"),
            ("FlagsNoModify", "uint"),
            ("Color", "RGBAi"),
            ("Opacity", "float"),
            ("Tweenage", "float"),
            ("SpaceSwitchingStack", "Reference<class CAnimStackSpaceSwitching>"));

        Class(schema, "class CAnimation", dataObject,
            ("StackList", "RefArray<class CAnimStack>"),
            ("CurrentStack", "Reference<class CAnimStack>"),
            ("CurrentTime", "int"));

        Class(schema, "class CCharacterPose", dataObject,
            ("Matrices", "vector<Mat4f>"));

        Class(schema, "class CDummy", dataObject);

        var object3D = Class(schema, "class CObject3D", dataObject,
            ("Flags", "uint"),
            ("LTM", "Mat4f"),
            ("Constraint", "Reference<class CController3D>"));

        var cameraBase = Class(schema, "class CCameraBase", object3D,
            ("CameraType", "int"),
            ("RenderMode", "uchar"),
            ("RenderFlags", "uint"),
            ("_matrix", "Mat4f"),
            ("_targetDistance", "float"));

        Class(schema, "class CCamera", cameraBase,
            ("_ratio", "float"));

        Class(schema, "class CCameraOrtho", cameraBase,
            ("CentimetersInHeight", "float"),
            ("WorldOffsetX", "float"),
            ("WorldOffsetY", "float"));

        Class(schema, "class CCharacter", object3D,
            ("Controllers", "RefArray<class CObject3D>"),
            ("CurrentAnim", "Reference<class CAnimation>"),
            ("JointArray", "RefArray<class CJoint>"),
            ("CurrentPose", "Reference<class CCharacterPose>"),
            ("RefPose", "Reference<class CCharacterPose>"),
            ("RefPoseOld", "Reference<class CCharacterPose>"),
            ("AudioObject", "Reference<class CAudioObject>"),
            ("StartMode", "bool"),
            ("HiddenVerticesCounter", "uint"),
            ("RigidModified", "bool"),
            ("SmoothModified", "bool"));

        Class(schema, "class CObject3DHiddenInTree", object3D,
            ("HierarchyChangedNotifier", "bool"));

        var stackKey = new AktStructDef("class CAnimStackKey");
        stackKey.Members.Add(new AktMember("Time", "int"));
        stackKey.Members.Add(new AktMember("Flags", "uint"));
        stackKey.Members.Add(new AktMember("FadeIn", "ushort"));
        stackKey.Members.Add(new AktMember("FadeOut", "ushort"));
        schema.AddStruct(stackKey);

        return schema;
    }

    private static AktClassDef Class(
        AktSchema schema, string name, AktClassDef? parent, params (string Name, string Type)[] members)
    {
        var def = new AktClassDef(name);
        foreach (var (memberName, type) in members)
            def.Members.Add(new AktMember(memberName, type));
        schema.AddClass(def, parent);
        return def;
    }
}
