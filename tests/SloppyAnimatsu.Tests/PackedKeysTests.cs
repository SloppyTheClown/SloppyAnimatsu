using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Key lists held as packed float columns: element views keep their identity, edits write
/// through, inserts/removes keep views in step, a value that does not fit turns the list back
/// into a plain one, and the typed readers agree with the per-key wrappers.
/// </summary>
public class PackedKeysTests
{
    private static IEnumerable<(AnimTrack Track, AktMrsKeyColumn Column)> PackedTracks(SceneModel model)
    {
        foreach (var obj in model.Document.AllObjects())
            if (model.Wrap(obj) is AnimTrack track
                && SceneValues.Items(track.Source, "KeyList") is AktMrsKeyColumn { IsPacked: true } column)
                yield return (track, column);
    }

    private static string Hash(SceneModel model) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AktWriter.Write(model.Document)));

    [SceneTheory, SceneSamples]
    public void ReadTransforms_AgreeWithTheKeyWrappers(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        int checkedTracks = 0;
        foreach (var (track, _) in PackedTracks(model))
        {
            if (!track.HasPackedTransformKeys) continue;
            foreach (var field in new[] { "KeyList", "TInList", "TOutList" })
            {
                var fast = track.ReadTransforms(field);
                IReadOnlyList<AnimKey?> slow = field switch { "KeyList" => track.Keys, "TInList" => track.TangentsIn, _ => track.TangentsOut };
                Assert.Equal(slow.Count, fast.Length);
                for (int i = 0; i < fast.Length; i++)
                    Assert.Equal((slow[i] as TransformKey)?.Value, fast[i]);
            }
            var spaces = track.ReadSpaces();
            for (int i = 0; i < spaces.Length; i++)
                Assert.Same((track.Keys[i] as TransformKey)?.Space, spaces[i]);
            if (++checkedTracks == 50) break;
        }
        Assert.True(checkedTracks > 0, "the sample has no packed transform keys");
    }

    [SceneTheory, SceneSamples]
    public void Views_KeepIdentity_WriteThrough_AndFollowInsertsAndRemoves(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        var (track, column) = PackedTracks(model).FirstOrDefault(p => p.Track.HasPackedTransformKeys && p.Column.Count >= 3);
        if (track == null) return;

        var second = column[1];
        Assert.Same(second, column[1]);
        Assert.Equal(1, column.IndexOf(second));

        // An edit through the key wrapper lands in the column's floats.
        var key = (TransformKey)track.Keys[1]!;
        var moved = key.Value with { Translation = key.Value.Translation + new Vector3(1.5f, -2f, 3f) };
        key.Value = moved;
        Assert.True(column.IsPacked);
        Assert.Equal(moved, track.ReadTransforms("KeyList")[1]);

        // Removing the first element moves the view down with its slot.
        var first = column[0];
        column.RemoveAt(0);
        Assert.Same(second, column[0]);
        Assert.Equal(0, column.IndexOf(second));
        Assert.Equal(-1, column.IndexOf(first));
        Assert.Equal(moved, track.ReadTransforms("KeyList")[0]);

        // The removed view keeps its values, and putting it back packs it again.
        column.Insert(0, first);
        Assert.True(column.IsPacked);
        Assert.Same(second, column[1]);
        Assert.Equal(moved, track.ReadTransforms("KeyList")[1]);

        // A value that does not fit a packed key turns the list into a plain one, views intact.
        int count = column.Count;
        column.Add("not a key");
        Assert.False(column.IsPacked);
        Assert.Equal(count + 1, column.Count);
        Assert.Same(second, column[1]);
        Assert.Equal(moved, ((TransformKey)track.Keys[1]!).Value);
    }

    [SceneTheory, SceneSamples]
    public void FieldSnapshot_RestoresAPackedTrackByteIdentically(string path)
    {
        if (!AktPackedKeys.Enabled) return;
        var model = TestFiles.LoadScene(path);
        var (track, column) = PackedTracks(model).FirstOrDefault(p => p.Track.HasPackedTransformKeys && p.Column.Count >= 2);
        if (track == null) return;
        string before = Hash(model);

        foreach (bool share in new[] { false, true })
        {
            var snapshot = AktFieldSnapshot.Capture(track.Source, share);
            var key = (TransformKey)track.Keys[0]!;
            key.Value = key.Value with { Scale = key.Value.Scale * 2 };
            ((AktPackedList)SceneValues.Items(track.Source, "KeyList")).RemoveAt(1);
            Assert.NotEqual(before, Hash(model));

            snapshot.RestoreInto(track.Source);
            Assert.Equal(before, Hash(model));
            Assert.True(SceneValues.Items(track.Source, "KeyList") is AktPackedList { IsPacked: true });
        }
    }
}
