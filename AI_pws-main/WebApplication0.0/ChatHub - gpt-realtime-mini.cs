using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

private async Task GetAIResponseStreaming(string userMessage)
{
    var apiKey = _config["OpenAI:ApiKey"];
    // connecting the WebSocket
    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
    await socket.ConnectAsync(
        new Uri("wss://api.openai.com/v1/realtime?model=gpt-realtime-mini"), // Connect to Realtime API with GPT-Realtime-mini
        CancellationToken.None
    );

    // Sending the initial user message (input)
    var initPayload = JsonSerializer.Serialize(new
    {
        type = "input_text",
        text = userMessage
    });

    await socket.SendAsync(
        Encoding.UTF8.GetBytes(initPayload),
        WebSocketMessageType.Text,
        true,
        CancellationToken.None
    );
    // Receiving streaming responses (HTTP Post instead of WebSocket to get no streaming (final response only) / remove "while (socket.State == WebSocketState.Open"))
    var buffer = new byte[8192];
    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            break;
        }
        // Parsing the streamed GPT output
        var messageJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;

            // Realtime API streams chunks with type="output_text.delta"
            if (root.TryGetProperty("type", out var typeProp))
            {
                var type = typeProp.GetString();
                if (type == "output_text.delta")
                {
                    var textChunk = root.GetProperty("text").GetString();
                    await Clients.All.SendAsync("ReceiveMessage", "AI", textChunk);
                }
                else if (type == "output_audio.delta")
                {
                    var audioChunk = Convert.FromBase64String(root.GetProperty("audio").GetString());
                    await Clients.All.SendAsync("ReceiveAudio", "AI", audioChunk);
                }
            }
        }

        // Error handling
        catch (JsonException)
        {
            // Ignore invalid JSON chunks (happens in streaming)
        }
    }
}
