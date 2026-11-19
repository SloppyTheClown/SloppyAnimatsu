using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The keyboard shortcut dialog's own code, moved into <see cref="ShortcutSession"/> and <see cref="ShortcutKeyMap"/>: the
/// rows it lists, the capture state machine - a modifier alone, a key the key map does not name and a key
/// another action already uses are all refused, and Escape cancels - the Unbind/Reset/Reset all
/// edits, and the save-on-close that creates a missing file. The shipped map is used, so the user's
/// shortcuts.slopa is never read or written.
/// </summary>
public class ShortcutSessionTests
{
    [Fact]
    public void Session_ListsEveryShippedActionAndItsKey()
    {
        var shortcuts = Shortcuts.CreateDefault();
        var session = new ShortcutSession(shortcuts);

        Assert.Equal(shortcuts.ActionNames, session.Actions);
        Assert.EndsWith(Shortcuts.FileName, session.FilePath);
        Assert.Contains(session.FilePath, session.Heading);
        Assert.Equal(0, session.SelectedIndex);
        Assert.Equal(session.Actions[0], session.SelectedAction);
        Assert.Equal(session.SelectionHelp, session.Status);
        Assert.False(session.Dirty);
        Assert.Null(session.Capturing);

        var rows = session.Rows;
        Assert.Equal(session.Actions.Count, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            string action = session.Actions[i];
            Assert.True(action.Length < ShortcutSession.NameWidth, $"'{action}' is wider than the name column");
            var key = shortcuts.KeyFor(action);
            Assert.Equal(action, rows[i][..ShortcutSession.NameWidth].TrimEnd());
            Assert.Equal(
                key is { IsEmpty: false } pressed ? pressed.ToString(): ShortcutSession.NoKeyMark,
                rows[i][ShortcutSession.NameWidth..].Trim());
        }

        // The one shipped action with no key shows the mark, and its help says it has no key.
        int index = session.Actions.ToList().IndexOf("ResetAndFreeze");
        Assert.True(index >= 0);
        session.Select(index);
        Assert.Equal("ResetAndFreeze has no key.", session.Status);
        Assert.EndsWith(ShortcutSession.NoKeyMark, session.Rows[index]);
        Assert.Equal(0u, shortcuts.KeyFor("ResetAndFreeze")!.Value.Code);
    }

    [Fact]
    public void Capture_RefusesAModifierAnUnnamedKeyAndATakenKey()
    {
        var shortcuts = Shortcuts.CreateDefault();
        var session = new ShortcutSession(shortcuts);
        session.Select(session.Actions.ToList().IndexOf("Play"));
        var own = shortcuts.KeyFor("Play");
        Assert.NotNull(own);

        Assert.True(session.BeginCapture());
        Assert.Equal("Play", session.Capturing);
        Assert.Contains("Press the new key for Play", session.Status);

        var unnamed = session.Press(null, "FancyKey");
        Assert.True(unnamed.Handled);
        Assert.False(unnamed.Assigned);
        Assert.Equal("'FancyKey' is not a key the key map names.", unnamed.Status);
        // A refused key keeps the capture armed, so the next press is still attempted.
        Assert.Equal("Play", session.Capturing);

        var modifier = session.Modifier();
        Assert.True(modifier.Handled);
        Assert.False(modifier.Assigned);
        Assert.Equal(ShortcutSession.ModifierText, modifier.Status);

        // A key another action uses is refused, not stolen.
        var taken = ShortcutKey.Parse("DELETE");
        string? owner = shortcuts.ActionFor(taken);
        Assert.NotNull(owner);
        var duplicate = session.Press(taken, "Delete");
        Assert.True(duplicate.Handled);
        Assert.False(duplicate.Assigned);
        Assert.Contains(owner!, duplicate.Status);
        Assert.Equal(own, shortcuts.KeyFor("Play"));   // Play still holds its own key

        var cancel = session.Escape();
        Assert.True(cancel.Handled);
        Assert.False(cancel.Assigned);
        Assert.Equal(ShortcutSession.CancelledText, cancel.Status);
        Assert.Null(session.Capturing);
        Assert.False(session.Dirty);
        // With no capture armed Escape is not the dialog's: the window closes instead.
        Assert.False(session.Escape().Handled);
    }

    [Fact]
    public void Capture_AssignsAFreeKeyAndFreesTheOldOne()
    {
        var shortcuts = Shortcuts.CreateDefault();
        var session = new ShortcutSession(shortcuts);
        int index = session.Actions.ToList().IndexOf("Play");
        session.Select(index);
        var before = shortcuts.KeyFor("Play");
        Assert.NotNull(before);

        var free = new ShortcutKey(0x7B, ShortcutModifiers.Shift);   // SHIFT+F12
        Assert.Null(shortcuts.ActionFor(free));
        Assert.True(session.BeginCapture());
        var result = session.Press(free, "F12");

        Assert.True(result.Handled);
        Assert.True(result.Assigned);
        Assert.Equal("Play = SHIFT+F12.", result.Status);
        Assert.Null(session.Capturing);
        Assert.True(session.Dirty);
        Assert.Equal(free, shortcuts.KeyFor("Play"));
        Assert.EndsWith("SHIFT+F12", session.Rows[index]);
        // The key Play used to hold is free for another action.
        Assert.Null(shortcuts.ActionFor(before!.Value));

        // A code the key map does not name is refused even when the viewer hands one over.
        Assert.True(session.BeginCapture());
        var unknown = session.Press(new ShortcutKey(0x07, ShortcutModifiers.None), "x");
        Assert.False(unknown.Assigned);
        Assert.Contains("not a key the key map knows", unknown.Status);
    }

