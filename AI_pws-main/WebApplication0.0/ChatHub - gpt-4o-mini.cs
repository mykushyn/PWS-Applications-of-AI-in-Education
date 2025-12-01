using Microsoft.AspNetCore.SignalR;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;


public class ChatHub : Hub
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly string _docsPath;
    private static readonly ConcurrentDictionary<string, List<ChatMessage>> _conversations = new();
    public ChatHub(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs");
    }
    public class ChatMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
    }
    public async Task SendMessage(string user, string message, string bookName = null)
    {
        await Clients.All.SendAsync("ReceiveMessage", user, message);
        
        // Get relevant context from documents
        var context = GetRelevantContext(message, bookName);

        var conversationHistory = _conversations.GetOrAdd(user, _ => new List<ChatMessage>());  

        // Call AI API
        var aiResponse = await GetAIResponse(message, context, bookName, conversationHistory);
        // Update conversation history
        conversationHistory.Add(new ChatMessage { Role = "user", Content = message });
        conversationHistory.Add(new ChatMessage { Role = "assistant", Content = aiResponse });

        // geschiedenis beperken om token limiet voorkomen
        if (conversationHistory.Count > 20)
        {
            conversationHistory.RemoveRange(0, conversationHistory.Count - 20); // keep last 20 messages (20 = 10 user + 10 assistant)
        }

        await Clients.All.SendAsync("ReceiveMessage", "AI", aiResponse);

        // Generate TTS audio from AI response
        var aiAudio = await GetTTSAudio(aiResponse);

        if (aiAudio != null)
        {
            // Send audio as base64 string to avoid serialization differences
            var base64 = Convert.ToBase64String(aiAudio);
            await Clients.All.SendAsync("ReceiveAudio", "AI", base64);
        }
        else
        {
            Console.WriteLine("TTS: no audio returned for AI response.");
        }
    }
    //========================================= DISCONNECT HANDLING ========================================
    public override async Task OnDisconnectedAsync(Exception? exception)
{
    var user = Context.UserIdentifier;
    if (user != null)
    {
        _conversations.TryRemove(user, out _);
    }

    await base.OnDisconnectedAsync(exception);
}
/* als niet werkt --> public async Task ResetConversation(string user)
    {
        _conversations.TryRemove(user, out _);
        await Clients.Caller.SendAsync("ReceiveMessage", "System", "Conversation history has been reset.");
    }*/
        public async Task<List<ChatMessage>> GetConversationHistory(string user)
    {
        return _conversations.TryGetValue(user, out var history) ? history : new List<ChatMessage>();
    }
