using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>What a CHAR BANK row stands for.</summary>
public enum CharacterBankRowKind
{
    /// <summary>The reference pose of a character with no bind pose yet; it stands in for the bind pose.</summary>
    BuildPose,
    /// <summary>A bind pose under <c>__BindPoses</c>.</summary>
    BindPose,
    /// <summary>A custom reference pose (one that is not a bind pose).</summary>
    RefPose,
    /// <summary>Paint &amp; Check: no pose row current (<c>CurrentPose</c> null), the skinning mode.</summary>
    PaintAndCheck,
}

/// <summary>One row under a character in the CHAR BANK.</summary>
/// <param name="Kind">What the row stands for.</param>
/// <param name="Label">The row's text.</param>
/// <param name="Pose">The pose the row makes current; null for Paint &amp; Check.</param>
/// <param name="IsCurrent">Whether this row is the character's current mode.</param>
/// <param name="ActsAsRefPose">A pose that is also the reference pose (the app draws a blue figure).</param>
public sealed record CharacterBankRow(CharacterBankRowKind Kind, string Label, CharacterPose? Pose, bool IsCurrent, bool ActsAsRefPose);

/// <summary>
/// The CHAR BANK of the Rigging context (<c>CCharacterBankDialog</c>): the scene's characters, each
/// with its pose rows and Paint &amp; Check, and the Create Character / Create Ref Pose / Delete Ref
/// Pose commands. Selecting a row sets
/// <see cref="Character.CurrentPose"/>, which decides what the Rigging tools do: a pose row makes
/// TRANSFORM edits write that pose, Paint &amp; Check is where Bind Skin and the painter work
/// </summary>
public static class CharacterBank
{
    public const string PaintAndCheckLabel = "Paint & Check";

    /// <summary>The rows shown under <paramref name="character"/>, in display order.</summary>
    public static IReadOnlyList<CharacterBankRow> Rows(Character character)
    {
        var rows = new List<CharacterBankRow>();
        var current = character.CurrentPose;
        var reference = character.RefPose;
        var binds = character.BindPoses.ToList();
        if (binds.Count == 0)
        {
            if (reference != null)
                rows.Add(new(CharacterBankRowKind.BuildPose, "Build Pose", reference, ReferenceEquals(current, reference), true));
        }
        else
        {
            foreach (var bind in binds)
                rows.Add(new(CharacterBankRowKind.BindPose, string.IsNullOrEmpty(bind.Name) ? "Bind Pose" : bind.Name!, bind,
                    ReferenceEquals(current, bind), ReferenceEquals(reference, bind)));
            if (reference != null && !binds.Any(b => ReferenceEquals(b, reference)))
                rows.Add(new(CharacterBankRowKind.RefPose, "Ref Pose", reference, ReferenceEquals(current, reference), true));
        }
        bool anyCurrent = rows.Any(r => r.IsCurrent);
        rows.Add(new(CharacterBankRowKind.PaintAndCheck, PaintAndCheckLabel, null, !anyCurrent, false));
        return rows;
    }

    /// <summary>The row that is current for <paramref name="character"/>.</summary>
    public static CharacterBankRow CurrentRow(Character character) => Rows(character).First(r => r.IsCurrent);

    /// <summary>Whether Paint &amp; Check is the character's mode (no pose row current).</summary>
    public static bool IsPaintAndCheck(Character character) => CurrentRow(character).Kind == CharacterBankRowKind.PaintAndCheck;

    /// <summary>Whether a Bind Pose (or Build Pose) row is current, the mode that hides the skin weights.</summary>
    public static bool IsBindPoseCurrent(Character character) =>
        CurrentRow(character).Kind is CharacterBankRowKind.BindPose or CharacterBankRowKind.BuildPose;

    /// <summary>Makes <paramref name="row"/> the character's mode; false when it already was or cannot be.</summary>
    public static bool Select(Character character, CharacterBankRow row)
    {
        if (row.IsCurrent && ReferenceEquals(character.CurrentPose, row.Pose)) return false;
        if (!character.Source.Has("CurrentPose")) return false;
        character.CurrentPose = row.Pose;
        return true;
    }

    /// <summary>Create Ref Pose is available: the character is bound and its bind pose acts as the reference pose.</summary>
    public static bool CanCreateRefPose(Character character)
    {
        var binds = character.BindPoses.ToList();
        return binds.Count > 0 && character.RefPose is { } reference && binds.Any(b => ReferenceEquals(b, reference));
    }

    /// <summary>Delete Ref Pose is available: the character is bound and has a reference pose of its own.</summary>
    public static bool CanDeleteRefPose(Character character)
    {
        var binds = character.BindPoses.ToList();
        return binds.Count > 0 && character.RefPose is { } reference && !binds.Any(b => ReferenceEquals(b, reference));
    }

