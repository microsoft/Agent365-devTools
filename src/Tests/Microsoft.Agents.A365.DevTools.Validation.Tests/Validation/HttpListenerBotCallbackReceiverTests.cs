// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Validation;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Validation.Tests;

public class HttpListenerBotCallbackReceiverTests
{
    [Fact]
    public void IsSubstantiveMessage_TypingActivity_ReturnsFalse()
    {
        var response = new BotCallbackResponse("typing indicator", "typing");

        HttpListenerBotCallbackReceiver.IsSubstantiveMessage(response)
            .Should().BeFalse(because: "typing activities are never substantive responses");
    }

    [Theory]
    [InlineData("Here are your recent emails from today")]
    [InlineData("Got it..working on it")]
    [InlineData("Thinking...")]
    [InlineData("Hello! How can I help you today?")]
    public void IsSubstantiveMessage_MessageWithText_ReturnsTrue(string text)
    {
        var response = new BotCallbackResponse(text, "message");

        HttpListenerBotCallbackReceiver.IsSubstantiveMessage(response)
            .Should().BeTrue(
                because: "any message with text is substantive — interim detection is handled by the settle window");
    }

    [Fact]
    public void IsSubstantiveMessage_MessageWithNullText_ReturnsFalse()
    {
        var response = new BotCallbackResponse(null, "message");

        HttpListenerBotCallbackReceiver.IsSubstantiveMessage(response)
            .Should().BeFalse(because: "a message with no text is not substantive");
    }

    [Fact]
    public void IsSubstantiveMessage_NullType_WithText_ReturnsFalse()
    {
        var response = new BotCallbackResponse("some text", null);

        HttpListenerBotCallbackReceiver.IsSubstantiveMessage(response)
            .Should().BeFalse(because: "only message-type activities are substantive");
    }

    [Fact]
    public void IsSubstantiveMessage_MessageWithWhitespaceText_ReturnsFalse()
    {
        var response = new BotCallbackResponse("   ", "message");

        HttpListenerBotCallbackReceiver.IsSubstantiveMessage(response)
            .Should().BeFalse(because: "whitespace-only text is not substantive");
    }
}
