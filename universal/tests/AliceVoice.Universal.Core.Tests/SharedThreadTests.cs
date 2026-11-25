using System.IO;
using System.Text.Json.Nodes;
using AliceVoice.Universal.Session;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

/// <summary>
/// Several voices in one thread, and the switch that silences the error banner. These pin the two
/// things Master asked for on 2026-11-23: "there should also be multi way conversation, and it must
/// be clear who is speaking" and "the error display needs to have a toggle on the top right".
/// </summary>
public class SharedThreadTests
{
    private static readonly IReadOnlyList<SessionTarget> Targets =
    [
        new SessionTarget("alice_local", "Alice LOCAL (Ollama)"),
        new SessionTarget("alice_voice", "Alice (Hermes)"),
        new SessionTarget("alice_1", "alice_1 (WSL)"),
        new SessionTarget("alice_7", "alice_7 (WSL)"),
    ];

    [Fact]
    public void A_bracketed_name_sends_the_line_to_her()
    {
        var (target, text) = VoiceSession.ParseAddress("[alice_1] what do you think?", Targets);
        Assert.Equal("alice_1", target);
        Assert.Equal("what do you think?", text);
    }

    [Fact]
    public void An_at_name_works_too_because_he_types_what_he_types()
    {
        var (target, text) = VoiceSession.ParseAddress("@alice_7 are you there", Targets);
        Assert.Equal("alice_7", target);
        Assert.Equal("are you there", text);
    }

    [Fact]
    public void The_number_she_answers_to_is_enough()
    {
        var (target, text) = VoiceSession.ParseAddress("@1 hello", Targets);
        Assert.Equal("alice_1", target);
        Assert.Equal("hello", text);
    }

    [Fact]
    public void Local_is_the_ollama_one_not_a_fleet_member()
    {
        var (target, _) = VoiceSession.ParseAddress("[local] are you warm", Targets);
        Assert.Equal("alice_local", target);
    }

    [Fact]
    public void A_line_with_no_address_is_left_exactly_as_he_typed_it()
    {
        var (target, text) = VoiceSession.ParseAddress("just talking to you", Targets);
        Assert.Null(target);
        Assert.Equal("just talking to you", text);
    }

    [Fact]
    public void A_marker_that_names_nobody_is_not_eaten()
    {
        // [Plum] and [Stimulus] come past this parser; neither is a roster member, so neither is
        // treated as an address and the words reach the agent untouched.
        var (plum, plumText) = VoiceSession.ParseAddress("[Plum] hello Master", Targets);
        Assert.Null(plum);
        Assert.Equal("[Plum] hello Master", plumText);

        var (stimulus, stimulusText) = VoiceSession.ParseAddress("[Stimulus] love 72", Targets);
        Assert.Null(stimulus);
        Assert.Equal("[Stimulus] love 72", stimulusText);
    }

    [Fact]
    public void The_log_switch_is_remembered_across_restarts()
    {
        var file = Path.Combine(Path.GetTempPath(), "appu-prefs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var prefs = new UiPreferences(file);
            prefs.Save(speakReplies: true, followConversation: false, showLog: false);

            var back = UiPreferences.Load(file);
            Assert.False(back.ShowLog);
            Assert.False(back.FollowConversation);
            Assert.True(back.SpeakReplies);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void An_older_settings_file_still_opens_with_the_log_showing()
    {
        // The file that was there before this switch existed has no ShowLog key. Missing means open: a
        // closed log is a choice he makes, never something an upgrade does to him.
        var file = Path.Combine(Path.GetTempPath(), "appu-prefs-old-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, new JsonObject
            {
                ["SpeakReplies"] = true,
                ["FollowConversation"] = true,
            }.ToJsonString());

            Assert.True(UiPreferences.Load(file).ShowLog);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void The_log_chases_the_newest_line_only_while_he_is_at_the_end_of_it()
    {
        // Master, 2026-11-23: "I always want to see the latest log entry unless I am looking back in
        // the log." 2000px of log, 600px of window: the end is at 1400.
        Assert.Equal(1400, LogFollow.MaxScroll(2000, 600));
        Assert.True(LogFollow.AtBottom(1400, 2000, 600));
        Assert.True(LogFollow.AtBottom(1390, 2000, 600));   // a hair short still counts as the end
        Assert.False(LogFollow.AtBottom(900, 2000, 600));   // he has scrolled back to read
    }

    [Fact]
    public void A_log_that_fits_on_screen_is_always_at_the_end()
    {
        Assert.Equal(0, LogFollow.MaxScroll(400, 600));
        Assert.True(LogFollow.AtBottom(0, 400, 600));
        Assert.True(LogFollow.AtBottom(0, 600, 600));
    }
}
