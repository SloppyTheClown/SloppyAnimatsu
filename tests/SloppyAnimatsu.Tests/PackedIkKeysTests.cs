using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// IK and reverse-foot keys held as packed columns: their scalar members and per-joint chain
/// records read the same through the fast readers and the key wrappers, record edits write
/// through, a snapshot restores them, and a record list that takes a foreign value turns its key
/// list back into a plain one without losing values.
/// </summary>
public class PackedIkKeysTests
{
    private static IEnumerable<(AnimTrack Track, AktMrsKeyColumn Column)> IkTracks(SceneModel model)
    {
        foreach (var obj in model.Document.AllObjects())
            if (model.Wrap(obj) is AnimTrack track
                && SceneValues.Items(track.Source, "KeyList") is AktMrsKeyColumn { IsPacked: true } column
                && column.Shape.RecordCount > 0)
                yield return (track, column);
    }

    private static (AnimTrack Track, AktMrsKeyColumn Column, int Key)? TrackWithChain(SceneModel model)
    {
        foreach (var (track, column) in IkTracks(model))
            for (int i = 0; i < track.Keys.Count; i++)
                if (track.Keys[i] is IKKey ik && ik.ChainTransforms.Count > 0 && column.Count >= 2)
                    return (track, column, i);
        return null;
    }

