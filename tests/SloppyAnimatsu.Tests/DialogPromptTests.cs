using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The three small dialog rules moved out of the windows: the ref pose prompt
/// the Change Space list
/// and
/// the text prompt (<see cref="TextPrompt"/>). Each dialog now only draws what these return, so what
/// a button means and what counts as an answer can be checked without a window.
/// </summary>
public class DialogPromptTests
{
    [Fact]
    public void RestPosePrompt_OffersThreeAnswersThenCancel()
    {
        Assert.Equal("REFERENCE POSE", RestPosePrompt.Title);
        Assert.Equal(
            new[] { "Convert Animations", "Do Not Convert Animations", "Return to Previous Ref Pose", "Cancel" },
            RestPosePrompt.Choices.Select(choice => choice.Label).ToList());
        Assert.Equal(RestPosePrompt.Choices.Count - 1, RestPosePrompt.CancelChoice);
        Assert.Equal(RestPoseResolution.ConvertAnimations, RestPosePrompt.Resolve(0));
        Assert.Equal(RestPoseResolution.KeepAnimations, RestPosePrompt.Resolve(1));
        Assert.Equal(RestPoseResolution.ReturnToPrevious, RestPosePrompt.Resolve(2));
        Assert.Null(RestPosePrompt.Resolve(RestPosePrompt.CancelChoice));
        Assert.Null(RestPosePrompt.Resolve(-1));
        Assert.Null(RestPosePrompt.Resolve(99));
        // Every resolution is reachable, and only the cancel button answers nothing.
        Assert.Equal(
            Enum.GetValues<RestPoseResolution>().ToList(),
            RestPosePrompt.Choices.Select(choice => choice.Resolution).OfType<RestPoseResolution>().ToList());
    }

    [Fact]
    public void RestPosePrompt_MessageCountsTheChangedControllers()
    {
        string one = RestPosePrompt.Message(1);
        Assert.Contains("You modified your Reference Pose", one);
        Assert.Contains("(1 controller changed.)", one);
        string three = RestPosePrompt.Message(3);
        Assert.Contains("(3 controllers changed.)", three);
        Assert.DoesNotContain("(1 controller", three);
    }

    [Fact]
    public void TextPrompt_AnswersATrimmedNonBlankLine()
    {
        Assert.Null(TextPrompt.Answer(null));
        Assert.Null(TextPrompt.Answer(""));
        Assert.Null(TextPrompt.Answer(" \t "));
        Assert.Equal("Root", TextPrompt.Answer("  Root "));
        Assert.Equal("a b", TextPrompt.Answer("a b"));
        Assert.False(TextPrompt.HasAnswer(" "));
        Assert.True(TextPrompt.HasAnswer("x"));
    }

    [Fact]
    public void SpaceSwitchChoice_HasAMessageForAnEmptyList()
    {
        Assert.Contains(
            "Foremost you need to set some joints as available spaces in Rigging mode.",
            SpaceSwitchChoice.NoSpacesMessage);
        Assert.Contains("Picker", SpaceSwitchChoice.NoSpacesMessage);
    }

    /// <remarks>
    /// The dialog's rows are whatever the caller passes (the viewer passes the spaceable objects, and
    /// shows the message instead of the dialog when there are none), so this feeds the character's
    /// controllers: 7_AddingIK carries no spaceable flag, and the row shape must not depend on it.
    /// </remarks>
    [SceneTheory, SceneSamples]
    public void SpaceSwitchChoice_ListsTheParentThenEverySpaceWithItsCharacter(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.First();
        var spaces = character.Controllers.ToList();
        Assert.NotEmpty(spaces);

        var items = SpaceSwitchChoice.Items(spaces);
        Assert.Equal(spaces.Count + 1, items.Count);
        Assert.Equal(SpaceSwitchChoice.ParentLabel, items[0].Label);
        Assert.True(items[0].IsParent);
        Assert.Null(items[0].Space);
        for (int i = 0; i < spaces.Count; i++)
        {
            var space = spaces[i];
            Assert.False(items[i + 1].IsParent);
            Assert.Same(space, items[i + 1].Space);
            string label = items[i + 1].Label;
            if (space.Ancestor<Character>() is { } owner && !ReferenceEquals(owner, space))
            {
                // A joint of a character is qualified by that character's name.
                Assert.StartsWith((space.Name ?? "?") + "  (", label);
                Assert.Equal($"{space.Name}  ({owner.Name})", label);
            }
            else
            {
                Assert.Equal(space.Name ?? "?", label);
            }
        }

        var (parentChosen, parent) = SpaceSwitchChoice.Choose(items, 0, true);
        Assert.True(parentChosen);
        Assert.Null(parent);
        var (spaceChosen, chosen) = SpaceSwitchChoice.Choose(items, 1, true);
        Assert.True(spaceChosen);
        Assert.Same(spaces[0], chosen);
        // Cancel, no selection and an index that is no row choose nothing.
        Assert.False(SpaceSwitchChoice.Choose(items, 0, false).Chosen);
        Assert.False(SpaceSwitchChoice.Choose(items, -1, true).Chosen);
        Assert.False(SpaceSwitchChoice.Choose(items, items.Count, true).Chosen);

        // A caller's list is passed through unchanged, whatever it holds (the spaceable set is
        // empty on 7_AddingIK, so this only shapes it when it is not).
        var spaceable = KeyEditor.SpaceableObjects(model.Scene).ToList();
        Assert.Equal(spaceable.Count + 1, SpaceSwitchChoice.Items(spaceable).Count);

        // A character with nothing spaceable still offers only the parent row.
        var empty = SpaceSwitchChoice.Items([]);
        Assert.Single(empty);
        Assert.True(SpaceSwitchChoice.Choose(empty, 0, true).Chosen);
    }
}
