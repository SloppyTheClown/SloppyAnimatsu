namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// Adds <c>CRetargetingMatching</c> to a scene that never declared it
/// a <c>CDataObject</c> child of a character
/// carrying the target character and the two parallel object lists. The member layout, order and
/// spellings are the ones <c>1_InitialImport.akt</c> declares - the only sample whose schema has the
/// class, and the shape the writer produced for its retarget.
/// </summary>
public static class RetargetingMatchingSchema
{
    public const string Class = "CRetargetingMatching";

    /// <summary>
    /// Adds the class under <c>CDataObject</c> when the scene lacks it, and returns whether it was
    /// added. A scene without <c>CDataObject</c> cannot hold one and throws.
    /// </summary>
    public static bool Ensure(AktSchema schema)
    {
        if (schema.FindClass(Class) != null) return false;
        var parent = schema.FindClass("CDataObject")
                     ?? throw new InvalidOperationException(
                         "The scene schema has no CDataObject to derive CRetargetingMatching from.");
        var def = new AktClassDef(Class);
        def.Members.Add(new AktMember("TargetCharacter", "Reference<class CCharacter>"));
        def.Members.Add(new AktMember("Sources", "RefArray<class CDataObject>"));
        def.Members.Add(new AktMember("Targets", "RefArray<class CDataObject>"));
        def.Members.Add(new AktMember("NullIndices", "vector<uint>"));
        def.Members.Add(new AktMember("IsLastUsedRetargetingMatching", "bool"));
        schema.AddClass(def, parent);
        return true;
    }
}