//========================================= EIND DISCONNECT HANDLING ========================================
    private string GetRelevantContext(string userMessage, string bookName)
    {
        try
        {
            // Create Documents folder if it doesn't exist
            if (!Directory.Exists(_docsPath))
            {
                Directory.CreateDirectory(_docsPath);
                return string.Empty;
            }

            var relevantChunks = new List<string>();

            // Get all .txt files from the Documents folder
            var txtFiles = Directory.GetFiles(_docsPath, "*.txt");

            // If a specific book is selected, prioritize that book's file
            if (!string.IsNullOrEmpty(bookName))         //-------------------------------wordt gekozen in index.html dropdown (bijv. biologie/natuurkunde etc.)
            {
                var bookFile = txtFiles.FirstOrDefault(f => 
                    Path.GetFileNameWithoutExtension(f).Contains(bookName, StringComparison.OrdinalIgnoreCase));
                
                if (bookFile != null)
                {
                    var bookContent = File.ReadAllText(bookFile);
                    // Simple keyword matching - split into chunks
                    var chunks = SplitIntoChunks(bookContent, 500);
                    relevantChunks.AddRange(FindRelevantChunks(chunks, userMessage, 3));
                }
            }

            // If we don't have enough context, search all files
            if (relevantChunks.Count < 2)
            {
                foreach (var file in txtFiles)
                {
                    var content = File.ReadAllText(file);
                    var chunks = SplitIntoChunks(content, 500);
                    relevantChunks.AddRange(FindRelevantChunks(chunks, userMessage, 2));
                    
                    if (relevantChunks.Count >= 3) break;
                }
            }

            return relevantChunks.Any() 
                ? string.Join("\n\n---\n\n", relevantChunks) 
                : string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting context: {ex.Message}");
            return string.Empty;
        }
    }

    private List<string> SplitIntoChunks(string text, int chunkSize)
    {
        var chunks = new List<string>();
        var sentences = text.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        var currentChunk = "";

        foreach (var sentence in sentences)
        {
            if (currentChunk.Length + sentence.Length > chunkSize && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.Trim());
                currentChunk = "";
            }
            currentChunk += sentence + ". ";
        }

        if (!string.IsNullOrWhiteSpace(currentChunk))
        {
            chunks.Add(currentChunk.Trim());
        }

        return chunks;
    }

    private List<string> FindRelevantChunks(List<string> chunks, string query, int topK)
    {
        // Simple keyword-based relevance scoring
        var queryWords = query.ToLower()
            .Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .ToHashSet();

        var scoredChunks = chunks.Select(chunk => new
        {
            Chunk = chunk,
            Score = queryWords.Count(word => chunk.ToLower().Contains(word))
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Take(topK)
        .Select(x => x.Chunk)
        .ToList();

        return scoredChunks;
    }

    private async Task<string> GetAIResponse(string userMessage, string context, string bookName, List<ChatMessage> conversationHistory)
    {
        var client = _httpClientFactory.CreateClient();
        var apiKey = _config["OpenAI:ApiKey"];
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        
         // Build the system prompt ------------------------ kan je hier nog iets mee? --> onderzoeken (BuildSystemPrompt)
        var systemPrompt = BuildSystemPrompt(bookName);

        // Build messages array with system prompt, context, and user message
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        // Add context if available
        if (!string.IsNullOrEmpty(context))
        {
            messages.Add(new 
            { 
                role = "system", 
                content = $@"Here is relevant information from the textbook:
                    {context}
                    You MUST use ONLY information from these textbooks to teach. Base your step-by-step guidance on these concepts." 
            });
        }

        foreach (var msg in conversationHistory)
        {
            messages.Add(new { role = msg.Role, content = msg.Content });
        }

        // Add user message
        messages.Add(new
        {
            role = "user",
            content = $@"{userMessage}
                Remember: Guide me through this step-by-step. Don't give me the final answer directly." 
        });



        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = messages.ToArray(),
            temperature = 0.4,
            max_tokens = 150,
            presence_penalty = 0.5,
            frequency_penalty = 0.4
        };

        var response = await client.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", requestBody);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            return $"AI request failed: {response.StatusCode}: {errorText}";
        }

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var msgContent))
                return msgContent.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
    //------------------------- PROMPTEN ------------------------
        private string BuildSystemPrompt(string bookName)
    {
        var basePrompt = @"You are a friendly and patient virtual teacher, named Prismiq, for Prism AI, designed to help students learn from their textbooks through GUIDED LEARNING. Your creators are Tarek Almallouhi and Mykyta Kushynov.

CRITICAL RULES - YOU MUST FOLLOW THESE:
1. NEVER give the final answer directly
2. ALWAYS break down problems into smaller steps
3. Guide students through each step with questions
4. Wait for student responses before revealing the next step
5. REMEMBER the conversation context - refer back to previous explanations when relevant

YOUR TEACHING METHOD:
When a student asks a question, you MUST follow this structure:

Step 1: Acknowledge their question and identify what concept it relates to
Step 2: Break the problem into 2-4 smaller steps
Step 3: Guide them through the FIRST step only by:
   - Explaining the concept needed
   - Asking a guiding question
   - Providing a hint if needed
Step 4: Wait for them to respond before continuing

CONVERSATION CONTINUITY:
- Remember what you've already explained in this conversation
- Build on previous explanations
- If a student asks a follow-up question, acknowledge their progress
- Reference earlier parts of the conversation when helpful

EXAMPLES OF GOOD RESPONSES:
BAD: ""The answer is 42 because you multiply 6 by 7.""
GOOD: ""Great question! To solve this, we need to understand multiplication. First, can you tell me what 6 × 7 means in your own words? Think about it as repeated addition.""

YOUR PERSONALITY:
- Friendly and encouraging
- Patient and supportive
- Adapt to the student's tone (casual or formal)
- Show empathy for personal problems
- Celebrate their progress

USING TEXTBOOK CONTENT:
- Base your teaching on the provided textbook excerpts
- If information is missing, honestly say so
- Don't make up facts or guess";

        if (!string.IsNullOrEmpty(bookName))
        {
            basePrompt += $"You are currently helping with: {bookName}";
            basePrompt += $"Focus your explanations and examples on topics covered in the {bookName} textbook.";
        }

        basePrompt += "\n\nAlways be concise but thorough. Your goal is to help students understand, not just give them answers.";

        return basePrompt;
    }
    //------------------------- EIND PROMPTEN ------------------------

    // TTS: text ? audio
    private async Task<byte[]> GetTTSAudio(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var client = _httpClientFactory.CreateClient();
        var apiKey = _config["OpenAI:ApiKey"];
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var ttsRequest = new
        {
            model = "gpt-4o-mini-tts",
            voice = "sage",
            input = text,
            format = "mp3"
        };

        var response = await client.PostAsJsonAsync("https://api.openai.com/v1/audio/speech", ttsRequest);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"TTS request failed: {response.StatusCode}: {err}");
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync();
    }

    // STT: audio ? text
    private async Task<string> GetSTTText(byte[] audioBytes)
    {
        // Check if audio exists
        if (audioBytes == null || audioBytes.Length == 0) return string.Empty;
        // Create HTTP client and set API key
        var client = _httpClientFactory.CreateClient();
        var apiKey = _config["OpenAI:ApiKey"];
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        // Prepare multipart form data
        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mp3");
        content.Add(audioContent, "file", "audio.mp3");
        content.Add(new StringContent("whisper-1"), "model");
        // Send POST request to OpenAI
        var response = await client.PostAsync("https://api.openai.com/v1/audio/transcriptions", content);
        if (!response.IsSuccessStatusCode) return string.Empty;
        // Parse JSON response
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("text", out var textProp))
            return textProp.GetString() ?? string.Empty;

        return string.Empty;
    }
}
