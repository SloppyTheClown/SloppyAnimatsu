namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// SloppyAnimatsu's own material members: a texture swap list on
/// <c>CMaterial</c> and stepped texture-swap keys on <c>CAnimation</c>. They are extra members of
/// existing classes, typed with types the app already reads, so the app loads the scene and skips
/// them; no class or struct is added.
/// </summary>
public static class MaterialSchema
{
    public const string MaterialClass = "CMaterial";
    public const string AnimationClass = "CAnimation";

    /// <summary>The swap list: <c>CTexture</c>s under <c>__Textures</c>, in display order.</summary>
    public const string TexturesMember = "MyAkTextures";
    public const string TexturesType = "RefArray<class CTexture>";
    /// <summary>Display names, parallel to <see cref="TexturesMember"/>; empty means "the file name".</summary>
    public const string NamesMember = "MyAkTextureNames";
    public const string NamesType = "vector<QString>";
    /// <summary>The rest index into the swap list (shown when no key applies).</summary>
    public const string ActiveMember = "MyAkActiveTexture";
    /// <summary>
    /// Alpha is ignored when true. Stored inverted so the default (false) keeps the texture's alpha,
    /// which is how a material without the member has always been drawn.
    /// </summary>
    public const string OpaqueMember = "MyAkOpaque";
    /// <summary>Nearest-texel sampling (Blender's "Closest"), for pixel-art textures.</summary>
    public const string NearestMember = "MyAkNearestFilter";

    /// <summary>Texture keys on <c>CAnimation</c>: three parallel arrays (material, frame, swap-list index).</summary>
    public const string KeyMaterialsMember = "MyAkTextureKeyMaterials";
    public const string KeyMaterialsType = "RefArray<class CMaterial>";
    public const string KeyTimesMember = "MyAkTextureKeyTimes";
    public const string KeyIndicesMember = "MyAkTextureKeyIndices";

    /// <summary>Compensate Scale keys on <c>CAnimation</c>: three parallel arrays (object, frame, 0/1).</summary>
    public const string CompensateTargetsMember = "MyAkCompensateKeyTargets";
    public const string CompensateTargetsType = "RefArray<class CObject3D>";
    public const string CompensateFramesMember = "MyAkCompensateKeyTimes";
    public const string CompensateValuesMember = "MyAkCompensateKeyValues";

    /// <summary>
    /// The Anim Bank's "Added" sort on <c>CAnimation</c>: a sequence number (1, 2, ...) given to the
    /// bank's unnumbered animations, in bank order, before the bank is rearranged; 0 means none yet
    /// (listed after the numbered ones, in bank order). See <c>AnimBankView</c>.
    /// </summary>
    public const string AddedOrderMember = "MyAkAddedOrder";

    public const string SceneClass = "CScene";
    /// <summary>
    /// On <c>CScene</c>: whether the main view was flat (orthographic) when saved. Its pose is in the
    /// Perspective camera (<c>CameraList[0]</c>), which plain .akt readers understand too.
    /// </summary>
    public const string ViewFlatMember = "MyAkViewFlat";

    /// <summary>
    /// Plugin data on <c>CScene</c>: one row per plugin that stored something. Ids and texts are
    /// parallel; the references of every row are in one array, with the owning row's index beside
    /// each in <see cref="PluginRefOwnersMember"/>. See <c>PluginSceneData</c>.
    /// </summary>
    public const string PluginIdsMember = "MyAkPluginIds";
    public const string PluginDataMember = "MyAkPluginData";
    public const string PluginRefsMember = "MyAkPluginRefs";
    public const string PluginRefsType = "RefArray<class CDataObject>";
    public const string PluginRefOwnersMember = "MyAkPluginRefOwners";

    /// <summary>
    /// Shared Anim Banks on <c>CScene</c>: the linked characters with the group number of each, and
    /// the bank folders/animations with the row number of each (one row = the same item in every
    /// member of a group). See <c>SharedAnimBanks</c>.
    /// </summary>
    public const string SharedBankCharactersMember = "MyAkSharedBankCharacters";
    public const string SharedBankGroupsMember = "MyAkSharedBankGroups";
    public const string SharedBankItemsMember = "MyAkSharedBankItems";
    public const string SharedBankItemRowsMember = "MyAkSharedBankItemRows";

