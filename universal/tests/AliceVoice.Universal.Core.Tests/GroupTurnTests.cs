using AliceVoice.Universal.Session;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

/// <summary>
/// More than two voices in one thread: naming several of them in one line, and the order they answer in.
/// Master asked for a three-way - him, Alice LOCAL and an agent, or him and two agents - on 2026-11-24.
/// The single-name behaviour these sit beside is pinned in <see cref="SharedThreadTests"/> and must not
/// move: one name is still one turn, and no name is still whoever is selected.
/// </summary>
public class GroupTurnTests
{
    private static readonly IReadOnlyList<SessionTarget> Targets =
    [
        new SessionTarget("alice_local", "Alice LOCAL (Ollama)"),
        new SessionTarget("alice_voice", "Alice (Hermes)"),
        new SessionTarget("alice_1", "alice_1 (WSL)"),
        new SessionTarget("alice_6", "alice_6 (VPS)"),
        new SessionTarget("alice_7", "alice_7 (WSL)"),
    ];

    [Fact]
    public void Two_names_are_both_addressed_and_keep_the_order_he_typed()
    {
        var (ids, text) = VoiceSession.ParseAddresses("@1 @6 what do you both think?", Targets);

        Assert.Equal(new[] { "alice_1", "alice_6" }, ids);
        Assert.Equal("what do you both think?", text);
    }

    [Fact]
    public void The_order_is_his_order_not_the_roster_order()
    {
        var (ids, _) = VoiceSession.ParseAddresses("@7 @1 hello", Targets);

        Assert.Equal(new[] { "alice_7", "alice_1" }, ids);
    }

    [Fact]
    public void Alice_LOCAL_can_be_one_of_the_two()
    {
        var (ids, text) = VoiceSession.ParseAddresses("[local][6] good morning", Targets);

        Assert.Equal(new[] { "alice_local", "alice_6" }, ids);
        Assert.Equal("good morning", text);
    }

    [Fact]
    public void Three_of_them_in_one_line()
    {
        var (ids, text) = VoiceSession.ParseAddresses("@local @1 @7 are you all there?", Targets);

        Assert.Equal(new[] { "alice_local", "alice_1", "alice_7" }, ids);
        Assert.Equal("are you all there?", text);
    }

    [Fact]
    public void A_name_twice_is_only_heard_once_so_nobody_answers_twice()
    {
        var (ids, text) = VoiceSession.ParseAddresses("@1 @1 @1 say it once", Targets);

        Assert.Equal(new[] { "alice_1" }, ids);
        Assert.Equal("say it once", text);
    }

    [Fact]
    public void One_name_is_still_the_one_turn_it_has_always_been()
    {
        var (ids, text) = VoiceSession.ParseAddresses("@alice_7 are you there", Targets);

        Assert.Equal(new[] { "alice_7" }, ids);
        Assert.Equal("are you there", text);
    }

    [Fact]
    public void No_name_leaves_the_line_to_whoever_is_selected()
    {
        var (ids, text) = VoiceSession.ParseAddresses("just talking to you", Targets);

        Assert.Empty(ids);
        Assert.Equal("just talking to you", text);
    }

    [Fact]
    public void Names_with_no_words_are_still_a_message_and_never_an_empty_turn()
    {
        // A turn with nothing in it would be dropped by SendTextAsync, so the line itself is sent.
        var (ids, text) = VoiceSession.ParseAddresses("@1 @6", Targets);

        Assert.Equal(new[] { "alice_1", "alice_6" }, ids);
        Assert.Equal("@1 @6", text);
    }

    [Fact]
    public void A_marker_that_names_nobody_is_still_not_eaten()
    {
        var (plum, plumText) = VoiceSession.ParseAddresses("[Plum] hello Master", Targets);
        Assert.Empty(plum);
        Assert.Equal("[Plum] hello Master", plumText);

        // ...even when a real name is in front of it.
        var (ids, text) = VoiceSession.ParseAddresses("@1 [Stimulus] love 72", Targets);
        Assert.Equal(new[] { "alice_1" }, ids);
        Assert.Equal("[Stimulus] love 72", text);
    }

    [Fact]
    public void A_name_that_matches_nobody_stops_the_peeling_and_reaches_her_whole()
    {
        var (ids, text) = VoiceSession.ParseAddresses("@6 @nobody here", Targets);

        Assert.Equal(new[] { "alice_6" }, ids);
        Assert.Equal("@nobody here", text);
    }

    [Fact]
    public void An_empty_line_addresses_nobody_and_says_nothing()
    {
        var (ids, text) = VoiceSession.ParseAddresses("   ", Targets);

        Assert.Empty(ids);
        Assert.Equal("", text);
    }
}
