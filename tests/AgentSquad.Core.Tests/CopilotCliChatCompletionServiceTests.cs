using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.Tests;

public class CopilotCliChatCompletionServiceTests
{
    [Fact]
    public void FormatChatHistory_SingleUserMessage_NoLabels()
    {
        var history = new ChatHistory();
        history.AddUserMessage("Write a hello world program.");

        var prompt = CopilotCliChatCompletionService.FormatChatHistoryAsPrompt(history);

        Assert.Contains("[CRITICAL DIRECTIVE", prompt);
        Assert.Contains("Write a hello world program.", prompt);
    }

    [Fact]
    public void FormatChatHistory_SystemPlusUserMessage_IncludesSystemContext()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("You are a senior engineer.");
        history.AddUserMessage("Write a hello world program.");

        var prompt = CopilotCliChatCompletionService.FormatChatHistoryAsPrompt(history);

        Assert.Contains("[SYSTEM CONTEXT]", prompt);
        Assert.Contains("You are a senior engineer.", prompt);
        Assert.Contains("Write a hello world program.", prompt);
    }

    [Fact]
    public void FormatChatHistory_MultiTurnConversation_FormatsAsLabeled()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("You are an architect.");
        history.AddUserMessage("Design a system for X.");
        history.AddAssistantMessage("Here is my design for X...");
        history.AddUserMessage("Now add caching.");

        var prompt = CopilotCliChatCompletionService.FormatChatHistoryAsPrompt(history);

        Assert.Contains("[SYSTEM CONTEXT]", prompt);
        Assert.Contains("You are an architect.", prompt);
        Assert.Contains("[CONVERSATION HISTORY]", prompt);
        Assert.Contains("[USER]: Design a system for X.", prompt);
        Assert.Contains("[ASSISTANT]: Here is my design for X...", prompt);
        Assert.Contains("[USER]: Now add caching.", prompt);
        Assert.Contains("[INSTRUCTION]:", prompt);
        Assert.Contains("[REMINDER]:", prompt);
    }

    [Fact]
    public void FormatChatHistory_EmptyHistory_ReturnsDirectiveOnly()
    {
        var history = new ChatHistory();
        var prompt = CopilotCliChatCompletionService.FormatChatHistoryAsPrompt(history);
        Assert.Contains("[CRITICAL DIRECTIVE", prompt);
    }

    [Fact]
    public void FormatChatHistory_SystemOnly_ReturnsSystemContext()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("You are helpful.");

        var prompt = CopilotCliChatCompletionService.FormatChatHistoryAsPrompt(history);

        Assert.Contains("[SYSTEM CONTEXT]", prompt);
        Assert.Contains("You are helpful.", prompt);
    }

    [Fact]
    public void FormatChatHistory_MultipleSystemMessages_CombinesThem()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("You are a developer.");
        history.AddSystemMessage("Follow clean code principles.");
        history.AddUserMessage("Write a function.");

        var prompt = CopilotCliChatCompletionService.FormatChatHistoryAsPrompt(history);

        Assert.Contains("You are a developer.", prompt);
        Assert.Contains("Follow clean code principles.", prompt);
    }

    [Fact]
    public void FormatChatHistory_FastMode_InjectsBrevityConstraint()
    {
        var history = new ChatHistory();
        history.AddUserMessage("Write a document.");

        var config = new CopilotCliConfig { FastMode = true };
        var prompt = CopilotCliChatCompletionService.FormatChatHistoryAsPrompt(history, config);

        Assert.Contains("[SPEED MODE", prompt);
        Assert.Contains("500 words", prompt);
    }

    [Fact]
    public void FormatChatHistory_NormalMode_NoBrevityConstraint()
    {
        var history = new ChatHistory();
        history.AddUserMessage("Write a document.");

        var config = new CopilotCliConfig { FastMode = false };
        var prompt = CopilotCliChatCompletionService.FormatChatHistoryAsPrompt(history, config);

        Assert.DoesNotContain("[SPEED MODE", prompt);
    }

    [Fact]
    public void StripMetaCommentary_MetaPrefixWithHeading_ExtractsContent()
    {
        var response = "I've created the PMSpec document. Here it is:\n\n# PM Specification\n\n## Executive Summary\nWe are building a dashboard.";
        var stripped = CopilotCliChatCompletionService.StripMetaCommentary(response);
        Assert.StartsWith("# PM Specification", stripped);
    }

    [Fact]
    public void StripMetaCommentary_CleanContent_ReturnsUnchanged()
    {
        var response = "# Architecture Document\n\n## Overview\nThis is the architecture.";
        var stripped = CopilotCliChatCompletionService.StripMetaCommentary(response);
        Assert.Equal(response, stripped);
    }

    [Fact]
    public void StripMetaCommentary_TrailingMeta_RemovesTrailing()
    {
        var response = "# Document\n\nContent here.\n\nThe file has been saved to the session-state folder.";
        var stripped = CopilotCliChatCompletionService.StripMetaCommentary(response);
        Assert.DoesNotContain("session-state", stripped);
        Assert.Contains("Content here.", stripped);
    }

    [Theory]
    [InlineData("Authentication failure: Authentication required", true)]
    [InlineData("Process error: Authentication failure: Authentication required", true)]
    [InlineData("unauthorized access", true)]
    [InlineData("401 Unauthorized", true)]
    [InlineData("rate limit exceeded", true)]
    [InlineData("429 Too Many Requests", true)]
    [InlineData("connection refused", true)]
    [InlineData("request timed out", true)]
    [InlineData("503 Service Unavailable", true)]
    [InlineData("Copilot CLI is not available", false)]
    [InlineData("Failed to parse response", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTransientError_ClassifiesCorrectly(string? error, bool expected)
    {
        Assert.Equal(expected, CopilotCliChatCompletionService.IsTransientError(error));
    }
}