    /// <summary>
    /// Paint &amp; Check's pose on <c>CScene</c>: the posed controllers, and ten floats per controller
    /// (translation xyz, rotation quaternion xyzw, scale xyz). See <c>CheckPoseStore</c>.
    /// </summary>
    public const string CheckPoseTargetsMember = "MyAkCheckPoseTargets";
    public const string CheckPoseValuesMember = "MyAkCheckPoseValues";
    public const string CheckPoseFeetMember = "MyAkCheckPoseFeet";
    public const string CheckPoseFootAnglesMember = "MyAkCheckPoseFootAngles";

    /// <summary>Appends the members the scene's schema lacks; returns whether anything was added.</summary>
    public static bool Ensure(AktSchema schema)
    {
        bool added = false;
        if (schema.FindClass(MaterialClass) is { } material)
        {
            added |= Add(material, TexturesMember, TexturesType);
            added |= Add(material, NamesMember, NamesType);
            added |= Add(material, ActiveMember, "int");
            added |= Add(material, OpaqueMember, "bool");
            added |= Add(material, NearestMember, "bool");
        }
        if (schema.FindClass(AnimationClass) is { } animation)
        {
            added |= Add(animation, KeyMaterialsMember, KeyMaterialsType);
            added |= Add(animation, KeyTimesMember, "vector<int>");
            added |= Add(animation, KeyIndicesMember, "vector<int>");
            added |= Add(animation, CompensateTargetsMember, CompensateTargetsType);
            added |= Add(animation, CompensateFramesMember, "vector<int>");
            added |= Add(animation, CompensateValuesMember, "vector<int>");
            added |= Add(animation, AddedOrderMember, "int");
        }
        if (schema.FindClass(SceneClass) is { } scene)
        {
            added |= Add(scene, ViewFlatMember, "bool");
            added |= Add(scene, PluginIdsMember, NamesType);
            added |= Add(scene, PluginDataMember, NamesType);
            added |= Add(scene, PluginRefsMember, PluginRefsType);
            added |= Add(scene, PluginRefOwnersMember, "vector<int>");
            added |= Add(scene, SharedBankCharactersMember, PluginRefsType);
            added |= Add(scene, SharedBankGroupsMember, "vector<int>");
            added |= Add(scene, SharedBankItemsMember, PluginRefsType);
            added |= Add(scene, SharedBankItemRowsMember, "vector<int>");
            added |= Add(scene, CheckPoseTargetsMember, PluginRefsType);
            added |= Add(scene, CheckPoseValuesMember, "vector<float>");
            added |= Add(scene, CheckPoseFeetMember, PluginRefsType);
            added |= Add(scene, CheckPoseFootAnglesMember, "vector<float>");
        }
        return added;
    }

    /// <summary>
    /// <see cref="Ensure"/>, then gives every material and animation under <paramref name="roots"/>
    /// a value for each declared member (the writer needs one per member).
    /// </summary>
    public static void EnsureFields(AktSchema schema, IEnumerable<AktObject> roots)
    {
        if (!Ensure(schema) && Filled.TryGetValue(schema, out _)) return;
        foreach (var root in roots)
            foreach (var obj in root.PreOrder())
                if (obj.ClassKey is MaterialClass or AnimationClass or SceneClass)
                    AktSchemaMerge.FillMissingMembers(schema, obj);
        Filled.AddOrUpdate(schema, Filled);
    }

    // Schemas whose objects were filled once; a later new material or animation is filled by its
    // creator through AktSchema.NewObject, which gives every declared member a value.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AktSchema, object> Filled = new();

    private static bool Add(AktClassDef def, string name, string type)
    {
        if (def.Members.Any(m => m.Name == name)) return false;
        def.Members.Add(new AktMember(name, type));
        return true;
    }
}
