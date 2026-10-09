using MeetingAssist.App.Interop;

namespace MeetingAssist.Tests;

/// <summary>
/// Combination parsing is the one part of the hotkey layer that can be tested without a
/// window. It matters: a typo in the config file must leave one action unbound with a warning,
/// never crash the app on startup or bind the wrong key.
/// </summary>
public class HotkeyComboTests
{
    [Theory]
    [InlineData("Ctrl+Alt+G", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x47)]
    [InlineData("ctrl+alt+g", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x47)]
    [InlineData("Control+Alt+Space", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20)]
    [InlineData("Ctrl+Alt+Shift+Space", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x20)]
    [InlineData("Ctrl+Alt+1", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31)]
    [InlineData("Ctrl+Alt+F5", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x74)]
    [InlineData("Ctrl+Alt+f5", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x74)]
    [InlineData("Win+Alt+Enter", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x0D)]
    [InlineData("  Ctrl + Alt + P  ", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x50)]
    public void Valid_combinations_parse(string combo, HotkeyModifiers expectedMods, uint expectedKey)
    {
        Assert.True(HotkeyCombo.TryParse(combo, out var mods, out var key));
        Assert.Equal(expectedMods, mods);
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("G")]                 // no modifier would swallow the key system-wide
    [InlineData("Space")]
    [InlineData("Ctrl+Alt")]          // modifiers only, no key
    [InlineData("Ctrl+Alt+G+H")]      // two keys
    [InlineData("Ctrl+Alt+F25")]      // beyond F24
    [InlineData("Ctrl+Alt+NotAKey")]
    public void Invalid_combinations_are_rejected_rather_than_guessed(string combo)
    {
        Assert.False(HotkeyCombo.TryParse(combo, out _, out _));
    }

    [Fact]
    public void Every_action_has_a_default_binding()
    {
        // An action with no default would be silently unreachable.
        var settings = new HotkeySettings();
        foreach (var action in Enum.GetValues<HotkeyAction>())
            Assert.True(settings.Bindings.ContainsKey(action.ToString()), $"{action} has no default");
    }

    [Fact]
    public void Default_bindings_all_parse_and_are_distinct()
    {
        var resolved = new HotkeySettings().Resolve().ToArray();

        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, resolved.Length);

        // Two actions on one combination means the second silently never fires.
        var duplicates = resolved
            .GroupBy(b => (b.Modifiers, b.VirtualKey))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" and ", g.Select(b => b.Action)));

        Assert.Empty(duplicates);
    }

    [Fact]
    public void An_unparseable_binding_is_skipped_without_taking_the_others_with_it()
    {
        var settings = new HotkeySettings();
        settings.Bindings[nameof(HotkeyAction.AskNarrow)] = "Ctrl+Alt+ThisIsNotAKey";

        var resolved = settings.Resolve().ToArray();

        Assert.DoesNotContain(resolved, b => b.Action == HotkeyAction.AskNarrow);
        Assert.Contains(resolved, b => b.Action == HotkeyAction.ToggleSession);
    }

    [Fact]
    public void Bindings_left_on_the_old_defaults_move_to_the_new_ones()
    {
        var saved = new HotkeySettings { Version = 0 };
        saved.Bindings[nameof(HotkeyAction.AskNarrow)] = "Ctrl+Alt+G";   // old default
        saved.Bindings[nameof(HotkeyAction.MockNext)] = "Ctrl+Alt+M";    // the user's choice
        saved.Bindings.Remove(nameof(HotkeyAction.TogglePause));         // did not exist then

        var merged = HotkeySettings.Merge(saved);

        Assert.Equal("Ctrl+Alt+S", merged.Bindings[nameof(HotkeyAction.AskNarrow)]);
        Assert.Equal("Ctrl+Alt+M", merged.Bindings[nameof(HotkeyAction.MockNext)]);
        Assert.Equal("Ctrl+Alt+F", merged.Bindings[nameof(HotkeyAction.TogglePause)]);
        Assert.Equal(2, merged.Version);
    }

    [Fact]
    public void A_current_file_keeps_an_old_combination_the_user_chose_again()
    {
        var saved = new HotkeySettings { Version = 2 };
        saved.Bindings[nameof(HotkeyAction.AskNarrow)] = "Ctrl+Alt+G";

        Assert.Equal("Ctrl+Alt+G", HotkeySettings.Merge(saved).Bindings[nameof(HotkeyAction.AskNarrow)]);
    }
}