    private static string Hash(SceneModel model) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AktWriter.Write(model.Document)));

    [SceneTheory, SceneSamples]
    public void IkKeys_ArePacked_AndTheFastReadersAgreeWithTheWrappers(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        int tracks = 0;
        foreach (var (track, column) in IkTracks(model))
        {
            var flags = track.ReadIKFlags();
            Assert.Equal(track.Keys.Count, flags.Length);
            for (int i = 0; i < flags.Length; i++)
                Assert.Equal((track.Keys[i] as IKKey)?.Flags ?? 0u, flags[i]);

            bool reverseFoot = AktNames.PolymorphicChain(AktNames.Key(column.Shape.TypeName)).Contains("CKeyReverseFoot");
            foreach (var field in new[] { "KeyList", "TInList", "TOutList" })
            {
                var items = SceneValues.Items(track.Source, field);
                for (int i = 0; i < items.Count; i++)
                {
                    var key = (AktStruct)items[i]!;
                    Assert.Equal(key.GetOr("PoleAngle", 0f), track.KeyScalar(field, i, "PoleAngle"));
                    if (reverseFoot)
                        foreach (var member in new[] { "RollAngle", "TwistAngle", "BankAngle" })
                            Assert.Equal(key.GetOr(member, 0f), track.KeyScalar(field, i, member));
                }
            }

            // The transform readers take IK keys too.
            var transforms = track.ReadTransforms("KeyList");
            for (int i = 0; i < transforms.Length; i++)
                Assert.Equal((track.Keys[i] as TransformKey)?.Value, transforms[i]);
            if (++tracks == 30) break;
        }
        // Not every sample animates IK; the ones that do must pack it.
        bool hasIk = model.Document.AllObjects().Any(o => model.Wrap(o) is AnimTrack t && t.Keys.Any(k => k is IKKey));
        Assert.True(!hasIk || tracks > 0, "the sample has IK keys but none of its IK key lists is packed");
    }

    [SceneTheory, SceneSamples]
    public void ChainEdits_WriteThrough_AndASnapshotRestoresThem(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        if (TrackWithChain(model) is not var (track, column, index)) return;
        string before = Hash(model);

        foreach (bool share in new[] { false, true })
        {
            var snapshot = AktFieldSnapshot.Capture(track.Source, share);
            var key = (IKKey)track.Keys[index]!;
            var chain = key.ChainTransforms.ToArray();
            chain[0] = chain[0] with { Translation = chain[0].Translation + new Vector3(0.25f, -1f, 2f) };
            key.SetChainTransforms(chain);
            Assert.True(column.IsPacked);
            Assert.Equal(chain[0].Translation, ((IKKey)track.Keys[index]!).ChainTransforms[0].Translation);
            Assert.NotEqual(before, Hash(model));

            snapshot.RestoreInto(track.Source);
            Assert.Equal(before, Hash(model));
            Assert.True(SceneValues.Items(track.Source, "KeyList") is AktMrsKeyColumn { IsPacked: true });
        }
    }

    [SceneTheory, SceneSamples]
    public void AClonedKey_SharesNothingItCanChange(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        if (TrackWithChain(model) is not var (track, _, index)) return;
        var original = (AktStruct)SceneValues.Items(track.Source, "KeyList")[index]!;
        var originalChain = new IKKey(model, original).ChainTransforms;
        string before = Hash(model);

        var copy = original.Clone();
        var entry = (AktStruct)SceneValues.Items(copy, "RefArray")[0]!;
        var t = (float[])entry.Get<float[]>("Translation").Clone();
        t[0] += 10f;
        entry.Set("Translation", t);

        Assert.Equal(originalChain, new IKKey(model, original).ChainTransforms);
        Assert.Equal(before, Hash(model));
        Assert.Equal(t[0], ((AktStruct)SceneValues.Items(copy, "RefArray")[0]!).Get<float[]>("Translation")[0]);
    }

    /// <summary>Turns every packed IK key list of <paramref name="model"/> into a plain list of key structs.</summary>
    private static int DemoteIkLists(SceneModel model)
    {
        int n = 0;
        foreach (var (track, _) in IkTracks(model).ToList())
            foreach (var field in new[] { "KeyList", "TInList", "TOutList" })
                if (SceneValues.Items(track.Source, field) is AktMrsKeyColumn { IsPacked: true } column)
                {
                    column.Add("not a key");
                    column.RemoveAt(column.Count - 1);
                    Assert.False(column.IsPacked);
                    n++;
                }
        return n;
    }

    [SceneTheory, SceneSamples]
    public void RescaleAndReframe_OfPackedIkKeys_MatchThePlainLists(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        foreach (bool reframe in new[] { false, true })
        {
            string? expected = null;
            foreach (bool demote in new[] { true, false })
            {
                var model = TestFiles.LoadScene(path);
                if (!IkTracks(model).Any()) return;
                int packedBefore = IkTracks(model).Count();
                if (demote) Assert.True(DemoteIkLists(model) > 0);
                var character = model.Scene.Characters.First();
                if (reframe)
                {
                    var offset = Matrix4x4.CreateFromYawPitchRoll(0.6f, 0.2f, -0.3f) * Matrix4x4.CreateTranslation(120f, -40f, 75f);
                    character.Source.Set("LTM", AktMath.ToArray(character.LocalMatrix * offset));
                    Assert.True(SceneReframe.Normalize(model, ReframeParts.Translation | ReframeParts.Rotation).Changed);
                }
                else
                {
                    character.LocalMatrix = Matrix4x4.CreateScale(2) * character.LocalMatrix;
                    SceneRescale.Normalize(model);
                }
                string hash = Hash(model);
                if (demote) expected = hash;
                else
                {
                    Assert.Equal(expected, hash);
                    Assert.Equal(packedBefore, IkTracks(model).Count());   // the edited lists stay packed
                }
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void AForeignRecord_DemotesTheKeyList_ValuesIntact(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        if (TrackWithChain(model) is not var (track, column, index)) return;
        int other = index == 0 ? 1 : 0;
        var otherKey = (AktStruct)column[other]!;
        var otherChain = new IKKey(model, otherKey).ChainTransforms;
        var key = (AktStruct)column[index]!;
        var firstRecord = (AktStruct)SceneValues.Items(key, "RefArray")[0]!;
        var firstTranslation = (float[])firstRecord.Get<float[]>("Translation").Clone();
        int count = column.Count;

        SceneValues.Items(key, "RefArray").Add("not a record");

        Assert.False(column.IsPacked);
        Assert.Equal(count, column.Count);
        Assert.Same(otherKey, column[other]);
        Assert.Equal(otherChain, new IKKey(model, otherKey).ChainTransforms);
        Assert.Same(firstRecord, SceneValues.Items(key, "RefArray")[0]);
        Assert.Equal(firstTranslation, firstRecord.Get<float[]>("Translation"));
        Assert.Equal("not a record", SceneValues.Items(key, "RefArray")[^1]);
    }
}