    [Fact]
    public void Edits_UnbindResetAndResetAllReportAndMarkDirty()
    {
        var shortcuts = Shortcuts.CreateDefault();
        var session = new ShortcutSession(shortcuts);
        int index = session.Actions.ToList().IndexOf("Play");
        session.Select(index);
        var shipped = shortcuts.KeyFor("Play");
        Assert.NotNull(shipped);

        Assert.True(session.Unbind());
        Assert.True(session.Dirty);
        Assert.Null(shortcuts.KeyFor("Play"));
        Assert.Equal("Play has no key.", session.Status);
        Assert.EndsWith(ShortcutSession.NoKeyMark, session.Rows[index]);
        // The action stays listed after Unbind, so it can be bound again.
        Assert.Contains("Play", session.Actions);

        Assert.False(session.Unbind());
        Assert.Equal(ShortcutSession.NothingToUnbindText, session.Status);

        Assert.True(session.Reset());
        Assert.Equal(shipped, shortcuts.KeyFor("Play"));
        Assert.Contains("Play is bound to", session.Status);

        // A custom binding is what Reset all drops.
        Assert.True(shortcuts.TrySet("MyCustomAction", new ShortcutKey(0x7A, ShortcutModifiers.None), out string? reason), reason);
        Assert.True(session.ResetAll());
        Assert.True(session.Dirty);
        Assert.DoesNotContain("MyCustomAction", shortcuts.ActionNames);
        Assert.Equal(Shortcuts.DefaultBindings.Select(b => b.Code).ToList(), shortcuts.Bindings.Select(b => b.Code).ToList());
    }

    [Fact]
    public void Save_WritesTheMapAndCreatesAMissingFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-shortcuts-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(folder, Shortcuts.FileName);
        try
        {
            var shortcuts = Shortcuts.CreateDefault();
            var session = new ShortcutSession(shortcuts, file);
            Assert.Null(shortcuts.Path);
            Assert.Equal(file, session.FilePath);
            Assert.Contains(file, session.Heading);

            Assert.True(session.TrySave(), session.Status);
            Assert.True(File.Exists(file));
            Assert.Equal(file, shortcuts.Path);
            Assert.Equal($"Saved {file}.", session.Status);
            Assert.False(session.Dirty);

            var loaded = Shortcuts.Load(file);
            Assert.Equal(
                Shortcuts.DefaultBindings.Select(b => b.Code).ToList(),
                loaded.Bindings.Select(b => b.Code).ToList());

            // An edited map is written when the dialog closes; nothing edited closes without a write.
            session.Select(session.Actions.ToList().IndexOf("Play"));
            Assert.True(session.Unbind());
            Assert.True(session.TryClose());
            Assert.Null(Shortcuts.Load(file).KeyFor("Play"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Save_ReportsAWriteItCannotDoAndKeepsTheWindowOpen()
    {
        string blocker = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-shortcuts-block-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the folder should be");
        try
        {
            var session = new ShortcutSession(Shortcuts.CreateDefault(), Path.Combine(blocker, Shortcuts.FileName));
            session.RequestSave();
            Assert.False(session.TrySave());
            Assert.StartsWith("Could not save the shortcuts:", session.Status);
            // A failed save keeps the dialog open, so the edit is not lost.
            Assert.False(session.TryClose());
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void KeyMap_NamesEveryShippedBindingAndRoundTrips()
    {
        // Every key the shipped map binds must be capturable in the dialog; the map's one no-key
        // placeholder (ResetAndFreeze, code 0) has no key to capture.
        foreach (var binding in Shortcuts.DefaultBindings.Where(b => !b.Key.IsEmpty))
        {
            string? name = ShortcutKeyMap.KeyName(binding.Key.VirtualKey);
            Assert.False(
                string.IsNullOrWhiteSpace(name),
                $"{binding.Action} uses VK {binding.Key.VirtualKey:X2}, which has no UI key");
        }

        // One name per code, and every name gives its code back.
        Assert.Equal(
            ShortcutKeyMap.Table.Count,
            ShortcutKeyMap.Table.Values.Distinct(StringComparer.Ordinal).Count());
        foreach (var (code, name) in ShortcutKeyMap.Table)
        {
            Assert.True(ShortcutKeyMap.TryVirtualKey(name, out int back), name);
            Assert.Equal(code, back);
            // The viewer parses the name as an Avalonia.Input.Key member.
            Assert.Matches("^[A-Za-z0-9]+$", name);
            Assert.True(ShortcutKey.IsKnownKey(code), $"VK {code:X2} has a UI key but is not one the key map names");
        }

        Assert.Null(ShortcutKeyMap.KeyName(0x07));
        Assert.False(ShortcutKeyMap.TryVirtualKey("NoSuchKey", out _));
        Assert.False(ShortcutKeyMap.TryVirtualKey(null, out _));

        // The spellings the viewer parses, spelled the way Avalonia spells them.
        Assert.Equal("Return", ShortcutKeyMap.KeyName(0x0D));
        Assert.Equal("D0", ShortcutKeyMap.KeyName(0x30));
        Assert.Equal("NumPad0", ShortcutKeyMap.KeyName(0x60));
        Assert.Equal("F12", ShortcutKeyMap.KeyName(0x7B));
        Assert.Equal("OemPlus", ShortcutKeyMap.KeyName(0xBB));
        Assert.Equal("Space", ShortcutKeyMap.KeyName(0x20));
    }
}
