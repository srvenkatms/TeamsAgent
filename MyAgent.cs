// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using System.Threading.Tasks;
using System.Threading;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System;

namespace EmptyAgent;

public class MyAgent : AgentApplication
{
    private readonly HttpClient _httpClient;

    public MyAgent(AgentApplicationOptions options, HttpClient httpClient) : base(options)
    {
        _httpClient = httpClient;
        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeMessageAsync);
        OnActivity(ActivityTypes.Message, OnMessageAsync, rank: RouteRank.Last);
    }

    private async Task WelcomeMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        foreach (ChannelAccount member in turnContext.Activity.MembersAdded)
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(MessageFactory.Text("Hello and Welcome!"), cancellationToken);
            }
        }
    }

    private async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        try
        {
            // Generate a random session ID
            var sessionId = Guid.NewGuid().ToString("N")[..8]; // Use first 8 characters
            
            // Prepare the payload
            var payload = new
            {
                prompt = turnContext.Activity.Text,
                userId = turnContext.Activity.From.Id ?? "unknown",
                sessionId = sessionId
            };

            // Serialize the payload to JSON
            var jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            // Call the external API
            var response = await _httpClient.PostAsync("https://flourskagent-huggbughfpctfngs.westus2-01.azurewebsites.net/api/chat", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                // Read the response content
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                
                // Send the API response back to the user
                await turnContext.SendActivityAsync(MessageFactory.Text(responseContent), cancellationToken);
            }
            else
            {
                // Handle API error
                await turnContext.SendActivityAsync(MessageFactory.Text($"Sorry, I encountered an error while processing your request. Status: {response.StatusCode}"), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Handle any exceptions
            await turnContext.SendActivityAsync(MessageFactory.Text($"Sorry, I encountered an error: {ex.Message}"), cancellationToken);
        }
    }
}