    /// <summary>
    /// Create Ref Pose: a new <c>__RefPose</c> built from the character's current state (the bind
    /// pose with the <c>__RigAnim</c> keys composed on, so a pose made in Paint &amp; Check is kept),
    /// which becomes the reference pose and the current row. <paramref name="posed"/> (Paint &amp;
    /// Check's runtime pose, <see cref="PoseEvaluator.CheckPoseChanges"/>) replaces the local
    /// transforms of the controllers it names. Returns it, or null when unavailable.
    /// </summary>
    public static CharacterPose? CreateRefPose(Character character, IReadOnlyDictionary<Object3D, Transform>? posed = null) =>
        CanCreateRefPose(character) ? new RestPoseEditor(character).CreateRefPose(posed) : null;

    /// <summary>
    /// Create Ref Pose from Paint &amp; Check: the new reference pose is exactly the pose
    /// <paramref name="check"/> shows - every controller's check local, the unposed ones at their
    /// rest - and Paint &amp; Check stays the current row with its pose, so the view does not jump
    /// back to the bind pose. Null when unavailable.
    /// </summary>
    public static CharacterPose? CreateRefPoseFromCheckPose(Character character, PoseEvaluator check)
    {
        if (!CanCreateRefPose(character)) return null;
        var shown = new Dictionary<Object3D, Transform>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < check.Controllers.Count; i++) shown[check.Controllers[i]] = check.CheckLocal(i);
        var created = new RestPoseEditor(character).CreateRefPose(shown);
        if (created != null) character.CurrentPose = null;
        return created;
    }

    /// <summary>
    /// Delete Ref Pose: the bind pose acts as the reference pose again. The custom pose is removed
    /// unless it is also <c>RefPoseOld</c>; a current row on it moves to the bind pose. The
    /// animations are resolved against the change on the way back to Animation (the REFERENCE POSE
    /// dialog, <see cref="RestPoseEditor.IsResolutionPending"/>). Returns false when unavailable.
    /// </summary>
    public static bool DeleteRefPose(Character character)
    {
        if (!CanDeleteRefPose(character)) return false;
        var custom = character.RefPose!;
        var bind = character.BindPoses.First();
        character.RefPose = bind;
        if (ReferenceEquals(character.CurrentPose, custom)) character.CurrentPose = bind;
        if (!ReferenceEquals(character.RefPoseOld, custom)) custom.Source.Parent?.RemoveChild(custom.Source);
        return true;
    }

    /// <summary>The name Create Character gives: "Character", then "Character 2", ... unused in the scene.</summary>
    public static string NextCharacterName(Scene scene)
    {
        var used = new HashSet<string>(scene.Characters.Select(c => c.Name ?? ""), StringComparer.Ordinal);
        if (!used.Contains("Character")) return "Character";
        for (int i = 2; ; i++)
            if (!used.Contains("Character " + i)) return "Character " + i;
    }

    /// <summary>
    /// Create Character: a new empty character (<see cref="SceneFactory.NewCharacter"/>) added to
    /// the scene and made its current character. Throws <see cref="InvalidOperationException"/>
    /// when the scene's schema lacks one of the classes a character needs.
    /// </summary>
    public static Character CreateCharacter(SceneModel model)
    {
        var source = SceneFactory.NewCharacter(model.Document.Schema, NextCharacterName(model.Scene));
        model.Scene.Source.AddChild(source);
        var character = (Character)model.Wrap(source);
        if (model.Scene.Source.Has("CurrentCharacter")) model.Scene.CurrentCharacter = character;
        return character;
    }
}

public sealed partial class RestPoseEditor
{
    /// <summary>
    /// Builds a custom reference pose from the current state of the pose that is the reference
    /// pose now (<see cref="CharacterBank.CreateRefPose"/>); a stale <c>__RefPose</c> child is
    /// reused and overwritten. <paramref name="posed"/> overrides the named controllers' slots with
    /// those local transforms. Null when there is no reference pose or the schema lacks the class.
    /// </summary>
    public CharacterPose? CreateRefPose(IReadOnlyDictionary<Object3D, Transform>? posed = null)
    {
        if (Character.RefPose is not { } current) return null;
        AktObject source;
        if (Character.Child("__RefPose") is CharacterPose existing && !ReferenceEquals(existing, current)
            && !ReferenceEquals(existing, Character.RefPoseOld))
        {
            source = existing.Source;
        }
        else
        {
            try
            {
                source = Character.Model.Document.Schema.NewObject("CCharacterPose");
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            source.Set("Name", "__RefPose");
            Character.Source.AddChild(source);
        }
        var matrices = CurrentStateMatrices(current);
        if (posed != null)
            foreach (var (controller, local) in posed)
                if (SlotOf(controller) is var slot and >= 0 && slot < matrices.Length) matrices[slot] = local.ToMatrix();
        source.Set("Matrices", matrices.Select(m => (object?)AktMath.ToArray(m)).ToList());
        var pose = (CharacterPose)Character.Model.Wrap(source);
        Character.CurrentPose = pose;
        Character.RefPose = pose;
        return pose;
    }
}
